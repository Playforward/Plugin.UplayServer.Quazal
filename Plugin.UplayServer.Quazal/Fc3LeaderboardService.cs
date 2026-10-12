using System.Buffers.Binary;

namespace Plugin.UplayServer.Quazal;

/// <summary>
/// Far Cry 3's generated Leaderboard service (protocol 109 / 0x6d): where the
/// client reads XP from -- its own, its match-mates', and the global boards.
/// </summary>
internal static class Fc3LeaderboardService
{
    public const byte ProtocolId = 109;
    public const uint MethodGetTop = 1;
    /// <summary>Like 1 with a region: (board, u32, country code packed in a u32, u32, u32, u32, limit, bool).</summary>
    public const uint MethodGetRegionalTop = 2;
    public const uint MethodGetByPids = 3;
    public const uint MethodUnknown7 = 7;
    public const uint MethodUnknown8 = 8;
    public const uint MethodUnknown9 = 9;
    public const uint MethodGetForPlayers = 10;

    /// <summary>Row width by board: five values on board 0, six on the others.</summary>
    private const int XpBoardColumns = 5;
    private const int StatBoardColumns = 6;

    /// <summary>What the working server answers methods 7 and 9 with.</summary>
    private const uint MirroredCount = 8;

    /// <summary>
    /// Method 1: (board, u32, u32, limit, bool) -> the top rows. Only players
    /// this server currently holds are ranked; see the class remarks.
    /// </summary>
    public static byte[] BuildGetTopResult(RmcRequest request, IEnumerable<uint> knownPids, bool probe = false)
    {
        ReadOnlySpan<byte> p = request.Parameters;
        uint board = p.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(p) : 0;
        uint limit = p.Length >= 9 ? BinaryPrimitives.ReadUInt32LittleEndian(p[(p.Length - 5)..]) : 10;

        int take = (int)Math.Min(limit, 100);
        IReadOnlyList<Playforward.StoredScore>? top = OnlineDataStore.TopScores(Fc3IntelService.XpBoard, take);
        if (top is not null)
        {
            var entries = top.Where(score => score.Score > 0 && score.Pid is > 0 and <= uint.MaxValue)
                .Select(score => ((uint)score.Pid, (uint)Math.Min(score.Score, uint.MaxValue)))
                .ToList();
            Serilog.Log.Information("[quazal] FC3 Leaderboard.GetTop board={Board} limit={Limit} -> {Rows} row(s) from the title-wide board",
                board, limit, entries.Count);
            return Rows(board, entries, probe ? request.MethodId : 0);
        }

        var ranked = knownPids
            .Select(pid => (Pid: pid, Xp: PlayerStatistics.Xp(pid)))
            .Where(entry => entry.Xp > 0)
            .OrderByDescending(entry => entry.Xp)
            .Take(take)
            .ToList();
        Serilog.Log.Information("[quazal] FC3 Leaderboard.GetTop board={Board} limit={Limit} -> {Rows} row(s) from players this server holds",
            board, limit, ranked.Count);
        return Rows(board, ranked, probe ? request.MethodId : 0);
    }

    /// <summary>
    /// Method 3: (board, u32, qlist&lt;pid&gt;, u32, limit, bool) -> rows for
    /// those pids. This is how the client asks for its own entry.
    /// </summary>
    public static byte[] BuildGetByPidsResult(RmcRequest request, bool probe = false)
    {
        ReadOnlySpan<byte> p = request.Parameters;
        uint board = p.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(p) : 0;
        List<uint> pids = ReadPidList(p, 8);
        Serilog.Log.Information("[quazal] FC3 Leaderboard.GetByPids board={Board} pids=[{Pids}]", board, string.Join(", ", pids));
        return Rows(board, WithXp(pids), probe ? request.MethodId : 0);
    }

    /// <summary>
    /// Method 10: (qlist&lt;pid&gt;, u32) -> rows for those pids, sent after a
    /// match for everybody who was in it.
    /// </summary>
    public static byte[] BuildGetForPlayersResult(RmcRequest request, bool probe = false)
    {
        ReadOnlySpan<byte> p = request.Parameters;
        List<uint> pids = ReadPidList(p, 0);
        int after = 4 + pids.Count * 4;
        uint trailing = p.Length >= after + 4 ? BinaryPrimitives.ReadUInt32LittleEndian(p[after..]) : 0;
        List<(uint Pid, uint Xp)> entries = WithXp(pids);
        Serilog.Log.Information("[quazal] FC3 Leaderboard.GetForPlayers pids=[{Pids}] trailing={Trailing} -> {Rows}",
            string.Join(", ", pids), trailing,
            string.Join(", ", entries.Select(e => $"{e.Pid}: level {Fc3RankTable.RankFor(e.Xp)} at {e.Xp} xp")));
        if (probe)
            return ProbeRows(entries);
        return Rows(0, entries, 0);
    }

