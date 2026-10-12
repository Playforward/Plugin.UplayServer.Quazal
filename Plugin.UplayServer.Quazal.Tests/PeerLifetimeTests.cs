using System.Net;
using Microsoft.Extensions.Time.Testing;
using Plugin.UplayServer.Quazal.Enums;
using Plugin.UplayServer.Quazal.Playforward;
using Plugin.UplayServer.Quazal.Serializables;

namespace Plugin.UplayServer.Quazal.Tests;

/// <summary>
/// PRUDP sessions used to be kept until the server restarted, in a table
/// capped at MaxPeers. So a server that stayed up filled it with players long
/// gone and then refused every new one
/// </summary>
[Collection("OnlineDataStore")]
public sealed class PeerLifetimeTests
{
    private const uint LocalPid = 2101;

    /// <summary>A LAN title, where a configured account can log in with a ticket.</summary>
    private static readonly QuazalTitle Lan = QuazalTitle.ForTests("peer-lifetime-lan", 6594, 6595);

    /// <summary>A Playforward title, for what the sweep tells its gateway.</summary>
    private static readonly QuazalTitle WithPlayforward = QuazalTitle.ForTests("peer-lifetime-pf", 6596, 6597);

    private static readonly PlayforwardTokenFactory Tokens = new();
    private static readonly PlayforwardGateway Gateway;

    private static readonly TimeSpan Handshake = TimeSpan.FromSeconds(new QuazalSettings().PeerHandshakeTimeoutSeconds);
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(new QuazalSettings().PeerIdleTimeoutSeconds);
    private static readonly TimeSpan BindingIdle = TimeSpan.FromSeconds(new PlayforwardSettings().BindingIdleTimeoutSeconds);
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    /// <summary>Shared with the gateway, which is process-wide; tests in one class run one at a time.</summary>
    private static readonly FakeTimeProvider Time = new();

    static PeerLifetimeTests()
    {
        var store = new InMemoryProgressStore();
        Gateway = PlayforwardGateway.Start(new PlayforwardSettings
        {
            Enabled = true,
            PublicKeyBase64 = Tokens.PublicKeyBase64Url,
            Issuer = PlayforwardTokenFactory.Issuer,
            BaseUrl = "http://playforward.test",
        }, WithPlayforward.TitleId, PlayforwardTokenFactory.Audience,
            WithPlayforward.Settings.Playforward.LoginNameMaxLength, store, Time)!;
        PlayforwardTitles.Register(PlayforwardTitle.ForTests(WithPlayforward, PlayforwardTokenFactory.Audience, Gateway, store));
    }

    private static IPEndPoint Stranger(int n) => new(IPAddress.Parse($"203.0.113.{n}"), 3074);

    /// <summary>A responder serving one title, driven on that title's secure port.</summary>
    private sealed class Server(QuazalTitle title, int maxPeers = 8, int unprovenPerAddress = 8) : IDisposable
    {
        private readonly QuazalResponder _responder = new(new QuazalSettings
        {
            Accounts = [new QuazalAccount { Username = "player", Password = "unused", Pid = LocalPid }],
            MaxPeers = maxPeers,
            MaxUnprovenPeersPerAddress = unprovenPerAddress,
        }, title.OnItsPorts(), Time);

        private readonly int _port = title.SecureServerPort;

        private static PrudpPacketV0 Packet(PacketType type, PacketFlags flags, ushort sequence, uint signature, byte[]? payload = null) => new()
        {
            Source = new VirtualPort { StreamType = StreamType.OldRVSecure, Port = 14 },
            Destination = new VirtualPort { StreamType = StreamType.OldRVSecure, Port = 1 },
            PacketType = type,
            Flags = flags,
            SequenceId = sequence,
            Signature = signature,
            ConnectionSignature = type == PacketType.Connect ? 0x11223344u : 0,
            Payload = payload ?? [],
            ChecksumWidth = PrudpPacketV0.NexChecksumWidth,
            ChecksumAlgorithm = QuazalChecksumAlgorithm.NexByteFold,
        };

        /// <summary>Sends a SYN; the signature its SYN-ACK carries, or null when there was none.</summary>
        public uint? Syn(IPEndPoint from)
        {
            PrudpPacketV0 syn = Packet(PacketType.Syn, PacketFlags.NeedAck, 0, 0);
            IReadOnlyList<byte[]> replies = _responder.Handle(syn, from, _port, syn.ToArray());
            if (replies.Count == 0)
                return null;
            Assert.True(PrudpPacketV0.TryDeserialize(Assert.Single(replies), out PrudpPacketV0 synAck,
                PrudpPacketV0.NexChecksumWidth, QuazalChecksumAlgorithm.NexByteFold));
            return synAck.ConnectionSignature;
        }

