using System.Net;

namespace Plugin.UplayServer.Quazal;

/// <summary>
/// The per-title protocol tables: which (service, method) each title answers,
/// and with what. "Which services does this game have" is data here rather
/// than control flow, so a protocol id is never interpreted without knowing
/// the title
/// </summary>
internal sealed partial class QuazalResponder
{
    /// <summary>
    /// One request in flight: what a handler may need beyond the RMC bytes.
    /// </summary>
    private readonly record struct Call(Peer Peer, IPEndPoint Source, (IPEndPoint Remote, int LocalPort, byte SourcePort) Key)
    {
        /// <summary>The authenticated principal, or 0 on a connection that has not logged in.</summary>
        public uint Pid => Peer.AuthenticatedPid ?? 0;

        /// <summary>The title the peer resolved to at SYN.</summary>
        public QuazalTitle Title => Peer.Title;

        /// <summary>Its slug, which every pid-keyed store is scoped by.</summary>
        public string TitleId => Peer.Title.TitleId;
    }

    private delegate byte[]? RmcHandler(RmcRequest rmc, in Call call);

    /// <summary>
    /// A title family's (service, method) → handler map, built once. A
    /// family's table starts as a copy of the shared base and may override
    /// any entry; nothing is special-cased in the dispatcher.
    /// </summary>
    private sealed class ProtocolTable
    {
        private readonly Dictionary<(int Service, uint Method), RmcHandler> _handlers;

        public ProtocolTable() => _handlers = [];

        private ProtocolTable(ProtocolTable copy) => _handlers = new(copy._handlers);

        public ProtocolTable Derive() => new(this);

        public void Add(int service, uint method, RmcHandler handler) => _handlers[(service, method)] = handler;

        public bool TryGet(int service, uint method, out RmcHandler handler) =>
            _handlers.TryGetValue((service, method), out handler!);
    }

    private readonly Dictionary<QuazalProtocolFamily, ProtocolTable> _tables = [];

    /// <summary>
    /// Builds a table for each family among the served titles. Called from
    /// the constructor, after the settings are in place, since several
    /// handlers read them.
    /// </summary>
    private void BuildProtocolTables(IEnumerable<QuazalTitle> titles)
    {
        ProtocolTable shared = BuildSharedTable();
        foreach (QuazalProtocolFamily family in titles.Select(t => t.ProtocolFamily).Distinct())
        {
            _tables[family] = family switch
            {
                QuazalProtocolFamily.FarCry3 => BuildFarCry3Table(shared),
                QuazalProtocolFamily.Overlord => BuildOverlordTable(shared),
                QuazalProtocolFamily.FutureSoldier => BuildFutureSoldierTable(shared),
                _ => throw new InvalidOperationException($"no protocol table for family {family}"),
            };
        }
    }

    private ProtocolTable TableFor(QuazalTitle title) => _tables[title.ProtocolFamily];

    /// <summary>
    /// The services every RendezVous title speaks identically: the login and
    /// secure-connection handshakes, locale, NAT probing, and the telemetry
    /// configuration. Anything whose meaning depends on the game is not here.
    /// </summary>
    private ProtocolTable BuildSharedTable()
    {
        var t = new ProtocolTable();

        t.Add(TicketGranting.ProtocolId, TicketGranting.MethodLogin,
            (rmc, in c) => BuildLoginReply(rmc, c.Source, c.Peer, settings));
        t.Add(TicketGranting.ProtocolId, TicketGranting.MethodLoginEx,
            (rmc, in c) => BuildLoginReply(rmc, c.Source, c.Peer, settings));
        t.Add(TicketGranting.ProtocolId, TicketGranting.MethodRequestTicket,
            (rmc, in c) => BuildRequestTicketReply(rmc, c.Source, c.Peer, settings));

        t.Add(SecureConnection.ProtocolId, SecureConnection.MethodRegister, (rmc, in c) => BuildRegisterReply(rmc, c.Source, c.Peer));
        t.Add(SecureConnection.ProtocolId, SecureConnection.MethodRegisterEx, (rmc, in c) => BuildRegisterReply(rmc, c.Source, c.Peer));

        t.Add(Localization.ProtocolId, Localization.MethodSetLocaleCode, (rmc, in _) => Localization.BuildSetLocaleCodeResult(rmc));

        t.Add(NatTraversal.ProtocolId, NatTraversal.MethodRequestProbeInitiation, (rmc, in _) => NatTraversal.BuildVoidResult(rmc, "RequestProbeInitiation"));
        t.Add(NatTraversal.ProtocolId, NatTraversal.MethodRequestProbeInitiationExt,
            (rmc, in c) => NatTraversal.BuildRequestProbeInitiationExtResult(rmc, c.Pid, NotifyTarget));
        t.Add(NatTraversal.ProtocolId, NatTraversal.MethodReportNatTraversalResult, (rmc, in _) => NatTraversal.BuildVoidResult(rmc, "ReportNATTraversalResult"));
        t.Add(NatTraversal.ProtocolId, NatTraversal.MethodReportNatProperties, (rmc, in _) => NatTraversal.BuildVoidResult(rmc, "ReportNATProperties"));

        t.Add(TrackingProtocol.ProtocolId, TrackingProtocol.MethodGetConfiguration, (rmc, in _) => TrackingProtocol.BuildGetConfigurationResult());
        t.Add(TrackingProtocol.ProtocolId, TrackingProtocol.MethodSendUserInfo, (rmc, in _) => TrackingProtocol.BuildSendUserInfoResult(rmc));

        return t;
    }

