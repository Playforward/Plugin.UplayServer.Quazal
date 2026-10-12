namespace Plugin.UplayServer.Quazal;

/// <summary>
/// Protocol 29: pids to names, which the leaderboard and scoreboard screens
/// call for every row before they can draw it. Refusing it is why the global
/// leaderboard screen stayed empty while the rows themselves were being
/// answered.
/// </summary>
internal static class NameLookupService
{
    public const byte ProtocolId = 29;
    public const uint MethodNamesForPids = 12;
    public const uint MethodDisplayNamesForNames = 14;

    public static byte[] BuildNamesForPidsResult(RmcRequest request, Func<uint, string> nameForPid)
    {
        ReadOnlySpan<byte> p = request.Parameters;
        var pids = new List<uint>();
        if (p.Length >= 4)
        {
            uint count = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p);
            for (int offset = 4; pids.Count < count && pids.Count < 64 && p.Length >= offset + 4; offset += 4)
                pids.Add(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p[offset..]));
        }

        using var body = new MemoryStream();
        body.Write(BitConverter.GetBytes((uint)pids.Count));
        var described = new List<string>();
        foreach (uint pid in pids)
        {
            string name = nameForPid(pid);
            body.Write(BitConverter.GetBytes(pid));
            TicketGranting.WriteString(body, name);
            described.Add($"{pid}={name}");
        }
        Serilog.Log.Information("[quazal] names for pids: {Names}", string.Join(", ", described));
        return body.ToArray();
    }

    public static byte[] BuildDisplayNamesForNamesResult(RmcRequest request)
    {
        ReadOnlySpan<byte> p = request.Parameters;
        var names = new List<string>();
        int offset = 0;
        if (p.Length >= 4)
        {
            uint count = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p);
            offset = 4;
            while (names.Count < count && names.Count < 64 && TryReadString(p, ref offset, out string name))
                names.Add(name);
        }

        using var body = new MemoryStream();
        body.Write(BitConverter.GetBytes((uint)names.Count));
        foreach (string name in names)
        {
            TicketGranting.WriteString(body, name);
            TicketGranting.WriteString(body, name);
        }
        Serilog.Log.Information("[quazal] display names for [{Names}]", string.Join(", ", names));
        return body.ToArray();
    }

    private static bool TryReadString(ReadOnlySpan<byte> p, ref int offset, out string value)
    {
        value = string.Empty;
        if (p.Length < offset + 2) return false;
        int length = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(p[offset..]);
        offset += 2;
        if (length == 0) return true;
        if (p.Length < offset + length) return false;
        value = System.Text.Encoding.UTF8.GetString(p.Slice(offset, length - 1));
        offset += length;
        return true;
    }
}
