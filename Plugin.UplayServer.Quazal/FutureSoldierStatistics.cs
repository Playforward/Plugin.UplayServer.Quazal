using System.Buffers.Binary;
using System.Collections.Concurrent;
using Serilog;

namespace Plugin.UplayServer.Quazal;

/// <summary>
/// Ghost Recon Future Soldier's Player Statistics service (protocol 108):
/// the store behind it and the wire shapes it speaks
/// </summary>
internal static class FutureSoldierStatistics
{
    /// <summary>The namespace a title's protocol 108 rows are stored under in Playforward.</summary>
    private const string BlobNamespace = "grfs-stats";

    /// <summary>The one key under it: a player's whole row set, in <see cref="Serialize"/>'s format.</summary>
    private const string RowsKey = "rows";

    /// <summary>The title-wide counter match ids (protocol 111 method 1) are drawn from.</summary>
    private const string MatchIdCounter = "grfs_match_id";

    /// <summary>A context key that matches any value in a query.</summary>
    public const uint AnyKey = 0xFFFFFFFF;

    /// <summary>As <see cref="OnlineDataStore"/>: a load that failed is not retried on every read.</summary>
    private static readonly TimeSpan PrimeRetryAfter = TimeSpan.FromSeconds(30);

    /// <summary>One row: its address and its values, each with the width it was written at.</summary>
    internal sealed class Row
    {
        public required byte Board { get; init; }
        public required uint[] Keys { get; init; }
        /// <summary>Column -> value bytes (4 or 8), in first-write order.</summary>
        public Dictionary<byte, byte[]> Values { get; } = [];
    }

    /// <summary>A player's rows, in first-write order, with their address index.</summary>
    private sealed class RowSet
    {
        public List<Row> Rows { get; } = [];
        public Dictionary<string, Row> ByAddress { get; } = new(StringComparer.Ordinal);

        public Row GetOrAdd(byte board, uint[] keys)
        {
            string address = Address(board, keys);
            if (!ByAddress.TryGetValue(address, out Row? row))
            {
                row = new Row { Board = board, Keys = keys };
                Rows.Add(row);
                ByAddress[address] = row;
            }
            return row;
        }

        private static string Address(byte board, uint[] keys) => $"{board}:{string.Join(',', keys)}";
    }

    /// <summary>What a query asks of one board.</summary>
    internal sealed record Query(byte Board, uint[] Keys, byte[] Columns);

    /// <summary>One value as a write carries it.</summary>
    internal sealed record Update(byte Board, uint[] Keys, byte Column, byte[] Value, byte Op);

    private static readonly ConcurrentDictionary<(string TitleId, uint Pid), RowSet> Players = [];

    /// <summary>Players whose stored rows have been loaded this session, or when a load last failed.</summary>
    private static readonly ConcurrentDictionary<(string TitleId, uint Pid), DateTimeOffset?> Primed = [];

    private static long _nextLocalMatchId = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Applies a write: every value is set on its row, creating the row on first sight.</summary>
    public static void Write(string titleId, uint pid, IReadOnlyList<Update> updates)
    {
        RowSet rows = RowsOf(titleId, pid);
        lock (rows)
        {
            foreach (Update update in updates)
            {
                if (update.Op != 2)
                    Log.Warning("[quazal] PlayerStatistics pid={Pid} board {Board} column {Column}: op {Op} is not the 'set' seen on the wire; applied as one",
                        pid, update.Board, update.Column, update.Op);
                rows.GetOrAdd(update.Board, update.Keys).Values[update.Column] = update.Value;
            }
            Persist(titleId, pid, rows);
        }
    }

    /// <summary>
    /// The rows a query lists for a player, at one value width: each row of
    /// a queried board whose keys the query matches, restricted to the
    /// queried columns of that width, and only when that leaves something.
    /// </summary>
    public static IReadOnlyList<(byte Board, uint[] Keys, IReadOnlyList<(byte Column, byte[] Value)> Values)> Read(
        string titleId, uint pid, IReadOnlyList<Query> queries, int width)
    {
        var listed = new List<(byte, uint[], IReadOnlyList<(byte, byte[])>)>();
        if (!Players.TryGetValue((titleId, pid), out RowSet? rows) && !TryPrime(titleId, pid, out rows))
            return listed;
        lock (rows)
        {
            foreach (Query query in queries)
            {
                foreach (Row row in rows.Rows)
                {
                    if (row.Board != query.Board || !Matches(query.Keys, row.Keys))
                        continue;
                    var values = new List<(byte, byte[])>();
                    foreach (byte column in query.Columns)
                        if (row.Values.TryGetValue(column, out byte[]? value) && value.Length == width)
                            values.Add((column, value));
                    if (values.Count > 0)
                        listed.Add((row.Board, row.Keys, values));
                }
            }
        }
        return listed;
    }

