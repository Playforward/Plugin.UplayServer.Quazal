namespace Plugin.UplayServer.Quazal;

/// <summary>Legacy Game Session service (protocol 42).</summary>
internal static class GameSession
{
    public const byte ProtocolId = 42;
    public const uint MethodCreateSession = 1;
    public const uint MethodUpdateSession = 2;
    public const uint MethodDeleteSession = 3;
    public const uint MethodMigrateSession = 4;
    public const uint MethodLeaveSession = 5;
    public const uint MethodGetSession = 6;
    public const uint MethodSearchSessions = 7;
    public const uint MethodAddParticipants = 8;
    public const uint MethodRemoveParticipants = 9;
    public const uint MethodGetParticipantCount = 10;
    public const uint MethodGetParticipants = 11;
    /// <summary>
    /// (typeId, sessionId, qlist&lt;pid&gt;, u8, string): invite players to a
    /// session. The server's part is delivering a NotificationEvent of type
    /// 7005 to each invitee (community capture, 2026-09-17); a target of
    /// 0xFFFFFFFF produced nothing there and does here.
    /// </summary>
    public const uint MethodSendInvitation = 12;
    public const uint MethodJoinSession = 22;

    /// <summary>What a working server sends a player when they are invited to a session.</summary>
    public const uint NotificationInvited = 7005;
    /// <summary>... and when a host has added them to one.</summary>
    public const uint NotificationAddedToSession = 7003;

    /// <summary>Delivers an RMC request to a player's session: (pid, protocol, method, parameters) -> sent.</summary>
    public delegate bool Notifier(uint pid, byte protocolId, uint methodId, byte[] parameters);

    /// <summary>
    /// A NotificationEvent as the working server encodes it (23 bytes for an
    /// invitation): <c>u32 pidSource, u32 type, u32 param1, u32 param2,
    /// string strParam, u32 param3</c>. For session events param2 is the
    /// session id and param3 its type id; 7003 carries the added player in
    /// param1.
    /// </summary>
    public static byte[] NotificationEvent(uint pidSource, uint type, uint param1, uint param2, uint param3)
    {
        using var body = new MemoryStream();
        TicketGranting.WriteU32(body, pidSource);
        TicketGranting.WriteU32(body, type);
        TicketGranting.WriteU32(body, param1);
        TicketGranting.WriteU32(body, param2);
        TicketGranting.WriteString(body, string.Empty);
        TicketGranting.WriteU32(body, param3);
        return body.ToArray();
    }

    public static byte[] BuildSendInvitationResult(RmcRequest request, uint inviter, Notifier notify)
    {
        if (!request.TryReadU32(0, out uint typeId) || !request.TryReadU32(4, out uint sessionId) ||
            !request.TryReadU32(8, out uint count) || count > 64)
        {
            Serilog.Log.Warning("[quazal] GameSession.SendInvitation malformed: {Parameters}", Convert.ToHexString(request.Parameters));
            return [];
        }
        var delivered = new List<uint>();
        for (int i = 0, offset = 12; i < count && request.TryReadU32(offset, out uint pid); i++, offset += 4)
        {
            if (pid == 0xFFFFFFFF) continue;
            if (notify(pid, NotificationProtocolId, NotificationMethod,
                    NotificationEvent(inviter, NotificationInvited, 0, sessionId, typeId)))
                delivered.Add(pid);
        }
        Serilog.Log.Information("[quazal] GameSession.SendInvitation from pid={Inviter} to session {Type}/{Session}: delivered to [{Pids}]",
            inviter, typeId, sessionId, string.Join(",", delivered));
        return [];
    }

    /// <summary>NotificationEvent lives on protocol 14, method 1 (ProcessNotificationEvent).</summary>
    public const byte NotificationProtocolId = 14;
    public const uint NotificationMethod = 1;

    /// <summary>
    /// GetSession (typeId, sessionId): one GameSessionSearchResult
    /// </summary>
    public static byte[]? BuildGetSessionResult(RmcRequest request, string titleId)
    {
        if (!request.TryReadU32(0, out uint typeId) || !request.TryReadU32(4, out uint sessionId))
            return null;
        var found = GameSessionStore.FindByType(titleId, typeId).FirstOrDefault(candidate => candidate.SessionId == sessionId);
        if (found.HostPid == 0 && found.HostUrls is null)
        {
            Serilog.Log.Warning("[quazal] GameSession.GetSession {Type}/{Session}: no such session", typeId, sessionId);
            return null;
        }
        byte[] list = BuildSearchResults(titleId, typeId, [found]);
        Serilog.Log.Information("[quazal] GameSession.GetSession {Type}/{Session} -> host pid={Host}, {Bytes} bytes",
            typeId, sessionId, found.HostPid, list.Length - 4);
        return list[4..];    // the one record, without the list's count
    }