    /// <summary>Far Cry 3: the pre-Overlord services, and its own extensions above 100.</summary>
    private ProtocolTable BuildFarCry3Table(ProtocolTable shared)
    {
        ProtocolTable t = shared.Derive();

        RmcHandler sendTags = (rmc, in c) =>
        {
            QuazalTitle title = c.Title;
            return TrackingProtocol.BuildSendTagsResult(rmc, PlayerBehind(c.Key, c.Peer), (reporter, e) =>
            {
                uint changed = PlayerStatistics.Apply(reporter, e, named => ResolvePlayer(title, named), settings.StatisticsFromTracking);
                if (changed != 0) Fc3MatchLedger.Tell(0, changed, NotifyPlayer);
            });
        };
        t.Add(TrackingProtocol.ProtocolId, TrackingProtocol.MethodSendTag, sendTags);
        t.Add(TrackingProtocol.ProtocolId, TrackingProtocol.MethodSendTags, sendTags);

        AddGameSession(t);
        AddPrivileges(t);
        AddUplayWin(t);

        t.Add(Fc3FriendsPresenceService.ProtocolId, Fc3FriendsPresenceService.MethodUpdateState,
            (rmc, in _) => Fc3FriendsPresenceService.BuildUpdateStateResult(rmc));
        t.Add(Fc3FriendsPresenceService.ProtocolId, Fc3FriendsPresenceService.MethodUpdatePrincipal,
            (rmc, in _) => Fc3FriendsPresenceService.BuildUpdatePrincipalResult(rmc));
        t.Add(Fc3FriendsPresenceService.ProtocolId, Fc3FriendsPresenceService.MethodPostSessionUpdate,
            (rmc, in _) => Fc3FriendsPresenceService.BuildVoidResult(rmc, "PostSessionUpdate"));

        t.Add(Fc3Protocol108Service.ProtocolId, Fc3Protocol108Service.MethodPostSessionUpdate,
            (rmc, in _) => Fc3Protocol108Service.BuildVoidResult(rmc));
        t.Add(Fc3Protocol108Service.ProtocolId, Fc3Protocol108Service.MethodSessionQuery,
            (rmc, in _) => Fc3Protocol108Service.BuildSessionQueryResult(rmc));
        t.Add(Fc3Protocol108Service.ProtocolId, Fc3Protocol108Service.MethodSessionMigrated,
            (rmc, in _) => Fc3Protocol108Service.BuildVoidResult(rmc));

        t.Add(RemoteMonitor.ProtocolId, RemoteMonitor.MethodInit, (rmc, in _) => RemoteMonitor.BuildInitResult(rmc));
        t.Add(RemoteMonitor.ProtocolId, RemoteMonitor.MethodLog, (rmc, in _) => RemoteMonitor.BuildLogResult(rmc));

        t.Add(FriendsCustom.ProtocolId, FriendsCustom.MethodTranslateIds, (rmc, in _) => FriendsCustom.BuildTranslateIdsResult(rmc));
        t.Add(FriendsCustom.ProtocolId, FriendsCustom.MethodLookupByName, (rmc, in c) =>
        {
            QuazalTitle title = c.Title;
            return FriendsCustom.BuildLookupByNameResult(rmc, named => ResolvePlayer(title, named));
        });

        t.Add(News.ProtocolId, News.MethodGetChannels, (rmc, in _) => News.BuildEmptyListResult(rmc, "GetChannels"));
        t.Add(News.ProtocolId, News.MethodGetChannelsByTypes, (rmc, in _) => News.BuildEmptyListResult(rmc, "GetChannelsByTypes"));
        t.Add(News.ProtocolId, News.MethodGetSubscribableChannels, (rmc, in _) => News.BuildEmptyListResult(rmc, "GetSubscribableChannels"));
        t.Add(News.ProtocolId, News.MethodGetSubscribedChannels, (rmc, in _) => News.BuildEmptyListResult(rmc, "GetSubscribedChannels"));
        t.Add(News.ProtocolId, News.MethodGetNewsHeaders, (rmc, in _) => News.BuildEmptyListResult(rmc, "GetNewsHeaders"));
        t.Add(News.ProtocolId, News.MethodGetNewsMessages, (rmc, in _) => News.BuildGetNewsMessagesResult(rmc));
        t.Add(News.ProtocolId, News.MethodGetNumberOfNews, (rmc, in _) => News.BuildNumberOfNewsResult(rmc));
        t.Add(News.ProtocolId, News.MethodGetNewsMessagesByType, (rmc, in _) => News.BuildEmptyListResult(rmc, "GetNewsMessagesByType"));
        t.Add(News.ProtocolId, News.MethodGetNewsHeadersByType, (rmc, in _) => News.BuildGetNewsHeadersByTypeResult(rmc));

        t.Add(TitleStorage.ProtocolId, TitleStorage.MethodBrowseContentsByType, (rmc, in _) => TitleStorage.BuildBrowseContentsByTypeResult(rmc));

        t.Add(UserStorage.ProtocolId, UserStorage.MethodGetOwnContents, (rmc, in _) => UserStorage.BuildGetOwnContentsResult(rmc));
        t.Add(UserStorage.ProtocolId, UserStorage.MethodUploadContent, (rmc, in _) => UserStorage.BuildUploadContentResult(rmc));
        t.Add(UserStorage.ProtocolId, UserStorage.MethodSearchContentsByPlayers, (rmc, in _) => UserStorage.BuildSearchContentsByPlayersResult(rmc));

        t.Add(Fc3MatchmakeExtensionService.ProtocolId, Fc3MatchmakeExtensionService.MethodUnknown1,
            (rmc, in _) => Fc3MatchmakeExtensionService.BuildUnknown1Result(rmc));
        t.Add(Fc3Protocol112Service.ProtocolId, Fc3Protocol112Service.MethodUnknown1,
            (rmc, in _) => Fc3Protocol112Service.BuildUnknown1Result(rmc));
        t.Add(Fc3AccountLinkService.ProtocolId, Fc3AccountLinkService.MethodSubmitPrincipalId,
            (rmc, in _) => Fc3AccountLinkService.BuildSubmitPrincipalIdResult(rmc));

        if (settings.IpToLocationEnabled)
            t.Add(IpToLocationService.ProtocolId, IpToLocationService.MethodGetLocation, (rmc, in _) => IpToLocationService.BuildGetLocationResult(rmc));

        t.Add(Fc3IntelService.ProtocolId, Fc3IntelService.MethodGetIntel, (rmc, in c) => Fc3IntelService.BuildGetIntelResult(rmc, c.Pid));
        t.Add(Fc3IntelService.ProtocolId, Fc3IntelService.MethodAddXp, (rmc, in c) => Fc3IntelService.BuildAddXpResult(rmc, c.Pid));
        t.Add(Fc3IntelService.ProtocolId, Fc3IntelService.MethodReportFound, (rmc, in c) => Fc3IntelService.BuildReportFoundResult(rmc, c.Pid));
        t.Add(Fc3IntelService.ProtocolId, Fc3IntelService.MethodClaimDecoded, (rmc, in c) => Fc3IntelService.BuildClaimDecodedResult(rmc, c.Pid));
        t.Add(Fc3IntelService.ProtocolId, Fc3IntelService.MethodStartDecoding, (rmc, in c) => Fc3IntelService.BuildStartDecodingResult(rmc, c.Pid));

        RmcHandler leaderboardTop = (rmc, in c) => Fc3LeaderboardService.BuildGetTopResult(rmc, KnownPlayerPids(c.Title), settings.LeaderboardProbe);
        t.Add(Fc3LeaderboardService.ProtocolId, Fc3LeaderboardService.MethodGetTop, leaderboardTop);
        t.Add(Fc3LeaderboardService.ProtocolId, Fc3LeaderboardService.MethodGetRegionalTop, leaderboardTop);
        t.Add(Fc3LeaderboardService.ProtocolId, Fc3LeaderboardService.MethodGetByPids, (rmc, in _) => Fc3LeaderboardService.BuildGetByPidsResult(rmc, settings.LeaderboardProbe));
        t.Add(Fc3LeaderboardService.ProtocolId, Fc3LeaderboardService.MethodGetForPlayers, (rmc, in _) => Fc3LeaderboardService.BuildGetForPlayersResult(rmc, settings.LeaderboardProbe));
        RmcHandler leaderboardCount = (rmc, in _) => Fc3LeaderboardService.BuildCountResult(rmc);
        t.Add(Fc3LeaderboardService.ProtocolId, Fc3LeaderboardService.MethodUnknown7, leaderboardCount);
        t.Add(Fc3LeaderboardService.ProtocolId, Fc3LeaderboardService.MethodUnknown8, leaderboardCount);
        t.Add(Fc3LeaderboardService.ProtocolId, Fc3LeaderboardService.MethodUnknown9, leaderboardCount);

        AddNameLookup(t);

        t.Add(Fc3OnlineDataService.ProtocolId, Fc3OnlineDataService.MethodFetchOnlineData,
            (rmc, in c) => Fc3OnlineDataService.BuildFetchOnlineDataResult(rmc, c.Pid));
        t.Add(Fc3OnlineDataService.ProtocolId, Fc3OnlineDataService.MethodStoreOnlineData,
            (rmc, in c) => Fc3OnlineDataService.BuildStoreOnlineDataResult(rmc, c.Pid, c.TitleId, NotifyPlayer));
        t.Add(Fc3OnlineDataService.ProtocolId, Fc3OnlineDataService.MethodDeleteOnlineData,
            (rmc, in c) => Fc3OnlineDataService.BuildDeleteOnlineDataResult(rmc, c.Pid));
        t.Add(Fc3OnlineDataService.ProtocolId, Fc3OnlineDataService.MethodFetchPackages,
            (rmc, in c) => Fc3OnlineDataService.BuildFetchPackagesResult(rmc, settings.FetchPackageDataIds, c.Pid));

        return t;
    }

