using System.Buffers.Binary;
using System.Collections.Concurrent;
using Serilog;

namespace Plugin.UplayServer.Quazal;

/// <summary>
/// Splinter Cell Blacklist's Player Stats service (protocol 55): the store
/// behind WriteStats / ReadStatsByPlayers and the wire shapes it speaks. The
/// shapes are the DDL's (5th-echelon scripts/ddls/PlayerStatsService.json)
/// </summary>
internal static class BlacklistStatistics
{
    /// <summary>The namespace a title's protocol 55 rows are stored under in Playforward.</summary>
    private const string BlobNamespace = "scbl-stats";

    /// <summary>The one key under it: a player's whole row set, in <see cref="Serialize"/>'s format.</summary>
    private const string RowsKey = "rows";

    /// <summary>As <see cref="OnlineDataStore"/>: a load that failed is not retried on every read.</summary>
    private static readonly TimeSpan PrimeRetryAfter = TimeSpan.FromSeconds(30);

    /// <summary>The boards whose writes are per-round increments (see the class remarks).</summary>
    private static readonly HashSet<uint> AccumulatingBoards = [10, 11, 12, 13, 20, 25, 30, 31];

    /// <summary>Stats on those boards that are labels, not counts, and so replace rather than add.</summary>
    private static readonly HashSet<uint> ReplacedStats = [126, 199, 214, 230];

    /// <summary>The stat a leaderboard ranks by: XP earned.</summary>
    public const uint RankStat = 127;

    /// <summary>Leaderboard board -> the statboard whose rows it ranks (see the class remarks).</summary>
    private static uint SourceBoard(uint leaderboard) => leaderboard == 5 ? 10u : leaderboard;

    /// <summary>The Playforward score board a statboard row's XP is ranked on.</summary>
    internal static string ScoreBoard(uint board, uint context) => $"scbl:{board}:{context}";

    /// <summary>How deep a leaderboard is ranked: a player below this has no rank.</summary>
    private const int RankDepth = 1000;

    /// <summary>The top of a board is fetched once per screen, not once per query on it.</summary>
    private static readonly TimeSpan TopCacheFor = TimeSpan.FromSeconds(5);
    private static readonly ConcurrentDictionary<(string TitleId, string Board), (IReadOnlyList<Playforward.StoredScore> Scores, DateTimeOffset At)> TopCache = [];

    /// <summary>A Quazal Variant: the wire's tag and its value as a long or double.</summary>
    internal readonly record struct Variant(byte Tag, long Integer, double Real)
    {
        public const byte TagInt64 = 1;
        public const byte TagDouble = 2;
        public const byte TagBool = 3;
        public const byte TagUInt64 = 6;

        public static Variant Int64(long value) => new(TagInt64, value, 0);
        public static Variant Double(double value) => new(TagDouble, 0, value);

        public static readonly Variant Zero = Int64(0);

        /// <summary>This plus <paramref name="delta"/>, when both are numbers of the same kind; else the delta.</summary>
        public Variant Plus(Variant delta)
        {
            if (Tag == delta.Tag && Tag is TagInt64 or TagUInt64)
                return this with { Integer = Integer + delta.Integer };
            if (Tag == delta.Tag && Tag == TagDouble)
                return this with { Real = Real + delta.Real };
            return delta;
        }

        public override string ToString() => Tag == TagDouble ? Real.ToString("R") : Integer.ToString();
    }

    /// <summary>One row: its address, when it was last written, and its values in first-write order.</summary>
    internal sealed class Row
    {
        public required uint Board { get; init; }
        public required uint Context { get; init; }
        public DateTimeOffset SubmittedAt { get; set; }
        public Dictionary<uint, Variant> Values { get; } = [];
    }

    private sealed class RowSet
    {
        public List<Row> Rows { get; } = [];
        public Dictionary<(uint Board, uint Context), Row> ByAddress { get; } = [];

        public Row GetOrAdd(uint board, uint context)
        {
            if (!ByAddress.TryGetValue((board, context), out Row? row))
            {
                row = new Row { Board = board, Context = context };
                Rows.Add(row);
                ByAddress[(board, context)] = row;
            }
            return row;
        }
    }

    /// <summary>One PlayerStatUpdate: a board, the contexts it lands in, the stats.</summary>
    internal sealed record Update(uint Board, uint[] Contexts, IReadOnlyList<(uint StatId, Variant Value)> Stats);

