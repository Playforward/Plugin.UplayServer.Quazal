using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Plugin.UplayServer.Quazal.Enums;
using Plugin.UplayServer.Quazal.Serializables;

namespace Plugin.UplayServer.Quazal.Tests;

/// <summary>
/// Community operators run these listeners on the open internet, so every
/// datagram is one anybody could have sent.
/// </summary>
[Collection("OnlineDataStore")]
public sealed class HostilePacketTests
{
    private static readonly QuazalTitle Title = QuazalTitle.ForTests("hostile-packets", 6620, 6621);
    private static readonly IPEndPoint Attacker = new(IPAddress.Parse("198.51.100.7"), 3074);

    private static QuazalResponder NewResponder(QuazalTitle? title = null) =>
        new(new QuazalSettings(), (title ?? Title).OnItsPorts());

    /// <summary>What the plugin's UDP callback does with a datagram, minus the Playforward launch envelope.</summary>
    private static IReadOnlyList<byte[]> Receive(QuazalResponder responder, int port, byte[] datagram, IPEndPoint? from = null)
    {
        from ??= Attacker;
        if (QuazalResponder.TryCreateNatDetectReply(datagram, from, out byte[] natReply))
            return [natReply];
        (int width, QuazalChecksumAlgorithm algorithm) = responder.ResolveDialect(from, port, datagram);
        if (!PrudpPacketV0.TryDeserialize(datagram, out PrudpPacketV0 packet, width, algorithm))
            return [];
        return responder.Handle(packet, from, port, datagram);
    }

    private static PrudpPacketV0 Packet(PacketType type, PacketFlags flags, ushort sequence, uint signature,
        byte[]? payload = null, byte fragment = 0) => new()
    {
        Source = new VirtualPort { StreamType = StreamType.RV, Port = 15 },
        Destination = new VirtualPort { StreamType = StreamType.RV, Port = 1 },
        PacketType = type,
        Flags = flags,
        SequenceId = sequence,
        Signature = signature,
        ConnectionSignature = type is PacketType.Syn or PacketType.Connect ? 0x11223344u : 0,
        FragmentId = fragment,
        Payload = payload ?? [],
        ChecksumWidth = PrudpPacketV0.NexChecksumWidth,
        ChecksumAlgorithm = QuazalChecksumAlgorithm.NexByteFold,
    };

    /// <summary>SYN, then CONNECT with <paramref name="connectPayload"/>; the session's signature and the CONNECT's replies.</summary>
    private static (uint Signature, IReadOnlyList<byte[]> ConnectReplies) Open(QuazalResponder responder, int port,
        byte[]? connectPayload = null)
    {
        IReadOnlyList<byte[]> synAck = Receive(responder, port, Packet(PacketType.Syn, PacketFlags.NeedAck, 0, 0).ToArray());
        Assert.True(PrudpPacketV0.TryDeserialize(Assert.Single(synAck), out PrudpPacketV0 reply,
            PrudpPacketV0.NexChecksumWidth, QuazalChecksumAlgorithm.NexByteFold));
        uint signature = reply.ConnectionSignature;
        PrudpPacketV0 connect = Packet(PacketType.Connect, PacketFlags.NeedAck | PacketFlags.Reliable, 1, signature, connectPayload);
        return (signature, Receive(responder, port, connect.ToArray()));
    }

    /// <summary>u32 size, protocol|0x80, callId, methodId, parameters.</summary>
    private static byte[] Rmc(byte protocol, uint method, byte[]? parameters = null)
    {
        parameters ??= [];
        byte[] message = new byte[13 + parameters.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(message, (uint)(9 + parameters.Length));
        message[4] = (byte)(protocol | 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(5), 7);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(9), method);
        parameters.CopyTo(message, 13);
        return message;
    }

    private static byte[] Zlib(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(data);
        return output.ToArray();
    }

