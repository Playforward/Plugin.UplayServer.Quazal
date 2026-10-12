using System.Collections.Concurrent;

namespace Plugin.UplayServer.Quazal.Playforward;

/// <summary>
/// One stored value: the game's own bytes under a namespace and key.
/// </summary>
internal sealed record StoredBlob(string Namespace, string Key, byte[] Payload);

/// <summary>One player's entry on a board, with the name they last logged in under.</summary>
internal sealed record StoredScore(long Pid, long Score, string DisplayName);

/// <summary>
/// Where a player's identity and progress live between sessions -- and, since
/// two servers of one title must see the same player, where they live
/// <em>outside</em> this process.
/// </summary>
internal interface IProgressStore
{
    /// <summary>The PID this title knows an account by, allocated on first sight; null if unreachable.</summary>
    uint? ResolvePid(string accountId, string displayName);

    /// <summary>Everything stored for a player; null if unreachable.</summary>
    IReadOnlyList<StoredBlob>? LoadPlayer(uint pid);

    bool SaveBlob(uint pid, string ns, string key, byte[] payload);

    /// <summary>Title-wide values every server of the title shares; null if unreachable.</summary>
    IReadOnlyList<StoredBlob>? LoadTitle();

    bool SaveTitleBlob(string ns, string key, byte[] payload);

    /// <summary>The next value of a title-wide counter, starting at 1; null if unreachable.</summary>
    uint? NextCounter(string name);

    /// <summary>
    /// Records a player's number on a board 
    /// </summary>
    bool SaveScore(uint pid, string board, long score);

    /// <summary>Records several of a player's numbers at once, all or none.</summary>
    bool SaveScores(uint pid, IReadOnlyDictionary<string, long> scores);

    /// <summary>A board's top entries, highest first; null if unreachable.</summary>
    IReadOnlyList<StoredScore>? TopScores(string board, int limit);

    /// <summary>The board entries of particular players (absent when they have none); null if unreachable.</summary>
    IReadOnlyList<StoredScore>? ScoresFor(string board, IReadOnlyCollection<uint> pids);
}

/// <summary>
/// The store as Playforward provides it
/// </summary>
internal sealed class PlayforwardProgressStore : IProgressStore
{
    private readonly PlayforwardServerClient _client;

    public PlayforwardProgressStore(PlayforwardServerClient client) => _client = client;

    public uint? ResolvePid(string accountId, string displayName) =>
        ToPid(_client.ResolvePrincipalAsync(accountId, displayName, CancellationToken.None).GetAwaiter().GetResult());

    public IReadOnlyList<StoredBlob>? LoadPlayer(uint pid) =>
        _client.LoadPlayerBlobsAsync(pid, CancellationToken.None).GetAwaiter().GetResult();

    public bool SaveBlob(uint pid, string ns, string key, byte[] payload) =>
        _client.PutPlayerBlobAsync(pid, ns, key, payload, CancellationToken.None).GetAwaiter().GetResult();

    public IReadOnlyList<StoredBlob>? LoadTitle() =>
        _client.LoadTitleBlobsAsync(CancellationToken.None).GetAwaiter().GetResult();

    public bool SaveTitleBlob(string ns, string key, byte[] payload) =>
        _client.PutTitleBlobAsync(ns, key, payload, CancellationToken.None).GetAwaiter().GetResult();

    public uint? NextCounter(string name) =>
        ToPid(_client.NextCounterAsync(name, CancellationToken.None).GetAwaiter().GetResult());

    public bool SaveScore(uint pid, string board, long score) =>
        _client.PutScoreAsync(pid, board, score, CancellationToken.None).GetAwaiter().GetResult();

    public bool SaveScores(uint pid, IReadOnlyDictionary<string, long> scores) =>
        _client.PutScoresAsync(pid, scores, CancellationToken.None).GetAwaiter().GetResult();

    public IReadOnlyList<StoredScore>? TopScores(string board, int limit) =>
        _client.TopScoresAsync(board, limit, CancellationToken.None).GetAwaiter().GetResult();

    public IReadOnlyList<StoredScore>? ScoresFor(string board, IReadOnlyCollection<uint> pids) =>
        _client.ScoresForAsync(board, pids, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// Playforward's numbers are 64-bit
    /// </summary>
    private static uint? ToPid(long? value)
    {
        if (value is null)
            return null;
        if (value is < 1 or > uint.MaxValue)
        {
            Log.Error("[playforward] Playforward answered {Value}, which does not fit this title's 32-bit ids", value);
            return null;
        }
        return (uint)value.Value;
    }
}

/// <summary>
/// A store held in this process, for the test harness's self-test and the
/// unit tests.
/// </summary>
internal sealed class InMemoryProgressStore : IProgressStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, uint> _pids = new(StringComparer.Ordinal);
    private readonly Dictionary<(uint Pid, string Ns, string Key), byte[]> _blobs = [];
    private readonly Dictionary<(string Ns, string Key), byte[]> _titleBlobs = [];
    private readonly Dictionary<string, uint> _counters = new(StringComparer.Ordinal);
    private readonly Dictionary<(uint Pid, string Board), long> _scores = [];
    private readonly Dictionary<uint, string> _names = [];
    private uint _nextPid;

