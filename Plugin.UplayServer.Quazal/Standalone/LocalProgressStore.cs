using Plugin.UplayServer.Quazal.Playforward;

namespace Plugin.UplayServer.Quazal.Standalone;

/// <summary>
/// The player-data store for a title served without Playforward: the same
/// contract (<see cref="IProgressStore"/>) kept in files on this machine, so
/// every title's statistics and online-data code runs unchanged whichever
/// store is behind it.
/// <para>
/// One folder per title: <c>principals.json</c> (who has a PID),
/// <c>title.json</c> (title-wide values and counters), and
/// <c>players/&lt;pid&gt;.json</c> (one player's values and scores). The whole
/// title is read at start and served from memory; every change is written
/// through to the one file it touches, by replacing it whole, so a crash
/// mid-write leaves the previous copy rather than half of each.
/// </para>
/// This is one server's store. Two servers of one title pointed at different
/// folders have different players; that is what Playforward is for.
/// </summary>
internal sealed class LocalProgressStore : IProgressStore
{
    private sealed class Principal
    {
        public string AccountId { get; set; } = string.Empty;
        public uint Pid { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset LastSeen { get; set; }
    }

    private sealed class BlobEntry
    {
        public string Namespace { get; set; } = string.Empty;
        public string Key { get; set; } = string.Empty;
        public byte[] Payload { get; set; } = [];
    }

