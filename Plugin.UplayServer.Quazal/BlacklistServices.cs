using System.Buffers.Binary;

namespace Plugin.UplayServer.Quazal;

/// <summary>
/// The services Splinter Cell Blacklist walks once it is online.
/// </summary>
internal static class BlacklistServices
{
    /// <summary>
    /// Game Session Ex (123). Its one method is the search a joining client
    /// runs, and it differs from GameSession.SearchSessions (42) only by
    /// carrying each session's participant list alongside the result
    /// </summary>
    internal static class GameSessionEx
    {
        public const ushort ProtocolId = 123;
        public const uint MethodSearchSessions = 1;

        /// <summary>
        /// A QList&lt;GameSessionSearchResultEx&gt;: each entry is the same
        /// GameSessionSearchResult that protocol 42 returns, followed by a
        /// QList&lt;GameSessionParticipant&gt; of {pid, name, station urls}.
        /// </summary>
        public static byte[] BuildSearchSessionsResult(RmcRequest request, uint searchingPid,
            Func<uint, string> nameForPid, string titleId)
        {
            if (!GameSession.TryReadProperties(request, 0, out uint typeId, out uint headerB,
                                               out Dictionary<uint, int> criteria))
            {
                Serilog.Log.Warning("[quazal] GameSessionEx.SearchSessions malformed: {Parameters}",
                    Convert.ToHexString(request.Parameters));
                return Quazal.EmptyList;
            }

            var candidates = GameSessionStore.FindByType(titleId, typeId);
            candidates = Playforward.PartySeating.Steer(
                Playforward.PlayforwardTitles.ForTitle(titleId)?.Gateway, searchingPid, candidates);

            using var body = new MemoryStream();
            TicketGranting.WriteU32(body, (uint)candidates.Count);
            foreach (var candidate in candidates)
            {
                byte[] one = GameSession.BuildSearchResults(titleId, typeId, [candidate]);
                body.Write(one.AsSpan(4));

                IReadOnlyList<uint> participants =
                    GameSessionStore.GetParticipants(titleId, typeId, candidate.SessionId);
                TicketGranting.WriteU32(body, (uint)participants.Count);
                foreach (uint participant in participants)
                {
                    TicketGranting.WriteU32(body, participant);
                    TicketGranting.WriteString(body, nameForPid(participant));
                    IReadOnlyList<string> urls = GameSessionStore.GetStationUrls(titleId, participant);
                    TicketGranting.WriteU32(body, (uint)urls.Count);
                    foreach (string url in urls)
                        TicketGranting.WriteString(body, url);
                }
            }

            byte[] result = body.ToArray();
            Serilog.Log.Information(
                "[quazal] GameSessionEx.SearchSessions type={TypeId} headerB={HeaderB} criteria=[{Criteria}] pid={Pid}" +
                " -> {Count} session(s), {Bytes} bytes",
                typeId, headerB, string.Join(",", criteria.Select(c => $"{c.Key:x}={c.Value}")),
                searchingPid, candidates.Count, result.Length);
            return result;
        }
    }

    private static void Log(string service, string method, RmcRequest request, string answer)
        => Serilog.Log.Information("[quazal] {Service}.{Method} -> {Answer} ({Parameters})",
            service, method, answer, Convert.ToHexString(request.Parameters));

    /// <summary>Tracking Extension (1001): which telemetry group this user is in.</summary>
    internal static class TrackingExtension
    {
        public const ushort ProtocolId = 1001;
        public const uint MethodGetTrackingUserGroup = 1;
        public const uint MethodGetTrackingUserGroupTags = 2;

        /// <summary>
        /// Group 0.
        /// </summary>
        public static byte[] BuildUserGroupResult()
        {
            Serilog.Log.Information("[quazal] TrackingExtension.GetTrackingUserGroup -> group 0");
            return [0x00, 0x00, 0x00, 0x00];
        }

        public static byte[] BuildUserGroupTagsResult()
        {
            Serilog.Log.Information("[quazal] TrackingExtension.GetTrackingUserGroupTags -> 0 tags");
            return Quazal.EmptyList;
        }
    }

    /// <summary>Challenge Helper (105): friend and online challenge generation.</summary>
    internal static class ChallengeHelper
    {
        public const ushort ProtocolId = 105;
        public const uint MethodGenerateMyFriendChallenges = 1;
        public const uint MethodGenerateFriendChallenges = 2;
        public const uint MethodGetOnlineChallenges = 3;

        public static byte[] BuildEmptyChallengeList(RmcRequest request)
        {
            Log("ChallengeHelper", "GenerateChallenges", request, "0 challenges");
            return Quazal.EmptyList;
        }
    }

    /// <summary>Clan Helper (106).</summary>
    internal static class ClanHelper
    {
        public const ushort ProtocolId = 106;
        public const uint MethodGetClanInfoByPid = 5;
        public const uint MethodGetClanInfoByClid = 6;
        public const uint MethodGetMemberListByPid = 7;
        public const uint MethodGetMemberListByClid = 8;
        public const uint MethodGenerateClanChallenges = 9;

