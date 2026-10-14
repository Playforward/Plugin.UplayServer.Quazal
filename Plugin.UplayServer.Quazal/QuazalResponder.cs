using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Plugin.UplayServer.Quazal.Enums;
using Plugin.UplayServer.Quazal.Serializables;
using System.Net;

namespace Plugin.UplayServer.Quazal;

/// <summary>
/// The transport boundary for a legacy Quazal listener. It establishes only the
/// PRUDP envelope encrypted RMC login 
/// </summary>
internal sealed partial class QuazalResponder : IDisposable
{
    private readonly QuazalSettings settings;
    private readonly IReadOnlyDictionary<int, QuazalTitle> _titlesByPort;

    /// <summary>
    /// Every title this responder serves, keyed by listening port. The port
    /// is the whole of resolution: a packet's title is known from the socket
    /// it arrived on, before a byte of it is parsed, and the choice never
    /// depends on anything the client could get wrong or forge.
    /// </summary>
    /// <param name="settings">The plugin's settings.</param>
    /// <param name="titlesByPort">The titles served, by listening port.</param>
    /// <param name="time">The clock sessions go idle by; the system's unless a test supplies one.</param>
    public QuazalResponder(QuazalSettings settings, IReadOnlyDictionary<int, QuazalTitle> titlesByPort,
        TimeProvider? time = null)
    {
        this.settings = settings;
        _titlesByPort = titlesByPort;
        _time = time ?? TimeProvider.System;
        // One identity document may back several titles; load each once.
        foreach (string file in titlesByPort.Values.Select(t => t.Descriptor.RendezVousIdentityFile).Distinct())
            _signingIdentities[file] = RendezVousIdentity.Load(file);
        BuildProtocolTables(titlesByPort.Values);
    }

    private sealed class Peer
    {
        /// <summary>The title resolved from the port this session arrived on, fixed at SYN.</summary>
        public required QuazalTitle Title { get; init; }
        public required uint ServerSignature { get; set; }
        public uint ClientSignature { get; set; }
        public byte AccessKeyChecksum { get; set; }
        public bool SynAcknowledged { get; set; }
        public bool Connected { get; set; }
        public ECDiffieHellman? SecureAgreement { get; set; }
        public byte[]? SecureSessionKey { get; set; }
        public byte[]? SecureConnectSignature { get; set; }
        public byte[]? SecureConnectExtension { get; set; }
        public byte[]? LastClientEcdhBlob { get; set; }
        public uint? AuthenticatedPid { get; set; }
        /// <summary>Sealed reply for a Kerberos secure CONNECT, if one was opened.</summary>
        public byte[]? KerberosConnectResponse { get; set; }
        public ushort ServerSequenceId { get; set; } = 1;
        public ushort RmcResponseCounter { get; set; }
        public int ChecksumWidth { get; set; } = PrudpPacketV0.LegacyChecksumWidth;
        public QuazalChecksumAlgorithm ChecksumAlgorithm { get; set; }
            = QuazalChecksumAlgorithm.LegacyWordSum;
        /// <summary>
        /// RMC bytes of a request still arriving in PRUDP fragments
        /// (fragment ids 1, 2 ... then 0 for the last); decoded once the
        /// last one lands.
        /// </summary>
        public List<byte> PendingFragments { get; } = [];
        /// <summary>
        /// The replies already sent for recent reliable DATA sequence ids,
        /// so a retransmission is answered again rather than executed
        /// again (see <see cref="ReplayWindow"/>).
        /// </summary>
        public Dictionary<ushort, List<byte[]>> Answered { get; } = [];
        public Queue<ushort> AnsweredOrder { get; } = [];
        /// <summary>Where this session's packets come from, and the header shape they carry, for pushing to it.</summary>
        public IPEndPoint? Remote { get; set; }
        public int LocalPort { get; set; }
        public VirtualPort ClientPort { get; set; }
        public VirtualPort ServerPort { get; set; }
        public byte SessionId { get; set; }
        /// <summary>Call ids of requests this server sends to the client (notifications).</summary>
        public uint ServerCallId { get; set; } = 50_000;
        /// <summary>The Rendez-Vous connection id Register handed this session, once it has.</summary>
        public uint? ConnectionId { get; set; }

        // Written by the receive path and read by the sweep, on different threads.
        private long _lastHeard;

        /// <summary>
        /// When this session last sent a packet the server accepted, as a
        /// <see cref="TimeProvider"/> timestamp. See <see cref="QuazalResponder.ForgetIdlePeers"/>.
        /// </summary>
        public long LastHeard
        {
            get => Volatile.Read(ref _lastHeard);
            set => Volatile.Write(ref _lastHeard, value);
        }
    }

    /// <summary>Last connection id handed out by Register; the next is one more.</summary>
    private int _lastConnectionId = (int)SecureConnection.FirstConnectionId - 1;

    /// <summary>
    /// Sends bytes to a client endpoint on one of the listening ports --
    /// the socket layer's, set by the plugin -- for the requests this server
    /// makes of a client: notification events.
    /// </summary>
    public Action<IPEndPoint, int, byte[]>? Sender { get; set; }

    /// <summary>
    /// How many reliable DATA sequence ids a peer remembers replies for.
    /// The client retransmits a reliable packet it has not seen ACKed within
    /// tens of milliseconds
    /// </summary>
    private const int ReplayWindow = 64;

    /// <summary>
    /// Connections are keyed by remote endpoint, local port *and* the
    /// packet's source virtual port. A client reuses one UDP source port for
    /// everything, so the listening port separates authentication from
    /// secure traffic
    /// </summary>
    private readonly ConcurrentDictionary<(IPEndPoint Remote, int LocalPort, byte SourcePort), Peer> _peers = [];

    private static byte SourcePortOf(ReadOnlySpan<byte> datagram) => datagram.Length > 0 ? datagram[0] : (byte)0;

    /// <summary>
    /// The authenticated players of one title this server currently holds
    /// </summary>
    private IEnumerable<uint> KnownPlayerPids(QuazalTitle title) =>
        _peers.Values
            .Where(peer => ReferenceEquals(peer.Title, title))
            .Select(peer => peer.AuthenticatedPid).OfType<uint>().Distinct();

    /// <summary>
    /// A tracking event's reporter, or 0 when the connection is not a
    /// player's 
    /// </summary>
    private static uint PlayerPidOrZero(Peer peer) =>
        peer.AuthenticatedPid is { } pid && pid != TicketGranting.TrackingPid ? pid : 0;

    /// <summary>Quazal's Core::NotImplemented result code.</summary>
    private const uint CoreNotImplemented = 0x80010002;

    /// <summary>Quazal's Core::AccessDenied result code (106-0107 in Nintendo's numbering).</summary>
    private const uint CoreAccessDenied = 0x80010006;

    /// <summary>
    /// Whether this call is the telemetry account, on a Playforward title,
    /// reaching past telemetry; see <see cref="QuazalSettings.IsConfinedToTelemetry"/>.
    /// </summary>
    private static bool IsConfinedToTelemetry(Peer peer, RmcRequest rmc) =>
        QuazalSettings.IsConfinedToTelemetry(peer.Title, peer.AuthenticatedPid, rmc.ServiceId);

