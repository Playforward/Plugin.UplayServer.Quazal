using System.Buffers.Binary;
using System.Text;

namespace Plugin.UplayServer.Quazal;

/// <summary>
/// The services Ghost Recon Future Soldier walks once it is online. Its
/// protocol ids were read off the game's own registration routine
/// (Hermes::RDVInterface::registerFriendProtocols in "Future Soldier
/// DX11.exe", 0x8b2d80: each client protocol is constructed, then
/// RegisterExtraProtocol(id) is called with the id as an immediate):
///
///   108 PlayerStatistics   109 RichPresence   110 Clans   111 Misc
///   112 MetaSession        113 GameInfo       114 ContactsExtensions
///   116 HermesAchievements 117 SocialNetworks 118 GameStorageFileVersion
///   119 GR5Award           120 UplayWin (uplaywinservices.cpp, 0xa44cc3)
///   121 MapList            124 IP2Location    125 TrackingExtension
///   127 OnlinePass         5003 OverlordCore (0x7F extended framing)
/// </summary>
internal static class FutureSoldierServices
{
    /// <summary>Uplay Win's id in this title; the handlers are the shared UplayWin ones.</summary>
    public const int UplayWinProtocolId = 120;

    private static void Log(string service, string method, RmcRequest request, string answer)
        => Serilog.Log.Information("[quazal] {Service}.{Method} -> {Answer} ({Parameters})",
            service, method, answer, Convert.ToHexString(request.Parameters));

    /// <summary>
    /// Friends (20), the OSDK 1.3 FriendsProtocol from the executable's DDL.
    /// Method 13 is GetRelationships(resultRange) -> (uiTotalCount: uint32,
    /// lstRelationshipsList: std_list&lt;RelationshipData&gt;); the game asks
    /// for the first 200 right after login. The working server answers 0 and
    /// an empty list (8 bytes) to a fresh account.
    /// </summary>
    internal static class Friends
    {
        public const ushort ProtocolId = 20;
        public const uint MethodGetDetailedList = 12;
        public const uint MethodGetRelationships = 13;

        /// <summary>
        /// GetDetailedList(byRelationship: byte, bReversed: bool) ->
        /// std_list&lt;FriendData&gt;. The game asks for relationship 3
        /// after every session it creates; the working server's answer to a
        /// fresh account is the empty list.
        /// </summary>
        public static byte[] BuildGetDetailedListResult(RmcRequest request)
        {
            Log("Friends", "GetDetailedList", request, "0 friends");
            return Quazal.EmptyList;
        }

        public static byte[] BuildGetRelationshipsResult(RmcRequest request)
        {
            Log("Friends", "GetRelationships", request, "0 relationships");
            using var body = new MemoryStream();
            TicketGranting.WriteU32(body, 0);   // uiTotalCount
            body.Write(Quazal.EmptyList);       // lstRelationshipsList
            return body.ToArray();
        }
    }

    /// <summary>
    /// Protocol 47, method 7: no parameters, and the working server answers
    /// a u32 0 (an empty list or a zero count). Called once in the post-login
    /// walk and again after every session the client creates; its service
    /// is not among the ids registerFriendProtocols names, so it is one of
    /// the OSDK's own.
    /// </summary>
    internal static class Protocol47
    {
        public const ushort ProtocolId = 47;
        public const uint MethodUnknown7 = 7;

        public static byte[] BuildZeroResult(RmcRequest request)
        {
            Log("Protocol47", "method 7", request, "0");
            return [0x00, 0x00, 0x00, 0x00];
        }
    }

    /// <summary>
    /// Tracking Extension at 125 (Blacklist registers the same service at
    /// 1001). This older revision takes the pid as a string ("6554") where
    /// Blacklist's DDL has uint32, and answers a single byte where
    /// Blacklist's is a uint32 group.
    /// </summary>
    internal static class TrackingExtension
    {
        public const ushort ProtocolId = 125;
        public const uint MethodGetTrackingUserGroup = 1;