    /// <summary>A DATA payload on the authentication connection: RC4 under the default key over encoding byte + body.</summary>
    private static byte[] AuthPayload(byte encoding, ReadOnlySpan<byte> body) =>
        new Rc4Stream(TicketGranting.DefaultAuthKey).Process([encoding, .. body]);

    private static IReadOnlyList<byte[]> SendData(QuazalResponder responder, uint signature, ushort sequence,
        byte[] payload, byte fragment = 0) =>
        Receive(responder, Title.Ports[0],
            Packet(PacketType.Data, PacketFlags.NeedAck | PacketFlags.Reliable, sequence, signature, payload, fragment).ToArray());

    /// <summary>A method no title implements: answered Core::NotImplemented, so the reply proves the request was decoded.</summary>
    private static byte[] Unimplemented() => Rmc(0x55, 99);

    [Fact]
    public void Random_datagrams_from_a_stranger_never_throw_and_never_amplify()
    {
        using QuazalResponder responder = NewResponder();
        var random = new Random(20261017);
        byte[] syn = Packet(PacketType.Syn, PacketFlags.NeedAck, 0, 0).ToArray();
        byte[] data = Packet(PacketType.Data, PacketFlags.NeedAck | PacketFlags.Reliable, 2, 0x1234, [1, 2, 3, 4]).ToArray();

        for (int n = 0; n < 20_000; n++)
        {
            byte[] datagram;
            switch (n % 4)
            {
                case 0:
                    datagram = new byte[random.Next(0, 1500)];
                    random.NextBytes(datagram);
                    break;
                case 1:
                    datagram = new byte[random.Next(10, 64)];
                    random.NextBytes(datagram);
                    datagram[2] = (byte)random.Next(256);
                    break;
                default:
                    byte[] seed = n % 4 == 2 ? syn : data;
                    datagram = seed.AsSpan(0, random.Next(0, seed.Length + 1)).ToArray();
                    if (datagram.Length > 0)
                        datagram[random.Next(datagram.Length)] ^= (byte)(1 << random.Next(8));
                    break;
            }

            var from = new IPEndPoint(IPAddress.Parse($"198.51.100.{n % 200 + 1}"), 3074 + n % 7);
            foreach (int port in Title.Ports)
            {
                IReadOnlyList<byte[]> replies = Receive(responder, port, datagram, from);
                Assert.True(replies.Count <= 1, $"{replies.Count} replies to {Convert.ToHexString(datagram)}");
                Assert.True(replies.Sum(r => r.Length) <= datagram.Length + 48,
                    $"{replies.Sum(r => r.Length)}-byte reply to a {datagram.Length}-byte datagram");
            }
        }
    }

    [Fact]
    public void Every_truncation_of_a_valid_packet_is_dropped_or_handled()
    {
        using QuazalResponder responder = NewResponder();
        (uint signature, _) = Open(responder, Title.Ports[0]);
        byte[][] valid =
        [
            Packet(PacketType.Syn, PacketFlags.NeedAck, 0, 0).ToArray(),
            Packet(PacketType.Connect, PacketFlags.NeedAck | PacketFlags.HasSize, 1, signature, [9, 9, 9]).ToArray(),
            Packet(PacketType.Data, PacketFlags.NeedAck | PacketFlags.HasSize, 3, signature, AuthPayload(0, Unimplemented())).ToArray(),
            Packet(PacketType.Ping, PacketFlags.NeedAck, 4, signature).ToArray(),
        ];
        foreach (byte[] packet in valid)
        {
            for (int length = 0; length <= packet.Length; length++)
                Receive(responder, Title.Ports[0], packet[..length]);
            byte[] lying = [.. packet];
            if (((PacketFlags)(lying[2] >> 3)).HasFlag(PacketFlags.HasSize))
            {
                int sizeAt = (PacketType)(lying[2] & 7) == PacketType.Data ? 11 : 14;
                BinaryPrimitives.WriteUInt16LittleEndian(lying.AsSpan(sizeAt), ushort.MaxValue);
                Assert.False(PrudpPacketV0.TryDeserialize(lying, out _, PrudpPacketV0.NexChecksumWidth,
                    QuazalChecksumAlgorithm.NexByteFold));
            }
        }
    }