    /// <summary>
    /// The per-player probe (<c>LeaderboardProbe</c>)
    /// </summary>
    private static byte[] ProbeRows(IReadOnlyCollection<(uint Pid, uint Xp)> entries)
    {
        uint[] small = [11, 22, 33, 44];
        uint[] xpSized = [5500, 7300, 9500, 12000];
        var body = new byte[4 + entries.Count * (4 + XpBoardColumns * 4)];
        int offset = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset), (uint)entries.Count); offset += 4;
        foreach ((uint pid, _) in entries)
        {
            uint[] values = pid % 2 == 0 ? small : xpSized;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset), XpBoardColumns); offset += 4;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset), pid); offset += 4;
            foreach (uint value in values)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset), value); offset += 4;
            }
            Serilog.Log.Warning("[quazal] FC3 Leaderboard PROBE: GetForPlayers row for pid={Pid} = [{Values}]", pid, string.Join(", ", values));
        }
        return body;
    }

    /// <summary>Methods 7 and 9: one uint32, mirrored from the working server.</summary>
    public static byte[] BuildCountResult(RmcRequest request)
    {
        Serilog.Log.Information("[quazal] FC3 Leaderboard method {Method} -> {Value}", request.MethodId, MirroredCount);
        var body = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(body, MirroredCount);
        return body;
    }

    /// <summary>
    /// Each pid with its XP: the title-wide board's number where Playforward
    /// has one (so an offline player in a finished match is ranked too),
    /// else what this server holds for the pid.
    /// </summary>
    private static List<(uint Pid, uint Xp)> WithXp(IReadOnlyCollection<uint> pids)
    {
        var known = new Dictionary<uint, uint>();
        foreach (Playforward.StoredScore score in OnlineDataStore.ScoresFor(Fc3IntelService.XpBoard, pids) ?? [])
            if (score.Pid is > 0 and <= uint.MaxValue)
                known[(uint)score.Pid] = (uint)Math.Min(Math.Max(score.Score, 0), uint.MaxValue);
        return pids.Select(pid => (pid, known.TryGetValue(pid, out uint xp) ? xp : PlayerStatistics.Xp(pid))).ToList();
    }

    /// <summary>One row per player, in the board's column layout: pid, zero, XP, then the board's statistics.</summary>
    private static byte[] Rows(uint board, IReadOnlyCollection<(uint Pid, uint Xp)> entries, uint probeMethod = 0, int? width = null, bool level = false)
    {
        int columns = width ?? (board == 0 ? XpBoardColumns : StatBoardColumns);
        if (probeMethod != 0)
            Serilog.Log.Warning("[quazal] FC3 Leaderboard PROBE: method {Method} rows carry {Base}+index in every column but pid and xp",
                probeMethod, probeMethod * 10);
        var body = new byte[4 + entries.Count * (4 + columns * 4)];
        int offset = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset), (uint)entries.Count); offset += 4;
        foreach ((uint pid, uint xp) in entries)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset), (uint)columns); offset += 4;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset), pid); offset += 4;
            uint second = probeMethod != 0 ? probeMethod * 10 + 1 : level ? Fc3RankTable.RankFor(xp) : 0;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset), second); offset += 4;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset), xp); offset += 4;
            for (int index = 3; index < columns; index++, offset += 4)
                BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset), probeMethod != 0 ? probeMethod * 10 + (uint)index : 0);
        }
        return body;
    }

    private static List<uint> ReadPidList(ReadOnlySpan<byte> p, int offset)
    {
        var pids = new List<uint>();
        if (p.Length < offset + 4) return pids;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(p[offset..]);
        offset += 4;
        for (uint i = 0; i < count && i < 64 && p.Length >= offset + 4; i++, offset += 4)
            pids.Add(BinaryPrimitives.ReadUInt32LittleEndian(p[offset..]));
        return pids;
    }
}