        public static byte[] BuildUserGroupResult(RmcRequest request)
        {
            Log("TrackingExtension", "GetTrackingUserGroup", request, "group 0");
            return [0x00];
        }
    }

    /// <summary>
    /// Overlord Core (5003) as this game's JobOverlordPostLogin::requestSettings
    /// calls it. The client's JobUpdateConnectionState (0x802440 / 0x802550)
    /// compares OVERLORD_VERSION and _OSDK_VERSION major.minor against its
    /// own statics, 0.7.0.374915 at 0xe7630c and 1.3.7.0 at 0xe7631,
    /// and fails the login with 0x806c0027 on a mismatch. The working server
    /// sends exactly those two keys and nothing else (70 bytes), with the
    /// OSDK at 1.3.15.2508; that map is reproduced here rather than
    /// Blacklist's forty-odd entries with the versions rewritten.
    /// </summary>
    internal static class OverlordSettings
    {
        public const string OverlordVersion = "0.7.0.374915";
        public const string OsdkVersion = "1.3.15.2508";

        private static readonly byte[] Result = Build();

        private static byte[] Build()
        {
            using var body = new MemoryStream();
            TicketGranting.WriteU32(body, 2);
            TicketGranting.WriteString(body, "_OSDK_VERSION");
            body.WriteByte(4);                  // Variant: string
            TicketGranting.WriteString(body, OsdkVersion);
            TicketGranting.WriteString(body, "OVERLORD_VERSION");
            body.WriteByte(4);
            TicketGranting.WriteString(body, OverlordVersion);
            return body.ToArray();
        }

        public static byte[] BuildRequestSettingsResult(RmcRequest request)
        {
            Serilog.Log.Information(
                "[quazal] OverlordCore.requestSettings -> {N}-byte settings map (OVERLORD_VERSION {Ov}, _OSDK_VERSION {Osdk})",
                Result.Length, OverlordVersion, OsdkVersion);
            return Result;
        }
    }

    /// <summary>
    /// Overlord News (5002): GetNews(predicate) -> qlist&lt;NewsItem&gt;. The
    /// working server always has one item (120 bytes) whatever the predicate
    /// ("ProtoPredicateUbiNews" and "ProtoPredicatePrincipals" both got the
    /// same one); its shape is the one 5th-echelon names: id, three unknown
    /// u32 (0, 1, 2 there), a source string, three DateTimes, title, link,
    /// body. One item with this server's text, so the client sees what a
    /// working server shows it.
    /// </summary>
    internal static class OverlordNews
    {
        public const ushort ProtocolId = 5002;
        public const uint MethodGetNews = 1;

        public const string Source = "Playforward";
        public const string Title = "Ghost Recon Future Soldier is online";
        public const string Link = "";
        public const string Body = "This server is run by the Playforward network.";

        public static byte[] BuildGetNewsResult(RmcRequest request)
        {
            Log("OverlordNews", "GetNews", request, "1 item");
            return BuildGetNewsResult(DateTimeOffset.UtcNow);
        }

        internal static byte[] BuildGetNewsResult(DateTimeOffset at)
        {
            ulong stamp = QuazalDateTime(at);
            using var body = new MemoryStream();
            TicketGranting.WriteU32(body, 1);
            TicketGranting.WriteU32(body, 0);           // id
            TicketGranting.WriteU32(body, 0);
            TicketGranting.WriteU32(body, 1);
            TicketGranting.WriteU32(body, 2);
            TicketGranting.WriteString(body, Source);
            WriteU64(body, stamp);                      // created
            WriteU64(body, stamp);                      // updated
            WriteU64(body, stamp);                      // expires
            TicketGranting.WriteString(body, Title);
            TicketGranting.WriteString(body, Link);
            TicketGranting.WriteString(body, Body);
            return body.ToArray();
        }
    }

    /// <summary>
    /// Overlord Dare (5008, 5th-echelon's mapping): method 1 takes nothing
    /// and the working server answers twelve zero bytes. Three zero u32 or
    /// an empty list and a zero u64
    /// </summary>
    internal static class OverlordDare
    {
        public const ushort ProtocolId = 5008;
        public const uint MethodUnknown1 = 1;