    /// <summary>
    /// Splinter Cell Blacklist.
    /// </summary>
    private ProtocolTable BuildOverlordTable(ProtocolTable shared)
    {
        ProtocolTable t = shared.Derive();

        RmcHandler sendTags = (rmc, in c) => TrackingProtocol.BuildSendTagsResult(rmc, c.Pid, static (_, _) => { });
        t.Add(TrackingProtocol.ProtocolId, TrackingProtocol.MethodSendTag, sendTags);
        t.Add(TrackingProtocol.ProtocolId, TrackingProtocol.MethodSendTags, sendTags);

        AddGameSession(t);
        AddPrivileges(t);
        AddUplayWin(t);
        AddNameLookup(t);

        t.Add(OverlordCore.ProtocolId, OverlordCore.MethodFetchConfig, (rmc, in _) => OverlordCore.BuildFetchConfigResult(rmc));

        t.Add(BlacklistServices.GameSessionEx.ProtocolId, BlacklistServices.GameSessionEx.MethodSearchSessions,
            (rmc, in c) =>
            {
                QuazalTitle title = c.Title;
                return BlacklistServices.GameSessionEx.BuildSearchSessionsResult(rmc, c.Pid, pid => settings.NameForPid(title, pid), title.TitleId);
            });

        t.Add(BlacklistServices.TrackingExtension.ProtocolId, BlacklistServices.TrackingExtension.MethodGetTrackingUserGroup,
            (rmc, in _) => BlacklistServices.TrackingExtension.BuildUserGroupResult());
        t.Add(BlacklistServices.TrackingExtension.ProtocolId, BlacklistServices.TrackingExtension.MethodGetTrackingUserGroupTags,
            (rmc, in _) => BlacklistServices.TrackingExtension.BuildUserGroupTagsResult());

        RmcHandler emptyChallenges = (rmc, in _) => BlacklistServices.ChallengeHelper.BuildEmptyChallengeList(rmc);
        t.Add(BlacklistServices.ChallengeHelper.ProtocolId, BlacklistServices.ChallengeHelper.MethodGenerateMyFriendChallenges, emptyChallenges);
        t.Add(BlacklistServices.ChallengeHelper.ProtocolId, BlacklistServices.ChallengeHelper.MethodGenerateFriendChallenges, emptyChallenges);
        t.Add(BlacklistServices.ChallengeHelper.ProtocolId, BlacklistServices.ChallengeHelper.MethodGetOnlineChallenges, emptyChallenges);

        RmcHandler noClan = (rmc, in _) => BlacklistServices.ClanHelper.BuildNoClanResult(rmc);
        RmcHandler noClanMembers = (rmc, in _) => BlacklistServices.ClanHelper.BuildEmptyListResult(rmc);
        t.Add(BlacklistServices.ClanHelper.ProtocolId, BlacklistServices.ClanHelper.MethodGetClanInfoByPid, noClan);
        t.Add(BlacklistServices.ClanHelper.ProtocolId, BlacklistServices.ClanHelper.MethodGetClanInfoByClid, noClan);
        t.Add(BlacklistServices.ClanHelper.ProtocolId, BlacklistServices.ClanHelper.MethodGetMemberListByPid, noClanMembers);
        t.Add(BlacklistServices.ClanHelper.ProtocolId, BlacklistServices.ClanHelper.MethodGetMemberListByClid, noClanMembers);
        t.Add(BlacklistServices.ClanHelper.ProtocolId, BlacklistServices.ClanHelper.MethodGenerateClanChallenges, noClanMembers);

        t.Add(BlacklistServices.LadderHelper.ProtocolId, BlacklistServices.LadderHelper.MethodGetUnixUtc,
            (rmc, in _) => BlacklistServices.LadderHelper.BuildUnixUtcResult());
        t.Add(BlacklistServices.LadderHelper.ProtocolId, BlacklistServices.LadderHelper.MethodAreLaddersAvailableInCountry,
            (rmc, in _) => BlacklistServices.LadderHelper.BuildBoolResult(true, "AreLaddersAvailableInCountry"));
        t.Add(BlacklistServices.LadderHelper.ProtocolId, BlacklistServices.LadderHelper.MethodCheckLadderIsRunning,
            (rmc, in _) => BlacklistServices.LadderHelper.BuildBoolResult(false, "CheckLadderIsRunning"));

        t.Add(BlacklistServices.PlayerStats.ProtocolId, BlacklistServices.PlayerStats.MethodWriteStats,
            (rmc, in c) => BlacklistServices.PlayerStats.BuildWriteStatsResult(rmc, c.Pid, c.TitleId));
        t.Add(BlacklistServices.PlayerStats.ProtocolId, BlacklistServices.PlayerStats.MethodReadStatsByPlayers,
            (rmc, in c) =>
            {
                QuazalTitle title = c.Title;
                return BlacklistServices.PlayerStats.BuildReadStatsByPlayersResult(rmc, pid => settings.NameForPid(title, pid), title.TitleId);
            });
        t.Add(BlacklistServices.PlayerStats.ProtocolId, BlacklistServices.PlayerStats.MethodReadLeaderboardsNearPlayer,
            (rmc, in c) =>
            {
                QuazalTitle title = c.Title;
                return BlacklistServices.PlayerStats.BuildReadLeaderboardsNearPlayerResult(rmc, pid => settings.NameForPid(title, pid), title.TitleId);
            });
        t.Add(BlacklistServices.PlayerStats.ProtocolId, BlacklistServices.PlayerStats.MethodReadLeaderboardsByRank,
            (rmc, in c) =>
            {
                QuazalTitle title = c.Title;
                return BlacklistServices.PlayerStats.BuildReadLeaderboardsByRankResult(rmc, pid => settings.NameForPid(title, pid), title.TitleId);
            });
        t.Add(BlacklistServices.PlayerStats.ProtocolId, BlacklistServices.PlayerStats.MethodReadLeaderboardsByPlayers,
            (rmc, in c) =>
            {
                QuazalTitle title = c.Title;
                return BlacklistServices.PlayerStats.BuildReadLeaderboardsByPlayersResult(rmc, pid => settings.NameForPid(title, pid), title.TitleId);
            });

        t.Add(BlacklistServices.BlacklistUserStorage.ProtocolId, BlacklistServices.BlacklistUserStorage.MethodSearchContents,
            (rmc, in _) => BlacklistServices.BlacklistUserStorage.BuildSearchContentsResult(rmc));
        t.Add(BlacklistServices.BlacklistUserStorage.ProtocolId, BlacklistServices.BlacklistUserStorage.MethodSearchContentsWithTotal,
            (rmc, in _) => BlacklistServices.BlacklistUserStorage.BuildSearchContentsWithTotalResult(rmc));

        t.Add(BlacklistServices.UbiAccountManagement.ProtocolId, BlacklistServices.UbiAccountManagement.MethodHasAcceptedLatestTos,
            (rmc, in _) => BlacklistServices.UbiAccountManagement.BuildHasAcceptedLatestTosResult());

        t.Add(BlacklistServices.OverlordNews.ProtocolId, BlacklistServices.OverlordNews.MethodGetNews,
            (rmc, in _) => BlacklistServices.OverlordNews.BuildEmptyNewsResult(rmc));
        t.Add(BlacklistServices.OverlordChallenge.ProtocolId, BlacklistServices.OverlordChallenge.MethodGetChallenges,
            (rmc, in _) => BlacklistServices.OverlordChallenge.BuildEmptyChallengeList(rmc));

        return t;
    }