    private sealed class PlayerFile
    {
        public List<BlobEntry> Blobs { get; set; } = [];
        public Dictionary<string, long> Scores { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class TitleFile
    {
        public List<BlobEntry> Blobs { get; set; } = [];
        public Dictionary<string, uint> Counters { get; set; } = new(StringComparer.Ordinal);
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly Lock _gate = new();
    private readonly string _directory;
    private readonly string _titleId;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, Principal> _byAccount = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, Principal> _byPid = [];
    private readonly Dictionary<uint, PlayerFile> _players = [];
    private readonly TitleFile _title;
    private uint _nextPid;

    private LocalProgressStore(string directory, string titleId, uint pidStart, TimeProvider time, TitleFile title)
    {
        _directory = directory;
        _titleId = titleId;
        _time = time;
        _title = title;
        _nextPid = pidStart;
    }

    /// <summary>
    /// Opens a title's folder, creating it if need be, or returns null with
    /// the reason logged. A principals file that cannot be read stops the
    /// title rather than starting it empty: allocating from scratch would
    /// hand an existing player's PID to a stranger.
    /// </summary>
    public static LocalProgressStore? Open(string dataDirectory, string titleId, uint pidStart, TimeProvider? time = null)
    {
        string directory = Path.GetFullPath(Path.Combine(dataDirectory, titleId));
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "players"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Error("[standalone] {Title}: cannot create {Directory}: {Reason}", titleId, directory, exception.Message);
            return null;
        }

        if (!TryRead(Path.Combine(directory, "principals.json"), out List<Principal>? principals) ||
            !TryRead(Path.Combine(directory, "title.json"), out TitleFile? title))
        {
            Log.Error("[standalone] {Title}: refusing to start from {Directory}: fix or restore the file named above (a fresh start would reissue existing players' PIDs)",
                titleId, directory);
            return null;
        }

        var store = new LocalProgressStore(directory, titleId, pidStart, time ?? TimeProvider.System, title ?? new TitleFile());
        foreach (Principal principal in principals ?? [])
        {
            if (principal.Pid == 0 || string.IsNullOrEmpty(principal.AccountId) ||
                store._byPid.ContainsKey(principal.Pid) || store._byAccount.ContainsKey(principal.AccountId))
            {
                Log.Warning("[standalone] {Title}: skipped a duplicate or empty principal (pid={Pid}, account={Account})",
                    titleId, principal.Pid, principal.AccountId);
                continue;
            }
            store._byAccount[principal.AccountId] = principal;
            store._byPid[principal.Pid] = principal;
            store._nextPid = Math.Max(store._nextPid, principal.Pid + 1);
        }

        foreach (string file in Directory.EnumerateFiles(Path.Combine(directory, "players"), "*.json"))
        {
            if (!uint.TryParse(Path.GetFileNameWithoutExtension(file), out uint pid) || !store._byPid.ContainsKey(pid))
                continue;
            if (TryRead(file, out PlayerFile? player) && player is not null)
                store._players[pid] = player;
            else
                Quarantine(file);
        }

        Log.Information("[standalone] {Title}: {Players} player(s) known, {Saved} with saved progress, next pid {Next} ({Directory})",
            titleId, store._byPid.Count, store._players.Count, store._nextPid, directory);
        return store;
    }

    /// <summary>Whether this store gave out a PID.</summary>
    public bool Knows(uint pid)
    {
        lock (_gate)
            return _byPid.ContainsKey(pid);
    }

    /// <summary>The name a PID last logged in under, or null for a PID this store never gave out.</summary>
    public string? NameFor(uint pid)
    {
        lock (_gate)
            return _byPid.TryGetValue(pid, out Principal? principal) ? principal.DisplayName : null;
    }

    /// <summary>The PID an account already has, without allocating one.</summary>
    public uint? FindPid(string accountId)
    {
        lock (_gate)
            return _byAccount.TryGetValue(accountId, out Principal? principal) ? principal.Pid : null;
    }

    public int PlayerCount
    {
        get
        {
            lock (_gate)
                return _byPid.Count;
        }
    }

    public uint? ResolvePid(string accountId, string displayName)
    {
        lock (_gate)
        {
            DateTimeOffset now = _time.GetUtcNow();
            if (_byAccount.TryGetValue(accountId, out Principal? known))
            {
                known.DisplayName = displayName;
                known.LastSeen = now;
                // A name or a last-seen time is not worth failing a login over.
                WritePrincipals();
                return known.Pid;
            }

            if (_nextPid == 0 || _nextPid == uint.MaxValue)
            {
                Log.Error("[standalone] {Title}: out of PIDs", _titleId);
                return null;
            }
            var principal = new Principal
            {
                AccountId = accountId,
                Pid = _nextPid,
                DisplayName = displayName,
                CreatedAt = now,
                LastSeen = now,
            };
            _byAccount[accountId] = principal;
            _byPid[principal.Pid] = principal;
            if (!WritePrincipals())
            {
                // An unsaved PID would be handed to somebody else next run.
                _byAccount.Remove(accountId);
                _byPid.Remove(principal.Pid);
                return null;
            }
            _nextPid++;
            return principal.Pid;
        }
    }

    public IReadOnlyList<StoredBlob>? LoadPlayer(uint pid)
    {
        lock (_gate)
        {
            if (!_byPid.ContainsKey(pid))
                return null;
            return _players.TryGetValue(pid, out PlayerFile? player)
                ? player.Blobs.Select(b => new StoredBlob(b.Namespace, b.Key, b.Payload.ToArray())).ToList()
                : [];
        }
    }

    public bool SaveBlob(uint pid, string ns, string key, byte[] payload)
    {
        lock (_gate)
        {
            if (!_byPid.ContainsKey(pid))
                return false;
            PlayerFile player = PlayerOf(pid);
            Upsert(player.Blobs, ns, key, payload);
            return WritePlayer(pid, player);
        }
    }

    public IReadOnlyList<StoredBlob>? LoadTitle()
    {
        lock (_gate)
            return _title.Blobs.Select(b => new StoredBlob(b.Namespace, b.Key, b.Payload.ToArray())).ToList();
    }

    public bool SaveTitleBlob(string ns, string key, byte[] payload)
    {
        lock (_gate)
        {
            Upsert(_title.Blobs, ns, key, payload);
            return WriteTitle();
        }
    }

    public uint? NextCounter(string name)
    {
        lock (_gate)
        {
            _title.Counters.TryGetValue(name, out uint value);
            _title.Counters[name] = ++value;
            if (WriteTitle())
                return value;
            // Handing out a value that was not saved would reissue it next run.
            _title.Counters[name] = value - 1;
            return null;
        }
    }

    public bool SaveScore(uint pid, string board, long score) =>
        SaveScores(pid, new Dictionary<string, long> { [board] = score });

    public bool SaveScores(uint pid, IReadOnlyDictionary<string, long> scores)
    {
        lock (_gate)
        {
            if (!_byPid.ContainsKey(pid))
                return false;
            PlayerFile player = PlayerOf(pid);
            foreach ((string board, long score) in scores)
                player.Scores[board] = score;
            return WritePlayer(pid, player);
        }
    }

    public IReadOnlyList<StoredScore>? TopScores(string board, int limit)
    {
        lock (_gate)
        {
            return _players
                .Where(entry => entry.Value.Scores.ContainsKey(board))
                .Select(entry => (Pid: entry.Key, Score: entry.Value.Scores[board]))
                .OrderByDescending(entry => entry.Score).ThenBy(entry => entry.Pid)
                .Take(Math.Max(0, limit))
                .Select(entry => new StoredScore(entry.Pid, entry.Score, _byPid[entry.Pid].DisplayName))
                .ToList();
        }
    }

    public IReadOnlyList<StoredScore>? ScoresFor(string board, IReadOnlyCollection<uint> pids)
    {
        lock (_gate)
        {
            var found = new List<StoredScore>();
            foreach (uint pid in pids.Distinct())
            {
                if (_players.TryGetValue(pid, out PlayerFile? player) && player.Scores.TryGetValue(board, out long score))
                    found.Add(new StoredScore(pid, score, _byPid[pid].DisplayName));
            }
            return found;
        }
    }

    private PlayerFile PlayerOf(uint pid)
    {
        if (!_players.TryGetValue(pid, out PlayerFile? player))
            _players[pid] = player = new PlayerFile();
        return player;
    }

    private static void Upsert(List<BlobEntry> blobs, string ns, string key, byte[] payload)
    {
        BlobEntry? existing = blobs.FirstOrDefault(b => b.Namespace == ns && b.Key == key);
        if (existing is null)
            blobs.Add(new BlobEntry { Namespace = ns, Key = key, Payload = payload.ToArray() });
        else
            existing.Payload = payload.ToArray();
    }

    private bool WritePrincipals() =>
        Write(Path.Combine(_directory, "principals.json"), _byPid.Values.OrderBy(p => p.Pid).ToList());

    private bool WriteTitle() => Write(Path.Combine(_directory, "title.json"), _title);

    private bool WritePlayer(uint pid, PlayerFile player) =>
        Write(Path.Combine(_directory, "players", $"{pid}.json"), player);

    /// <summary>Replaces a file whole: written beside it, then moved over it.</summary>
    private bool Write<T>(string path, T value)
    {
        string temporary = path + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(value, Json));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Error("[standalone] {Title}: could not write {Path}: {Reason}", _titleId, path, exception.Message);
            return false;
        }
    }

    /// <summary>Reads a file, treating a missing one as empty; false only when it exists and cannot be read.</summary>
    private static bool TryRead<T>(string path, out T? value) where T : class
    {
        value = null;
        if (!File.Exists(path))
            return true;
        try
        {
            value = JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Json);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Error("[standalone] could not read {Path}: {Reason}", path, exception.Message);
            return false;
        }
    }

    /// <summary>Moves an unreadable player file aside, so the player starts over rather than the title failing.</summary>
    private static void Quarantine(string path)
    {
        string aside = $"{path}.unreadable-{DateTime.UtcNow:yyyyMMddHHmmss}";
        try
        {
            File.Move(path, aside);
            Log.Error("[standalone] moved unreadable {Path} to {Aside}; that player's progress starts over", path, aside);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Error("[standalone] could not move unreadable {Path} aside: {Reason}", path, exception.Message);
        }
    }
}