    /// <summary>
    /// MigrateSession (typeId, sessionId): the caller becomes host of a new
    /// session carrying the old one's players and properties, and gets the
    /// new key back
    /// </summary>
    public static byte[]? BuildMigrateSessionResult(RmcRequest request, uint newHost, string titleId)
    {
        if (!request.TryReadU32(0, out uint typeId) || !request.TryReadU32(4, out uint sessionId))
            return null;
        uint migrated = Interlocked.Increment(ref _nextSessionId) - 1;
        if (!GameSessionStore.MigrateSession(titleId, typeId, sessionId, migrated, newHost))
        {
            Serilog.Log.Warning("[quazal] GameSession.MigrateSession {Type}/{Session} refused: pid={Pid} is not in that session",
                typeId, sessionId, newHost);
            return null;
        }
        Playforward.PlayforwardTitles.Nudge(titleId);
        using var body = new MemoryStream();
        TicketGranting.WriteU32(body, typeId);
        TicketGranting.WriteU32(body, migrated);
        Serilog.Log.Information("[quazal] GameSession.MigrateSession {Type}/{Session} -> {Migrated}, host pid={Host}",
            typeId, sessionId, migrated, newHost);
        return body.ToArray();
    }
    public const uint MethodPostLobbyNotification = 23;
    public const uint MethodRegisterUrls = 21;

    private static uint _nextSessionId = 1;

    /// <summary>
    /// FC3's multiplayer front end creates a legacy GameSession before it can
    /// publish or join a co-op lobby.  The request begins with its type id;
    /// the documented result is the corresponding (type, session) key.
    /// Keeping this as a real key rather than returning an empty body is
    /// important: the client passes that key to its later session operations.
    /// </summary>
    public static byte[]? BuildCreateSessionResult(RmcRequest request, uint hostPid, string titleId)
    {
        if (!request.TryReadU32(0, out uint typeId))
        {
            Serilog.Log.Warning("[quazal] GameSession.CreateSession malformed: {Parameters}",
                Convert.ToHexString(request.Parameters));
            return null;
        }

        uint sessionId = Interlocked.Increment(ref _nextSessionId) - 1;
        GameSessionStore.CreateSession(titleId, typeId, sessionId, hostPid);
        if (request.TryReadU32(4, out uint count) && count <= 64)
        {
            var initial = new Dictionary<uint, int>();
            int pos = 8;
            for (uint i = 0; i < count && request.TryReadU32(pos, out uint propId) && request.TryReadU32(pos + 4, out uint raw); i++, pos += 8)
                initial[propId] = unchecked((int)raw);
            if (initial.Count > 0)
                GameSessionStore.SetProperties(titleId, typeId, sessionId, initial);
        }
        Playforward.PlayforwardTitles.Nudge(titleId);
        using var body = new MemoryStream();
        TicketGranting.WriteU32(body, typeId);
        TicketGranting.WriteU32(body, sessionId);
        Serilog.Log.Information("[quazal] GameSession.CreateSession type={TypeId} host={HostPid} -> {SessionId}",
            typeId, hostPid, sessionId);
        return body.ToArray();
    }

    /// <summary>Methods whose documented result is void.</summary>
    public static byte[] BuildVoidResult(RmcRequest request, string method)
    {
        Serilog.Log.Information("[quazal] GameSession.{Method} accepted ({Parameters})", method,
            Convert.ToHexString(request.Parameters));
        return [];
    }

    /// <summary>GetSession/GetParticipants use list-shaped results.</summary>
    public static byte[] BuildEmptyListResult(RmcRequest request, string method)
    {
        Serilog.Log.Information("[quazal] GameSession.{Method} -> 0 entries", method);
        return Quazal.EmptyList;
    }

    internal static bool TryReadProperties(RmcRequest request, int offset, out uint headerA, out uint headerB,
        out Dictionary<uint, int> properties)
    {
        properties = [];
        headerA = headerB = 0;
        if (!request.TryReadU32(offset, out headerA) || !request.TryReadU32(offset + 4, out headerB) ||
            !request.TryReadU32(offset + 8, out uint count))
            return false;

        int pos = offset + 12;
        for (int i = 0; i < count && request.TryReadU32(pos, out uint propId) &&
             request.TryReadU32(pos + 4, out uint rawValue); i++, pos += 8)
            properties[propId] = unchecked((int)rawValue);
        return true;
    }