    [Fact]
    public void Random_rmc_on_an_open_session_never_throws()
    {
        using QuazalResponder responder = NewResponder();
        (uint signature, _) = Open(responder, Title.Ports[0]);
        var random = new Random(4242);
        byte[] protocols = [10, 11, 13, 14, 15, 24, 29, 35, 36, 42, 49, 50, 51, 53, 58, 101, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 0x7F];

        for (int n = 0; n < 5_000; n++)
        {
            byte[] parameters = new byte[random.Next(0, 160)];
            random.NextBytes(parameters);
            if (parameters.Length >= 4 && random.Next(2) == 0)
                BinaryPrimitives.WriteUInt32LittleEndian(parameters, (uint)random.Next(0, 70));
            byte[] rmc = Rmc(protocols[random.Next(protocols.Length)], (uint)random.Next(0, 40), parameters);
            if (random.Next(10) == 0)
                BinaryPrimitives.WriteUInt32LittleEndian(rmc, (uint)random.Next());   // a lying RMC size
            byte[] payload = random.Next(4) == 0 ? AuthPayload(1, Zlib(rmc)) : AuthPayload(0, rmc);
            SendData(responder, signature, (ushort)(10 + n), payload);
        }
    }

    [Fact]
    public void A_zlib_bomb_is_dropped_without_inflating_it()
    {
        using QuazalResponder responder = NewResponder();
        (uint signature, _) = Open(responder, Title.Ports[0]);
        byte[] bomb = Zlib(new byte[QuazalResponder.MaxRmcMessageBytes * 16]);
        Assert.True(bomb.Length < 32_768);
        byte[] payload = AuthPayload(1, bomb);

        long before = GC.GetAllocatedBytesForCurrentThread();
        IReadOnlyList<byte[]> replies = SendData(responder, signature, 2, payload);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Single(replies);   // the transport ACK, and no RMC answer
        Assert.True(allocated < 3L * QuazalResponder.MaxRmcMessageBytes,
            $"inflating a {bomb.Length}-byte datagram allocated {allocated} bytes");
        Assert.Equal(2, SendData(responder, signature, 3, AuthPayload(1, Zlib(Unimplemented()))).Count);
    }

    [Fact]
    public void A_corrupt_zlib_stream_is_dropped()
    {
        using QuazalResponder responder = NewResponder();
        (uint signature, _) = Open(responder, Title.Ports[0]);
        byte[] corrupt = [.. Zlib(Unimplemented())];
        corrupt[2] ^= 0xFF;
        corrupt[^5] ^= 0xFF;

        Assert.Single(SendData(responder, signature, 2, AuthPayload(1, corrupt)));
        Assert.Single(SendData(responder, signature, 3, AuthPayload(1, [0x78, 0x9C, 0xFF, 0xFF, 0xFF])));
    }

    [Fact]
    public void Fragments_are_capped_and_a_dropped_request_does_not_poison_the_next()
    {
        using QuazalResponder responder = NewResponder();
        (uint signature, _) = Open(responder, Title.Ports[0]);
        byte[] bigFragment = AuthPayload(1, Zlib(new byte[QuazalResponder.MaxRmcMessageBytes * 3 / 5]));

        Assert.Single(SendData(responder, signature, 2, bigFragment, fragment: 1));
        Assert.Single(SendData(responder, signature, 3, bigFragment, fragment: 2));   // past the cap: dropped

        Assert.Equal(2, SendData(responder, signature, 4, AuthPayload(0, Unimplemented())).Count);
    }

    [Fact]
    public void Fragments_under_the_cap_still_reassemble()
    {
        using QuazalResponder responder = NewResponder();
        (uint signature, _) = Open(responder, Title.Ports[0]);
        byte[] rmc = Unimplemented();

        Assert.Single(SendData(responder, signature, 2, AuthPayload(0, rmc[..6]), fragment: 1));
        Assert.Equal(2, SendData(responder, signature, 3, AuthPayload(0, rmc[6..])).Count);
    }