        public static byte[] BuildUnknown1Result(RmcRequest request)
        {
            Log("OverlordDare", "method 1", request, "12 zero bytes");
            return new byte[12];
        }
    }

    /// <summary>
    /// Overlord Tracking (5001): the match-telemetry event the client posts at
    /// the end of a match, right after its post-match <c>108/1</c> stats
    /// write. One <c>(String name, qlist&lt;(String, Variant)&gt; params)</c>;
    /// the capture's is <c>"MPM"</c> with <c>p1="Scout" p2="2" p3="Scout"</c>.
    /// </summary>
    internal static class OverlordTracking
    {
        public const ushort ProtocolId = 5001;
        public const uint MethodSendEvent = 1;

        public static byte[] BuildSendEventResult(RmcRequest request)
        {
            Log("OverlordTracking", "SendEvent", request, "void");
            return [];
        }
    }

    /// <summary>
    /// Player Statistics (108), the store <see cref="FutureSoldierStatistics"/>
    /// describes. Reads name their pids; writes are for the caller.
    /// </summary>
    internal static class PlayerStatistics
    {
        public const ushort ProtocolId = 108;
        public const uint MethodWriteStats32 = 1;
        public const uint MethodWriteStats64 = 2;
        public const uint MethodReadStats32 = 6;
        public const uint MethodReadStats64 = 7;

        public static byte[]? BuildWriteResult(RmcRequest request, uint pid, string titleId, int width)
        {
            if (!FutureSoldierStatistics.TryParseWrite(request.Parameters, width, out var updates))
            {
                Serilog.Log.Warning("[quazal] PlayerStatistics.WriteStats{Width} malformed: {Parameters}",
                    width * 8, Convert.ToHexString(request.Parameters));
                return null;
            }
            FutureSoldierStatistics.Write(titleId, pid, updates);
            Serilog.Log.Information("[quazal] PlayerStatistics.WriteStats{Width} pid={Pid}: {Values} value(s) on boards [{Boards}]",
                width * 8, pid, updates.Count, string.Join(",", updates.Select(u => u.Board).Distinct()));
            return [];
        }

        public static byte[]? BuildReadResult(RmcRequest request, string titleId, int width)
        {
            if (!FutureSoldierStatistics.TryParseRead(request.Parameters, out var queries, out var pids))
            {
                Serilog.Log.Warning("[quazal] PlayerStatistics.ReadStats{Width} malformed: {Parameters}",
                    width * 8, Convert.ToHexString(request.Parameters));
                return null;
            }
            byte[] result = FutureSoldierStatistics.BuildReadResult(titleId, queries, pids, width);
            Serilog.Log.Information("[quazal] PlayerStatistics.ReadStats{Width} boards [{Boards}] for pids [{Pids}] -> {Bytes} bytes",
                width * 8, string.Join(",", queries.Select(q => q.Board)), string.Join(",", pids), result.Length);
            return result;
        }
    }

    /// <summary>
    /// Rich Presence (109): method 1 (u32, string) /// </summary>
    internal static class RichPresence
    {
        public const ushort ProtocolId = 109;
        public const uint MethodSetPresence = 1;

        public static byte[] BuildVoidResult(RmcRequest request)
        {
            Log("RichPresence", "method 1", request, "accepted");
            return [];
        }
    }

    /// <summary>
    /// Clans (110). The three methods the game calls all come back "no
    /// clan": 13 (no parameters) a u32 0; 26 (qlist&lt;pid&gt;  
    /// </summary>
    internal static class Clans
    {
        public const ushort ProtocolId = 110;
        public const uint MethodUnknown13 = 13;
        public const uint MethodUnknown26 = 26;
        public const uint MethodUnknown28 = 28;

        public static byte[] BuildZeroU32Result(RmcRequest request, string method)
        {
            Log("Clans", method, request, "0");
            return [0x00, 0x00, 0x00, 0x00];
        }