    /// <summary>One StatboardQuery. Sort criteria are parsed and ignored: nothing here ranks.</summary>
    internal sealed record Query(uint Board, uint[] Contexts, uint ResetFrequency, uint[] StatIds);

    private static readonly ConcurrentDictionary<(string TitleId, uint Pid), RowSet> Players = [];

    /// <summary>Players whose stored rows have been loaded this session, or when a load last failed.</summary>
    private static readonly ConcurrentDictionary<(string TitleId, uint Pid), DateTimeOffset?> Primed = [];

    /// <summary>Whether a write to this board and stat adds to the stored value or replaces it.</summary>
    internal static bool Accumulates(uint board, uint statId) =>
        AccumulatingBoards.Contains(board) && !ReplacedStats.Contains(statId);

    /// <summary>Applies a write for the caller: each update lands in each of its contexts.</summary>
    public static void Write(string titleId, uint pid, IReadOnlyList<Update> updates, DateTimeOffset now)
    {
        RowSet rows = RowsOf(titleId, pid);
        lock (rows)
        {
            foreach (Update update in updates)
            {
                foreach (uint context in update.Contexts)
                {
                    Row row = rows.GetOrAdd(update.Board, context);
                    row.SubmittedAt = now;
                    foreach ((uint statId, Variant value) in update.Stats)
                    {
                        row.Values[statId] = Accumulates(update.Board, statId) && row.Values.TryGetValue(statId, out Variant stored)
                            ? stored.Plus(value)
                            : value;
                    }
                    if (AccumulatingBoards.Contains(update.Board) && update.Stats.Any(s => s.StatId == RankStat))
                        RecordScore(titleId, pid, row);
                }
            }
            Persist(titleId, pid, rows);
        }
    }

    /// <summary>The row's XP as its Playforward score, when the pid is a Playforward principal.</summary>
    private static void RecordScore(string titleId, uint pid, Row row)
    {
        Playforward.IProgressStore? store = StoreFor(titleId, pid);
        if (store is null || !row.Values.TryGetValue(RankStat, out Variant xp) || xp.Tag == Variant.TagDouble)
            return;
        string board = ScoreBoard(row.Board, row.Context);
        if (!store.SaveScore(pid, board, xp.Integer))
            Log.Warning("[progress] could not record pid={Pid}'s {Title} score {Score} on {Board}", pid, titleId, xp.Integer, board);
        TopCache.TryRemove((titleId, board), out _);
    }

    /// <summary>Everything, for tests.</summary>
    public static void Clear()
    {
        Players.Clear();
        Primed.Clear();
        TopCache.Clear();
    }

    /// <summary>The player's row at an address, or null: none stored, or nothing loadable.</summary>
    public static Row? Find(string titleId, uint pid, uint board, uint context)
    {
        if (!Players.TryGetValue((titleId, pid), out RowSet? rows) && !TryPrime(titleId, pid, out rows))
            return null;
        lock (rows)
            return rows.ByAddress.GetValueOrDefault((board, context));
    }

    /// <summary>Drops a player's cached rows, so the next read loads what Playforward now holds.</summary>
    public static void Forget(string titleId, uint pid)
    {
        Players.TryRemove((titleId, pid), out _);
        Primed.TryRemove((titleId, pid), out _);
    }

    private static RowSet RowsOf(string titleId, uint pid)
    {
        if (Players.TryGetValue((titleId, pid), out RowSet? rows) || TryPrime(titleId, pid, out rows))
            return rows;
        return Players.GetOrAdd((titleId, pid), _ => new RowSet());
    }

    /// <summary>The title's Playforward store, when the pid is one of its principals.</summary>
    private static Playforward.IProgressStore? StoreFor(string titleId, uint pid)
    {
        Playforward.PlayforwardTitle? title = Playforward.PlayforwardTitles.ForTitle(titleId);
        return title?.Gateway.ForPid(pid) is null ? null : title.Store;
    }