        /// <summary>SYN and CONNECT, logging in with a ticket if asked; the session's signature.</summary>
        public uint Open(IPEndPoint from, bool withTicket = false)
        {
            uint signature = Syn(from) ?? throw new InvalidOperationException("no SYN-ACK");
            byte[]? payload = null;
            if (withTicket)
            {
                (byte[] sessionKey, byte[] inner) = TicketGrantingTests.Issue(LocalPid);
                payload = new Rc4Stream(TicketGranting.DefaultAuthKey).Process(TicketGrantingTests.Connect(inner, sessionKey, LocalPid));
            }
            PrudpPacketV0 connect = Packet(PacketType.Connect, PacketFlags.NeedAck | PacketFlags.Reliable, 1, signature, payload);
            Assert.NotEmpty(_responder.Handle(connect, from, _port, connect.ToArray()));
            return signature;
        }

        /// <summary>Whether the server still holds this session: a PING it knows is acknowledged.</summary>
        public bool Ping(IPEndPoint from, uint signature, ushort sequence)
        {
            PrudpPacketV0 ping = Packet(PacketType.Ping, PacketFlags.NeedAck | PacketFlags.Reliable, sequence, signature);
            return _responder.Handle(ping, from, _port, ping.ToArray()).Count > 0;
        }

        public int Sweep() => _responder.Sweep();

        public void Dispose() => _responder.Dispose();
    }

    [Fact]
    public void AQuietSessionIsForgottenAndALiveOneIsNot()
    {
        using var server = new Server(Lan);
        uint talking = server.Open(Stranger(1));
        uint quiet = server.Open(Stranger(2));

        Time.Advance(Idle - OneSecond);
        Assert.True(server.Ping(Stranger(1), talking, 2));
        Time.Advance(2 * OneSecond);

        Assert.Equal(1, server.Sweep());
        Assert.True(server.Ping(Stranger(1), talking, 3));
        Assert.False(server.Ping(Stranger(2), quiet, 2));
    }

    [Fact]
    public void AHandshakeThatNeverCompletesIsForgottenSooner()
    {
        using var server = new Server(Lan);
        Assert.NotNull(server.Syn(Stranger(3)));

        Time.Advance(Handshake + OneSecond);
        Assert.Equal(1, server.Sweep());
    }

    [Fact]
    public void ASpoofedSynFloodCannotLockPlayersOut()
    {
        const int maxPeers = 4;
        using var server = new Server(Lan, maxPeers);
        IPEndPoint player = Stranger(10);
        uint session = server.Open(player, withTicket: true);

        // Far more spoofed SYNs than the table holds, all at once: nothing
        // has had time to go idle.
        for (int n = 100; n < 100 + 10 * maxPeers; n++)
            Assert.NotNull(server.Syn(Stranger(n)));

        // The logged-in player was never displaced, and somebody new still
        // gets in -- where a full table used to refuse everybody.
        Assert.True(server.Ping(player, session, 2));
        Assert.NotNull(server.Syn(Stranger(11)));
    }

    [Fact]
    public void OneAddressCannotHoldEveryUnprovenSlot()
    {
        using var server = new Server(Lan, unprovenPerAddress: 2);
        IPAddress greedy = IPAddress.Parse("198.51.100.5");

        Assert.NotNull(server.Syn(new IPEndPoint(greedy, 1001)));
        Assert.NotNull(server.Syn(new IPEndPoint(greedy, 1002)));
        Assert.Null(server.Syn(new IPEndPoint(greedy, 1003)));

        Assert.NotNull(server.Syn(Stranger(12)));
    }

    [Fact]
    public void APlayersLoginBindingLastsAsLongAsTheirGameTalks()
    {
        // The sweep is what tells Playforward a player is still here; without
        // it a binding would either never end or end in the middle of a match.
        using var server = new Server(WithPlayforward);
        IPEndPoint lanGame = new(IPAddress.Parse("192.168.1.61"), 3074);
        Assert.NotNull(Gateway.PreBind(Tokens.Mint("9d4b2e1a-0000-4000-8000-0000000000c1"), lanGame.Address));
        uint session = server.Open(lanGame);

        for (ushort sequence = 2; sequence < 6; sequence++)
        {
            Time.Advance(BindingIdle - OneSecond);
            Assert.True(server.Ping(lanGame, session, sequence));
            server.Sweep();
        }
        Assert.NotNull(Gateway.ForAddress(lanGame.Address));

        // The game stops: its session goes idle, and then its binding does.
        Time.Advance(Idle + OneSecond);
        server.Sweep();
        Time.Advance(BindingIdle + OneSecond);
        server.Sweep();
        Assert.Null(Gateway.ForAddress(lanGame.Address));
    }
}