        public static byte[] BuildZeroU64Result(RmcRequest request)
        {
            Log("Clans", "method 28", request, "8 zero bytes");
            return new byte[8];
        }
    }

    /// <summary>
    /// Misc (111). Method 1 takes nothing and returns a u64 the host then
    /// reports as MATCHID in its FPSHOST_START telemetry 
    /// </summary>
    internal static class Misc
    {
        public const ushort ProtocolId = 111;
        public const uint MethodGetMatchId = 1;
        public const uint MethodGetSettings = 3;
        public const uint MethodUnknown4 = 4;

        /// <summary>The working server's settings map, key -> value, in its order.</summary>
        public static readonly IReadOnlyList<(uint Key, uint Value)> Settings =
        [
            (5, 15), (1, 42_818_548), (2, 20), (7, 4_161_195), (8, 0x7FFFFF), (4, 3600), (9, 0xA443FF), (3, 0x400110), (0, 57_334_772),
        ];

        public static byte[] BuildGetMatchIdResult(RmcRequest request, string titleId)
        {
            ulong id = FutureSoldierStatistics.NextMatchId(titleId);
            Log("Misc", "GetMatchId", request, $"match id {id}");
            using var body = new MemoryStream();
            WriteU64(body, id);
            return body.ToArray();
        }

        public static byte[] BuildGetSettingsResult(RmcRequest request)
        {
            Log("Misc", "method 3", request, $"{Settings.Count} settings");
            using var body = new MemoryStream();
            TicketGranting.WriteU32(body, (uint)Settings.Count);
            foreach ((uint key, uint value) in Settings)
            {
                TicketGranting.WriteU32(body, key);
                TicketGranting.WriteU32(body, value);
            }
            return body.ToArray();
        }

        public static byte[] BuildVoidResult(RmcRequest request)
        {
            Log("Misc", "method 4", request, "accepted");
            return [];
        }
    }

    /// <summary>
    /// Map List (121). Method 1, GetAvailableMapCategories (no parameters),
    /// lists the categories the playlist menu offers
    /// </summary>
    internal static class MapList
    {
        public const ushort ProtocolId = 121;
        public const uint MethodGetAvailableMapCategories = 1;
        public const uint MethodGetMapList = 2;

        public static readonly IReadOnlyList<uint> Categories = [0x1C012, 0x1C022, 0x1C042, 0x1C082, 0x18102, 0x18202];

        public static byte[] BuildGetAvailableMapCategoriesResult(RmcRequest request)
        {
            Log("MapList", "GetAvailableMapCategories", request, $"{Categories.Count} categories");
            return BuildList(Categories);
        }

        /// <summary>The maps for a category: the configured pool, whatever was asked.</summary>
        public static byte[] BuildGetMapListResult(RmcRequest request, IReadOnlyList<uint> maps)
        {
            request.TryReadU32(0, out uint selection);
            Serilog.Log.Information("[quazal] MapList.GetMapList(0x{Selection:X}) -> {Count} maps [{Maps}]",
                selection, maps.Count, string.Join(",", maps));
            return BuildList(maps);
        }

        private static byte[] BuildList(IReadOnlyList<uint> values)
        {
            using var body = new MemoryStream();
            TicketGranting.WriteU32(body, (uint)values.Count);
            foreach (uint value in values)
                TicketGranting.WriteU32(body, value);
            return body.ToArray();
        }
    }

    /// <summary>
    /// Uplay Win (120) GetRewards(startRowIndex, maximumRows, gameCode,
    /// cultureName, platformCode) -> qlist&lt;UplayReward&gt;. The working
    /// server lists the game's four Uplay rewards, every one purchased for
    /// the PC platform, with no names or descriptions
    /// </summary>
    internal static class Rewards
    {
        public static readonly IReadOnlyList<string> Codes = ["GRFSREWARD01PC", "GRFSREWARD02", "GRFSREWARD03", "GRFSREWARD04"];