    /// <summary>
    /// header = (typeId, sessionId), confirmed from our own local capture and
    /// matching the community server's shape byte-for-byte.
    /// </summary>
    public static byte[] BuildUpdateSessionResult(RmcRequest request, uint caller, string titleId)
    {
        if (TryReadProperties(request, 0, out uint typeId, out uint sessionId, out Dictionary<uint, int> props))
        {
            if (!GameSessionStore.IsHost(titleId, typeId, sessionId, caller))
            {
                Serilog.Log.Warning("[quazal] GameSession.UpdateSession {Type}/{Session} refused: pid={Pid} does not host it",
                    typeId, sessionId, caller);
                return [];
            }
            GameSessionStore.SetProperties(titleId, typeId, sessionId, props);
            Serilog.Log.Information(
                "[quazal] GameSession.UpdateSession type={TypeId} session={SessionId} properties=[{Props}]",
                typeId, sessionId, string.Join(",", props.Select(p => $"{p.Key:x}={p.Value}")));
        }
        else
        {
            Serilog.Log.Warning("[quazal] GameSession.UpdateSession malformed: {Parameters}",
                Convert.ToHexString(request.Parameters));
        }
        return [];
    }

    /// <summary>
    /// header meaning is unconfirmed for this method (no existing session id to
    /// key off), but the shape matches UpdateSession's exactly.
    /// </summary>
    /// <param name="excludeOwn">
    /// Leave out sessions the searching player hosts or is in. Assassin's
    /// Creed Brotherhood keeps searching after it creates its own session and,
    /// handed that session back, tries to join itself until it crashes.
    /// </param>
    public static byte[] BuildSearchSessionsResult(RmcRequest request, uint searchingPid, string titleId, bool excludeOwn = false)
    {
        if (TryReadProperties(request, 0, out uint headerA, out uint headerB, out Dictionary<uint, int> criteria))
        {
            int? playlist = criteria.TryGetValue(0x64, out int p) ? p : null;
            var candidates = playlist.HasValue ? GameSessionStore.FindByType(titleId, headerA) : [];
            if (excludeOwn)
            {
                candidates = candidates
                    .Where(c => c.HostPid != searchingPid && !GameSessionStore.IsMember(titleId, headerA, c.SessionId, searchingPid))
                    .ToList();
            }

            candidates = Playforward.PartySeating.Steer(
                Playforward.PlayforwardTitles.ForTitle(titleId)?.Gateway, searchingPid, candidates);

            byte[] result = BuildSearchResults(titleId, headerA, candidates);
            Serilog.Log.Information(
                "[quazal] GameSession.SearchSessions headerA={A} headerB={B} playlist={Playlist} pid={Pid} -> {Count} session result(s), {Bytes} bytes",
                headerA, headerB, playlist, searchingPid, candidates.Count, result.Length);
            return result;
        }
        else
        {
            Serilog.Log.Warning("[quazal] GameSession.SearchSessions malformed: {Parameters}",
                Convert.ToHexString(request.Parameters));
        }
        return Quazal.EmptyList;
    }

    /// <param name="alsoListed">
    /// Further server-side attributes a title's working server appends after
    /// 5 and 6 (Future Soldier's lists 50 and 51); none for Far Cry 3.
    /// </param>
    internal static byte[] BuildSearchResults(string titleId, uint typeId, IReadOnlyList<(uint SessionId, uint HostPid,
        IReadOnlyList<string> HostUrls, IReadOnlyDictionary<uint, int> Properties)> candidates,
        IReadOnlyList<(uint Id, int Value)>? alsoListed = null)
    {
        using var body = new MemoryStream();
        TicketGranting.WriteU32(body, (uint)candidates.Count);
        foreach ((uint sessionId, uint hostPid, IReadOnlyList<string> hostUrls,
                  IReadOnlyDictionary<uint, int> properties) in candidates)
        {
            TicketGranting.WriteU32(body, typeId);
            TicketGranting.WriteU32(body, sessionId);
            TicketGranting.WriteU32(body, hostPid);
            TicketGranting.WriteU32(body, (uint)hostUrls.Count);
            foreach (string url in hostUrls)
                TicketGranting.WriteString(body, url);
            var listed = new Dictionary<uint, int>(properties)
            {
                [0x05] = Math.Max(1, GameSessionStore.GetParticipantCount(titleId, typeId, sessionId)),
            };
            listed.TryAdd(0x06, 0);
            foreach ((uint id, int value) in alsoListed ?? [])
                listed.TryAdd(id, value);
            TicketGranting.WriteU32(body, (uint)listed.Count);
            foreach ((uint id, int value) in listed)
            {
                TicketGranting.WriteU32(body, id);
                TicketGranting.WriteU32(body, unchecked((uint)value));
            }
        }
        return body.ToArray();
    }

    /// <summary>
    /// u32 typeId | u32 sessionId | u32 count | (u32 pid | u32 unknown){count} | u8 trailing.
    /// The trailing byte's meaning is unconfirmed and ignored.
    /// </summary>
    private static bool TryReadParticipants(RmcRequest request, out uint typeId, out uint sessionId,
        out List<uint> pids)
    {
        pids = [];
        typeId = sessionId = 0;
        if (!request.TryReadU32(0, out typeId) || !request.TryReadU32(4, out sessionId) ||
            !request.TryReadU32(8, out uint count))
            return false;

        int offset = 12;
        for (int i = 0; i < count && request.TryReadU32(offset, out uint pid); i++, offset += 8)
            pids.Add(pid);
        return true;
    }