        /// <summary>
        /// ClanInfo is {uint32 CLID, string Tag, string Title, string Motto}.
        /// A player in no clan is CLID 0xFFFFFFFF with empty strings
        /// </summary>
        public static byte[] BuildNoClanResult(RmcRequest request)
        {
            using var body = new MemoryStream();
            TicketGranting.WriteU32(body, uint.MaxValue);
            TicketGranting.WriteString(body, string.Empty);
            TicketGranting.WriteString(body, string.Empty);
            TicketGranting.WriteString(body, string.Empty);
            Log("ClanHelper", "GetClanInfo", request, "no clan");
            return body.ToArray();
        }

        public static byte[] BuildEmptyListResult(RmcRequest request)
        {
            Log("ClanHelper", "list", request, "0 entries");
            return Quazal.EmptyList;
        }
    }

    /// <summary>
    /// Ladder Helper (107). Note this id is Far Cry 3's Intel service, which is
    /// why the whole file is gated on the peer's title.
    /// </summary>
    internal static class LadderHelper
    {
        public const ushort ProtocolId = 107;
        public const uint MethodGetUnixUtc = 1;
        public const uint MethodAreLaddersAvailableInCountry = 2;
        public const uint MethodCheckLadderIsRunning = 3;

        /// <summary>
        /// The client asks the server for the time rather than trusting the
        /// machine's clock, and uses it to decide whether a ladder window is
        /// open. Seconds since the Unix epoch, as a uint32.
        /// </summary>
        public static byte[] BuildUnixUtcResult()
        {
            uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Serilog.Log.Information("[quazal] LadderHelper.GetUnixUTC -> {Time}", now);
            byte[] result = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(result, now);
            return result;
        }

        public static byte[] BuildBoolResult(bool value, string method)
        {
            Serilog.Log.Information("[quazal] LadderHelper.{Method} -> {Value}", method, value);
            return [value ? (byte)1 : (byte)0];
        }
    }

    /// <summary>Player Stats (55): statboards and leaderboards, both backed by <see cref="BlacklistStatistics"/>.</summary>
    internal static class PlayerStats
    {
        public const ushort ProtocolId = 55;
        public const uint MethodWriteStats = 1;
        public const uint MethodReadStatsByPlayers = 2;
        public const uint MethodReadLeaderboardsNearPlayer = 3;
        public const uint MethodReadLeaderboardsByRank = 4;
        public const uint MethodReadLeaderboardsByPlayers = 5;

        /// <summary>WriteStats returns nothing; the updates land under the caller's pid.</summary>
        public static byte[]? BuildWriteStatsResult(RmcRequest request, uint pid, string titleId)
        {
            if (!BlacklistStatistics.TryParseWrite(request.Parameters, out var updates))
            {
                Serilog.Log.Warning("[quazal] PlayerStats.WriteStats malformed: {Parameters}", Convert.ToHexString(request.Parameters));
                return null;
            }
            BlacklistStatistics.Write(titleId, pid, updates, DateTimeOffset.UtcNow);
            Serilog.Log.Information("[quazal] PlayerStats.WriteStats pid={Pid}: {Updates}", pid,
                string.Join("; ", updates.Select(u =>
                    $"board {u.Board} ctx [{string.Join(",", u.Contexts)}] {string.Join(" ", u.Stats.Select(s => $"{s.StatId}={s.Value}"))}")));
            return [];
        }

        /// <summary>A QList&lt;StatboardResult&gt;, one per (board, context) asked.</summary>
        public static byte[]? BuildReadStatsByPlayersResult(RmcRequest request, Func<uint, string> nameForPid, string titleId)
        {
            if (!BlacklistStatistics.TryParseRead(request.Parameters, out var pids, out var queries))
            {
                Serilog.Log.Warning("[quazal] PlayerStats.ReadStatsByPlayers malformed: {Parameters}", Convert.ToHexString(request.Parameters));
                return null;
            }
            byte[] result = BlacklistStatistics.BuildReadResult(titleId, pids, queries, nameForPid, out int rows);
            Serilog.Log.Information("[quazal] PlayerStats.ReadStatsByPlayers pids [{Pids}] boards [{Boards}] -> {Rows} row(s), {Bytes} bytes",
                string.Join(",", pids), string.Join(",", queries.Select(q => q.Board).Distinct()), rows, result.Length);
            return result;
        }

        /// <summary>ReadLeaderboardsNearPlayer: the entries around one player on each queried board.</summary>
        public static byte[]? BuildReadLeaderboardsNearPlayerResult(RmcRequest request, Func<uint, string> nameForPid, string titleId)
        {
            if (!BlacklistStatistics.TryParseNearPlayer(request.Parameters, out uint pid, out uint count, out var queries))
                return Malformed("ReadLeaderboardsNearPlayer", request);
            byte[] result = BlacklistStatistics.BuildLeaderboardResult(titleId, queries,
                ranking => BlacklistStatistics.Near(ranking, pid, count), nameForPid, out int entries);
            LogLeaderboard("ReadLeaderboardsNearPlayer", $"pid={pid} count={count}", queries, entries, result);
            return result;
        }

