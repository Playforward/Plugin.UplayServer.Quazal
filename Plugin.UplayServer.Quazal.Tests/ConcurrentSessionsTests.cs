using System.Net;
using Plugin.UplayServer.Quazal.Enums;
using Plugin.UplayServer.Quazal.Serializables;

namespace Plugin.UplayServer.Quazal.Tests;

/// <summary>
/// Far Cry 3 runs two PRUDP sessions from one UDP endpoint on the secure
/// port: the player's (source virtual port 14) and the telemetry account's
/// (15). Each must keep its own signature and state; a SYN on one must not
/// disturb the other.
/// </summary>
public sealed class ConcurrentSessionsTests
{
    private const int LocalPort = 6571;
    private static readonly IPEndPoint Client = new(IPAddress.Parse("192.168.1.179"), 3074);

    /// <summary>The one title these sessions belong to, owning the port they arrive on.</summary>
    private static QuazalResponder NewResponder() =>
        new(new QuazalSettings(), QuazalTitle.ForTests("farcry3", 6570, LocalPort).OnItsPorts());

    private static PrudpPacketV0 Packet(byte sourcePort, PacketType type, PacketFlags flags, ushort sequence, uint signature) => new()
    {
        Source = new VirtualPort { StreamType = StreamType.OldRVSecure, Port = sourcePort },
        Destination = new VirtualPort { StreamType = StreamType.OldRVSecure, Port = 1 },
        PacketType = type,
        Flags = flags,
        SequenceId = sequence,
        Signature = signature,
        ConnectionSignature = type == PacketType.Connect ? 0x11223344u : 0,
        ChecksumWidth = PrudpPacketV0.NexChecksumWidth,
        ChecksumAlgorithm = QuazalChecksumAlgorithm.NexByteFold,
    };

    private static uint Open(QuazalResponder responder, byte sourcePort)
    {
        PrudpPacketV0 syn = Packet(sourcePort, PacketType.Syn, PacketFlags.NeedAck, 0, 0);
        var synAck = responder.Handle(syn, Client, LocalPort, syn.ToArray());
        uint signature = PrudpPacketV0.TryDeserialize(Assert.Single(synAck), out PrudpPacketV0 reply,
            PrudpPacketV0.NexChecksumWidth, QuazalChecksumAlgorithm.NexByteFold)
            ? reply.ConnectionSignature
            : throw new InvalidOperationException("no SYN-ACK");
        PrudpPacketV0 connect = Packet(sourcePort, PacketType.Connect, PacketFlags.NeedAck | PacketFlags.Reliable, 1, signature);
        responder.Handle(connect, Client, LocalPort, connect.ToArray());
        return signature;
    }

    private static bool Accepted(QuazalResponder responder, byte sourcePort, ushort sequence, uint signature)
    {
        PrudpPacketV0 ping = Packet(sourcePort, PacketType.Ping, PacketFlags.NeedAck | PacketFlags.Reliable, sequence, signature);
        return responder.Handle(ping, Client, LocalPort, ping.ToArray()).Count > 0;
    }

    /// <summary>
    /// A DATA packet carrying <paramref name="rmc"/> bytes framed the way the
    /// client frames them: a leading plain-encoding byte, RC4 under the
    /// CD&amp;ML key.
    /// </summary>
    private static PrudpPacketV0 Data(byte sourcePort, ushort sequence, uint signature, byte fragmentId, byte[] rmc)
    {
        byte[] framed = new byte[rmc.Length + 1];
        rmc.CopyTo(framed, 1);
        PrudpPacketV0 data = Packet(sourcePort, PacketType.Data, PacketFlags.NeedAck | PacketFlags.Reliable, sequence, signature);
        data.FragmentId = fragmentId;
        data.Payload = new Rc4Stream(TicketGranting.DefaultAuthKey).Process(framed);
        return data;
    }