    public InMemoryProgressStore(uint pidStart = 2_000_000) => _nextPid = pidStart;

    /// <summary>When false, every call answers as an unreachable Playforward would.</summary>
    public bool Reachable { get; set; } = true;

    public uint? ResolvePid(string accountId, string displayName)
    {
        if (!Reachable) return null;
        lock (_gate)
        {
            if (!_pids.TryGetValue(accountId, out uint pid))
                _pids[accountId] = pid = _nextPid++;
            _names[pid] = displayName;
            return pid;
        }
    }

    /// <summary>Playforward's reset: the values go, the pid stays.</summary>
    public void ResetProgress(string accountId)
    {
        lock (_gate)
        {
            if (!_pids.TryGetValue(accountId, out uint pid)) return;
            foreach (var key in _blobs.Keys.Where(k => k.Pid == pid).ToList())
                _blobs.Remove(key);
            foreach (var key in _scores.Keys.Where(k => k.Pid == pid).ToList())
                _scores.Remove(key);
        }
    }

    /// <summary>Playforward's forget: values and pid; the pid is retired.</summary>
    public void DeletePlayer(string accountId)
    {
        lock (_gate)
        {
            if (!_pids.Remove(accountId, out uint pid)) return;
            foreach (var key in _blobs.Keys.Where(k => k.Pid == pid).ToList())
                _blobs.Remove(key);
            foreach (var key in _scores.Keys.Where(k => k.Pid == pid).ToList())
                _scores.Remove(key);
        }
    }

    public IReadOnlyList<StoredBlob>? LoadPlayer(uint pid)
    {
        if (!Reachable) return null;
        lock (_gate)
        {
            if (!_pids.ContainsValue(pid)) return null;
            return _blobs.Where(e => e.Key.Pid == pid)
                .Select(e => new StoredBlob(e.Key.Ns, e.Key.Key, e.Value)).ToList();
        }
    }

    public bool SaveBlob(uint pid, string ns, string key, byte[] payload)
    {
        if (!Reachable) return false;
        lock (_gate)
        {
            if (!_pids.ContainsValue(pid)) return false;
            _blobs[(pid, ns, key)] = payload;
            return true;
        }
    }

    public IReadOnlyList<StoredBlob>? LoadTitle()
    {
        if (!Reachable) return null;
        lock (_gate)
            return _titleBlobs.Select(e => new StoredBlob(e.Key.Ns, e.Key.Key, e.Value)).ToList();
    }

    public bool SaveTitleBlob(string ns, string key, byte[] payload)
    {
        if (!Reachable) return false;
        lock (_gate)
            _titleBlobs[(ns, key)] = payload;
        return true;
    }

    public bool SaveScore(uint pid, string board, long score)
    {
        if (!Reachable) return false;
        lock (_gate)
        {
            if (!_pids.ContainsValue(pid)) return false;
            _scores[(pid, board)] = score;
            return true;
        }
    }

    public bool SaveScores(uint pid, IReadOnlyDictionary<string, long> scores)
    {
        if (!Reachable) return false;
        lock (_gate)
        {
            if (!_pids.ContainsValue(pid)) return false;
            foreach ((string board, long score) in scores)
                _scores[(pid, board)] = score;
            return true;
        }
    }

    /// <summary>A player's number on a board, as Playforward would hold it; null when there is none.</summary>
    public long? Score(uint pid, string board)
    {
        lock (_gate)
            return _scores.TryGetValue((pid, board), out long score) ? score : null;
    }

    public IReadOnlyList<StoredScore>? TopScores(string board, int limit)
    {
        if (!Reachable) return null;
        lock (_gate)
            return _scores.Where(e => e.Key.Board == board)
                .OrderByDescending(e => e.Value).ThenBy(e => e.Key.Pid)
                .Take(limit)
                .Select(e => new StoredScore(e.Key.Pid, e.Value, _names.GetValueOrDefault(e.Key.Pid, "")))
                .ToList();
    }

    public IReadOnlyList<StoredScore>? ScoresFor(string board, IReadOnlyCollection<uint> pids)
    {
        if (!Reachable) return null;
        lock (_gate)
            return pids.Where(pid => _scores.ContainsKey((pid, board)))
                .Select(pid => new StoredScore(pid, _scores[(pid, board)], _names.GetValueOrDefault(pid, "")))
                .ToList();
    }

    public uint? NextCounter(string name)
    {
        if (!Reachable) return null;
        lock (_gate)
        {
            _counters.TryGetValue(name, out uint value);
            _counters[name] = ++value;
            return value;
        }
    }

    public int BlobCount => _blobs.Count;
}