        public static byte[] BuildGetRewardsResult(RmcRequest request)
        {
            Log("UplayWin", "GetRewards", request, $"{Codes.Count} rewards, all purchased");
            using var body = new MemoryStream();
            TicketGranting.WriteU32(body, (uint)Codes.Count);
            foreach (string code in Codes)
            {
                TicketGranting.WriteString(body, code);     // m_code
                body.Write(Quazal.EmptyString);             // m_name
                body.Write(Quazal.EmptyString);             // m_description
                TicketGranting.WriteU32(body, 0);           // m_value
                body.Write(Quazal.EmptyString);             // m_rewardTypeName
                body.Write(Quazal.EmptyString);             // m_gameCode
                TicketGranting.WriteU32(body, 1);           // m_platforms
                TicketGranting.WriteString(body, "PC");     //   m_platformCode
                body.WriteByte(1);                          //   m_purchased
            }
            return body.ToArray();
        }
    }

    /// <summary>
    /// Privileges (35) GetPrivilegesEx(locale) -> qlist&lt;PrivilegeEx{m_ID
    /// u32, m_description string, m_duration i32}&gt;.
    /// </summary>
    internal static class PrivilegesEx
    {
        /// <summary>The duration on all but one entry: 1,576,800,000 s, fifty 365-day years.</summary>
        public const int FiftyYears = 0x5DFC0F00;

        public static readonly IReadOnlyList<(uint Id, string Description, int Duration)> Granted =
        [
            (1, "Allow to play online", FiftyYears),
            (9001, "DLC1", FiftyYears),
            (9002, "DLC2", FiftyYears),
            (9003, "Ghost Recon Future Soldier DLC 3", FiftyYears),
            (5010, "Character_30K", FiftyYears),
            (5020, "Character_GhostLeader", FiftyYears),
            (5030, "Character_Pepper", FiftyYears),
            (5040, "Character_Kozak", FiftyYears),
            (5050, "Unlock an R6 weapon skin by having a save file or achievement for GRAW 1/2, or SC: Conviction", FiftyYears),
            (5060, "Unlock a Tom Clancy weapon skin by having a save game or achievement for GRAW 1/2, or SC: Conviction", FiftyYears),
            (5070, "Unlock a Splinter Cell weapon skin by having a save game or achievement from SC: Conviction", FiftyYears),
            (5080, "WeaponCustomization_TwoForOneDecisionPoint_1", FiftyYears),
            (5090, "Weapon_Defender", FiftyYears),
            (5100, "Weapon_WildBoar", FiftyYears),
            (5110, "Weapon_MP412", FiftyYears),
            (5120, "Weapon_FiveSevenCM", FiftyYears),
            (5140, "Currency_AttachmentCredits", FiftyYears),
            (5150, "Weapon_G106GrenadeLauncher", FiftyYears),
            (5160, "Weapon_Mk40GrenadeLauncher", FiftyYears),
            (5170, "ConsoleGRFSTheme", FiftyYears),
            (5180, "WeaponCustomization_TwoForOneDecisionPoint_2", FiftyYears),
            (5190, "Weapon_M60_RPK74MAssaultRifle", FiftyYears),
            (5200, "GameTypeMap_NewGuerrillaMapMH08_Rooftops", 0),
            (5300, "WeaponSkin_ChocolateChip", FiftyYears),
            (5301, "WeaponSkin_German", FiftyYears),
            (5302, "WeaponSkin_Indonesian", FiftyYears),
            (5303, "WeaponSkin_Swedish", FiftyYears),
            (1025, "placeholder_1025", FiftyYears),
            (1028, "placeholder_1028", FiftyYears),
            (1029, "placeholder_1029", FiftyYears),
            (1030, "placeholder_1030", FiftyYears),
            (1034, "placeholder_1034", FiftyYears),
            (1040, "placeholder_1040", FiftyYears),
            (1043, "placeholder_1043", FiftyYears),
            (1054, "placeholder_1054", FiftyYears),
            (1055, "placeholder_1055", FiftyYears),
            (1056, "placeholder_1056", FiftyYears),
            (1057, "placeholder_1057", FiftyYears),
            (1058, "placeholder_1058", FiftyYears),
            (1106, "placeholder_1106", FiftyYears),
            (1107, "placeholder_1107", FiftyYears),
            (1150, "placeholder_1150", FiftyYears),
            (2050, "placeholder_2050", FiftyYears),
            (2060, "placeholder_2060", FiftyYears),
            (2065, "placeholder_2065", FiftyYears),
            (2070, "placeholder_2070", FiftyYears),
            (2090, "placeholder_2090", FiftyYears),
            (2100, "placeholder_2100", FiftyYears),
            (2110, "placeholder_2110", FiftyYears),
            (2120, "placeholder_2120", FiftyYears),
            (1000, "placeholder_1000", FiftyYears),
            (1001, "placeholder_1001", FiftyYears),
            (1002, "placeholder_1002", FiftyYears),
            (1003, "placeholder_1003", FiftyYears),
            (1004, "placeholder_1004", FiftyYears),
            (6002, "placeholder_6002", FiftyYears),
            (6003, "placeholder_6003", FiftyYears),
            (6004, "placeholder_6004", FiftyYears),
            (6005, "placeholder_6005", FiftyYears),
            (6006, "placeholder_6006", FiftyYears),
        ];