    [Fact]
    public void AFragmentedRequestIsAnsweredOnceItsLastFragmentArrives()
    {
        var responder = NewResponder();
        uint player = Open(responder, sourcePort: 14);

        byte[] parameters = new byte[1200];
        var rmc = new List<byte>();
        rmc.AddRange(BitConverter.GetBytes((uint)(9 + parameters.Length)));
        rmc.Add(UserStorage.ProtocolId | 0x80);
        rmc.AddRange(BitConverter.GetBytes(7u));                          // callId
        rmc.AddRange(BitConverter.GetBytes(UserStorage.MethodGetOwnContents));
        rmc.AddRange(parameters);
        byte[] whole = [.. rmc];

        PrudpPacketV0 first = Data(14, 2, player, fragmentId: 1, whole[..963]);
        var held = responder.Handle(first, Client, LocalPort, first.ToArray());
        Assert.Single(held);

        PrudpPacketV0 last = Data(14, 3, player, fragmentId: 0, whole[963..]);
        var replies = responder.Handle(last, Client, LocalPort, last.ToArray());
        Assert.Equal(2, replies.Count);
        Assert.True(PrudpPacketV0.TryDeserialize(replies[1], out PrudpPacketV0 reply,
            PrudpPacketV0.NexChecksumWidth, QuazalChecksumAlgorithm.NexByteFold));
        byte[] clear = new Rc4Stream(TicketGranting.DefaultAuthKey).Process(reply.Payload);
        Assert.Equal(0, clear[0]);                                        // plain
        Assert.Equal(UserStorage.ProtocolId, clear[5]);                   // response: protocol without the request bit
        Assert.Equal(7u, BitConverter.ToUInt32(clear, 7));                // callId echoed
    }

    /// <summary>An unfragmented UserStorage.GetOwnContents request with the given call id.</summary>
    private static byte[] GetOwnContents(uint callId)
    {
        var rmc = new List<byte>();
        rmc.AddRange(BitConverter.GetBytes(9u));
        rmc.Add(UserStorage.ProtocolId | 0x80);
        rmc.AddRange(BitConverter.GetBytes(callId));
        rmc.AddRange(BitConverter.GetBytes(UserStorage.MethodGetOwnContents));
        return [.. rmc];
    }

    [Fact]
    public void ARetransmittedRequestGetsItsOriginalReplyAgainAndIsNotExecutedTwice()
    {
        var responder = NewResponder();
        uint player = Open(responder, sourcePort: 14);

        PrudpPacketV0 request = Data(14, 2, player, fragmentId: 0, GetOwnContents(7));
        var first = responder.Handle(request, Client, LocalPort, request.ToArray());
        var again = responder.Handle(request, Client, LocalPort, request.ToArray());
        Assert.Equal(2, first.Count);
        Assert.Equal(first.Select(Convert.ToHexString), again.Select(Convert.ToHexString));

        PrudpPacketV0 next = Data(14, 3, player, fragmentId: 0, GetOwnContents(8));
        var replies = responder.Handle(next, Client, LocalPort, next.ToArray());
        Assert.True(PrudpPacketV0.TryDeserialize(replies[1], out PrudpPacketV0 reply,
            PrudpPacketV0.NexChecksumWidth, QuazalChecksumAlgorithm.NexByteFold));
        Assert.True(PrudpPacketV0.TryDeserialize(first[1], out PrudpPacketV0 firstReply,
            PrudpPacketV0.NexChecksumWidth, QuazalChecksumAlgorithm.NexByteFold));
        Assert.Equal(firstReply.SequenceId + 1, reply.SequenceId);
    }

    [Fact]
    public void ARetransmittedFragmentIsNotAppendedTwice()
    {
        var responder = NewResponder();
        uint player = Open(responder, sourcePort: 14);

        byte[] parameters = new byte[1200];
        var rmc = new List<byte>();
        rmc.AddRange(BitConverter.GetBytes((uint)(9 + parameters.Length)));
        rmc.Add(UserStorage.ProtocolId | 0x80);
        rmc.AddRange(BitConverter.GetBytes(9u));
        rmc.AddRange(BitConverter.GetBytes(UserStorage.MethodGetOwnContents));
        rmc.AddRange(parameters);
        byte[] whole = [.. rmc];

        PrudpPacketV0 first = Data(14, 2, player, fragmentId: 1, whole[..963]);
        responder.Handle(first, Client, LocalPort, first.ToArray());
        responder.Handle(first, Client, LocalPort, first.ToArray());   // retransmitted
        PrudpPacketV0 last = Data(14, 3, player, fragmentId: 0, whole[963..]);
        var replies = responder.Handle(last, Client, LocalPort, last.ToArray());
        Assert.Equal(2, replies.Count);
    }

    [Fact]
    public void ATelemetryReconnectDoesNotDropThePlayersSession()
    {
        var responder = NewResponder();

        uint player = Open(responder, sourcePort: 14);
        Assert.True(Accepted(responder, 14, 2, player));

        uint telemetry = Open(responder, sourcePort: 15);
        Assert.True(Accepted(responder, 15, 2, telemetry));
        uint telemetryAgain = Open(responder, sourcePort: 15);
        Assert.NotEqual(telemetry, telemetryAgain);

        Assert.True(Accepted(responder, 14, 3, player));
        Assert.False(Accepted(responder, 14, 4, telemetryAgain));
        Assert.True(Accepted(responder, 15, 2, telemetryAgain));
    }
}
