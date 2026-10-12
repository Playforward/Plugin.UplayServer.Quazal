using System.Collections.Concurrent;

namespace Plugin.UplayServer.Quazal;

/// <summary>
/// Real shared state for legacy GameSession (protocol 42). Before this existed,
/// CreateSession handed out a session id but AddParticipants/GetParticipants/
/// GetParticipantCount/SearchSessions never recorded or reported anything real
/// so two peers could never see each other's session no matter what the
/// client did. Confirmed 2026-08-28 from a captured AddParticipants request:
///
///   u32 typeId | u32 sessionId | u32 count | (u32 pid | u32 unknown){count} | u8 trailing
/// </summary>
internal sealed record SessionSnapshot(
    string TitleId,
    uint TypeId,
    uint SessionId,
    uint HostPid,
    IReadOnlyList<uint> Participants,
    IReadOnlyDictionary<uint, int> Properties);

/// <summary>
/// Every session and every pid here belongs to a title. Pids are not unique
/// across titles
/// </summary>
internal static class GameSessionStore
{
    private sealed class Session
    {
        public required string TitleId { get; init; }
        public required uint TypeId { get; init; }
        public required uint SessionId { get; init; }
        public uint HostPid { get; set; }
        public HashSet<uint> Participants { get; } = [];
        public Dictionary<uint, int> Properties { get; } = [];
    }

    private static readonly ConcurrentDictionary<(string TitleId, uint TypeId, uint SessionId), Session> _sessions = [];
    private static readonly ConcurrentDictionary<(string TitleId, uint Pid), string[]> _stationUrlsByPid = [];

    /// <summary>
    /// A session's key. Titles compare case-insensitively everywhere else
    /// here, so the key folds case to agree with them.
    /// </summary>
    private static (string TitleId, uint TypeId, uint SessionId) Key(string titleId, uint typeId, uint sessionId) =>
        (titleId.ToLowerInvariant(), typeId, sessionId);

    private static (string TitleId, uint TypeId, uint SessionId) KeyOf(Session session) =>
        Key(session.TitleId, session.TypeId, session.SessionId);

    public static void CreateSession(string titleId, uint typeId, uint sessionId, uint hostPid) =>
        _sessions[Key(titleId, typeId, sessionId)] = new Session
        {
            TitleId = titleId,
            TypeId = typeId,
            SessionId = sessionId,
            HostPid = hostPid,
        };

    public static bool Exists(string titleId, uint typeId, uint sessionId) =>
        _sessions.ContainsKey(Key(titleId, typeId, sessionId));

    /// <summary>Whether <paramref name="pid"/> hosts this session.</summary>
    public static bool IsHost(string titleId, uint typeId, uint sessionId, uint pid) =>
        _sessions.TryGetValue(Key(titleId, typeId, sessionId), out Session? session) && session.HostPid == pid;

    /// <summary>Whether <paramref name="pid"/> hosts or plays in this session.</summary>
    public static bool IsMember(string titleId, uint typeId, uint sessionId, uint pid)
    {
        if (!_sessions.TryGetValue(Key(titleId, typeId, sessionId), out Session? session))
            return false;
        if (session.HostPid == pid)
            return true;
        lock (session.Participants)
            return session.Participants.Contains(pid);
    }

    /// <summary>
    /// Whether <paramref name="caller"/> may add or remove
    /// <paramref name="pids"/> in an existing session: its host may seat or
    /// remove anybody, anyone else only themselves.
    /// </summary>
    public static bool MayChangeRoster(string titleId, uint typeId, uint sessionId, uint caller, IReadOnlyCollection<uint> pids) =>
        Exists(titleId, typeId, sessionId) &&
        (pids.All(pid => pid == caller) || IsHost(titleId, typeId, sessionId, caller));

    /// <summary>
    /// Host migration: a new session under <paramref name="newHost"/> with the
    /// old one's other players and properties; the old one stays until its
    /// players leave it, which the client does next. Returns false, creating
    /// nothing, when there is no such session or the new host is not in it.
    /// </summary>
    public static bool MigrateSession(string titleId, uint typeId, uint sessionId, uint newSessionId, uint newHost)
    {
        if (!_sessions.TryGetValue(Key(titleId, typeId, sessionId), out Session? old) ||
            !IsMember(titleId, typeId, sessionId, newHost))
            return false;

        var migrated = new Session { TitleId = titleId, TypeId = typeId, SessionId = newSessionId, HostPid = newHost };
        lock (old.Participants)
            foreach (uint pid in old.Participants) migrated.Participants.Add(pid);
        lock (old.Properties)
            foreach ((uint id, int value) in old.Properties) migrated.Properties[id] = value;
        migrated.Participants.Add(newHost);
        _sessions[Key(titleId, typeId, newSessionId)] = migrated;
        return true;
    }

    /// <summary>Stores the peer endpoints published through RegisterURLs.</summary>
    public static void SetStationUrls(string titleId, uint pid, IReadOnlyList<string> urls) =>
        _stationUrlsByPid[(titleId, pid)] = urls.ToArray();

    public static void DeleteSession(string titleId, uint typeId, uint sessionId) =>
        _sessions.TryRemove(Key(titleId, typeId, sessionId), out _);