    private static bool TryPrime(string titleId, uint pid, out RowSet rows)
    {
        rows = null!;
        Playforward.IProgressStore? store = StoreFor(titleId, pid);
        if (store is null)
            return false;
        if (Primed.TryGetValue((titleId, pid), out DateTimeOffset? failedAt) &&
            (failedAt is null || DateTimeOffset.UtcNow - failedAt < PrimeRetryAfter))
            return false;

        IReadOnlyList<Playforward.StoredBlob>? blobs = store.LoadPlayer(pid);
        if (blobs is null)
        {
            Primed[(titleId, pid)] = DateTimeOffset.UtcNow;
            Log.Warning("[progress] could not load pid={Pid}'s {Title} player stats from Playforward; serving what this session holds", pid, titleId);
            return false;
        }
        var loaded = new RowSet();
        Playforward.StoredBlob? saved = blobs.FirstOrDefault(b => b.Namespace == BlobNamespace && b.Key == RowsKey);
        if (saved is not null && !TryDeserialize(saved.Payload, loaded))
            Log.Warning("[progress] pid={Pid}'s {Title} player stats blob could not be read; starting them over", pid, titleId);
        rows = Players.GetOrAdd((titleId, pid), loaded);
        Primed[(titleId, pid)] = null;
        Log.Information("[progress] pid={Pid}'s {Title} player stats loaded from Playforward: {Rows} row(s)", pid, titleId, loaded.Rows.Count);
        return true;
    }

    private static void Persist(string titleId, uint pid, RowSet rows)
    {
        Playforward.IProgressStore? store = StoreFor(titleId, pid);
        if (store is not null && !store.SaveBlob(pid, BlobNamespace, RowsKey, Serialize(rows)))
            Log.Warning("[progress] could not save pid={Pid}'s {Title} player stats to Playforward; keeping them in memory for this session", pid, titleId);
    }

    private static byte[] Serialize(RowSet rows)
    {
        using var body = new MemoryStream();
        TicketGranting.WriteU32(body, (uint)rows.Rows.Count);
        foreach (Row row in rows.Rows)
        {
            TicketGranting.WriteU32(body, row.Board);
            TicketGranting.WriteU32(body, row.Context);
            FutureSoldierServices.WriteU64(body, (ulong)row.SubmittedAt.ToUnixTimeSeconds());
            TicketGranting.WriteU32(body, (uint)row.Values.Count);
            foreach ((uint statId, Variant value) in row.Values)
            {
                TicketGranting.WriteU32(body, statId);
                body.WriteByte(value.Tag);
                FutureSoldierServices.WriteU64(body, value.Tag == Variant.TagDouble
                    ? (ulong)BitConverter.DoubleToInt64Bits(value.Real)
                    : (ulong)value.Integer);
            }
        }
        return body.ToArray();
    }

    private static bool TryDeserialize(byte[] blob, RowSet into)
    {
        int offset = 0;
        if (!TryU32(blob, ref offset, out uint rowCount) || rowCount > 100_000)
            return false;
        for (uint r = 0; r < rowCount; r++)
        {
            if (!TryU32(blob, ref offset, out uint board) || !TryU32(blob, ref offset, out uint context) ||
                !TryU64(blob, ref offset, out ulong submitted) || !TryU32(blob, ref offset, out uint valueCount) || valueCount > 4096)
                return false;
            Row row = into.GetOrAdd(board, context);
            row.SubmittedAt = DateTimeOffset.FromUnixTimeSeconds((long)submitted);
            for (uint v = 0; v < valueCount; v++)
            {
                if (!TryU32(blob, ref offset, out uint statId) || !TryVariant(blob, ref offset, out Variant value, fixedWidth: true))
                    return false;
                row.Values[statId] = value;
            }
        }
        return offset == blob.Length;
    }

    /// <summary>Parses WriteStats: a QList&lt;PlayerStatUpdate&gt;.</summary>
    public static bool TryParseWrite(byte[] parameters, out List<Update> updates)
    {
        updates = [];
        int offset = 0;
        if (!TryU32(parameters, ref offset, out uint count) || count > 1024)
            return false;
        for (uint n = 0; n < count; n++)
        {
            if (!TryU32(parameters, ref offset, out uint board) || !TryU32List(parameters, ref offset, out uint[] contexts) ||
                !TryU32(parameters, ref offset, out uint statCount) || statCount > 4096)
                return false;
            var stats = new List<(uint, Variant)>((int)statCount);
            for (uint s = 0; s < statCount; s++)
            {
                if (!TryU32(parameters, ref offset, out uint statId) || !TryVariant(parameters, ref offset, out Variant value, fixedWidth: false))
                    return false;
                stats.Add((statId, value));
            }
            updates.Add(new Update(board, contexts, stats));
        }
        return offset == parameters.Length;
    }