        private static readonly byte[] Result = Build();

        private static byte[] Build()
        {
            using var body = new MemoryStream();
            TicketGranting.WriteU32(body, (uint)Granted.Count);
            foreach ((uint id, string description, int duration) in Granted)
            {
                TicketGranting.WriteU32(body, id);
                TicketGranting.WriteString(body, description);
                TicketGranting.WriteU32(body, unchecked((uint)duration));
            }
            return body.ToArray();
        }

        public static byte[] BuildGetPrivilegesExResult(RmcRequest request)
        {
            Log("Privileges", "GetPrivilegesEx", request, $"{Granted.Count} privileges");
            return Result;
        }
    }

    /// <summary>
    /// Tracking 3 (36) GetConfiguration: the tags this game is asked to
    /// report, as the working server lists them (42, in this order). The
    /// shared handler lists Far Cry 3's 77; the telemetry itself is read by
    /// nobody here yet, so the list only decides what the client sends.
    /// </summary>
    internal static class Telemetry
    {
        public static readonly IReadOnlyList<string> ConfiguredTags =
        [
            "GAME_START", "GAME_STOP", "LINKAPP_VIEW", "UPLAY_START", "UPLAY_STOP", "GAME_DLC", "FPSCLIENT_START", "FPSCLIENT_STOP",
            "LEVEL_START", "LEVEL_STOP", "OBJECTIVE_START", "OBJECTIVE_STOP", "UPLAY_BROWSE", "FPSHOST_START", "FPSHOST_STOP", "WAVE_STOP",
            "FIGHT_STOP", "AWARD_UNLOCK", "PLAYER_DEATH", "GAME_SAVE", "INSTALL_START", "INSTALL_STOP", "MENU_ENTER", "MENU_EXIT",
            "MENU_OPTIONCHANGE", "MM_RES", "PLAYER_KILL", "PLAYER_SAVED", "UNINSTALL_START", "UNINSTALL_STOP", "VIDEO_START", "VIDEO_STOP",
            "INTEL_REPORT", "MM_HOSTMIGR", "MM_MATCHSTART", "MM_MATCHSTOP", "PLAYER_DESPAWN", "PLAYER_POS", "PLAYER_STEALTH", "PLAYER_SYNCSHOT",
            "WEAPON_USED", "PLAYER_PASS",
        ];

        private static readonly byte[] Result = Build();

        private static byte[] Build()
        {
            using var body = new MemoryStream();
            TicketGranting.WriteU32(body, (uint)ConfiguredTags.Count);
            foreach (string tag in ConfiguredTags)
                TicketGranting.WriteString(body, tag);
            return body.ToArray();
        }

        public static byte[] BuildGetConfigurationResult()
        {
            Serilog.Log.Information("[quazal] Tracking.GetConfiguration -> {Count} tags", ConfiguredTags.Count);
            return Result;
        }
    }

