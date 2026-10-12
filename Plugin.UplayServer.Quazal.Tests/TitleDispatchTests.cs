using System.Buffers.Binary;
using System.Net;
using Plugin.UplayServer.Quazal.Enums;
using Plugin.UplayServer.Quazal.Serializables;

namespace Plugin.UplayServer.Quazal.Tests;

/// <summary>
/// A protocol id is never interpreted without knowing the title. The same
/// (protocol, method) bytes on Far Cry 3's port and on Blacklist's must reach
/// two different services, and a method a title has no handler for must be
/// refused as not implemented rather than answered by another game's.
/// </summary>
public sealed class TitleDispatchTests
{
    private const int Fc3Port = 6570;
    private const int BlacklistPort = 6572;
    private const int FutureSoldierPort = 6574;
    private const byte CollidingProtocol = 107;
    private const uint CollidingMethod = 1;
    private const uint CoreNotImplemented = 0x80010002;
    private static readonly IPEndPoint Client = new(IPAddress.Parse("192.168.1.179"), 3074);

    private static QuazalResponder NewResponder()
    {
        var fc3 = QuazalTitle.ForTests("farcry3", Fc3Port, 6571, QuazalProtocolFamily.FarCry3);
        var blacklist = QuazalTitle.ForTests("splinter-cell-blacklist", BlacklistPort, 6573, QuazalProtocolFamily.Overlord);
        var futureSoldier = QuazalTitle.ForTests("ghost-recon-future-soldier", FutureSoldierPort, 6575, QuazalProtocolFamily.FutureSoldier);
        var byPort = new Dictionary<int, QuazalTitle>();
        foreach (var title in new[] { fc3, blacklist, futureSoldier })
            foreach (int port in title.Ports)
                byPort[port] = title;
        return new QuazalResponder(new QuazalSettings(), byPort);
    }

    private static PrudpPacketV0 Packet(PacketType type, PacketFlags flags, ushort sequence, uint signature) => new()
    {
        Source = new VirtualPort { StreamType = StreamType.RV, Port = 15 },
        Destination = new VirtualPort { StreamType = StreamType.RV, Port = 1 },
        PacketType = type,
        Flags = flags,
        SequenceId = sequence,
        Signature = signature,
        ConnectionSignature = type == PacketType.Connect ? 0x11223344u : 0,
        ChecksumWidth = PrudpPacketV0.NexChecksumWidth,
        ChecksumAlgorithm = QuazalChecksumAlgorithm.NexByteFold,
    };

    /// <summary>SYN and CONNECT on a port, returning the server's signature.</summary>
    private static uint Open(QuazalResponder responder, int port)
    {
        PrudpPacketV0 syn = Packet(PacketType.Syn, PacketFlags.NeedAck, 0, 0);
        var synAck = responder.Handle(syn, Client, port, syn.ToArray());
        uint signature = PrudpPacketV0.TryDeserialize(Assert.Single(synAck), out PrudpPacketV0 reply,
            PrudpPacketV0.NexChecksumWidth, QuazalChecksumAlgorithm.NexByteFold)
            ? reply.ConnectionSignature
            : throw new InvalidOperationException("no SYN-ACK");
        PrudpPacketV0 connect = Packet(PacketType.Connect, PacketFlags.NeedAck | PacketFlags.Reliable, 1, signature);
        responder.Handle(connect, Client, port, connect.ToArray());
        return signature;
    }

    /// <summary>u32 size, protocol|0x80, callId, methodId, no parameters.</summary>
    private static byte[] Rmc(byte protocol, uint method, uint callId = 7)
    {
        byte[] message = new byte[4 + 1 + 4 + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(message, 9);
        message[4] = (byte)(protocol | 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(5), callId);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(9), method);
        return message;
    }