    [Fact]
    public void Nat_detect_replies_stay_within_a_probe_and_reject_short_datagrams()
    {
        byte[] probe = new byte[13];
        probe[0] = (byte)StreamType.NATEcho << 4;
        probe[1] = (byte)StreamType.NATEcho << 4;
        probe[2] = (byte)PacketType.User;

        for (int length = 0; length < 12; length++)
            Assert.False(QuazalResponder.TryCreateNatDetectReply(probe.AsSpan(0, Math.Min(length, probe.Length)), Attacker, out _));

        var widest = new IPEndPoint(IPAddress.Parse("255.255.255.255"), 65535);
        foreach (int length in new[] { 12, 13, 64, 1400 })
        {
            byte[] datagram = new byte[length];
            probe.AsSpan(0, 3).CopyTo(datagram);
            Assert.True(QuazalResponder.TryCreateNatDetectReply(datagram, widest, out byte[] reply));
            Assert.True(reply.Length <= length + 48, $"{reply.Length}-byte reply to a {length}-byte probe");
        }
    }

    [Fact]
    public void A_nat_echo_is_answered_no_larger_than_it_arrived()
    {
        using QuazalResponder responder = NewResponder();
        var echo = new PrudpPacketV0
        {
            Source = new VirtualPort { StreamType = StreamType.NATEcho, Port = 1 },
            Destination = new VirtualPort { StreamType = StreamType.NATEcho, Port = 1 },
            PacketType = PacketType.Ping,
            Flags = PacketFlags.HasSize | PacketFlags.NeedAck,
            Payload = new byte[1200],
            ChecksumWidth = PrudpPacketV0.LegacyChecksumWidth,
        };
        byte[] datagram = echo.ToArray();

        byte[] reply = Assert.Single(Receive(responder, Title.Ports[0], datagram));
        Assert.True(reply.Length <= datagram.Length);
    }

    [Theory]
    [InlineData(0xFFFFFFFFu)]
    [InlineData(0xFFFFFFFCu)]
    [InlineData(0xFFFFFFF5u)]
    [InlineData(0x7FFFFFFFu)]
    [InlineData(0x80000000u)]
    [InlineData(0u)]
    [InlineData(8u)]
    public void Rmc_sizes_that_overflow_or_undershoot_do_not_parse(uint size)
    {
        foreach (byte protocol in new byte[] { 0x8A, 0xFF })
        {
            byte[] message = new byte[32];
            BinaryPrimitives.WriteUInt32LittleEndian(message, size);
            message[4] = protocol;
            Assert.False(RmcRequest.TryParse(message, out _));
        }
    }

    [Fact]
    public void Derived_keys_are_remembered_but_handed_out_as_copies()
    {
        byte[] first = TicketGranting.DeriveKey(TicketGranting.TrackingPid, TicketGranting.TrackingPassword);
        first[0] ^= 0xFF;
        byte[] second = TicketGranting.DeriveKey(TicketGranting.TrackingPid, TicketGranting.TrackingPassword);

        Assert.NotEqual(first, second);
        first[0] ^= 0xFF;
        Assert.Equal(first, second);
    }

    /// <summary>A title whose secure CONNECT is FC5's signed P-256 exchange, with a throwaway identity.</summary>
    private sealed class EcdhServer : IDisposable
    {
        public static readonly int Port = 6630;
        private readonly string _identityFile = Path.Combine(Path.GetTempPath(), $"hostile-identity-{Guid.NewGuid():N}.json");
        public QuazalResponder Responder { get; }