    /// <summary>
    /// Ghost Recon Future Soldier
    /// </summary>
    private ProtocolTable BuildFutureSoldierTable(ProtocolTable shared)
    {
        ProtocolTable t = shared.Derive();

        RmcHandler sendTags = (rmc, in c) => TrackingProtocol.BuildSendTagsResult(rmc, c.Pid, static (_, _) => { });
        t.Add(TrackingProtocol.ProtocolId, TrackingProtocol.MethodSendTag, sendTags);
        t.Add(TrackingProtocol.ProtocolId, TrackingProtocol.MethodSendTags, sendTags);
        t.Add(TrackingProtocol.ProtocolId, TrackingProtocol.MethodGetConfiguration,
            (rmc, in _) => FutureSoldierServices.Telemetry.BuildGetConfigurationResult());

        AddGameSession(t);
        // This game's DDL and its working server differ from Far Cry 3's on
        // these (see FutureSoldierServices.Sessions).
        t.Add(GameSession.ProtocolId, GameSession.MethodAddParticipants,
            (rmc, in c) => FutureSoldierServices.Sessions.BuildAddParticipantsResult(rmc, c.Pid, NotifyPlayer, c.TitleId));
        t.Add(GameSession.ProtocolId, GameSession.MethodSearchSessions,
            (rmc, in c) => FutureSoldierServices.Sessions.BuildSearchSessionsResult(rmc, c.Pid, c.TitleId));
        t.Add(GameSession.ProtocolId, FutureSoldierServices.Sessions.MethodSearchSessionsWithParticipants,
            (rmc, in c) => FutureSoldierServices.Sessions.BuildSearchSessionsWithParticipantsResult(rmc, c.TitleId));
        t.Add(GameSession.ProtocolId, GameSession.MethodLeaveSession,
            (rmc, in c) => FutureSoldierServices.Sessions.BuildLeaveResult(rmc, c.Pid, "LeaveSession", c.TitleId));
        t.Add(GameSession.ProtocolId, FutureSoldierServices.Sessions.MethodAbandonSession,
            (rmc, in c) => FutureSoldierServices.Sessions.BuildLeaveResult(rmc, c.Pid, "AbandonSession", c.TitleId));

        AddPrivileges(t);
        t.Add(Privileges.ProtocolId, Privileges.MethodGetPrivilegesEx,
            (rmc, in _) => FutureSoldierServices.PrivilegesEx.BuildGetPrivilegesExResult(rmc));
        AddNameLookup(t);
        // Uplay Win rides id 120 here (uplaywinservices.cpp registers it as an
        // extra protocol), not the 49 Far Cry 3 and Blacklist use.
        AddUplayWin(t, FutureSoldierServices.UplayWinProtocolId);
        t.Add(FutureSoldierServices.UplayWinProtocolId, UplayWin.MethodGetRewards,
            (rmc, in _) => FutureSoldierServices.Rewards.BuildGetRewardsResult(rmc));

        // The DLC catalogue, when this server has one to offer (see
        // FutureSoldierTitleStorage); the shared empty answer otherwise.
        t.Add(TitleStorage.ProtocolId, TitleStorage.MethodBrowseContentsByType,
            (rmc, in _) => FutureSoldierTitleStorage.BuildBrowseContentsByTypeResult(rmc));
        t.Add(TitleStorage.ProtocolId, FutureSoldierTitleStorage.MethodDownloadContent,
            (rmc, in _) => FutureSoldierTitleStorage.BuildDownloadContentResult(rmc));

        t.Add(FutureSoldierServices.Friends.ProtocolId, FutureSoldierServices.Friends.MethodGetRelationships,
            (rmc, in _) => FutureSoldierServices.Friends.BuildGetRelationshipsResult(rmc));
        t.Add(FutureSoldierServices.Friends.ProtocolId, FutureSoldierServices.Friends.MethodGetDetailedList,
            (rmc, in _) => FutureSoldierServices.Friends.BuildGetDetailedListResult(rmc));
        t.Add(FutureSoldierServices.Protocol47.ProtocolId, FutureSoldierServices.Protocol47.MethodUnknown7,
            (rmc, in _) => FutureSoldierServices.Protocol47.BuildZeroResult(rmc));
        t.Add(FutureSoldierServices.TrackingExtension.ProtocolId, FutureSoldierServices.TrackingExtension.MethodGetTrackingUserGroup,
            (rmc, in _) => FutureSoldierServices.TrackingExtension.BuildUserGroupResult(rmc));

        t.Add(FutureSoldierServices.PlayerStatistics.ProtocolId, FutureSoldierServices.PlayerStatistics.MethodWriteStats32,
            (rmc, in c) => FutureSoldierServices.PlayerStatistics.BuildWriteResult(rmc, c.Pid, c.TitleId, 4));
        t.Add(FutureSoldierServices.PlayerStatistics.ProtocolId, FutureSoldierServices.PlayerStatistics.MethodWriteStats64,
            (rmc, in c) => FutureSoldierServices.PlayerStatistics.BuildWriteResult(rmc, c.Pid, c.TitleId, 8));
        t.Add(FutureSoldierServices.PlayerStatistics.ProtocolId, FutureSoldierServices.PlayerStatistics.MethodReadStats32,
            (rmc, in c) => FutureSoldierServices.PlayerStatistics.BuildReadResult(rmc, c.TitleId, 4));
        t.Add(FutureSoldierServices.PlayerStatistics.ProtocolId, FutureSoldierServices.PlayerStatistics.MethodReadStats64,
            (rmc, in c) => FutureSoldierServices.PlayerStatistics.BuildReadResult(rmc, c.TitleId, 8));

        t.Add(FutureSoldierServices.RichPresence.ProtocolId, FutureSoldierServices.RichPresence.MethodSetPresence,
            (rmc, in _) => FutureSoldierServices.RichPresence.BuildVoidResult(rmc));

        t.Add(FutureSoldierServices.Clans.ProtocolId, FutureSoldierServices.Clans.MethodUnknown13,
            (rmc, in _) => FutureSoldierServices.Clans.BuildZeroU32Result(rmc, "method 13"));
        t.Add(FutureSoldierServices.Clans.ProtocolId, FutureSoldierServices.Clans.MethodUnknown26,
            (rmc, in _) => FutureSoldierServices.Clans.BuildZeroU32Result(rmc, "method 26"));
        t.Add(FutureSoldierServices.Clans.ProtocolId, FutureSoldierServices.Clans.MethodUnknown28,
            (rmc, in _) => FutureSoldierServices.Clans.BuildZeroU64Result(rmc));

        t.Add(FutureSoldierServices.Misc.ProtocolId, FutureSoldierServices.Misc.MethodGetMatchId,
            (rmc, in c) => FutureSoldierServices.Misc.BuildGetMatchIdResult(rmc, c.TitleId));
        t.Add(FutureSoldierServices.Misc.ProtocolId, FutureSoldierServices.Misc.MethodGetSettings,
            (rmc, in _) => FutureSoldierServices.Misc.BuildGetSettingsResult(rmc));
        t.Add(FutureSoldierServices.Misc.ProtocolId, FutureSoldierServices.Misc.MethodUnknown4,
            (rmc, in _) => FutureSoldierServices.Misc.BuildVoidResult(rmc));

        t.Add(FutureSoldierServices.MapList.ProtocolId, FutureSoldierServices.MapList.MethodGetAvailableMapCategories,
            (rmc, in _) => FutureSoldierServices.MapList.BuildGetAvailableMapCategoriesResult(rmc));
        t.Add(FutureSoldierServices.MapList.ProtocolId, FutureSoldierServices.MapList.MethodGetMapList,
            (rmc, in _) => FutureSoldierServices.MapList.BuildGetMapListResult(rmc, settings.FutureSoldierQuickMatchMaps));

        t.Add(OverlordCore.ProtocolId, OverlordCore.MethodFetchConfig,
            (rmc, in _) => FutureSoldierServices.OverlordSettings.BuildRequestSettingsResult(rmc));
        t.Add(FutureSoldierServices.OverlordNews.ProtocolId, FutureSoldierServices.OverlordNews.MethodGetNews,
            (rmc, in _) => FutureSoldierServices.OverlordNews.BuildGetNewsResult(rmc));
        t.Add(FutureSoldierServices.OverlordDare.ProtocolId, FutureSoldierServices.OverlordDare.MethodUnknown1,
            (rmc, in _) => FutureSoldierServices.OverlordDare.BuildUnknown1Result(rmc));
        t.Add(FutureSoldierServices.OverlordTracking.ProtocolId, FutureSoldierServices.OverlordTracking.MethodSendEvent,
            (rmc, in _) => FutureSoldierServices.OverlordTracking.BuildSendEventResult(rmc));

        return t;
    }