    /// <summary>Parses ReadStatsByPlayers: the pids, then a QList&lt;StatboardQuery&gt;.</summary>
    public static bool TryParseRead(byte[] parameters, out List<uint> pids, out List<Query> queries)
    {
        queries = [];
        pids = [];
        int offset = 0;
        if (!TryU32List(parameters, ref offset, out uint[] asked) || asked.Length > 256)
            return false;
        pids.AddRange(asked);
        if (!TryU32(parameters, ref offset, out uint count) || count > 1024)
            return false;
        for (uint n = 0; n < count; n++)
        {
            if (!TryU32(parameters, ref offset, out uint board) || !TryU32List(parameters, ref offset, out uint[] contexts) ||
                !TryU32(parameters, ref offset, out uint resetFrequency) || !TryU32List(parameters, ref offset, out uint[] statIds) ||
                !TryU32(parameters, ref offset, out uint sortCount) || sortCount > 256)
                return false;
            offset += (int)sortCount * 8;   // PlayerStatSortCriteria {u32 statId, u32 order}
            if (offset > parameters.Length)
                return false;
            queries.Add(new Query(board, contexts, resetFrequency, statIds));
        }
        return offset == parameters.Length;
    }

    /// <summary>
    /// Builds the QList&lt;StatboardResult&gt; for a read: one per (board,
    /// context) asked, carrying the asked players' rows there and zero
    /// defaults for the asked stats.
    /// </summary>
    public static byte[] BuildReadResult(string titleId, IReadOnlyList<uint> pids, IReadOnlyList<Query> queries,
        Func<uint, string> nameForPid, out int rowsListed)
    {
        rowsListed = 0;
        var results = new List<(Query Query, uint Context, List<(uint Pid, Row Row)> Rows)>();
        foreach (Query query in queries)
        {
            foreach (uint context in query.Contexts.Length == 0 ? [0u] : query.Contexts)
            {
                var rows = new List<(uint, Row)>();
                foreach (uint pid in pids)
                    if (Find(titleId, pid, query.Board, context) is { } row)
                        rows.Add((pid, row));
                rowsListed += rows.Count;
                results.Add((query, context, rows));
            }
        }

        using var body = new MemoryStream();
        TicketGranting.WriteU32(body, (uint)results.Count);
        foreach ((Query query, uint context, var rows) in results)
        {
            TicketGranting.WriteU32(body, query.Board);
            TicketGranting.WriteU32(body, context);
            TicketGranting.WriteU32(body, query.ResetFrequency);
            TicketGranting.WriteU32(body, (uint)rows.Count);
            foreach ((uint pid, Row row) in rows)
            {
                TicketGranting.WriteU32(body, pid);
                TicketGranting.WriteString(body, nameForPid(pid));
                FutureSoldierServices.WriteU64(body, FutureSoldierServices.QuazalDateTime(row.SubmittedAt));
                TicketGranting.WriteU32(body, (uint)query.StatIds.Length);
                foreach (uint statId in query.StatIds)
                {
                    TicketGranting.WriteU32(body, statId);
                    WriteVariant(body, row.Values.GetValueOrDefault(statId, Variant.Zero));
                }
            }
            TicketGranting.WriteU32(body, (uint)query.StatIds.Length);
            foreach (uint statId in query.StatIds)
            {
                TicketGranting.WriteU32(body, statId);
                WriteVariant(body, Variant.Zero);
            }
        }
        return body.ToArray();
    }

    /// <summary>One LeaderboardQuery: a board, one context, the stats to list with each rank.</summary>
    internal sealed record LeaderboardQuery(uint Board, uint Context, uint ResetFrequency, uint[] StatIds);

    /// <summary>A ranked entry: 1-based rank, the score ranked by, and who.</summary>
    internal sealed record Ranked(uint Rank, long Score, uint Pid, string Name);

    /// <summary>Parses the QList&lt;LeaderboardQuery&gt; that ends every leaderboard read.</summary>
    private static bool TryLeaderboardQueries(byte[] parameters, ref int offset, out List<LeaderboardQuery> queries)
    {
        queries = [];
        if (!TryU32(parameters, ref offset, out uint count) || count > 256)
            return false;
        for (uint n = 0; n < count; n++)
        {
            if (!TryU32(parameters, ref offset, out uint board) || !TryU32(parameters, ref offset, out uint context) ||
                !TryU32(parameters, ref offset, out uint resetFrequency) || !TryU32List(parameters, ref offset, out uint[] statIds))
                return false;
            queries.Add(new LeaderboardQuery(board, context, resetFrequency, statIds));
        }
        return offset == parameters.Length;
    }