        public EcdhServer()
        {
            using ECDsa identity = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            File.WriteAllText(_identityFile, JsonSerializer.Serialize(new
            {
                EcdsaP256PrivateKeyPkcs8 = Convert.ToBase64String(identity.ExportPkcs8PrivateKey()),
            }));
            var title = new QuazalTitle
            {
                Descriptor = new ServerShared.Titles.TitleDescriptor
                {
                    TitleId = "hostile-ecdh",
                    Name = "hostile-ecdh",
                    RendezVousIdentityFile = _identityFile,
                    ProtocolFamily = "farcry3",
                    Matchers = new ServerShared.Titles.TitleMatchers { Ports = [Port, Port + 1] },
                },
                Settings = new QuazalTitleSettings { SecureServerPort = Port + 1 },
            };
            Responder = new QuazalResponder(new QuazalSettings(), title.OnItsPorts());
            Assert.True(Responder.HasSigningIdentity);
        }

        public void Dispose()
        {
            Responder.Dispose();
            File.Delete(_identityFile);
        }
    }

    private static byte[] Eck1(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y) =>
        [.. "ECK1"u8, 32, 0, 0, 0, .. x, .. y];

    [Fact]
    public void A_secure_connect_key_off_the_curve_is_dropped()
    {
        using var server = new EcdhServer();
        byte[] notAPoint = Eck1(Enumerable.Repeat((byte)0x01, 32).ToArray(), Enumerable.Repeat((byte)0x02, 32).ToArray());

        (_, IReadOnlyList<byte[]> replies) = Open(server.Responder, EcdhServer.Port, notAPoint);

        Assert.Empty(replies);
    }

    /// <summary>A named-RMC request: u32 size, string protocol, flag, callId, string method.</summary>
    private static byte[] NamedRmc(string? protocol, string? method)
    {
        byte[] protocolBytes = protocol is null ? [] : [.. Encoding.ASCII.GetBytes(protocol), 0];
        byte[] methodBytes = method is null ? [] : [.. Encoding.ASCII.GetBytes(method), 0];
        using var body = new MemoryStream();
        TicketGranting.WriteU16(body, (ushort)protocolBytes.Length);
        body.Write(protocolBytes);
        body.WriteByte(1);
        TicketGranting.WriteU32(body, 9);
        TicketGranting.WriteU16(body, (ushort)methodBytes.Length);
        body.Write(methodBytes);
        byte[] rmc = body.ToArray();
        return [.. BitConverter.GetBytes((uint)rmc.Length), .. rmc];
    }

    [Fact]
    public void A_named_rmc_with_an_empty_string_is_dropped()
    {
        using var server = new EcdhServer();
        using ECDiffieHellman client = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        ECParameters mine = client.ExportParameters(false);
        (uint signature, IReadOnlyList<byte[]> connectReplies) = Open(server.Responder, EcdhServer.Port, Eck1(mine.Q.X, mine.Q.Y));

        byte[] serverBlob = Assert.Single(connectReplies).AsSpan(14 + 64 + 2, 72).ToArray();
        Assert.Equal("ECK1"u8.ToArray(), serverBlob[..4]);
        using ECDiffieHellman serverKey = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = serverBlob[8..40], Y = serverBlob[40..72] },
        });
        byte[] key = client.DeriveKeyFromHash(serverKey.PublicKey, HashAlgorithmName.SHA1)[..16];

        IReadOnlyList<byte[]> Send(ushort sequence, byte[] rmc)
        {
            using Aes aes = Aes.Create();
            aes.Key = key;
            aes.GenerateIV();
            byte[] compressed = [1, .. Zlib(rmc)];
            byte[] payload = [.. aes.IV, .. aes.EncryptCbc(compressed, aes.IV)];
            return Receive(server.Responder, EcdhServer.Port,
                Packet(PacketType.Data, PacketFlags.NeedAck | PacketFlags.Reliable, sequence, signature, payload).ToArray());
        }

        Assert.Equal(2, Send(2, NamedRmc("LoginProtocol", "LoginWithToken_V1")).Count);
        Assert.Single(Send(3, NamedRmc(null, "LoginWithToken_V1")));
        Assert.Single(Send(4, NamedRmc("LoginProtocol", null)));
    }
}
