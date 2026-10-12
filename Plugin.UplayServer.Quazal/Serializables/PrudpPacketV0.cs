using System.Buffers.Binary;
using Plugin.UplayServer.Quazal.Enums;
using ServerShared.IO;

namespace Plugin.UplayServer.Quazal.Serializables;

public struct PrudpPacketV0() : ICustomSerializable
{
    /// <summary>Original Quazal RendezVous trailing checksum width (Far Cry 5).</summary>
    public const int LegacyChecksumWidth = 4;

    /// <summary>NEX V0 trailing checksum width (Far Cry 3).</summary>
    public const int NexChecksumWidth = 1;

    /// <summary>Header length before payload for a SYN/CONNECT without HAS_SIZE.</summary>
    private const int SynHeaderLength = 14;

    public VirtualPort Source = new();
    public VirtualPort Destination = new();
    public PacketType PacketType;
    public PacketFlags Flags;
    public byte SessionId;
    public uint Signature;
    public ushort SequenceId;

    // Packet Type Specific data
    public uint ConnectionSignature;
    public byte FragmentId;

    public ushort PayloadSize;
    public byte[] Payload = [];

    public byte[] TrailingPayload = [];

    public uint Checksum;

    public bool UseCompression;

    /// <summary>
    /// Wire width of the trailing checksum for this peer's dialect. Defaults to
    /// the legacy four bytes so titles already working on that layout are
    /// unaffected; Far Cry 3 peers are latched to one byte at SYN time.
    /// </summary>
    public int ChecksumWidth = LegacyChecksumWidth;

    /// <summary>Arithmetic used to produce <see cref="Checksum"/>.</summary>
    public QuazalChecksumAlgorithm ChecksumAlgorithm = QuazalChecksumAlgorithm.LegacyWordSum;

    /// <summary>
    /// Infers the trailing checksum width from a peer's opening SYN. A SYN
    /// carries no payload and no HAS_SIZE field, so everything past the
    /// 14-byte header is the checksum: 15 bytes means NEX V0, 18 means legacy.
    /// Returns null when the datagram is not a recognisable bare SYN, leaving
    /// the caller on its configured default.
    /// </summary>
    public static int? DetectChecksumWidth(ReadOnlySpan<byte> data)
    {
        if (data.Length < SynHeaderLength + NexChecksumWidth)
            return null;
        if ((PacketType)(data[2] & 0x07) != PacketType.Syn)
            return null;
        if (((PacketFlags)(data[2] >> 3)).HasFlag(PacketFlags.HasSize))
            return null;

        int trailing = data.Length - SynHeaderLength;
        return trailing is NexChecksumWidth or LegacyChecksumWidth ? trailing : null;
    }

    public void Deserialize(EndiannessReader reader)
    {
        Source = reader.ReadSerializable<VirtualPort>();
        Destination = reader.ReadSerializable<VirtualPort>();
        var flags = reader.ReadByte();
        PacketType = (PacketType)(flags & 0x7);
        Flags = (PacketFlags)(flags >> 3);
        SessionId = reader.ReadByte();
        Signature = reader.ReadUInt32();
        SequenceId = reader.ReadUInt16();

        if (PacketType is PacketType.Syn or PacketType.Connect)
            ConnectionSignature = reader.ReadUInt32();

        if (PacketType is PacketType.Data)
            FragmentId = reader.ReadByte();

        if (Flags.HasFlag(PacketFlags.HasSize))
            PayloadSize = reader.ReadUInt16();
        else
            PayloadSize = (ushort)(reader.BaseStream.Length - reader.BaseStream.Position - ChecksumWidth);

        Payload = reader.ReadBytes(PayloadSize);
        if (PacketType == PacketType.Connect && Flags.HasFlag(PacketFlags.Ack))
        {
            long trailing = reader.BaseStream.Length - reader.BaseStream.Position - ChecksumWidth;
            if (trailing > 0)
                TrailingPayload = reader.ReadBytes((int)trailing);
        }
        Checksum = ChecksumWidth == NexChecksumWidth ? reader.ReadByte() : reader.ReadUInt32();
    }