    /// <summary>
    /// The player a connection reports for. A player's own session reports
    /// for itself. The telemetry account's session
    /// </summary>
    private uint PlayerBehind((IPEndPoint Remote, int LocalPort, byte SourcePort) key, Peer peer)
    {
        uint own = PlayerPidOrZero(peer);
        if (own != 0) return own;
        foreach (((IPEndPoint remote, int localPort, byte sourcePort), Peer sibling) in _peers)
        {
            if (sourcePort == key.SourcePort || localPort != key.LocalPort || !remote.Equals(key.Remote))
                continue;
            uint pid = PlayerPidOrZero(sibling);
            if (pid != 0) return pid;
        }
        return 0;
    }

    /// <summary>
    /// A player as an event names them.
    /// </summary>
    private uint? ResolvePlayer(QuazalTitle title, string named)
    {
        if (uint.TryParse(named, out uint pid) && KnownPlayerPids(title).Contains(pid))
            return pid;
        foreach (uint known in KnownPlayerPids(title))
        {
            if (string.Equals(settings.NameForPid(title, known), named, StringComparison.OrdinalIgnoreCase))
                return known;
        }
        return Playforward.PlayerNames.PidFor(title.TitleId, named);
    }

    /// <summary>
    /// Picks the PRUDP dialect to parse a datagram with. A known peer keeps the
    /// width latched at SYN time; an unknown one is identified from the SYN
    /// itself, since a bare SYN is a 14-byte header plus its checksum.
    /// </summary>
    public (int Width, QuazalChecksumAlgorithm Algorithm) ResolveDialect(
        IPEndPoint source, int localPort, ReadOnlySpan<byte> data)
    {
        if (_peers.TryGetValue((source, localPort, SourcePortOf(data)), out Peer? known))
            return (known.ChecksumWidth, known.ChecksumAlgorithm);

        int width = PrudpPacketV0.DetectChecksumWidth(data) ?? PrudpPacketV0.LegacyChecksumWidth;
        return (width, width == PrudpPacketV0.NexChecksumWidth
            ? settings.NexChecksumAlgorithm
            : QuazalChecksumAlgorithm.LegacyWordSum);
    }

    /// <summary>
    /// The private half of each title's RendezVous identity, keyed by the
    /// document path the title names. Per title because the REST half
    /// advertises the matching public key per title, and a secure CONNECT
    /// signed with a different key than the client was told to expect fails
    /// its validation.
    /// </summary>
    private readonly Dictionary<string, ECDsa?> _signingIdentities = [];

    public bool HasSigningIdentity => _signingIdentities.Values.Any(identity => identity is not null);

    private ECDsa? SigningIdentityFor(QuazalTitle title) =>
        _signingIdentities.GetValueOrDefault(title.Descriptor.RendezVousIdentityFile);

    public IReadOnlyList<byte[]> Handle(PrudpPacketV0 request, IPEndPoint source, int localPort,
        ReadOnlySpan<byte> datagram)
    {
        if (request.Source.StreamType == StreamType.NATEcho ||
            request.Destination.StreamType == StreamType.NATEcho)
        {
            if (TryCreateNatDetectReply(datagram, source, out byte[] detected))
                return [detected];
            return [NatEchoReply(request, source).ToArray(0)];
        }

        if (settings.BlockPrudpSessions)
        {
            Log.Debug("[quazal] reference mode: dropping PRUDP {Type} from {Source} on :{Port}",
                request.PacketType, source, localPort);
            return [];
        }

        var key = (source, localPort, SourcePortOf(datagram));
        if (!settings.EnableTransportAcks)
            return [];

        bool needsAck = request.Flags.HasFlag(PacketFlags.NeedAck);

        if (request.PacketType == PacketType.Syn)
        {
            if (!needsAck)
                return [];
            if (!_peers.ContainsKey(key) && !AdmitPeer(source))
                return [];
            if (!_titlesByPort.TryGetValue(localPort, out QuazalTitle? title))
            {
                Log.Error("[quazal] ignoring SYN from {Source} on :{Port}: no title owns that port", source, localPort);
                return [];
            }
            Peer peer = _peers.GetOrAdd(key, _ =>
            {
                Log.Information("[quazal] {Source} on :{Port} is {Title}", source, localPort, title);
                return new()
                {
                    Title = title,
                    ServerSignature = NewSignature(),
                    LastHeard = _time.GetTimestamp(),
                };
            });
            peer.ChecksumWidth = request.ChecksumWidth;
            peer.ChecksumAlgorithm = request.ChecksumAlgorithm;
            if (!peer.SynAcknowledged || peer.Connected)
            {
                peer.ServerSignature = NewSignature();
                peer.ServerSequenceId = 1;
                peer.RmcResponseCounter = 0;
                peer.AuthenticatedPid = null;
                peer.Connected = false;
                ClearSecureState(peer);
            }
            LearnChecksumContribution(request, peer);
            peer.SynAcknowledged = true;
            peer.LastHeard = _time.GetTimestamp();
            return [SynAcknowledge(request, peer).ToArray(peer.AccessKeyChecksum)];
        }

        if (!_peers.TryGetValue(key, out Peer? existing))
        {
            Log.Debug("[quazal] ignoring {Type} from {Source}: no SYN state", request.PacketType, source);
            return [];
        }

        if (settings.RequireConnectionSignature && existing.ServerSignature != 0 &&
            request.PacketType is not PacketType.Syn && request.Signature != existing.ServerSignature)
        {
            Log.Warning("[quazal] dropped {Type} seq={Seq} from {Source}: signature 0x{Got:X8} is not this session's 0x{Want:X8} " +
                "(a retransmit from before a reconnect, or a spoof of this endpoint)",
                request.PacketType, request.SequenceId, source, request.Signature, existing.ServerSignature);
            return [];
        }

        existing.LastHeard = _time.GetTimestamp();
        LearnChecksumContribution(request, existing);
        existing.Remote = source;
        existing.LocalPort = localPort;
        existing.ClientPort = request.Source;
        existing.ServerPort = request.Destination;
        existing.SessionId = request.SessionId;

        if (request.PacketType == PacketType.Connect)
        {
            existing.ClientSignature = request.ConnectionSignature;
            existing.Connected = true;
            existing.KerberosConnectResponse = null;
            if (request.Payload.Length > 0 && !IsP256EcdhBlob(request.Payload))
            {
                byte[] clear = new Rc4Stream(TicketGranting.DefaultAuthKey).Process(request.Payload);
                byte[]? reply = TicketGranting.BuildSecureConnectResponse(
                    clear, out uint ticketPid, out uint checkValue);
                if (reply is not null && settings.FindAccountByPid(existing.Title, ticketPid) is null)
                {
                    Log.Warning("[quazal] secure CONNECT rejected for unconfigured pid={Pid} from {Source}",
                        ticketPid, source);
                    reply = null;
                }
                if (reply is null)
                {
                    Log.Warning("[quazal] secure CONNECT on :{Port} from {Source} could not be opened: {Clear}",
                        localPort, source, Convert.ToHexString(clear));
                }
                else
                {
                    existing.KerberosConnectResponse =
                        new Rc4Stream(TicketGranting.DefaultAuthKey).Process(reply);
                    existing.AuthenticatedPid = ticketPid;
                    Log.Information("[quazal] secure CONNECT on :{Port} accepted for pid={Pid}, check=0x{Check:X8} -> replying 0x{Reply:X8}",
                        localPort, ticketPid, checkValue, checkValue + 1);
                }
            }
            bool isFreshEcdhKey = IsP256EcdhBlob(request.Payload) &&
                !request.Payload.SequenceEqual(existing.LastClientEcdhBlob ?? []);
            if (settings.EnableAuthenticatedSecureConnect && isFreshEcdhKey &&
                SigningIdentityFor(existing.Title) is { } signingIdentity)
            {
                ClearSecureState(existing);
                existing.RmcResponseCounter = 0;
                existing.ServerSequenceId = 1;
                existing.LastClientEcdhBlob = request.Payload.ToArray();
                (existing.SecureConnectSignature, existing.SecureConnectExtension) =
                    CreateAuthenticatedSecureConnectEnvelope(existing, signingIdentity);
                existing.SecureSessionKey = DeriveSecureSessionKey(existing.SecureAgreement!, request.Payload);
                Log.Information("[quazal] generated authenticated P-256 secure-CONNECT envelope for {Source}", source);
            }
            return needsAck ? [ConnectAcknowledge(request, existing).ToArray(existing.AccessKeyChecksum)] : [];
        }

        bool reliableData = request.PacketType == PacketType.Data && request.Flags.HasFlag(PacketFlags.Reliable);
        if (reliableData && existing.Answered.TryGetValue(request.SequenceId, out List<byte[]>? again))
        {
            Log.Information("[quazal] DATA seq={Sequence} from {Source} retransmitted; re-sending its {Count} reply packet(s)",
                request.SequenceId, source, again.Count);
            return again;
        }

        List<byte[]>? loginResponse = null;
        if (request.PacketType == PacketType.Data && existing.SecureSessionKey is not null && request.Payload.Length > 0)
        {
            byte[]? secureReply = HandleSecureLoginData(request, source, existing, settings);
            if (secureReply is not null) loginResponse = [secureReply];
        }
        else if (request.PacketType == PacketType.Data && request.Payload.Length > 0)
            loginResponse = HandleUnsecuredRmc(request, source, key, existing, settings);

        if (request.PacketType == PacketType.Disconnect)
        {
            existing.Connected = false;
            existing.SynAcknowledged = false;
            ClearSecureState(existing);
        }

        if (!needsAck)
            return loginResponse is null ? [] : loginResponse;

        byte[] ack = Acknowledge(request, existing, existing.ClientSignature == 0 ? existing.ServerSignature : existing.ClientSignature)
            .ToArray(existing.AccessKeyChecksum);

        List<byte[]> replies;
        if (loginResponse is null)
        {
            replies = [ack];
        }
        else
        {
            replies = new List<byte[]>(loginResponse.Count + 1) { ack };
            replies.AddRange(loginResponse);
        }
        if (reliableData)
            Remember(existing, request.SequenceId, replies);
        return replies;
    }