    /// <summary>
    /// Sends one RMC request on a port and returns the decoded reply: whether
    /// it succeeded, and the result body (or the error code's bytes).
    /// </summary>
    private static (bool Success, byte[] Body) Call(QuazalResponder responder, int port, byte protocol, uint method)
    {
        uint signature = Open(responder, port);
        byte[] rmc = Rmc(protocol, method);
        byte[] framed = new byte[rmc.Length + 1];
        rmc.CopyTo(framed, 1);
        PrudpPacketV0 data = Packet(PacketType.Data, PacketFlags.NeedAck | PacketFlags.Reliable, 2, signature);
        data.Payload = new Rc4Stream(TicketGranting.DefaultAuthKey).Process(framed);

        var replies = responder.Handle(data, Client, port, data.ToArray());
        // The transport ACK precedes the reply.
        Assert.Equal(2, replies.Count);
        Assert.True(PrudpPacketV0.TryDeserialize(replies[1], out PrudpPacketV0 reply,
            PrudpPacketV0.NexChecksumWidth, QuazalChecksumAlgorithm.NexByteFold));

        byte[] clear = new Rc4Stream(TicketGranting.DefaultAuthKey).Process(reply.Payload);
        // Encoding byte, u32 size, protocol, success flag.
        ReadOnlySpan<byte> message = clear.AsSpan(1 + 4);
        Assert.Equal(protocol, message[0]);
        bool success = message[1] == 1;
        // Success: callId, methodId, then the result. Error: errorCode, callId.
        return (success, success ? message[(2 + 4 + 4)..].ToArray() : message[2..6].ToArray());
    }

    [Fact]
    public void TheSameProtocolIdReachesADifferentServicePerTitle()
    {
        var responder = NewResponder();

        (bool blacklistOk, byte[] blacklistBody) = Call(responder, BlacklistPort, CollidingProtocol, CollidingMethod);
        (bool fc3Ok, byte[] fc3Body) = Call(responder, Fc3Port, CollidingProtocol, CollidingMethod);

        // Blacklist's LadderHelper.GetUnixUTC is one u32: a timestamp.
        Assert.True(blacklistOk);
        Assert.Equal(4, blacklistBody.Length);
        uint stamped = BinaryPrimitives.ReadUInt32LittleEndian(blacklistBody);
        Assert.InRange(stamped, (uint)DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(), uint.MaxValue);

        // Far Cry 3's Fc3IntelService.GetIntel is the intel struct, which is not.
        Assert.True(fc3Ok);
        Assert.NotEqual(4, fc3Body.Length);
    }

    [Fact]
    public void AMethodATitleDoesNotHaveIsNotImplementedForThatTitle()
    {
        var responder = NewResponder();

        // Fc3OnlineDataService.FetchPackages (106/4) is Far Cry 3's; in
        // Blacklist 106 is the clan helper, which has no method 4.
        (bool ok, byte[] error) = Call(responder, BlacklistPort, Fc3OnlineDataService.ProtocolId, Fc3OnlineDataService.MethodFetchPackages);

        Assert.False(ok);
        Assert.Equal(CoreNotImplemented, BinaryPrimitives.ReadUInt32LittleEndian(error));
    }

    [Fact]
    public void AnOverlordServiceIsNotReachableFromFarCry3()
    {
        var responder = NewResponder();

        (bool ok, byte[] error) = Call(responder, Fc3Port,
            (byte)BlacklistServices.PlayerStats.ProtocolId, BlacklistServices.PlayerStats.MethodWriteStats);

        Assert.False(ok);
        Assert.Equal(CoreNotImplemented, BinaryPrimitives.ReadUInt32LittleEndian(error));
    }

    /// <summary>
    /// Future Soldier is the same era as Blacklist but not the same game: a
    /// Blacklist helper id (107, LadderHelper) and a Far Cry 3 extension id
    /// (107, intel) are both unimplemented for it until its own service on
    /// that id is identified, while the fixed-id RendezVous services answer.
    /// </summary>
    [Fact]
    public void FutureSoldierInheritsNeitherBlacklistsNorFarCry3sIds()
    {
        var responder = NewResponder();

        (bool collidingOk, byte[] error) = Call(responder, FutureSoldierPort, CollidingProtocol, CollidingMethod);
        Assert.False(collidingOk);
        Assert.Equal(CoreNotImplemented, BinaryPrimitives.ReadUInt32LittleEndian(error));

        (bool privilegesOk, _) = Call(responder, FutureSoldierPort, Privileges.ProtocolId, Privileges.MethodGetPrivileges);
        Assert.True(privilegesOk);
    }
}