    private void AddGameSession(ProtocolTable t)
    {
        t.Add(GameSession.ProtocolId, GameSession.MethodCreateSession, (rmc, in c) => GameSession.BuildCreateSessionResult(rmc, c.Pid, c.TitleId));
        t.Add(GameSession.ProtocolId, GameSession.MethodUpdateSession, (rmc, in c) => GameSession.BuildUpdateSessionResult(rmc, c.Pid, c.TitleId));
        t.Add(GameSession.ProtocolId, GameSession.MethodDeleteSession, (rmc, in _) => GameSession.BuildVoidResult(rmc, "DeleteSession"));
        t.Add(GameSession.ProtocolId, GameSession.MethodMigrateSession, (rmc, in c) => GameSession.BuildMigrateSessionResult(rmc, c.Pid, c.TitleId));
        t.Add(GameSession.ProtocolId, GameSession.MethodLeaveSession, (rmc, in _) => GameSession.BuildVoidResult(rmc, "LeaveSession"));
        t.Add(GameSession.ProtocolId, GameSession.MethodGetSession, (rmc, in c) => GameSession.BuildGetSessionResult(rmc, c.TitleId));
        t.Add(GameSession.ProtocolId, GameSession.MethodSendInvitation, (rmc, in c) => GameSession.BuildSendInvitationResult(rmc, c.Pid, NotifyPlayer));
        t.Add(GameSession.ProtocolId, GameSession.MethodSearchSessions, (rmc, in c) => GameSession.BuildSearchSessionsResult(rmc, c.Pid, c.TitleId));
        t.Add(GameSession.ProtocolId, GameSession.MethodAddParticipants, (rmc, in c) => GameSession.BuildAddParticipantsResult(rmc, c.Pid, NotifyPlayer, c.TitleId));
        t.Add(GameSession.ProtocolId, GameSession.MethodRemoveParticipants, (rmc, in c) => GameSession.BuildRemoveParticipantsResult(rmc, c.Pid, c.TitleId));
        t.Add(GameSession.ProtocolId, GameSession.MethodGetParticipantCount, (rmc, in c) => GameSession.BuildParticipantCountResult(rmc, c.TitleId));
        t.Add(GameSession.ProtocolId, GameSession.MethodGetParticipants, (rmc, in _) => GameSession.BuildEmptyListResult(rmc, "GetParticipants"));
        t.Add(GameSession.ProtocolId, GameSession.MethodRegisterUrls, (rmc, in c) => GameSession.BuildRegisterUrlsResult(rmc, c.Pid, c.TitleId));
        t.Add(GameSession.ProtocolId, GameSession.MethodJoinSession, (rmc, in _) => GameSession.BuildVoidResult(rmc, "JoinSession"));
        t.Add(GameSession.ProtocolId, GameSession.MethodPostLobbyNotification, (rmc, in _) => GameSession.BuildVoidResult(rmc, "PostLobbyNotification"));
    }