    /// <summary>Drops a player's cached rows, so the next read loads what Playforward now holds.</summary>
    public static void Forget(string titleId, uint pid)
    {
        Players.TryRemove((titleId, pid), out _);
        Primed.TryRemove((titleId, pid), out _);
    }

    /// <summary>Everything, for tests.</summary>
    public static void Clear()
    {
        Players.Clear();
        Primed.Clear();
    }

    /// <summary>
    /// A match id nobody else on the title has been given: from Playforward's
    /// counter when the title has one, else a local counter started at the
    /// clock so a restart does not reissue the last run's numbers. The working
    /// server's was a 32-bit number in a u64; the width is what matters.
    /// </summary>
    public static ulong NextMatchId(string titleId)
    {
        if (Playforward.PlayforwardTitles.ForTitle(titleId)?.Store.NextCounter(MatchIdCounter) is { } counted)
            return counted;
        return (ulong)Interlocked.Increment(ref _nextLocalMatchId);
    }

    private static bool Matches(uint[] wanted, uint[] actual)
    {
        if (wanted.Length != actual.Length)
            return false;
        for (int i = 0; i < wanted.Length; i++)
            if (wanted[i] != AnyKey && wanted[i] != actual[i])
                return false;
        return true;
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

    /// <summary>Loads a Playforward player's rows once per session; false when there is nothing to load from or the load failed.</summary>
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
            Log.Warning("[progress] could not load pid={Pid}'s {Title} statistics from Playforward; serving what this session holds", pid, titleId);
            return false;
        }
        var loaded = new RowSet();
        Playforward.StoredBlob? saved = blobs.FirstOrDefault(b => b.Namespace == BlobNamespace && b.Key == RowsKey);
        int rowCount = 0;
        if (saved is not null)
        {
            if (!TryDeserialize(saved.Payload, loaded))
                Log.Warning("[progress] pid={Pid}'s {Title} statistics blob could not be read; starting them over", pid, titleId);
            rowCount = loaded.Rows.Count;
        }
        rows = Players.GetOrAdd((titleId, pid), loaded);
        Primed[(titleId, pid)] = null;
        Log.Information("[progress] pid={Pid}'s {Title} statistics loaded from Playforward: {Rows} row(s)", pid, titleId, rowCount);
        return true;
    }

    private static void Persist(string titleId, uint pid, RowSet rows)
    {
        Playforward.IProgressStore? store = StoreFor(titleId, pid);
        if (store is not null && !store.SaveBlob(pid, BlobNamespace, RowsKey, Serialize(rows)))
            Log.Warning("[progress] could not save pid={Pid}'s {Title} statistics to Playforward; keeping them in memory for this session", pid, titleId);
    }

    private static byte[] Serialize(RowSet rows)
    {
        using var body = new MemoryStream();
        TicketGranting.WriteU32(body, (uint)rows.Rows.Count);
        foreach (Row row in rows.Rows)
        {
            WriteAddress(body, row.Board, row.Keys);
            TicketGranting.WriteU32(body, (uint)row.Values.Count);
            foreach ((byte column, byte[] value) in row.Values)
            {
                body.WriteByte(column);
                body.WriteByte((byte)value.Length);
                body.Write(value);
            }
        }
        return body.ToArray();
    }

    private static bool TryDeserialize(byte[] blob, RowSet into)
    {
        var reader = new Reader(blob);
        if (!reader.TryU32(out uint rowCount) || rowCount > 100_000)
            return false;
        for (uint r = 0; r < rowCount; r++)
        {
            if (!reader.TryAddress(out byte board, out uint[] keys) || !reader.TryU32(out uint valueCount) || valueCount > 256)
                return false;
            Row row = into.GetOrAdd(board, keys);
            for (uint v = 0; v < valueCount; v++)
            {
                if (!reader.TryU8(out byte column) || !reader.TryU8(out byte width) || width is not (4 or 8) ||
                    !reader.TryBytes(width, out byte[] value))
                    return false;
                row.Values[column] = value;
            }
        }
        return reader.AtEnd;
    }

    /// <summary>Parses a read (method 6 or 7): the queries and the pids they are about.</summary>
    public static bool TryParseRead(byte[] parameters, out List<Query> queries, out List<uint> pids)
    {
        queries = [];
        pids = [];
        var reader = new Reader(parameters);
        if (!reader.TryU32(out uint queryCount) || queryCount > 256)
            return false;
        for (uint q = 0; q < queryCount; q++)
        {
            if (!reader.TryAddress(out byte board, out uint[] keys) || !reader.TryU32(out uint columnCount) ||
                columnCount > 256 || !reader.TryBytes((int)columnCount, out byte[] columns))
                return false;
            queries.Add(new Query(board, keys, columns));
        }
        if (!reader.TryU32(out uint pidCount) || pidCount > 256)
            return false;
        for (uint p = 0; p < pidCount; p++)
        {
            if (!reader.TryU32(out uint pid)) return false;
            pids.Add(pid);
        }
        return reader.AtEnd;
    }

    /// <summary>Parses a write (method 1 or 2) of values <paramref name="width"/> bytes wide.</summary>
    public static bool TryParseWrite(byte[] parameters, int width, out List<Update> updates)
    {
        updates = [];
        var reader = new Reader(parameters);
        if (!reader.TryU32(out uint rowCount) || rowCount > 10_000)
            return false;
        for (uint r = 0; r < rowCount; r++)
        {
            if (!reader.TryAddress(out byte board, out uint[] keys) || !reader.TryU32(out uint valueCount) || valueCount > 256)
                return false;
            for (uint v = 0; v < valueCount; v++)
            {
                if (!reader.TryU8(out byte column) || !reader.TryBytes(width, out byte[] value) || !reader.TryU8(out byte op))
                    return false;
                updates.Add(new Update(board, keys, column, value, op));
            }
        }
        return reader.AtEnd;
    }

    /// <summary>Builds a read reply: one entry per asked pid, each with the rows the store lists for it.</summary>
    public static byte[] BuildReadResult(string titleId, IReadOnlyList<Query> queries, IReadOnlyList<uint> pids, int width)
    {
        using var body = new MemoryStream();
        TicketGranting.WriteU32(body, (uint)pids.Count);
        foreach (uint pid in pids)
        {
            var rows = Read(titleId, pid, queries, width);
            TicketGranting.WriteU32(body, pid);
            TicketGranting.WriteU32(body, (uint)rows.Count);
            foreach ((byte board, uint[] keys, IReadOnlyList<(byte Column, byte[] Value)> values) in rows)
            {
                WriteAddress(body, board, keys);
                TicketGranting.WriteU32(body, (uint)values.Count);
                foreach ((byte column, byte[] value) in values)
                {
                    body.WriteByte(column);
                    body.Write(value);
                }
            }
        }
        return body.ToArray();
    }

    private static void WriteAddress(Stream body, byte board, uint[] keys)
    {
        body.WriteByte(board);
        body.WriteByte((byte)keys.Length);
        TicketGranting.WriteU16(body, 0);
        body.WriteByte(0);
        foreach (uint key in keys)
            TicketGranting.WriteU32(body, key);
    }

    /// <summary>A bounds-checked cursor over the little-endian wire.</summary>
    private ref struct Reader(byte[] data)
    {
        private readonly byte[] _data = data;
        private int _offset;

        public readonly bool AtEnd => _offset == _data.Length;

        public bool TryU8(out byte value)
        {
            value = 0;
            if (_offset + 1 > _data.Length) return false;
            value = _data[_offset++];
            return true;
        }

        public bool TryU32(out uint value)
        {
            value = 0;
            if (_offset + 4 > _data.Length) return false;
            value = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(_offset));
            _offset += 4;
            return true;
        }

        public bool TryBytes(int count, out byte[] value)
        {
            value = [];
            if (count < 0 || _offset + count > _data.Length) return false;
            value = _data.AsSpan(_offset, count).ToArray();
            _offset += count;
            return true;
        }

        /// <summary>The row address: board, key count, two reserved zero bytes, a zero byte, the keys.</summary>
        public bool TryAddress(out byte board, out uint[] keys)
        {
            keys = [];
            if (!TryU8(out board) || !TryU8(out byte keyCount) || !TryBytes(3, out byte[] reserved) || keyCount > 8)
                return false;
            if (reserved[0] != 0 || reserved[1] != 0 || reserved[2] != 0)
                return false;
            keys = new uint[keyCount];
            for (int k = 0; k < keyCount; k++)
                if (!TryU32(out keys[k])) return false;
            return true;
        }
    }
}