    /// <summary>
    /// Game Session (42) where this game's DDL differs from what the shared
    /// handlers read, or where the working server behaved in a way the
    /// shared handlers do not.
    /// </summary>
    internal static class Sessions
    {
        public const uint MethodSearchSessionsWithParticipants = 24;
        public const uint MethodAbandonSession = 23;

        /// <summary>The server-added search-result attributes, after the player count (5) and 6.</summary>
        public static readonly IReadOnlyList<(uint Id, int Value)> ListedAttributes = [(50, 13), (51, 0)];

        public static byte[]? BuildAddParticipantsResult(RmcRequest request, uint caller, GameSession.Notifier notify, string titleId)
        {
            if (!TryReadKey(request, out uint typeId, out uint sessionId) ||
                !TryReadPidList(request, 8, out List<uint> publicPids, out int end) ||
                !TryReadPidList(request, end, out List<uint> privatePids, out end) || end != request.Parameters.Length)
            {
                Serilog.Log.Warning("[quazal] GameSession.AddParticipants malformed: {Parameters}", Convert.ToHexString(request.Parameters));
                return null;
            }
            List<uint> pids = [.. publicPids, .. privatePids];
            if (!GameSessionStore.MayChangeRoster(titleId, typeId, sessionId, caller, pids))
            {
                Serilog.Log.Warning("[quazal] GameSession.AddParticipants {Type}/{Session} refused: pid={Pid} may not seat [{Pids}]",
                    typeId, sessionId, caller, string.Join(",", pids));
                return [];
            }
            GameSessionStore.AddParticipants(titleId, typeId, sessionId, pids);
            foreach (uint pid in pids)
                if (pid != caller)
                    notify(pid, GameSession.NotificationProtocolId, GameSession.NotificationMethod,
                        GameSession.NotificationEvent(caller, GameSession.NotificationAddedToSession, pid, sessionId, typeId));
            Playforward.PlayforwardTitles.Nudge(titleId);
            Serilog.Log.Information(
                "[quazal] GameSession.AddParticipants type={TypeId} session={SessionId} public=[{Public}] private=[{Private}] -> {Count} total",
                typeId, sessionId, string.Join(",", publicPids), string.Join(",", privatePids), GameSessionStore.GetParticipantCount(titleId, typeId, sessionId));
            return [];
        }

        /// <summary>
        /// SearchSessions(GameSessionQuery{typeID, queryID, parameters}) ->
        /// qlist&lt;GameSessionSearchResult&gt;: every live session of the
        /// type that somebody else hosts, party-steered as Far Cry 3's is.
        /// </summary>
        public static byte[] BuildSearchSessionsResult(RmcRequest request, uint searchingPid, string titleId)
        {
            if (!GameSession.TryReadProperties(request, 0, out uint typeId, out uint queryId, out Dictionary<uint, int> criteria))
            {
                Serilog.Log.Warning("[quazal] GameSession.SearchSessions malformed: {Parameters}", Convert.ToHexString(request.Parameters));
                return Quazal.EmptyList;
            }
            var candidates = GameSessionStore.FindByType(titleId, typeId)
                .Where(candidate => candidate.HostPid != searchingPid)
                .ToArray();
            candidates = Playforward.PartySeating.Steer(
                Playforward.PlayforwardTitles.ForTitle(titleId)?.Gateway, searchingPid, candidates).ToArray();
            byte[] result = GameSession.BuildSearchResults(titleId, typeId, candidates, ListedAttributes);
            Serilog.Log.Information(
                "[quazal] GameSession.SearchSessions type={Type} query={Query} criteria=[{Criteria}] pid={Pid} -> {Count} session result(s), {Bytes} bytes",
                typeId, queryId, string.Join(",", criteria.Select(c => $"{c.Key}={c.Value}")), searchingPid, candidates.Length, result.Length);
            return result;
        }

