using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Plugin.UplayServer.Quazal;
using Plugin.UplayServer.Quazal.Enums;
using Plugin.UplayServer.Quazal.Serializables;
using ServerShared.IO;

namespace GameClientTestHarness;

/// <summary>
/// Builds the exact PRUDP/RMC packets Far Cry 3 sends to log in, using the
/// server's own serializer so the framing cannot drift from what the server
/// parses.
/// </summary>
internal static class LoginExClient
{
    /// <summary>Far Cry 3 frames PRUDP V0 with a one-byte trailing checksum.</summary>
    private const int Fc3ChecksumWidth = PrudpPacketV0.NexChecksumWidth;

    /// <summary>A fixed client-side connection signature. Any non-zero value works; the server echoes it back.</summary>
    private const uint ClientSignature = 0x11223344;

    /// <summary>Builds the opening SYN. A bare SYN is how the server detects the checksum dialect.</summary>
    public static PrudpPacketV0 Syn() => Framed(new PrudpPacketV0
    {
        Source = new VirtualPort { StreamType = StreamType.RV, Port = 15 },
        Destination = new VirtualPort { StreamType = StreamType.RV, Port = 1 },
        PacketType = PacketType.Syn,
        Flags = PacketFlags.NeedAck,
        SequenceId = 0,
        ConnectionSignature = 0,
    });

    /// <summary>Builds the CONNECT, carrying this client's signature.</summary>
    /// <param name="serverSignature">
    /// The signature the server issued in its SYN-ACK. The real client echoes it
    /// in every packet's Signature field from CONNECT on, which is how the
    /// server tells a genuine session from a spoof of its endpoint; the harness
    /// does the same so it stands in faithfully.
    /// </param>
    public static PrudpPacketV0 Connect(uint serverSignature) => Framed(new PrudpPacketV0
    {
        Source = new VirtualPort { StreamType = StreamType.RV, Port = 15 },
        Destination = new VirtualPort { StreamType = StreamType.RV, Port = 1 },
        PacketType = PacketType.Connect,
        Flags = PacketFlags.NeedAck | PacketFlags.Reliable,
        SequenceId = 1,
        Signature = serverSignature,
        ConnectionSignature = ClientSignature,
    });

    /// <summary>
    /// Builds the DATA packet carrying a LoginEx call, with the correlation code
    /// as the username. Unsecured RMC: RC4 under the fixed auth key, a leading
    /// plain-encoding byte, then the RMC request framing.
    /// </summary>
    /// <param name="serverSignature">The signature the server returned in its SYN-ACK.</param>
    public static PrudpPacketV0 LoginEx(string correlationCode, uint serverSignature)
        => LoginEx(correlationCode, serverSignature, serverSignature);

    /// <param name="serverSignature">The signature learned from the SYN-ACK.</param>
    /// <param name="signatureToSend">
    /// What to actually put on the wire 
    /// </param>
    public static PrudpPacketV0 LoginEx(string correlationCode, uint serverSignature, uint signatureToSend)
    {
        _ = serverSignature;
        byte[] rmc = BuildLoginExRmc(correlationCode);

        byte[] framed = new byte[rmc.Length + 1];
        rmc.CopyTo(framed, 1);
        byte[] payload = new Rc4Stream(TicketGranting.DefaultAuthKey).Process(framed);

        return Framed(new PrudpPacketV0
        {
            Source = new VirtualPort { StreamType = StreamType.RV, Port = 15 },
            Destination = new VirtualPort { StreamType = StreamType.RV, Port = 1 },
            PacketType = PacketType.Data,
            Flags = PacketFlags.NeedAck | PacketFlags.Reliable | PacketFlags.HasSize,
            SequenceId = 2,
            Signature = signatureToSend,
            FragmentId = 0,
            Payload = payload,
            PayloadSize = (ushort)payload.Length,
        });
    }

    /// <summary>
    /// Reads the PID out of a login reply, or returns null if it is not a
    /// success. Reverses BuildRmcResponse + BuildLoginResult
    /// </summary>
    public static uint? ReadLoginPid(PrudpPacketV0 reply)
    {
        if (reply.PacketType != PacketType.Data || reply.Payload.Length == 0)
            return null;

        byte[] clear = new Rc4Stream(TicketGranting.DefaultAuthKey).Process(reply.Payload);
        if (clear.Length < 2 || clear[0] != 0)
            return null;

        ReadOnlySpan<byte> rmc = clear.AsSpan(1);
        const int headerLength = 4 + 1 + 1 + 4 + 4;
        if (rmc.Length < headerLength + 8)
            return null;
        if (rmc[5] != 1)
            return null; // not a success response

        ReadOnlySpan<byte> body = rmc[headerLength..];
        uint resultCode = BinaryPrimitives.ReadUInt32LittleEndian(body);
        const uint resultSuccess = 0x00010001;
        if (resultCode != resultSuccess)
            return null;

        return BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
    }

    /// <summary>Serialises a packet to its on-the-wire bytes.</summary>
    public static byte[] ToBytes(PrudpPacketV0 packet) => packet.ToArray();

    /// <summary>Parses a datagram the server sent back.</summary>
    public static PrudpPacketV0 Parse(byte[] datagram)
    {
        var packet = new PrudpPacketV0 { ChecksumWidth = Fc3ChecksumWidth, ChecksumAlgorithm = QuazalChecksumAlgorithm.NexByteFold };
        using var stream = new MemoryStream(datagram);
        packet.Deserialize(new EndiannessReader(stream, Endianness.Little));
        return packet;
    }

    private static PrudpPacketV0 Framed(PrudpPacketV0 packet)
    {
        packet.ChecksumWidth = Fc3ChecksumWidth;
        packet.ChecksumAlgorithm = QuazalChecksumAlgorithm.NexByteFold;
        return packet;
    }

    /// <summary>
    /// LoginEx RMC: u32 size, protocol|0x80, callId, methodId, then a
    /// length-prefixed null-terminated username. FC3's LoginEx carries an
    /// AnyDataHolder after the username that a local account server does not
    /// validate, so an empty one is enough here.
    /// </summary>
    private static byte[] BuildLoginExRmc(string username)
    {
        byte[] name = Encoding.UTF8.GetBytes(username);

        using var parameters = new MemoryStream();
        // Length includes the null terminator, as ReadString expects.
        WriteU16(parameters, (ushort)(name.Length + 1));
        parameters.Write(name);
        parameters.WriteByte(0);

        using var message = new MemoryStream();
        message.WriteByte(TicketGranting.ProtocolId | 0x80);
        WriteU32(message, 1); // callId
        WriteU32(message, TicketGranting.MethodLoginEx);
        parameters.Position = 0;
        parameters.CopyTo(message);

        byte[] payload = message.ToArray();
        using var framed = new MemoryStream();
        WriteU32(framed, (uint)payload.Length);
        framed.Write(payload);
        return framed.ToArray();
    }

    private static void WriteU16(Stream stream, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteU32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }
}