    private static void Remember(Peer peer, ushort sequence, List<byte[]> replies)
    {
        peer.Answered[sequence] = replies;
        peer.AnsweredOrder.Enqueue(sequence);
        while (peer.AnsweredOrder.Count > ReplayWindow)
            peer.Answered.Remove(peer.AnsweredOrder.Dequeue());
    }

    private static PrudpPacketV0 SynAcknowledge(PrudpPacketV0 request, Peer peer) => new()
    {
        Source = request.Destination,
        Destination = request.Source,
        PacketType = request.PacketType,
        Flags = PacketFlags.Ack,
        SessionId = request.SessionId,
        Signature = request.ConnectionSignature,
        SequenceId = request.SequenceId,
        ConnectionSignature = peer.ServerSignature,
        FragmentId = request.FragmentId,
        Payload = [],
        PayloadSize = 0,
        Checksum = 0,
        ChecksumWidth = request.ChecksumWidth,
        ChecksumAlgorithm = request.ChecksumAlgorithm,
    };

    private static PrudpPacketV0 ConnectAcknowledge(PrudpPacketV0 request, Peer peer)
    {
        byte[] payload = peer.KerberosConnectResponse ?? peer.SecureConnectSignature ?? [];

        PacketFlags flags = PacketFlags.Ack;
        if (payload.Length > 0 && request.Flags.HasFlag(PacketFlags.HasSize))
            flags |= PacketFlags.HasSize;

        return new()
        {
            Source = request.Destination,
            Destination = request.Source,
            PacketType = PacketType.Connect,
            Flags = flags,
            SessionId = request.SessionId,
            Signature = peer.ClientSignature,
            SequenceId = request.SequenceId,
            ConnectionSignature = peer.ServerSignature,
            Payload = payload,
            TrailingPayload = peer.KerberosConnectResponse is null ? peer.SecureConnectExtension ?? [] : [],
            PayloadSize = (ushort)payload.Length,
            Checksum = 0,
            ChecksumWidth = peer.ChecksumWidth,
            ChecksumAlgorithm = peer.ChecksumAlgorithm,
        };
    }

    /// <summary>
    /// Creates FC5's verified OldRVSecure CONNECT extension:
    /// ECDSA-P256(SHA-256(ECK1)) | u16(72) | ECK1 | random(32).
    /// </summary>
    private static (byte[] Signature, byte[] Extension) CreateAuthenticatedSecureConnectEnvelope(Peer peer,
                                                                                                    ECDsa signingIdentity)
    {
        peer.SecureAgreement = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        ECParameters parameters = peer.SecureAgreement.ExportParameters(includePrivateParameters: false);
        if (parameters.Q.X is not { Length: 32 } x || parameters.Q.Y is not { Length: 32 } y)
            throw new CryptographicException("P-256 public coordinates were not 32 bytes");

        byte[] blob = new byte[8 + x.Length + y.Length];
        "ECK1"u8.CopyTo(blob);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(4), 32);
        x.CopyTo(blob, 8);
        y.CopyTo(blob, 8 + x.Length);

        byte[] hash = SHA256.HashData(blob);
        byte[] signature = signingIdentity.SignHash(hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        if (signature.Length != 64)
            throw new CryptographicException($"P-256 secure-CONNECT signature has unexpected length {signature.Length}");

        byte[] extension = new byte[sizeof(ushort) + blob.Length + 32];
        BinaryPrimitives.WriteUInt16LittleEndian(extension, (ushort)blob.Length);
        blob.CopyTo(extension, sizeof(ushort));
        RandomNumberGenerator.Fill(extension.AsSpan(sizeof(ushort) + blob.Length));
        return (signature, extension);
    }