        /// <summary>
        /// SearchSessionsWithParticipants(typeID, qlist&lt;pid&gt;) ->
        /// qlist&lt;GameSessionSearchWithParticipantsResult{qlist&lt;pid&gt;}&gt;:
        /// the rosters of the type's sessions that hold any of the named
        /// players. The game asks with an empty list right after login and
        /// gets an empty list back.
        /// </summary>
        public static byte[]? BuildSearchSessionsWithParticipantsResult(RmcRequest request, string titleId)
        {
            if (!request.TryReadU32(0, out uint typeId) || !TryReadPidList(request, 4, out List<uint> pids, out int end) ||
                end != request.Parameters.Length)
            {
                Serilog.Log.Warning("[quazal] GameSession.SearchSessionsWithParticipants malformed: {Parameters}", Convert.ToHexString(request.Parameters));
                return null;
            }
            var rosters = new List<IReadOnlyList<uint>>();
            if (pids.Count > 0)
                foreach ((uint sessionId, _, _, _) in GameSessionStore.FindByType(titleId, typeId))
                {
                    IReadOnlyList<uint> roster = GameSessionStore.GetParticipants(titleId, typeId, sessionId);
                    if (roster.Any(pids.Contains))
                        rosters.Add(roster);
                }
            using var body = new MemoryStream();
            TicketGranting.WriteU32(body, (uint)rosters.Count);
            foreach (IReadOnlyList<uint> roster in rosters)
            {
                TicketGranting.WriteU32(body, (uint)roster.Count);
                foreach (uint pid in roster)
                    TicketGranting.WriteU32(body, pid);
            }
            Serilog.Log.Information("[quazal] GameSession.SearchSessionsWithParticipants type={Type} pids=[{Pids}] -> {Count} session(s)",
                typeId, string.Join(",", pids), rosters.Count);
            return body.ToArray();
        }

        /// <summary>
        /// LeaveSession and AbandonSession (key): the caller is out of that
        /// session, and a session nobody is left in is gone
        /// </summary>
        public static byte[] BuildLeaveResult(RmcRequest request, uint caller, string method, string titleId)
        {
            if (TryReadKey(request, out uint typeId, out uint sessionId))
            {
                GameSessionStore.RemoveParticipants(titleId, typeId, sessionId, [caller]);
                Playforward.PlayforwardTitles.Nudge(titleId);
                Serilog.Log.Information("[quazal] GameSession.{Method} type={Type} session={Session} pid={Pid} -> {Count} left",
                    method, typeId, sessionId, caller, GameSessionStore.GetParticipantCount(titleId, typeId, sessionId));
            }
            else
                Serilog.Log.Warning("[quazal] GameSession.{Method} malformed: {Parameters}", method, Convert.ToHexString(request.Parameters));
            return [];
        }

        private static bool TryReadKey(RmcRequest request, out uint typeId, out uint sessionId)
        {
            sessionId = 0;
            return request.TryReadU32(0, out typeId) && request.TryReadU32(4, out sessionId);
        }

        private static bool TryReadPidList(RmcRequest request, int offset, out List<uint> pids, out int end)
        {
            pids = [];
            end = offset;
            if (!request.TryReadU32(offset, out uint count) || count > 64)
                return false;
            end = offset + 4;
            for (uint i = 0; i < count; i++, end += 4)
            {
                if (!request.TryReadU32(end, out uint pid)) return false;
                pids.Add(pid);
            }
            return true;
        }
    }

    internal static void WriteU64(Stream stream, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    /// <summary>
    /// A Quazal DateTime: bit-packed, low to high, second (6 bits), minute
    /// (6), hour (5), day (5), month (4), year
    /// </summary>
    internal static ulong QuazalDateTime(DateTimeOffset at)
    {
        DateTime t = at.UtcDateTime;
        return (uint)t.Second | ((ulong)(uint)t.Minute << 6) | ((ulong)(uint)t.Hour << 12) | ((ulong)(uint)t.Day << 17) |
               ((ulong)(uint)t.Month << 22) | ((ulong)(uint)t.Year << 26);
    }
}