    public void Serialize(EndiannessWriter writer)
    {
        writer.Write((byte)(((byte)Source.StreamType << 4) | Source.Port));
        writer.Write((byte)(((byte)Destination.StreamType << 4) | Destination.Port));
        writer.Write((byte)(((byte)Flags << 3) | (byte)PacketType));
        writer.Write(SessionId);
        writer.Write(Signature);
        writer.Write(SequenceId);

        if (PacketType is PacketType.Syn or PacketType.Connect)
            writer.Write(ConnectionSignature);
        if (PacketType is PacketType.Data)
            writer.Write(FragmentId);
        if (Flags.HasFlag(PacketFlags.HasSize))
            writer.Write((ushort)Payload.Length);

        writer.Write(Payload);
        writer.Write(TrailingPayload);
        if (ChecksumWidth == NexChecksumWidth)
            writer.Write((byte)Checksum);
        else
            writer.Write(Checksum);
    }

    /// <summary>
    /// Parses the one-byte-flags legacy Quazal V0 framing. <paramref name="checksumWidth"/>
    /// selects the peer's dialect; see <see cref="DetectChecksumWidth"/>.
    /// </summary>
    public static bool TryDeserialize(ReadOnlySpan<byte> data, out PrudpPacketV0 packet,
        int checksumWidth = LegacyChecksumWidth,
        QuazalChecksumAlgorithm algorithm = QuazalChecksumAlgorithm.LegacyWordSum)
    {
        packet = new()
        {
            ChecksumWidth = checksumWidth,
            ChecksumAlgorithm = algorithm,
        };
        if (data.Length < 10 + checksumWidth) return false;

        packet.Source = new()
        {
            StreamType = (StreamType)(data[0] >> 4),
            Port = (byte)(data[0] & 0x0f),
        };
        packet.Destination = new()
        {
            StreamType = (StreamType)(data[1] >> 4),
            Port = (byte)(data[1] & 0x0f),
        };
        packet.PacketType = (PacketType)(data[2] & 0x07);
        packet.Flags = (PacketFlags)(data[2] >> 3);
        packet.SessionId = data[3];
        packet.Signature = BinaryPrimitives.ReadUInt32LittleEndian(data[4..8]);
        packet.SequenceId = BinaryPrimitives.ReadUInt16LittleEndian(data[8..10]);

        int pos = 10;
        if (packet.PacketType is PacketType.Syn or PacketType.Connect)
        {
            if (data.Length < pos + 4 + checksumWidth) return false;
            packet.ConnectionSignature = BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
            pos += 4;
        }
        if (packet.PacketType is PacketType.Data)
        {
            if (data.Length < pos + 1 + checksumWidth) return false;
            packet.FragmentId = data[pos++];
        }
        if (packet.Flags.HasFlag(PacketFlags.HasSize))
        {
            if (data.Length < pos + 2 + checksumWidth) return false;
            packet.PayloadSize = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
            pos += 2;
        }
        else
        {
            packet.PayloadSize = (ushort)(data.Length - pos - checksumWidth);
        }

        if (packet.PayloadSize > data.Length - pos - checksumWidth) return false;
        packet.Payload = data.Slice(pos, packet.PayloadSize).ToArray();
        pos += packet.PayloadSize;
        int trailingLength = data.Length - pos - checksumWidth;
        if (trailingLength < 0) return false;
        if (trailingLength > 0)
        {
            if (packet.PacketType != PacketType.Connect || !packet.Flags.HasFlag(PacketFlags.Ack))
                return false;
            packet.TrailingPayload = data.Slice(pos, trailingLength).ToArray();
            pos += trailingLength;
        }
        if (pos != data.Length - checksumWidth) return false;
        packet.Checksum = checksumWidth == NexChecksumWidth
            ? data[pos]
            : BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
        return true;
    }