    private static void AddPrivileges(ProtocolTable t)
    {
        t.Add(Privileges.ProtocolId, Privileges.MethodGetPrivileges, (rmc, in _) => Privileges.BuildGetPrivilegesResult(rmc));
        t.Add(Privileges.ProtocolId, Privileges.MethodGetExpiredPrivileges, (rmc, in _) => Privileges.BuildEmptyPrivilegeListResult(rmc, "GetExpiredPrivileges"));
        t.Add(Privileges.ProtocolId, Privileges.MethodGetPrivilegesEx, (rmc, in _) => Privileges.BuildEmptyPrivilegeListResult(rmc, "GetPrivilegesEx"));
    }

    /// <summary>
    /// FC3 walks this service method by method, and every documented result
    /// here is either a list or a scalar, so the whole surface is answered at
    /// once rather than one game restart at a time.
    /// </summary>
    private static void AddUplayWin(ProtocolTable t, int protocolId = UplayWin.ProtocolId)
    {
        t.Add(protocolId, UplayWin.MethodGetActions, (rmc, in _) => UplayWin.BuildEmptyListResult(rmc, "GetActions"));
        t.Add(protocolId, UplayWin.MethodGetActionsCompleted, (rmc, in _) => UplayWin.BuildEmptyListResult(rmc, "GetActionsCompleted"));
        t.Add(protocolId, UplayWin.MethodGetActionsCount, (rmc, in _) => UplayWin.BuildZeroCountResult(rmc, "GetActionsCount"));
        t.Add(protocolId, UplayWin.MethodGetActionsCompletedCount, (rmc, in _) => UplayWin.BuildZeroCountResult(rmc, "GetActionsCompletedCount"));
        t.Add(protocolId, UplayWin.MethodGetRewards, (rmc, in _) => UplayWin.BuildEmptyListResult(rmc, "GetRewards"));
        t.Add(protocolId, UplayWin.MethodGetRewardsPurchased, (rmc, in _) => UplayWin.BuildEmptyListResult(rmc, "GetRewardsPurchased"));
        t.Add(protocolId, UplayWin.MethodUplayWelcome, (rmc, in _) => UplayWin.BuildEmptyListResult(rmc, "UplayWelcome"));
        t.Add(protocolId, UplayWin.MethodSetActionsCompleted, (rmc, in _) => UplayWin.BuildEmptyListResult(rmc, "SetActionsCompleted"));
        t.Add(protocolId, UplayWin.MethodGetUserToken, (rmc, in _) => UplayWin.BuildUserTokenResult(rmc));
        t.Add(protocolId, UplayWin.MethodGetVirtualCurrencyUserBalance, (rmc, in _) => UplayWin.BuildZeroCountResult(rmc, "GetVirtualCurrencyUserBalance"));
        t.Add(protocolId, UplayWin.MethodGetSectionsByKey, (rmc, in _) => UplayWin.BuildEmptyListResult(rmc, "GetSectionsByKey"));
    }

    /// <summary>
    /// Protocol 29's lookup methods. In Far Cry 3 this is the name-lookup
    /// service.
    /// </summary>
    private void AddNameLookup(ProtocolTable t)
    {
        t.Add(NameLookupService.ProtocolId, NameLookupService.MethodNamesForPids, (rmc, in c) =>
        {
            QuazalTitle title = c.Title;
            return NameLookupService.BuildNamesForPidsResult(rmc, pid => settings.NameForPid(title, pid));
        });
        t.Add(NameLookupService.ProtocolId, NameLookupService.MethodDisplayNamesForNames, (rmc, in _) => NameLookupService.BuildDisplayNamesForNamesResult(rmc));
    }
}