    public static byte[] BuildAddParticipantsResult(RmcRequest request, uint caller, Notifier notify, string titleId)
    {
        if (TryReadParticipants(request, out uint typeId, out uint sessionId, out List<uint> pids))
        {
            if (!GameSessionStore.MayChangeRoster(titleId, typeId, sessionId, caller, pids))
            {
                Serilog.Log.Warning("[quazal] GameSession.AddParticipants {Type}/{Session} refused: pid={Pid} may not seat [{Pids}]",
                    typeId, sessionId, caller, string.Join(",", pids));
                return [];
            }
            GameSessionStore.AddParticipants(titleId, typeId, sessionId, pids);
            foreach (uint pid in pids)
                if (pid != caller)
                    notify(pid, NotificationProtocolId, NotificationMethod,
                        NotificationEvent(caller, NotificationAddedToSession, pid, sessionId, typeId));
            Playforward.PlayforwardTitles.Nudge(titleId);
            Serilog.Log.Information(
                "[quazal] GameSession.AddParticipants type={TypeId} session={SessionId} pids=[{Pids}] -> {Count} total",
                typeId, sessionId, string.Join(",", pids), GameSessionStore.GetParticipantCount(titleId, typeId, sessionId));
        }
        else
        {
            Serilog.Log.Warning("[quazal] GameSession.AddParticipants malformed: {Parameters}",
                Convert.ToHexString(request.Parameters));
        }
        return [];
    }

    /// <remarks>
    /// The host removes anybody; anyone else only themselves. This used to
    /// remove whatever pids a request named from whatever session it named.
    /// </remarks>
    public static byte[] BuildRemoveParticipantsResult(RmcRequest request, uint caller, string titleId)
    {
        if (TryReadParticipants(request, out uint typeId, out uint sessionId, out List<uint> pids))
        {
            if (!GameSessionStore.MayChangeRoster(titleId, typeId, sessionId, caller, pids))
            {
                Serilog.Log.Warning("[quazal] GameSession.RemoveParticipants {Type}/{Session} refused: pid={Pid} may not remove [{Pids}]",
                    typeId, sessionId, caller, string.Join(",", pids));
                return [];
            }
            GameSessionStore.RemoveParticipants(titleId, typeId, sessionId, pids);
            Playforward.PlayforwardTitles.Nudge(titleId);
            Serilog.Log.Information(
                "[quazal] GameSession.RemoveParticipants type={TypeId} session={SessionId} pids=[{Pids}] -> {Count} total",
                typeId, sessionId, string.Join(",", pids), GameSessionStore.GetParticipantCount(titleId, typeId, sessionId));
        }
        else
        {
            Serilog.Log.Warning("[quazal] GameSession.RemoveParticipants malformed: {Parameters}",
                Convert.ToHexString(request.Parameters));
        }
        return [];
    }

    /// <summary>
    /// The result shape (a plain u32, no list envelope) is already established;
    /// only the value was hardcoded to zero before. Assumes the request begins
    /// with (typeId, sessionId) like every other keyed GameSession call
    /// </summary>
    public static byte[] BuildParticipantCountResult(RmcRequest request, string titleId)
    {
        int count = request.TryReadU32(0, out uint typeId) && request.TryReadU32(4, out uint sessionId)
            ? GameSessionStore.GetParticipantCount(titleId, typeId, sessionId)
            : 0;
        Serilog.Log.Information("[quazal] GameSession.GetParticipantCount -> {Count}", count);
        return BitConverter.GetBytes((uint)count);
    }

    /// <summary>
    /// Records the peer URLs published after NAT discovery. RegisterURLs is a
    /// void RMC method, so the normal success envelope has an empty body.
    /// </summary>
    public static byte[]? BuildRegisterUrlsResult(RmcRequest request, uint pid, string titleId)
    {
        IReadOnlyList<string> urls = SecureConnection.ReadStationUrls(request.Parameters);
        if (urls.Count == 0)
        {
            Serilog.Log.Warning("[quazal] GameSession.RegisterURLs listed no valid station URLs: {Parameters}",
                Convert.ToHexString(request.Parameters));
            return null;
        }

        GameSessionStore.SetStationUrls(titleId, pid, urls);
        Serilog.Log.Information("[quazal] GameSession.RegisterURLs pid={Pid}: {Count} URL(s) [{Urls}]",
            pid, urls.Count, string.Join(", ", urls));
        return Array.Empty<byte>();
    }
}
