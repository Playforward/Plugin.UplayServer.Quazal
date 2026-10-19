using System.Collections.Concurrent;
using System.Text;

namespace Plugin.UplayServer.Quazal;

/// <summary>
/// Backing store for FC3's online-data service (protocol 106), plus the column
/// vocabulary learned at runtime.
/// </summary>
internal static class OnlineDataStore
{
    /// <summary>The namespace this title's protocol 106 rows are stored under.</summary>
    private const string BlobNamespace = "onlinedata";

    /// <summary>The title-wide namespace for the learned column vocabulary, one key per data id.</summary>
    private const string ColumnsNamespace = "onlinedata-columns";

    /// <summary>The title-wide counter wall message ids are drawn from.</summary>
    private const string WallMessageCounter = "wall_message_id";

    /// <summary>
    /// How long to wait before asking Playforward again for a player it could
    /// not load. Long enough that an outage does not turn every wall poll into
    /// a timed-out request inside the PRUDP handler; short enough that a
    /// player is not stuck with an empty wall after a blip.
    /// </summary>
    private static readonly TimeSpan PrimeRetryAfter = TimeSpan.FromSeconds(30);

    private static readonly ConcurrentDictionary<(uint Pid, uint DataId), OnlineDataRowSet> Rows = [];

    /// <summary>Column names seen for each data id, from either direction.</summary>
    private static readonly ConcurrentDictionary<uint, HashSet<string>> Vocabulary = [];

    /// <summary>Players whose stored rows have been loaded this session, or when a load last failed.</summary>
    private static readonly ConcurrentDictionary<uint, DateTimeOffset?> Primed = [];

    /// <summary>
    /// Everything else a title keeps per player
    /// </summary>
    private static readonly ConcurrentDictionary<(uint Pid, string Ns, string Key), byte[]> Blobs = [];

    private static Playforward.IProgressStore? _store;
    private static Func<uint, bool> _isStored = _ => false;

    /// <summary>
    /// The one title whose protocol 106 rows this is. Online data is Far Cry
    /// 3's service and this store is keyed by pid alone, so it serves exactly
    /// one title
    /// </summary>
    private static string _titleId = string.Empty;

    public static string TitleId => _titleId;

    /// <summary>
    /// Identifier handed to wall rows the server completes. A real server
    /// assigns row identifiers; leaving `id`/`msg_id` at zero gives FC3 a row
    /// it cannot acknowledge (it filters the 0x0A table by msg_id). Drawn from
    /// a title-wide counter when a store is attached, so two servers cannot
    /// issue the same id; this is the fallback.
    /// </summary>
    private static uint _nextWallMessageId = 1;

    public static byte[] EmptyRowSet => [0x00, 0x00, 0x00, 0x00];