        /// <summary>ReadLeaderboardsByRank: a page of each queried board from a 1-based rank.</summary>
        public static byte[]? BuildReadLeaderboardsByRankResult(RmcRequest request, Func<uint, string> nameForPid, string titleId)
        {
            if (!BlacklistStatistics.TryParseByRank(request.Parameters, out uint startingRank, out uint count, out var queries))
                return Malformed("ReadLeaderboardsByRank", request);
            byte[] result = BlacklistStatistics.BuildLeaderboardResult(titleId, queries,
                ranking => BlacklistStatistics.FromRank(ranking, startingRank, count), nameForPid, out int entries);
            LogLeaderboard("ReadLeaderboardsByRank", $"from={startingRank} count={count}", queries, entries, result);
            return result;
        }

        /// <summary>ReadLeaderboardsByPlayers: how the client asks for its own rank.</summary>
        public static byte[]? BuildReadLeaderboardsByPlayersResult(RmcRequest request, Func<uint, string> nameForPid, string titleId)
        {
            if (!BlacklistStatistics.TryParseByPlayers(request.Parameters, out var pids, out var queries))
                return Malformed("ReadLeaderboardsByPlayers", request);
            byte[] result = BlacklistStatistics.BuildLeaderboardResult(titleId, queries,
                ranking => BlacklistStatistics.Of(ranking, pids), nameForPid, out int entries);
            LogLeaderboard("ReadLeaderboardsByPlayers", $"pids=[{string.Join(",", pids)}]", queries, entries, result);
            return result;
        }

        private static byte[]? Malformed(string method, RmcRequest request)
        {
            Serilog.Log.Warning("[quazal] PlayerStats.{Method} malformed: {Parameters}", method, Convert.ToHexString(request.Parameters));
            return null;
        }

        private static void LogLeaderboard(string method, string who, IReadOnlyList<BlacklistStatistics.LeaderboardQuery> queries, int entries, byte[] result)
            => Serilog.Log.Information("[quazal] PlayerStats.{Method} {Who} boards [{Boards}] -> {Entries} entr(ies), {Bytes} bytes",
                method, who, string.Join(",", queries.Select(q => $"{q.Board}:{q.Context}")), entries, result.Length);
    }

    /// <summary>
    /// User Storage (53) as Blacklist uses it. The existing UserStorage class
    /// answers the methods Far Cry 3 calls. Blacklist opens with SearchContents,
    /// which Far Cry 3 never sends.
    /// </summary>
    internal static class BlacklistUserStorage
    {
        public const ushort ProtocolId = 53;
        public const uint MethodSearchContents = 1;
        public const uint MethodSearchContentsWithTotal = 2;

        /// <summary>QList&lt;UserContent&gt;: nothing has been uploaded here.</summary>
        public static byte[] BuildSearchContentsResult(RmcRequest request)
        {
            Log("UserStorage", "SearchContents", request, "0 contents");
            return Quazal.EmptyList;
        }

        /// <summary>The same, plus the total row count the query would match.</summary>
        public static byte[] BuildSearchContentsWithTotalResult(RmcRequest request)
        {
            Log("UserStorage", "SearchContentsWithTotal", request, "0 contents");
            using var body = new MemoryStream();
            body.Write(Quazal.EmptyList);
            TicketGranting.WriteU32(body, 0);
            return body.ToArray();
        }
    }

    /// <summary>
    /// Ubi Account Management (29). Far Cry 3's name-lookup service shares this
    /// id and answers the lookup methods; only the terms-of-service check is
    /// added here.
    /// </summary>
    internal static class UbiAccountManagement
    {
        public const ushort ProtocolId = 29;
        public const uint MethodHasAcceptedLatestTos = 22;

        public static byte[] BuildHasAcceptedLatestTosResult()
        {
            Serilog.Log.Information("[quazal] UbiAccountManagement.HasAcceptedLatestTOS -> true, 0 failure reasons");
            using var body = new MemoryStream();
            body.WriteByte(0x01);
            body.Write(Quazal.EmptyList);
            return body.ToArray();
        }
    }

    /// <summary>Overlord News (5002), on the 0x7F extended framing.</summary>
    internal static class OverlordNews
    {
        public const ushort ProtocolId = 5002;
        public const uint MethodGetNews = 1;

        public static byte[] BuildEmptyNewsResult(RmcRequest request)
        {
            Log("OverlordNews", "GetNews", request, "0 items");
            return Quazal.EmptyList;
        }
    }

    /// <summary>Overlord Challenge (5007), on the 0x7F extended framing.</summary>
    internal static class OverlordChallenge
    {
        public const ushort ProtocolId = 5007;
        public const uint MethodGetChallenges = 1;

        public static byte[] BuildEmptyChallengeList(RmcRequest request)
        {
            Log("OverlordChallenge", "GetChallenges", request, "0 challenges");
            return Quazal.EmptyList;
        }
    }
}