    /// <summary>ReadLeaderboardsNearPlayer: (pid, count, queries).</summary>
    public static bool TryParseNearPlayer(byte[] parameters, out uint pid, out uint count, out List<LeaderboardQuery> queries)
    {
        int offset = 0;
        queries = [];
        count = 0;
        return TryU32(parameters, ref offset, out pid) && TryU32(parameters, ref offset, out count) &&
               TryLeaderboardQueries(parameters, ref offset, out queries);
    }

    /// <summary>ReadLeaderboardsByRank: (startingRank, count, queries).</summary>
    public static bool TryParseByRank(byte[] parameters, out uint startingRank, out uint count, out List<LeaderboardQuery> queries) =>
        TryParseNearPlayer(parameters, out startingRank, out count, out queries);

    /// <summary>ReadLeaderboardsByPlayers: (pids, queries).</summary>
    public static bool TryParseByPlayers(byte[] parameters, out List<uint> pids, out List<LeaderboardQuery> queries)
    {
        int offset = 0;
        pids = [];
        queries = [];
        if (!TryU32List(parameters, ref offset, out uint[] asked) || asked.Length > 256)
            return false;
        pids.AddRange(asked);
        return TryLeaderboardQueries(parameters, ref offset, out queries);
    }

    /// <summary>
    /// The board a query ranks, top first, from Playforward.
    /// </summary>
    internal static IReadOnlyList<Ranked> Ranking(string titleId, LeaderboardQuery query)
    {
        Playforward.PlayforwardTitle? title = Playforward.PlayforwardTitles.ForTitle(titleId);
        if (title is null)
            return [];
        string board = ScoreBoard(SourceBoard(query.Board), query.Context);
        IReadOnlyList<Playforward.StoredScore>? scores;
        if (TopCache.TryGetValue((titleId, board), out var cached) && DateTimeOffset.UtcNow - cached.At < TopCacheFor)
            scores = cached.Scores;
        else
        {
            scores = title.Store.TopScores(board, RankDepth);
            if (scores is null)
            {
                Log.Warning("[progress] could not read the {Title} leaderboard {Board} from Playforward", titleId, board);
                return [];
            }
            TopCache[(titleId, board)] = (scores, DateTimeOffset.UtcNow);
        }
        var ranked = new List<Ranked>(scores.Count);
        foreach (Playforward.StoredScore score in scores)
        {
            if (score.Pid is <= 0 or > uint.MaxValue)
                continue;
            Playforward.PlayerNames.Remember(titleId, score.Pid, score.DisplayName);
            ranked.Add(new Ranked((uint)ranked.Count + 1, score.Score, (uint)score.Pid, score.DisplayName));
        }
        return ranked;
    }

    /// <summary>
    /// Builds the QList&lt;LeaderboardResult&gt; for a read.
    /// </summary>
    public static byte[] BuildLeaderboardResult(string titleId, IReadOnlyList<LeaderboardQuery> queries,
        Func<IReadOnlyList<Ranked>, IEnumerable<Ranked>> select, Func<uint, string> nameForPid, out int entriesListed)
    {
        entriesListed = 0;
        using var body = new MemoryStream();
        TicketGranting.WriteU32(body, (uint)queries.Count);
        foreach (LeaderboardQuery query in queries)
        {
            IReadOnlyList<Ranked> ranking = Ranking(titleId, query);
            List<Ranked> entries = select(ranking).ToList();
            entriesListed += entries.Count;

            TicketGranting.WriteU32(body, query.Board);
            TicketGranting.WriteU32(body, query.Context);
            TicketGranting.WriteU32(body, query.ResetFrequency);
            TicketGranting.WriteU32(body, (uint)ranking.Count);
            TicketGranting.WriteU32(body, (uint)entries.Count);
            foreach (Ranked entry in entries)
            {
                Row? row = Find(titleId, entry.Pid, SourceBoard(query.Board), query.Context);
                TicketGranting.WriteU32(body, entry.Pid);
                TicketGranting.WriteString(body, entry.Name.Length > 0 ? entry.Name : nameForPid(entry.Pid));
                FutureSoldierServices.WriteU64(body, FutureSoldierServices.QuazalDateTime(row?.SubmittedAt ?? DateTimeOffset.UnixEpoch));
                TicketGranting.WriteU32(body, (uint)query.StatIds.Length);
                foreach (uint statId in query.StatIds)
                {
                    TicketGranting.WriteU32(body, statId);
                    WriteVariant(body, statId == RankStat ? Variant.Int64(entry.Score)
                        : row?.Values.GetValueOrDefault(statId, Variant.Zero) ?? Variant.Zero);
                }
                TicketGranting.WriteU32(body, 0);   // rankStatus: ranked
                TicketGranting.WriteU32(body, entry.Rank);
                WriteVariant(body, Variant.Int64(entry.Score));
            }
        }
        return body.ToArray();
    }