    /// <summary>
    /// Puts players into a session, and takes them out of every other one.
    /// </summary>
    public static void AddParticipants(string titleId, uint typeId, uint sessionId, IReadOnlyCollection<uint> pids)
    {
        Session session = _sessions.GetOrAdd(Key(titleId, typeId, sessionId),
            key => new Session { TitleId = titleId, TypeId = key.TypeId, SessionId = key.SessionId });
        lock (session.Participants)
        {
            foreach (uint pid in pids)
                session.Participants.Add(pid);
        }

        foreach (Session other in Of(titleId))
        {
            if (ReferenceEquals(other, session))
                continue;

            foreach (uint pid in pids)
            {
                bool evicted;
                lock (other.Participants)
                    evicted = other.Participants.Remove(pid);

                if (evicted)
                {
                    Log.Information(
                        "[quazal] pid={Pid} joined session {TypeId}-{SessionId}, so it left {OtherType}-{OtherSession}",
                        pid, typeId, sessionId, other.TypeId, other.SessionId);

                    DropIfEmpty(other);
                }
            }
        }
    }

    public static void RemoveParticipants(string titleId, uint typeId, uint sessionId, IReadOnlyCollection<uint> pids)
    {
        if (!_sessions.TryGetValue(Key(titleId, typeId, sessionId), out Session? session))
            return;
        lock (session.Participants)
        {
            foreach (uint pid in pids)
                session.Participants.Remove(pid);
        }
        DropIfEmpty(session);
    }

    /// <summary>
    /// Tears down a session the moment its last player leaves.
    /// </summary>
    private static void DropIfEmpty(Session session)
    {
        lock (session.Participants)
        {
            if (session.Participants.Count > 0)
                return;
        }

        if (_sessions.TryRemove(
                new KeyValuePair<(string, uint, uint), Session>(KeyOf(session), session)))
        {
            Log.Information("[quazal] session {TypeId}-{SessionId} is empty and has been torn down",
                session.TypeId, session.SessionId);
        }
    }

    public static int GetParticipantCount(string titleId, uint typeId, uint sessionId)
    {
        if (!_sessions.TryGetValue(Key(titleId, typeId, sessionId), out Session? session))
            return 0;
        lock (session.Participants)
            return session.Participants.Count;
    }

    public static IReadOnlyList<uint> GetParticipants(string titleId, uint typeId, uint sessionId)
    {
        if (!_sessions.TryGetValue(Key(titleId, typeId, sessionId), out Session? session))
            return [];
        lock (session.Participants)
            return session.Participants.ToArray();
    }

    /// <summary>
    /// Drops a player from every session on this server, reporting how many
    /// they were in.
    /// </summary>
    public static int RemovePlayerEverywhere(string titleId, uint pid)
    {
        int removed = 0;
        foreach (Session session in Of(titleId))
        {
            bool left;
            lock (session.Participants)
                left = session.Participants.Remove(pid);

            if (!left)
                continue;

            removed++;
            DropIfEmpty(session);
        }
        return removed;
    }

    /// <summary>
    /// Every live session with its roster, for the reporter that mirrors them
    /// into Playforward's directory.
    /// </summary>
    public static IReadOnlyList<SessionSnapshot> Snapshot(string titleId)
    {
        var snapshots = new List<SessionSnapshot>(_sessions.Count);
        foreach (Session session in Of(titleId))
        {
            uint[] participants;
            lock (session.Participants)
                participants = session.Participants.ToArray();

            Dictionary<uint, int> properties;
            lock (session.Properties)
                properties = new Dictionary<uint, int>(session.Properties);

            snapshots.Add(new SessionSnapshot(session.TitleId, session.TypeId, session.SessionId, session.HostPid,
                participants, properties));
        }
        return snapshots;
    }

    /// <summary>Every session, of any type, that this PID is in or hosts within one title.</summary>
    public static IReadOnlyList<(uint TypeId, uint SessionId)> SessionsContaining(string titleId, uint pid)
    {
        var found = new List<(uint, uint)>();
        foreach (Session session in Of(titleId))
        {
            bool present = session.HostPid == pid;
            if (!present)
            {
                lock (session.Participants)
                    present = session.Participants.Contains(pid);
            }
            if (present)
                found.Add((session.TypeId, session.SessionId));
        }
        return found;
    }

    /// <summary>
    /// Sets properties on an existing session; a key nobody created is
    /// ignored rather than conjured into a hostless session the directory
    /// would then advertise.
    /// </summary>
    public static void SetProperties(string titleId, uint typeId, uint sessionId, IReadOnlyDictionary<uint, int> properties)
    {
        if (!_sessions.TryGetValue(Key(titleId, typeId, sessionId), out Session? session))
            return;
        lock (session.Properties)
        {
            foreach ((uint id, int value) in properties)
                session.Properties[id] = value;
        }
    }

    /// <summary>
    /// The station urls a player registered, for a search result that has to
    /// describe participants and not just the host.
    /// </summary>
    public static IReadOnlyList<string> GetStationUrls(string titleId, uint pid) =>
        _stationUrlsByPid.TryGetValue((titleId, pid), out string[]? urls) ? urls : [];

    /// <summary>
    /// All currently live sessions of a title and type, for search/quick-match
    /// including their properties, so a caller can filter by the same
    /// criterion (e.g. property 0x64, the playlist/mode selector confirmed
    /// from a real capture 2026-08-28) that SearchSessions requests carry.
    /// </summary>
    public static IReadOnlyList<(uint SessionId, uint HostPid, IReadOnlyList<string> HostUrls,
        IReadOnlyDictionary<uint, int> Properties)>
        FindByType(string titleId, uint typeId) =>
        Of(titleId)
            .Where(s => s.TypeId == typeId)
            .Select(s => (s.SessionId, s.HostPid,
                GetStationUrls(titleId, s.HostPid),
                (IReadOnlyDictionary<uint, int>)s.Properties))
            .ToArray();

    private static IEnumerable<Session> Of(string titleId) =>
        _sessions.Values.Where(s => string.Equals(s.TitleId, titleId, StringComparison.OrdinalIgnoreCase));
}