    /// <summary>Returns a wire packet without requiring an allocation-heavy stream.</summary>
    public readonly byte[] ToArray(byte accessKeyChecksum = 0)
    {
        int header = 10;
        if (PacketType is PacketType.Syn or PacketType.Connect) header += 4;
        if (PacketType is PacketType.Data) header++;
        if (Flags.HasFlag(PacketFlags.HasSize)) header += 2;
        var output = new byte[header + Payload.Length + TrailingPayload.Length + ChecksumWidth];
        output[0] = (byte)(((byte)Source.StreamType << 4) | Source.Port);
        output[1] = (byte)(((byte)Destination.StreamType << 4) | Destination.Port);
        output[2] = (byte)(((byte)Flags << 3) | (byte)PacketType);
        output[3] = SessionId;
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), Signature);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(8), SequenceId);
        int pos = 10;
        if (PacketType is PacketType.Syn or PacketType.Connect)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(pos), ConnectionSignature);
            pos += 4;
        }
        if (PacketType is PacketType.Data) output[pos++] = FragmentId;
        if (Flags.HasFlag(PacketFlags.HasSize))
        {
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(pos), (ushort)Payload.Length);
            pos += 2;
        }
        Payload.AsSpan().CopyTo(output.AsSpan(pos));
        pos += Payload.Length;
        TrailingPayload.AsSpan().CopyTo(output.AsSpan(pos));
        pos += TrailingPayload.Length;

        uint checksum = CalculateChecksum(output.AsSpan(0, pos), accessKeyChecksum, ChecksumAlgorithm);
        if (ChecksumWidth == NexChecksumWidth)
            output[pos] = (byte)checksum;
        else
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(pos), checksum);
        return output;
    }

    public static uint CalculateChecksum(ReadOnlySpan<byte> data, byte accessKeyChecksum,
        QuazalChecksumAlgorithm algorithm)
        => algorithm switch
        {
            QuazalChecksumAlgorithm.NexByteFold => CalculateNexChecksum(data, accessKeyChecksum),
            QuazalChecksumAlgorithm.LegacyWordSumTruncated =>
                CalculateLegacyChecksum(data, accessKeyChecksum) & 0xFF,
            _ => CalculateLegacyChecksum(data, accessKeyChecksum),
        };

    public static uint CalculateLegacyChecksum(ReadOnlySpan<byte> data, byte accessKeyChecksum = 0)
    {
        ulong sum = accessKeyChecksum;
        int offset = 0;
        while (offset + sizeof(uint) <= data.Length)
        {
            sum += BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
            offset += sizeof(uint);
        }

        if (offset < data.Length)
        {
            Span<byte> tail = stackalloc byte[sizeof(uint)];
            data[offset..].CopyTo(tail);
            sum += BinaryPrimitives.ReadUInt32LittleEndian(tail);
        }

        return unchecked((uint)sum);
    }

    /// <summary>
    /// NEX V0: fold the 32-bit word sum into a byte by adding its four bytes,
    /// then add the unaligned tail bytes individually and the access-key
    /// contribution.
    /// </summary>
    public static uint CalculateNexChecksum(ReadOnlySpan<byte> data, byte accessKeyChecksum = 0)
    {
        uint words = 0;
        int offset = 0;
        while (offset + sizeof(uint) <= data.Length)
        {
            words = unchecked(words + BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]));
            offset += sizeof(uint);
        }

        uint sum = accessKeyChecksum;
        for (; offset < data.Length; offset++)
            sum += data[offset];

        sum += (words & 0xFF) + ((words >> 8) & 0xFF) + ((words >> 16) & 0xFF) + ((words >> 24) & 0xFF);
        return sum & 0xFF;
    }

    public readonly override string ToString()
    {
        return $"S:{Source} D: {Destination} T:{PacketType} F: {Flags} ({(byte)Flags}) SID: {SessionId} SIG: {Signature} SEQID: {SequenceId} " +
                $"CSIG: {ConnectionSignature}, FID: {FragmentId} PSize: {PayloadSize} C: {Checksum} (COMP:{UseCompression}) CW:{ChecksumWidth} ALG:{ChecksumAlgorithm}";
    }
}