    private static bool IsP256EcdhBlob(ReadOnlySpan<byte> payload) =>
        payload.Length == 72 && payload[..4].SequenceEqual("ECK1"u8) &&
        BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]) == 32;

    /// <summary>
    /// FC5 derives a 16-byte AES key with CNG's HASH KDF after the signed
    /// OldRVSecure ECDH exchange. The client public key is its CONNECT ECK1
    /// payload; its matching private key remains inside <paramref name="agreement"/>.
    /// </summary>
    private static byte[] DeriveSecureSessionKey(ECDiffieHellman agreement, ReadOnlySpan<byte> clientBlob)
    {
        if (!IsP256EcdhBlob(clientBlob))
            throw new CryptographicException("secure CONNECT did not contain an ECK1 P-256 public key");

        ECParameters parameters = new()
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = clientBlob.Slice(8, 32).ToArray(),
                Y = clientBlob.Slice(40, 32).ToArray(),
            },
        };
        using ECDiffieHellman client = ECDiffieHellman.Create(parameters);
        byte[] sharedHash = agreement.DeriveKeyFromHash(client.PublicKey, HashAlgorithmName.SHA1);
        try
        {
            if (sharedHash.Length < 16)
                throw new CryptographicException("ECDH HASH KDF produced fewer than 16 bytes");
            return sharedHash.AsSpan(0, 16).ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedHash);
        }
    }

    /// <summary>
    /// Handles RMC on the authentication connection, which is RC4-encrypted
    /// under Quazal's fixed default key rather than a negotiated session key.
    /// The payload carries a leading compression byte before the RMC message.
    /// </summary>
    private List<byte[]>? HandleUnsecuredRmc(PrudpPacketV0 request, IPEndPoint source,
        (IPEndPoint Remote, int LocalPort, byte SourcePort) key, Peer peer, QuazalSettings settings)
    {
        byte[] clear = new Rc4Stream(TicketGranting.DefaultAuthKey).Process(request.Payload);
        if (clear.Length < 2)
            return null;

        byte compression = clear[0];
        byte[] rmcBytes;
        if (compression == 0)
        {
            rmcBytes = clear[1..];
        }
        else if (TryDecompressSecureRmc(clear, out byte[] inflated))
        {
            rmcBytes = inflated;
        }
        else
        {
            Log.Warning("[quazal] DATA seq={Sequence} from {Source} has unsupported encoding {Compression} ({Length} bytes, starting {Header})",
                request.SequenceId, source, compression, clear.Length, HeaderForLog(clear));
            return null;
        }

        if (request.FragmentId != 0)
        {
            peer.PendingFragments.AddRange(rmcBytes);
            Log.Information("[quazal] DATA seq={Sequence} from {Source}: fragment {Fragment} held ({Length} bytes, {Pending} pending)",
                request.SequenceId, source, request.FragmentId, rmcBytes.Length, peer.PendingFragments.Count);
            return null;
        }
        if (peer.PendingFragments.Count > 0)
        {
            peer.PendingFragments.AddRange(rmcBytes);
            rmcBytes = [.. peer.PendingFragments];
            peer.PendingFragments.Clear();
            Log.Information("[quazal] DATA seq={Sequence} from {Source}: last fragment; reassembled {Length} bytes",
                request.SequenceId, source, rmcBytes.Length);
        }

        if (!RmcRequest.TryParse(rmcBytes, out RmcRequest rmc))
        {
            Log.Warning("[quazal] DATA seq={Sequence} from {Source} did not decode as RMC ({Length} bytes, starting {Header})",
                request.SequenceId, source, rmcBytes.Length, HeaderForLog(rmcBytes));
            return null;
        }

        Log.Information("[quazal] RMC request from {Source}: protocol={Protocol} method={Method} callId={CallId}",
            source, rmc.ServiceId, rmc.MethodId, rmc.CallId);
        Log.Information("[quazal] RMC request framing: seq={Sequence} sid={Session} encoding={Encoding} wire={WireLength} clear={ClearLength}",
            request.SequenceId, request.SessionId,
            compression == 0 ? "plain" : $"zlib/{compression}", request.Payload.Length, rmcBytes.Length);

        if (settings.SuppressOnlineData && peer.Title.ProtocolFamily == QuazalProtocolFamily.FarCry3 &&
            rmc.ProtocolId == Fc3OnlineDataService.ProtocolId)
        {
            Log.Warning("[quazal] SuppressOnlineData: dropping protocol {Protocol} method {Method} " +
                        "callId {CallId} with no reply (diagnostic)",
                rmc.ProtocolId, rmc.MethodId, rmc.CallId);
            return null;
        }

        byte[]? result = null;
        bool handled = false;
        bool confined = IsConfinedToTelemetry(peer, rmc);
        if (!confined)
        {
            handled = TableFor(peer.Title).TryGet(rmc.ServiceId, rmc.MethodId, out RmcHandler? handler);
            if (handled)
                result = handler!(rmc, new Call(peer, source, key));
        }

        byte[] response;
        if (confined)
        {
            Log.Warning("[playforward] {Title}: the telemetry account from {Source} called protocol={Protocol} method={Method}, which is not telemetry — replying Core::AccessDenied",
                peer.Title, source, rmc.ServiceId, rmc.MethodId);
            response = TicketGranting.BuildRmcErrorResponse(
                rmc.ProtocolId, rmc.CallId, CoreAccessDenied, rmc.ExtendedProtocolId);
        }
        else if (result is null)
        {
            Log.Warning("[quazal] protocol={Protocol} method={Method} not implemented for {Title} ({Reason}); parameters={Parameters} — replying Core::NotImplemented",
                rmc.ServiceId, rmc.MethodId, peer.Title,
                handled ? "the handler declined" : "no handler registered",
                ParametersForLog(rmc));
            response = TicketGranting.BuildRmcErrorResponse(
                rmc.ProtocolId, rmc.CallId, CoreNotImplemented, rmc.ExtendedProtocolId);
        }
        else
        {
            response = TicketGranting.BuildRmcResponse(
                rmc.ProtocolId, rmc.CallId, rmc.MethodId, result, rmc.ExtendedProtocolId);
        }

        var packets = new List<byte[]>();
        var wireLengths = new List<int>();
        int fragmentCount = (response.Length + MaxFragmentPayload - 1) / MaxFragmentPayload;
        if (fragmentCount < 1) fragmentCount = 1;
        byte firstEncoding = 0;
        for (int index = 0; index < fragmentCount; index++)
        {
            int start = index * MaxFragmentPayload;
            int length = Math.Min(MaxFragmentPayload, response.Length - start);
            ReadOnlySpan<byte> chunk = response.AsSpan(start, length);

            byte[] framed;
            if (compression != 0)
            {
                framed = CompressSecureRmc(chunk);
            }
            else
            {
                framed = new byte[length + 1];
                chunk.CopyTo(framed.AsSpan(1));
            }
            if (index == 0) firstEncoding = framed[0];

            byte[] encrypted = new Rc4Stream(TicketGranting.DefaultAuthKey).Process(framed);
            PrudpPacketV0 responsePacket = SecureDataPacket(request, peer, encrypted);
            responsePacket.FragmentId =
                index == fragmentCount - 1 ? (byte)0 : (byte)(index + 1);
            packets.Add(responsePacket.ToArray(peer.AccessKeyChecksum));
            wireLengths.Add(encrypted.Length);
        }

        Log.Information("[quazal] RMC response to {Source}: protocol={Protocol} method={Method} callId={CallId} sid={Session} encoding={Encoding} body={BodyLength} rmc={RmcLength} wire={WireLength}{Fragmented}",
            source, rmc.ProtocolId, rmc.MethodId, rmc.CallId, request.SessionId,
            compression == 0 ? "plain" : $"zlib/{firstEncoding}",
            result?.Length ?? 0, response.Length, wireLengths.Sum(),
            fragmentCount > 1 ? $" in {fragmentCount} fragments" : string.Empty);

        return packets;
    }

    /// <summary>
    /// Plaintext RMC bytes per PRUDP fragment. 963 is not a round number by
    /// accident
    /// </summary>
    private const int MaxFragmentPayload = 963;

    /// <summary>
    /// Secure Connection Register: records the urls the client says it can be
    /// reached on and returns its connection id plus the address the server
    /// actually sees it coming from.
    /// </summary>
    private byte[]? BuildRegisterReply(RmcRequest rmc, IPEndPoint source, Peer peer)
    {
        IReadOnlyList<string> urls = SecureConnection.ReadStationUrls(rmc.Parameters);
        if (urls.Count == 0)
        {
            Log.Warning("[quazal] Register from {Source} listed no station urls", source);
            return null;
        }

        int streamId = SecureConnection.ReadStreamId(urls);
        string publicUrl = SecureConnection.BuildPublicUrl(source.Address.ToString(), source.Port, streamId);
        uint connectionId = peer.ConnectionId ??= (uint)Interlocked.Increment(ref _lastConnectionId);
        Log.Information("[quazal] Register from {Source}: {Count} url(s) [{Urls}] -> rvcid={Cid} public={Public}",
            source, urls.Count, string.Join(", ", urls), connectionId, publicUrl);
        return SecureConnection.BuildRegisterResult(connectionId, publicUrl);
    }

    private const int RmcHeaderBytes = 13;

    internal static string ParametersForLog(RmcRequest rmc) =>
        rmc.ServiceId == TicketGranting.ProtocolId
            ? $"<{rmc.Parameters.Length} bytes withheld: authentication>"
            : Convert.ToHexString(rmc.Parameters);

    internal static string HeaderForLog(ReadOnlySpan<byte> message) =>
        Convert.ToHexString(message[..Math.Min(RmcHeaderBytes, message.Length)]);

    /// <summary>
    /// Ticket Granting Login. The account name selects a server-configured
    /// password and PID used to seal the returned ticket. FC3's LoginEx path
    /// instead uses its built-in PlayerTicketPassword and the returned PID.
    /// </summary>
    private static byte[]? BuildLoginReply(
        RmcRequest rmc, IPEndPoint source, Peer peer, QuazalSettings settings)
    {
        string? username = rmc.ReadString();
        if (username is null)
        {
            Log.Warning("[quazal] Login from {Source} has an unreadable username parameter", source);
            return null;
        }
        bool isPlayerLogin = rmc.MethodId == TicketGranting.MethodLoginEx;
        QuazalAccount? account = isPlayerLogin
            ? settings.FindPlayerAccount(peer.Title, username, source)
            : settings.FindAccount(username);
        if (account is null)
        {
            Log.Warning("[quazal] Login for unknown user {User} from {Source}", username, source);
            if (isPlayerLogin &&
                Playforward.PlayforwardTitles.ForTitle(peer.Title)?.Gateway.BoundAddresses() is { Count: > 0 } bound &&
                !bound.Contains(source.Address.ToString()) && !bound.Contains(source.ToString()))
            {
                Log.Warning(
                    "[playforward] nothing is bound to {Source}, but these are: {Bound}. " +
                    "If one of those is this same machine, the client's game-server URL and the shim's redirect_ip disagree -- " +
                    "they must be the same address, or one machine arrives here as two peers.",
                    source.Address, string.Join(", ", bound));
            }
            return null;
        }

        if (isPlayerLogin)
        {
            int usernameLength = rmc.Parameters.Length >= 2
                ? BinaryPrimitives.ReadUInt16LittleEndian(rmc.Parameters)
                : 0;
            int authOffset = 2 + usernameLength;
            int authLength = authOffset <= rmc.Parameters.Length ? rmc.Parameters.Length - authOffset : 0;
            Log.Information("[quazal] LoginEx auth holder: total={TotalLength} offset={Offset} length={AuthLength}",
                rmc.Parameters.Length, authOffset, authLength);
        }
        uint serverPid = isPlayerLogin
            ? TicketGranting.PlayerSecureServerPid
            : TicketGranting.TrackingSecureServerPid;
        uint serverCid = isPlayerLogin
            ? TicketGranting.PlayerSecureServerCid
            : TicketGranting.TrackingSecureServerCid;
        byte[]? sourceKey = isPlayerLogin
            ? TicketGranting.DeriveKey(account.Pid, TicketGranting.PlayerTicketPassword)
            : account.ReadSourceKey();
        byte[] ticket = TicketGranting.BuildTicket(
            account.Pid, account.Password, serverPid, out _, sourceKey);
        peer.AuthenticatedPid = account.Pid;
        Log.Information("[quazal] Login OK for {User} (pid={Pid}, server_pid={ServerPid}, server_cid={ServerCid}) -> {Source}",
            username, account.Pid, serverPid, serverCid, source);
        return TicketGranting.BuildLoginResult(
            account.Pid, ticket, settings.PublicHost, peer.Title.SecureServerPort,
            serverPid, serverCid);
    }

    /// <summary>
    /// Ticket Granting RequestTicket: hands the source user a ticket for the
    /// secure server, sealed with the source account's key.
    /// </summary>
    private static byte[]? BuildRequestTicketReply(
        RmcRequest rmc, IPEndPoint source, Peer peer, QuazalSettings settings)
    {
        if (!rmc.TryReadU32(0, out uint sourcePid) || !rmc.TryReadU32(4, out uint targetPid))
        {
            Log.Warning("[quazal] RequestTicket from {Source} has unreadable pid parameters", source);
            return null;
        }
        if (targetPid != TicketGranting.TrackingSecureServerPid &&
            targetPid != TicketGranting.PlayerSecureServerPid)
        {
            Log.Warning("[quazal] RequestTicket from {Source} targeted unsupported pid {Pid}",
                source, targetPid);
            return null;
        }
        QuazalAccount? account = settings.FindAccountByPid(peer.Title, sourcePid);
        if (account is null)
        {
            Log.Warning("[quazal] RequestTicket for unknown source pid {Pid} from {Source}", sourcePid, source);
            return null;
        }
        if (peer.AuthenticatedPid != sourcePid)
        {
            Log.Warning("[quazal] RequestTicket source pid {Pid} is not authenticated on {Source}",
                sourcePid, source);
            return null;
        }

        byte[]? sourceKey = sourcePid == TicketGranting.TrackingPid
            ? account.ReadSourceKey()
            : TicketGranting.DeriveKey(sourcePid, TicketGranting.PlayerTicketPassword);
        byte[] ticket = TicketGranting.BuildTicket(
            sourcePid, account.Password, targetPid, out _, sourceKey);
        Log.Information("[quazal] RequestTicket OK: source={Source} target={Target} -> {Peer}",
            sourcePid, targetPid, source);
        return TicketGranting.BuildRequestTicketResult(ticket);
    }

    /// <summary>
    /// Secure RMC DATA is an IV-prefixed AES-CBC/PKCS7 envelope. Decrypts and
    /// logs protocol/method (never the raw ticket payload), and for
    /// LoginProtocol::LoginWithToken_V1 only builds a reply.
    ///
    /// UNVERIFIED: the reply body is a best-effort construction
    /// </summary>
    private static byte[]? HandleSecureLoginData(PrudpPacketV0 request, IPEndPoint source, Peer peer, QuazalSettings settings)
    {
        try
        {
            byte[] clear = DecryptSecureData(peer.SecureSessionKey!, request.Payload);
            try
            {
                if (!TryDecompressSecureRmc(clear, out byte[] rmc))
                {
                    Log.Warning("[quazal] secure DATA seq={Sequence} from {Source} decrypted ({Length} bytes) but is not the expected compressed RMC envelope",
                        request.SequenceId, source, clear.Length);
                    return null;
                }

                if (!NamedRmcMessage.TryParse(rmc, out NamedRmcMessage message))
                {
                    int headerOnlyLength = Math.Min(70, rmc.Length);
                    Log.Warning("[quazal] secure DATA seq={Sequence} from {Source} decrypted ({Length} bytes) but is not a well-formed named-RMC message; header={Header}",
                        request.SequenceId, source, rmc.Length, Convert.ToHexString(rmc.AsSpan(0, headerOnlyLength)));
                    return null;
                }
                Log.Information("[quazal] decrypted secure RMC from {Source}: protocol={Protocol} method={Method} callId={CallId} rmcBytes={Length}",
                    source, message.Protocol, message.Method, message.CallId, rmc.Length);

                byte[]? result = message.Protocol switch
                {
                    "LoginProtocol" when message.Method.Contains("LoginWithToken", StringComparison.Ordinal) =>
                        BuildLoginResult(),
                    "LoginProtocol" when message.Method.Contains("Register", StringComparison.Ordinal) =>
                        BuildRegisterResult(),
                    _ => null,
                };
                if (result is null)
                    return null;

                byte[] response = message.BuildResponse(result, ++peer.RmcResponseCounter, settings.PublicHost, peer.Title.Ports[0]);
                byte[] compressed = CompressSecureRmc(response);
                byte[] secureResponse = EncryptSecureData(peer.SecureSessionKey!, compressed);
                Log.Information("[quazal] sending {Method} response to {Source} ({Length} plaintext bytes)",
                    message.Method, source, response.Length);
                return SecureDataPacket(request, peer, secureResponse).ToArray(peer.AccessKeyChecksum);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }
        catch (CryptographicException ex)
        {
            Log.Warning("[quazal] failed to decrypt secure DATA seq={Sequence} from {Source}: {Message}",
                request.SequenceId, source, ex.Message);
            return null;
        }
        catch (InvalidDataException ex)
        {
            Log.Warning("[quazal] failed to inflate secure DATA seq={Sequence} from {Source}: {Message}",
                request.SequenceId, source, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// UNVERIFIED: a real LoginWithToken_V1 result carries a 16-byte value
    /// with a trailing 0x0.
    /// </summary>
    private static byte[] BuildLoginResult()
    {
        byte[] result = new byte[17];
        RandomNumberGenerator.Fill(result.AsSpan(0, 16));
        result[16] = 0x00;
        return result;
    }

    /// <summary>
    /// UNVERIFIED: a real Register_V1 result is a 4-byte value (an ID? an RVCID?). 
    /// Same reasoning as <see cref="BuildLoginResult"/>
    /// </summary>
    private static byte[] BuildRegisterResult()
    {
        byte[] result = new byte[4];
        RandomNumberGenerator.Fill(result);
        return result;
    }

    /// <summary>
    /// Parses and rebuilds named-RMC envelope 
    /// </summary>
    private readonly struct NamedRmcMessage(string protocol, string method, uint callId)
    {
        public string Protocol { get; } = protocol;
        public string Method { get; } = method;
        public uint CallId { get; } = callId;

        /// <summary>
        /// Parses an incoming REQUEST (the 5-byte flag+callId shape, not the response's 6-byte shape).
        /// </summary>
        public static bool TryParse(ReadOnlySpan<byte> rmc, out NamedRmcMessage message)
        {
            message = default;
            int offset = 4;
            if (rmc.Length < offset + 2) return false;
            ushort protocolLength = BinaryPrimitives.ReadUInt16LittleEndian(rmc[offset..]);
            offset += 2;
            if (rmc.Length < offset + protocolLength + 1 + 4 + 2) return false;
            string protocol = NulTerminatedAscii(rmc.Slice(offset, protocolLength));
            offset += protocolLength;
            offset += 1; // request flag byte (observed value 1)
            uint callId = BinaryPrimitives.ReadUInt32LittleEndian(rmc[offset..]);
            offset += 4;
            ushort methodLength = BinaryPrimitives.ReadUInt16LittleEndian(rmc[offset..]);
            offset += 2;
            if (rmc.Length < offset + methodLength) return false;
            string method = NulTerminatedAscii(rmc.Slice(offset, methodLength)).TrimEnd('*');
            message = new NamedRmcMessage(protocol, method, callId);
            return true;
        }

        /// <summary>
        /// Builds a reply envelope carrying <paramref name="result"/>, addressed back to our own Quazal listener.
        /// </summary>
        public byte[] BuildResponse(ReadOnlySpan<byte> result, ushort responseOrdinal, string publicHost, int port)
        {
            byte[] protocolBytes = NulTerminatedBytes(Protocol);
            byte[] methodBytes = NulTerminatedBytes(Method + "*");
            byte[] urlBytes = NulTerminatedBytes($"prudp:/address={publicHost};port={port};type=2");

            int bodyLength = 2 + protocolBytes.Length + 1 + 1 + 4 + 2 + methodBytes.Length
                           + result.Length + 2 + urlBytes.Length;
            byte[] response = new byte[sizeof(uint) + bodyLength + sizeof(ushort)];
            int offset = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(offset), (uint)bodyLength); offset += 4;
            BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(offset), (ushort)protocolBytes.Length); offset += 2;
            protocolBytes.CopyTo(response.AsSpan(offset)); offset += protocolBytes.Length;
            response[offset++] = 0x00; // isResponse
            response[offset++] = 0x01; // version
            BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(offset), CallId); offset += 4;
            BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(offset), (ushort)methodBytes.Length); offset += 2;
            methodBytes.CopyTo(response.AsSpan(offset)); offset += methodBytes.Length;
            result.CopyTo(response.AsSpan(offset)); offset += result.Length;
            BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(offset), (ushort)urlBytes.Length); offset += 2;
            urlBytes.CopyTo(response.AsSpan(offset)); offset += urlBytes.Length;
            BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(offset), responseOrdinal);
            return response;
        }

        private static string NulTerminatedAscii(ReadOnlySpan<byte> withNul) =>
            Encoding.ASCII.GetString(withNul[..^1]);

        private static byte[] NulTerminatedBytes(string value)
        {
            byte[] bytes = new byte[value.Length + 1];
            Encoding.ASCII.GetBytes(value, bytes);
            return bytes;
        }
    }

    private static byte[] DecryptSecureData(ReadOnlySpan<byte> key, ReadOnlySpan<byte> payload)
    {
        if (payload.Length <= 16 || (payload.Length - 16) % 16 != 0)
            throw new CryptographicException($"invalid secure DATA length {payload.Length}");

        using Aes aes = Aes.Create();
        aes.Key = key.ToArray();
        aes.IV = payload[..16].ToArray();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using ICryptoTransform decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(payload[16..].ToArray(), 0, payload.Length - 16);
    }

    private static bool TryDecompressSecureRmc(ReadOnlySpan<byte> clear, out byte[] rmc)
    {
        rmc = [];
        if (clear.Length < 3 || clear[0] == 0)
            return false;

        using MemoryStream compressed = new(clear[1..].ToArray(), writable: false);
        using ZLibStream zlib = new(compressed, CompressionMode.Decompress);
        using MemoryStream output = new();
        zlib.CopyTo(output);
        rmc = output.ToArray();
        return rmc.Length > 0;
    }

    /// <summary>
    /// Mirrors <see cref="DecryptSecureData"/> for the reply direction: a
    /// fresh random 16-byte IV followed by AES-CBC/PKCS7 ciphertext.
    /// </summary>
    private static byte[] EncryptSecureData(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext)
    {
        Span<byte> iv = stackalloc byte[16];
        RandomNumberGenerator.Fill(iv);

        using Aes aes = Aes.Create();
        aes.Key = key.ToArray();
        aes.IV = iv.ToArray();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using ICryptoTransform encryptor = aes.CreateEncryptor();
        byte[] plaintextArray = plaintext.ToArray();
        byte[] ciphertext = encryptor.TransformFinalBlock(plaintextArray, 0, plaintextArray.Length);

        byte[] output = new byte[16 + ciphertext.Length];
        iv.CopyTo(output);
        ciphertext.CopyTo(output, 16);
        return output;
    }

    /// <summary>
    /// Mirrors <see cref="TryDecompressSecureRmc"/>: a compression-ratio byte
    /// followed by a zlib stream. The marker is
    /// ceil(uncompressed/compressed), capped to one byte.
    /// </summary>
    private static byte[] CompressSecureRmc(ReadOnlySpan<byte> rmc)
    {
        using MemoryStream compressedOutput = new();
        using (ZLibStream zlib = new(compressedOutput, CompressionMode.Compress, leaveOpen: true))
            zlib.Write(rmc);

        byte[] compressed = compressedOutput.ToArray();
        int ratio = compressed.Length == 0
            ? 1
            : (rmc.Length + compressed.Length - 1) / compressed.Length;

        using MemoryStream output = new(compressed.Length + 1);
        output.WriteByte((byte)Math.Clamp(ratio, 1, byte.MaxValue));
        output.Write(compressed);
        return output.ToArray();
    }

    /// <summary>
    /// Frames a reliable server-initiated secure DATA packet.
    /// </summary>
    public bool NotifyPlayer(uint pid, byte protocolId, uint methodId, byte[] parameters)
    {
        if (pid == 0 || pid == TicketGranting.TrackingPid) return false;
        return NotifyPeer(candidate => candidate.AuthenticatedPid == pid, $"pid={pid}", protocolId, methodId, parameters);
    }

    /// <summary>
    /// Sends an RMC request to the player a station url names: by pid when
    /// the url carries one, else by the RVCID Register handed that session,
    /// else to the player session at that address.
    /// </summary>
    public bool NotifyTarget(uint pid, uint rvcid, IPAddress? address, byte protocolId, uint methodId, byte[] parameters)
    {
        if (pid != 0)
            return NotifyPlayer(pid, protocolId, methodId, parameters);
        if (rvcid != 0)
            return NotifyPeer(candidate => candidate.ConnectionId == rvcid, $"rvcid={rvcid}", protocolId, methodId, parameters);
        if (address is null)
            return false;
        return NotifyPeer(
            candidate => candidate.AuthenticatedPid is { } candidatePid && candidatePid != TicketGranting.TrackingPid &&
                         candidate.Remote is not null && candidate.Remote.Address.Equals(address),
            $"address={address}", protocolId, methodId, parameters);
    }

    private bool NotifyPeer(Func<Peer, bool> match, string who, byte protocolId, uint methodId, byte[] parameters)
    {
        if (Sender is null) return false;
        Peer? peer = _peers.Values.FirstOrDefault(candidate =>
            match(candidate) && candidate.Connected && candidate.Remote is not null &&
            candidate.ClientPort.StreamType == StreamType.OldRVSecure);
        if (peer is null)
        {
            Log.Information("[quazal] notification {Protocol}/{Method} for {Who} not sent: no connected session", protocolId, methodId, who);
            return false;
        }
        uint pid = peer.AuthenticatedPid ?? 0;

        uint callId = peer.ServerCallId++;
        using var rmc = new MemoryStream();
        TicketGranting.WriteU32(rmc, (uint)(9 + parameters.Length));
        rmc.WriteByte((byte)(protocolId | 0x80));
        TicketGranting.WriteU32(rmc, callId);
        TicketGranting.WriteU32(rmc, methodId);
        rmc.Write(parameters);
        byte[] framed = new byte[rmc.Length + 1];
        rmc.ToArray().CopyTo(framed, 1);
        byte[] encrypted = new Rc4Stream(TicketGranting.DefaultAuthKey).Process(framed);
        var packet = new PrudpPacketV0
        {
            Source = peer.ServerPort,
            Destination = peer.ClientPort,
            PacketType = PacketType.Data,
            Flags = PacketFlags.Reliable | PacketFlags.NeedAck,
            SessionId = peer.SessionId,
            Signature = peer.ClientSignature,
            SequenceId = peer.ServerSequenceId++,
            FragmentId = 0,
            Payload = encrypted,
            PayloadSize = (ushort)encrypted.Length,
            Checksum = 0,
            ChecksumWidth = peer.ChecksumWidth,
            ChecksumAlgorithm = peer.ChecksumAlgorithm,
        };
        Sender(peer.Remote!, peer.LocalPort, packet.ToArray(peer.AccessKeyChecksum));
        Log.Information("[quazal] notification {Protocol}/{Method} callId={CallId} -> pid={Pid} at {Remote} ({Length} parameter bytes)",
            protocolId, methodId, callId, pid, peer.Remote, parameters.Length);
        return true;
    }

    private static PrudpPacketV0 SecureDataPacket(PrudpPacketV0 request, Peer peer, byte[] encryptedPayload) => new()
    {
        Source = request.Destination,
        Destination = request.Source,
        PacketType = PacketType.Data,
        Flags = PacketFlags.Reliable | PacketFlags.NeedAck,
        SessionId = request.SessionId,
        Signature = peer.ClientSignature,
        SequenceId = peer.ServerSequenceId++,
        FragmentId = 0,
        Payload = encryptedPayload,
        PayloadSize = (ushort)encryptedPayload.Length,
        Checksum = 0,
        ChecksumWidth = peer.ChecksumWidth,
        ChecksumAlgorithm = peer.ChecksumAlgorithm,
    };

    private static void ClearSecureState(Peer peer)
    {
        peer.PendingFragments.Clear();
        peer.Answered.Clear();
        peer.AnsweredOrder.Clear();
        peer.SecureAgreement?.Dispose();
        peer.SecureAgreement = null;
        peer.SecureConnectSignature = null;
        peer.SecureConnectExtension = null;
        if (peer.SecureSessionKey is not null)
        {
            CryptographicOperations.ZeroMemory(peer.SecureSessionKey);
            peer.SecureSessionKey = null;
        }
    }

    /// <summary>
    /// Acknowledges a client packet.
    /// </summary>
    private static PrudpPacketV0 Acknowledge(PrudpPacketV0 request, Peer peer, uint remoteSignature) => new()
    {
        Source = request.Destination,
        Destination = request.Source,
        PacketType = request.PacketType,
        Flags = PacketFlags.Ack,
        SessionId = request.SessionId,
        Signature = remoteSignature,
        SequenceId = request.SequenceId,
        ConnectionSignature = 0,
        FragmentId = request.FragmentId,
        Payload = [],
        PayloadSize = 0,
        Checksum = 0,
        ChecksumWidth = peer.ChecksumWidth,
        ChecksumAlgorithm = peer.ChecksumAlgorithm,
    };

    /// <summary>
    /// Recovers the peer's access-key contribution: reserialise its own packet
    /// with a zero key and take the difference against the checksum it sent.
    /// </summary>
    private static void LearnChecksumContribution(PrudpPacketV0 request, Peer peer)
    {
        byte[] zeroKey = request.ToArray();
        if (request.ChecksumWidth == PrudpPacketV0.NexChecksumWidth)
        {
            peer.AccessKeyChecksum = unchecked((byte)(request.Checksum - zeroKey[^1]));
            return;
        }

        uint zeroKeyChecksum = BinaryPrimitives.ReadUInt32LittleEndian(
            zeroKey.AsSpan(zeroKey.Length - sizeof(uint)));
        uint difference = unchecked(request.Checksum - zeroKeyChecksum);
        if (difference <= byte.MaxValue)
            peer.AccessKeyChecksum = (byte)difference;
        else
            Log.Debug("[quazal] checksum contribution from peer is not legacy-byte-shaped: 0x{Difference:X8}", difference);
    }

    private static uint NewSignature()
    {
        uint value;
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        do
        {
            RandomNumberGenerator.Fill(bytes);
            value = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        }
        while (value == 0);
        return value;
    }

    public void Dispose()
    {
        StopSweeping();
        foreach (ECDsa? identity in _signingIdentities.Values)
            identity?.Dispose();
        _signingIdentities.Clear();
        foreach (Peer peer in _peers.Values)
        {
            ClearSecureState(peer);
        }
        _peers.Clear();
    }

    /// <summary>
    /// Builds the reply to a stateless NAT-echo probe: the same packet with the
    /// stream endpoints swapped, carrying the observed source address so the
    /// client can classify its NAT. The probe payload is echoed verbatim; the
    /// observed endpoint is logged so the probe body can be decoded.
    /// </summary>
    internal static bool TryCreateNatDetectReply(ReadOnlySpan<byte> datagram, IPEndPoint source,
        out byte[] reply)
    {
        const int TokenOffset = 10;

        int tokenLength = datagram.Length - TokenOffset - 1;
        if (tokenLength < 1 ||
            (datagram[0] >> 4) != (byte)StreamType.NATEcho ||
            (datagram[1] >> 4) != (byte)StreamType.NATEcho ||
            (datagram[2] & 0x07) != (byte)PacketType.User)
        {
            reply = [];
            return false;
        }

        string url = $"udp:/address={source.Address.MapToIPv4()};port={source.Port}";
        byte[] ascii = Encoding.ASCII.GetBytes(url);
        reply = new byte[tokenLength + ascii.Length + 1];
        datagram.Slice(TokenOffset, tokenLength).CopyTo(reply);
        ascii.CopyTo(reply, tokenLength);
        reply[^1] = 0x00;

        if (ShouldLogNatDetect(source))
            Log.Information("[quazal] NAT-detect probe from {Source}: answering {Url} ({Length} bytes)",
                source, url, reply.Length);
        return true;
    }

    /// <summary>Last time each endpoint's NAT-detect reply was logged.</summary>
    private static readonly ConcurrentDictionary<string, DateTime> _natDetectLogged = new();

    /// <summary>How often one endpoint's NAT-detect replies are logged.</summary>
    private static readonly TimeSpan NatDetectLogInterval = TimeSpan.FromSeconds(30);

    /// <summary>When <see cref="_natDetectLogged"/> was last cleared of stale endpoints.</summary>
    private static DateTime _natDetectPruned = DateTime.MinValue;

    /// <summary>
    /// The most endpoints whose NAT-detect logging is tracked at once. 
    /// </summary>
    private const int NatDetectLogCapacity = 1024;

    private static bool ShouldLogNatDetect(IPEndPoint source)
    {
        DateTime now = DateTime.UtcNow;
        string key = source.ToString();
        if (_natDetectLogged.TryGetValue(key, out DateTime last) && now - last < NatDetectLogInterval)
            return false;
        PruneNatDetectLog(now);
        if (_natDetectLogged.Count >= NatDetectLogCapacity && !_natDetectLogged.ContainsKey(key))
            return false;
        _natDetectLogged[key] = now;
        return true;
    }

    private static void PruneNatDetectLog(DateTime now)
    {
        if (now - _natDetectPruned <= NatDetectLogInterval)
            return;
        _natDetectPruned = now;
        foreach ((string endpoint, DateTime logged) in _natDetectLogged)
        {
            if (now - logged >= NatDetectLogInterval)
                _natDetectLogged.TryRemove(new KeyValuePair<string, DateTime>(endpoint, logged));
        }
    }

    private static PrudpPacketV0 NatEchoReply(PrudpPacketV0 request, IPEndPoint source)
    {
        Log.Information("[quazal] NAT-echo probe from {Source} ({Length} bytes): {Payload}",
            source, request.Payload.Length, Convert.ToHexString(request.Payload));
        byte[] observed = new byte[6];
        source.Address.MapToIPv4().TryWriteBytes(observed, out _);
        BinaryPrimitives.WriteUInt16LittleEndian(observed.AsSpan(4), (ushort)source.Port);
        Log.Debug("[quazal] NAT-echo observed endpoint {Observed}", Convert.ToHexString(observed));

        byte[] payload = request.Payload;
        return new PrudpPacketV0
        {
            Source = request.Destination,
            Destination = request.Source,
            PacketType = request.PacketType,
            Flags = PacketFlags.None,
            SessionId = request.SessionId,
            Signature = request.Signature,
            SequenceId = request.SequenceId,
            Payload = payload,
            ChecksumWidth = request.ChecksumWidth,
            ChecksumAlgorithm = request.ChecksumAlgorithm,
        };
    }

}