    /// <summary>
    /// Puts the store behind the cache. The learned vocabulary is loaded now,
    /// because wall-row completion needs it before any query has taught it;
    /// a player's rows are loaded when that player is first asked about.
    /// </summary>
    public static void Attach(string titleId, Playforward.IProgressStore store, Func<uint, bool> isStored)
    {
        _titleId = titleId;
        _store = store;
        _isStored = isStored;

        IReadOnlyList<Playforward.StoredBlob>? title = store.LoadTitle();
        if (title is null)
        {
            Log.Warning("[progress] could not load this title's learned columns from {Store}; they will be relearned from the wire", Standalone.PlayerStores.Describe(titleId));
            return;
        }
        int loaded = 0;
        foreach (Playforward.StoredBlob blob in title.Where(b => b.Namespace == ColumnsNamespace))
        {
            if (!TryParseDataId(blob.Key, out uint dataId)) continue;
            var known = Vocabulary.GetOrAdd(dataId, _ => []);
            lock (known)
                foreach (string name in Encoding.UTF8.GetString(blob.Payload).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    if (known.Add(name)) loaded++;
        }
        Log.Information("[progress] online-data store attached to {Store}: {Columns} learned column name(s) restored", Standalone.PlayerStores.Describe(titleId), loaded);
    }

    /// <summary>Drops the store and everything cached from it.</summary>
    public static void Detach()
    {
        _store = null;
        _isStored = _ => false;
        _titleId = string.Empty;
        Rows.Clear();
        Blobs.Clear();
        Vocabulary.Clear();
        Primed.Clear();
        TopCache.Clear();
        Playforward.PlayerNames.Clear();
        PlayerStatistics.Clear();
        Fc3MatchLedger.Clear();
    }

    /// <summary>A per-player value outside the online-data rows, or null if none is held.</summary>
    public static byte[]? GetBlob(uint pid, string ns, string key)
    {
        EnsurePrimed(pid);
        return Blobs.TryGetValue((pid, ns, key), out byte[]? value) ? value : null;
    }

    /// <summary>Stores a per-player value outside the online-data rows, writing it through.</summary>
    public static void PutBlob(uint pid, string ns, string key, byte[] value)
    {
        EnsurePrimed(pid);
        Blobs[(pid, ns, key)] = value;
        if (_store is not null && _isStored(pid) && !_store.SaveBlob(pid, ns, key, value))
            Log.Warning("[progress] could not save pid={Pid} {Namespace}/{Key} to Playforward; keeping it in memory for this session",
                pid, ns, key);
    }

    /// <summary>
    /// Forgets a PID's cached rows, so the next fetch reads what Playforward
    /// now holds. Used when a player's binding ends and when Playforward says
    /// their data was reset or deleted.
    /// </summary>
    public static void Forget(uint pid)
    {
        foreach (var key in Rows.Keys.Where(key => key.Pid == pid).ToList())
            Rows.TryRemove(key, out _);
        foreach (var key in Blobs.Keys.Where(key => key.Pid == pid).ToList())
            Blobs.TryRemove(key, out _);
        Primed.TryRemove(pid, out _);
        PlayerStatistics.Forget(pid);
    }

    /// <summary>
    /// Records a player's numbers on title-wide boards, all or none
    /// (write-through only; nothing is held here). The one thing Playforward
    /// ranks across players, so a leaderboard
    /// </summary>
    public static void PutScores(uint pid, IReadOnlyDictionary<string, long> scores)
    {
        if (_store is not null && _isStored(pid) && !_store.SaveScores(pid, scores))
            Log.Warning("[progress] could not save pid={Pid}'s {Count} scores to Playforward; the leaderboards will lag",
                pid, scores.Count);
    }

    /// <summary>
    /// A board's top entries from Playforward, highest first,
    /// </summary>
    public static IReadOnlyList<Playforward.StoredScore>? TopScores(string board, int limit)
    {
        if (_store is null) return null;
        if (TopCache.TryGetValue((board, limit), out var cached) && DateTime.UtcNow - cached.At < TopCacheFor)
            return cached.Scores;
        IReadOnlyList<Playforward.StoredScore>? scores = _store.TopScores(board, limit);
        if (scores is null) return null;
        foreach (Playforward.StoredScore score in scores)
            Playforward.PlayerNames.Remember(_titleId, score.Pid, score.DisplayName);
        TopCache[(board, limit)] = (scores, DateTime.UtcNow);
        return scores;
    }

    /// <summary>The board entries of particular players from Playforward; null when it did not answer.</summary>
    public static IReadOnlyList<Playforward.StoredScore>? ScoresFor(string board, IReadOnlyCollection<uint> pids)
    {
        IReadOnlyList<Playforward.StoredScore>? scores = _store?.ScoresFor(board, pids);
        if (scores is null) return null;
        foreach (Playforward.StoredScore score in scores)
            Playforward.PlayerNames.Remember(_titleId, score.Pid, score.DisplayName);
        return scores;
    }

    private static readonly ConcurrentDictionary<(string Board, int Limit), (IReadOnlyList<Playforward.StoredScore> Scores, DateTime At)> TopCache = [];
    private static readonly TimeSpan TopCacheFor = TimeSpan.FromSeconds(5);

    public static uint NextWallMessageId()
    {
        if (_store?.NextCounter(WallMessageCounter) is { } value)
            return value;
        return Interlocked.Increment(ref _nextWallMessageId) - 1;
    }

    /// <summary>The title-wide counter row ids (a match's id, a statistics row's) are drawn from.</summary>
    private const string RowIdCounter = "row_id";
    private static int _nextRowId = 1;

    /// <summary>
    /// The next server-assigned row id, unique across every server of the
    /// title: the host uses the id a match store came back with as the
    /// <c>match_id</c> of every statistics row it uploads for it, so two
    /// servers issuing the same one would file two matches as one.
    /// </summary>
    public static uint NextRowId()
    {
        if (_store?.NextCounter(RowIdCounter) is { } value)
            return RowIdBase + value;
        return RowIdBase + (uint)(Interlocked.Increment(ref _nextRowId) - 1);
    }

    /// <summary>
    /// Row ids start well above anything another server has handed a client,
    /// so that a match id from here is never mistaken for one from there
    /// </summary>
    private const uint RowIdBase = 1_000_000;

    /// <summary>
    /// Loads a Playforward player's stored rows into memory, once per session.
    /// A load that failed is not retried until <see cref="PrimeRetryAfter"/>
    /// has passed: the fetch this sits inside is a wall poll every few
    /// seconds, and each retry is a request that waits out a timeout.
    /// </summary>
    private static void EnsurePrimed(uint pid)
    {
        if (_store is null || !_isStored(pid))
            return;
        if (Primed.TryGetValue(pid, out DateTimeOffset? failedAt) &&
            (failedAt is null || DateTimeOffset.UtcNow - failedAt < PrimeRetryAfter))
            return;

        IReadOnlyList<Playforward.StoredBlob>? blobs = _store.LoadPlayer(pid);
        if (blobs is null)
        {
            Primed[pid] = DateTimeOffset.UtcNow;
            Log.Warning("[progress] could not load pid={Pid}'s saved progress from Playforward; serving what this session has and retrying later", pid);
            return;
        }

        int restored = 0;
        foreach (Playforward.StoredBlob blob in blobs)
        {
            if (blob.Namespace != BlobNamespace)
            {
                if (Blobs.TryAdd((pid, blob.Namespace, blob.Key), blob.Payload))
                    restored++;
                continue;
            }
            if (!TryParseDataId(blob.Key, out uint dataId)) continue;
            int offset = 0;
            if (!OnlineDataRowSet.TryParse(blob.Payload, ref offset, out OnlineDataRowSet rowSet))
            {
                Log.Warning("[progress] stored online data pid={Pid} dataId=0x{DataId:X2} ({Length} bytes) did not parse; ignoring it",
                    pid, dataId, blob.Payload.Length);
                continue;
            }
            if (Rows.TryAdd((pid, dataId), rowSet))
                restored++;
        }
        Primed[pid] = null;
        Log.Information("[progress] restored pid={Pid}'s saved progress: {Count} value(s)", pid, restored);
    }

    private static string KeyFor(uint dataId) => $"0x{dataId:X2}";

    private static bool TryParseDataId(string key, out uint dataId) =>
        uint.TryParse(key.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? key[2..] : key,
            System.Globalization.NumberStyles.HexNumber, null, out dataId);

    /// <summary>
    /// The interned column-id table the online-data service shares across data
    /// ids, read out of FC3_d3d11.dll: sixteen literals populating the
    /// contiguous statics at 0x11e4365c-0x11e43690, plus `type` (0x114e8480),
    /// `data` (0x1157f9cc) and `msg_id` (0x1157f9d4). These are strings found in
    /// the binary, not names guessed to fit — the wall service has no ColIds of
    /// its own and borrows from this table, which is why several never appear on
    /// the wire at all.
    ///
    /// The wall consumer (FUN_10705082) requires exactly seven of them —
    /// `id`, `pid`, `unixtimestamp`, `type`, `data`, `blobData`, `blobCrc` — and
    /// drops a row silently if any is absent. FC3's own upload carries three, so
    /// the rest have to come from here. See docs/60 Addendum 4.
    /// </summary>
    public static readonly string[] InternedColumns =
    [
        "id", "unixtimestamp", "pid", "name", "client_id", "match_id",
        "weapon_id", "challenge_id", "group_id", "value", "blobData",
        "blobSize", "blobCrc", "xml", "type", "data", "msg_id",
    ];

    public static void Store(uint pid, uint dataId, OnlineDataRowSet rowSet)
    {
        EnsurePrimed(pid);
        Rows[(pid, dataId)] = rowSet;
        if (_store is not null && _isStored(pid) && !_store.SaveBlob(pid, BlobNamespace, KeyFor(dataId), rowSet.ToBytes()))
            Log.Warning("[progress] could not save pid={Pid} dataId=0x{DataId:X2} to Playforward; keeping it in memory for this session",
                pid, dataId);
    }

    /// <summary>
    /// Records that FC3 cleared the slot. An empty set is stored rather than
    /// the entry removed: "cleared" and "never stored" answer a fetch
    /// differently, because only the latter falls through to a title seed.
    /// </summary>
    public static void Clear(uint pid, uint dataId) =>
        Store(pid, dataId, new OnlineDataRowSet());

    public static OnlineDataRowSet? Fetch(uint pid, uint dataId)
    {
        EnsurePrimed(pid);
        return Rows.TryGetValue((pid, dataId), out OnlineDataRowSet? rowSet) ? rowSet : null;
    }

    public static string DescribeStored(uint pid)
    {
        var ids = Rows.Where(entry => entry.Key.Pid == pid && entry.Value.Rows.Count > 0)
            .Select(entry => $"0x{entry.Key.DataId:X2}").OrderBy(text => text).ToList();
        return ids.Count > 0 ? string.Join(", ", ids) : "none";
    }

    /// <summary>
    /// Records column names for a data id. Returns the names that had not been
    /// seen before, so growth of the vocabulary can be logged instead of the
    /// same list being repeated every poll.
    /// </summary>
    public static List<string> Learn(uint dataId, IEnumerable<string> names)
    {
        var known = Vocabulary.GetOrAdd(dataId, _ => []);
        var fresh = new List<string>();
        lock (known)
        {
            foreach (string name in names.Where(name => !string.IsNullOrEmpty(name)))
                if (known.Add(name))
                    fresh.Add(name);
        }
        if (fresh.Count > 0 && _store is not null)
        {
            string[] all;
            lock (known)
                all = [.. known];
            if (!_store.SaveTitleBlob(ColumnsNamespace, KeyFor(dataId), Encoding.UTF8.GetBytes(string.Join('\n', all))))
                Log.Warning("[progress] could not save the learned columns for dataId=0x{DataId:X2} to Playforward", dataId);
        }
        return fresh;
    }

    public static IReadOnlyCollection<string> KnownColumns(uint dataId)
    {
        if (!Vocabulary.TryGetValue(dataId, out HashSet<string>? known))
            return [];
        lock (known)
            return [.. known];
    }

    /// <summary>
    /// Every column name FC3 has named on the wire, for any data id. FC3's wall
    /// consumer looks its columns up by name, so a row carrying names it does
    /// not want is harmless while a row missing one it does want is silently
    /// dropped. That asymmetry makes a superset the right answer: serving every
    /// harvested name costs nothing and needs no invented identifiers.
    /// </summary>
    public static IReadOnlyCollection<string> AllKnownColumns()
    {
        var union = new HashSet<string>(InternedColumns);
        foreach (var entry in Vocabulary)
            lock (entry.Value)
                union.UnionWith(entry.Value);
        return union;
    }
}