    /// <summary>The <paramref name="count"/> entries centred on a player, or none when the player is unranked.</summary>
    public static IEnumerable<Ranked> Near(IReadOnlyList<Ranked> ranking, uint pid, uint count)
    {
        int index = -1;
        for (int i = 0; i < ranking.Count; i++)
            if (ranking[i].Pid == pid) { index = i; break; }
        if (index < 0 || count == 0)
            return [];
        int take = (int)Math.Min(count, (uint)ranking.Count);
        int start = Math.Clamp(index - take / 2, 0, ranking.Count - take);
        return ranking.Skip(start).Take(take);
    }

    /// <summary>The <paramref name="count"/> entries from a 1-based starting rank.</summary>
    public static IEnumerable<Ranked> FromRank(IReadOnlyList<Ranked> ranking, uint startingRank, uint count) =>
        ranking.Skip((int)Math.Max(startingRank, 1) - 1).Take((int)Math.Min(count, int.MaxValue));

    /// <summary>The entries of particular players, in the asked order; those unranked are absent.</summary>
    public static IEnumerable<Ranked> Of(IReadOnlyList<Ranked> ranking, IReadOnlyList<uint> pids) =>
        pids.Select(pid => ranking.FirstOrDefault(r => r.Pid == pid)).Where(r => r is not null)!;

    private static void WriteVariant(Stream body, Variant value)
    {
        body.WriteByte(value.Tag);
        switch (value.Tag)
        {
            case Variant.TagDouble:
                FutureSoldierServices.WriteU64(body, (ulong)BitConverter.DoubleToInt64Bits(value.Real));
                break;
            case Variant.TagBool:
                body.WriteByte(value.Integer != 0 ? (byte)1 : (byte)0);
                break;
            default:
                FutureSoldierServices.WriteU64(body, (ulong)value.Integer);
                break;
        }
    }

    /// <summary>
    /// A Variant off the wire (tag, then a value whose width the tag sets) or
    /// out of the blob (always 8 bytes after the tag). Only the numeric kinds
    /// the client has been seen sending (int64, double, bool, uint64) are
    /// accepted, a string or datetime Variant in a stat is a parse failure.
    /// </summary>
    private static bool TryVariant(byte[] data, ref int offset, out Variant value, bool fixedWidth)
    {
        value = default;
        if (offset >= data.Length)
            return false;
        byte tag = data[offset++];
        switch (tag)
        {
            case Variant.TagInt64:
            case Variant.TagUInt64:
                if (!TryU64(data, ref offset, out ulong bits)) return false;
                value = new Variant(tag, (long)bits, 0);
                return true;
            case Variant.TagDouble:
                if (!TryU64(data, ref offset, out ulong real)) return false;
                value = Variant.Double(BitConverter.Int64BitsToDouble((long)real));
                return true;
            case Variant.TagBool:
                if (fixedWidth)
                {
                    if (!TryU64(data, ref offset, out ulong flag)) return false;
                    value = new Variant(tag, (long)flag, 0);
                    return true;
                }
                if (offset >= data.Length) return false;
                value = new Variant(tag, data[offset++], 0);
                return true;
            default:
                return false;
        }
    }

    private static bool TryU32List(byte[] data, ref int offset, out uint[] values)
    {
        values = [];
        if (!TryU32(data, ref offset, out uint count) || count > 4096 || offset + count * 4 > data.Length)
            return false;
        values = new uint[count];
        for (uint i = 0; i < count; i++)
            TryU32(data, ref offset, out values[i]);
        return true;
    }

    private static bool TryU32(byte[] data, ref int offset, out uint value)
    {
        value = 0;
        if (offset + 4 > data.Length) return false;
        value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
        offset += 4;
        return true;
    }

    private static bool TryU64(byte[] data, ref int offset, out ulong value)
    {
        value = 0;
        if (offset + 8 > data.Length) return false;
        value = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset));
        offset += 8;
        return true;
    }
}
