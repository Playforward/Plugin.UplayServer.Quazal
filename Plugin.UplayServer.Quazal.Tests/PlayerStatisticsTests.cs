using System.Buffers.Binary;
using System.Text;
using Plugin.UplayServer.Quazal.Playforward;

namespace Plugin.UplayServer.Quazal.Tests;

/// <summary>
/// Statistics aggregation: the game's tracking events, in the shape the
/// retail client sends them, counted into the persistent player row.
/// </summary>
[Collection("OnlineDataStore")]
public sealed class PlayerStatisticsTests : IDisposable
{
    private const uint Alice = 2_000_000;
    private const uint Bob = 2_000_001;
    private const uint Telemetry = 0;

    public void Dispose() => OnlineDataStore.Detach();

    private static InMemoryProgressStore Playforward()
    {
        var store = new InMemoryProgressStore(Alice);
        store.ResolvePid("alice", "A");
        store.ResolvePid("bob", "B");
        OnlineDataStore.Detach();
        OnlineDataStore.Attach(TestTitle.Id, store, pid => pid is Alice or Bob);
        return store;
    }

    private static uint? Resolve(string named) => named switch
    {
        "Alice" or "2000000" => Alice,
        "Bob" or "2000001" => Bob,
        _ => null,
    };

    /// <summary>A SendTags request with the given events, in the captured TrackingTag layout.</summary>
    private static RmcRequest SendTags(params (string Tag, string Attributes)[] events)
    {
        var body = new List<byte>();
        body.AddRange(BitConverter.GetBytes((uint)events.Length));
        foreach ((string tag, string attributes) in events)
        {
            body.AddRange(BitConverter.GetBytes(0u));                 // trackingID
            Str(body, tag); Str(body, attributes);
            body.AddRange(BitConverter.GetBytes(0u));                 // deltaTime
            Str(body, "");                                            // newUserId
        }
        return new RmcRequest(TrackingProtocol.ProtocolId, 1, TrackingProtocol.MethodSendTags, [.. body]);
    }

    private static void Str(List<byte> body, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        body.AddRange(BitConverter.GetBytes((ushort)(bytes.Length + 1)));
        body.AddRange(bytes); body.Add(0);
    }

    private static byte[] Feed(uint reporter, RmcRequest request) =>
        TrackingProtocol.BuildSendTagsResult(request, reporter, (pid, e) => PlayerStatistics.Apply(pid, e, Resolve));

    [Fact]
    public void ParsesTheCapturedSendTagsBytes()
    {
        // The retail client's first SendTags to the community server
        // (2026-09-15 capture): three tags, including one with a 372 s delta.
        const string captured =
            "03000000000000000b0047414d455f5354415254002f0056455253494f4e3d302e312e3026455845433d666172637279335f64336431312e65786526504c4159455249443d000000000001000000000000090047414d455f4c4f43002c00415544494f4c414e473d656e676c697368265355425453544154453d3026535542544c414e473d4e4f4e45000000000001000000000000090050435f535045435300a40050524f433d414d4426435055533d382650524f4353504545443d323739392652414d3d3134383034264f533d26564944454f3d4154492656434152443d414d4420437573746f6d2047505520303933322028524144562056414e474f474829265652414d3d32303438264e455453504545443d31265245534f4c3d31323830207820383030264e554d444953503d3126434f4e54523d31264d49433d554e4b4e4f574e0074010000010000";
        var seen = new List<TrackingProtocol.TrackingEvent>();
        byte[] reply = TrackingProtocol.BuildSendTagsResult(
            new RmcRequest(TrackingProtocol.ProtocolId, 1, TrackingProtocol.MethodSendTags, Convert.FromHexString(captured)),
            Telemetry, (_, e) => seen.Add(e));

        Assert.Empty(reply);
        Assert.Equal(["GAME_START", "GAME_LOC", "PC_SPECS"], seen.Select(e => e.Tag));
        Assert.Equal("english", seen[1].Attributes["AUDIOLANG"]);
        Assert.Equal(372u, seen[2].DeltaTime);
        Assert.Equal("8", seen[2].Attributes["CPUS"]);
    }

    [Fact]
    public void TheConfigurationNamesTheWorkingServersTags()
    {
        byte[] body = TrackingProtocol.BuildGetConfigurationResult();
        Assert.Equal(1212, body.Length);
        Assert.Equal(77u, BinaryPrimitives.ReadUInt32LittleEndian(body));
        Assert.Contains("PLAYER_KILL\0", Encoding.ASCII.GetString(body));
    }

    // The two FPSCLIENT_STOP events of the 2026-09-15 match, verbatim: the
    // host (Blue, one kill with the P416) and the other player (Red, one death).
    private const string HostMatchEnd =
        "MATCHID=14600588199604146448&PLAYERID=&MPMODE=FCXTeamDeathMatch&MAP=pvp_sof12&TEAM=Blue&WINTEAM=Blue&PLAYERLEVEL=2&SCORE=750&REASON=TimeOut&TIME=59.0" +
        "&data=PL_KL[P416_1_FCXTeamDeathMatch_0xE1FA98D7:CTR!1]GD_USD[P416:CTR!2;HITS!5;BULLETSFIRED!8;KILLS!1;DUR!54|KW1911:CTR!1;HITS!0;BULLETSFIRED!7;KILLS!0;DUR!4]";
    private const string OtherMatchEnd =
        "MATCHID=14600588199604146448&PLAYERID=&MPMODE=FCXTeamDeathMatch&MAP=pvp_sof12&TEAM=Red&WINTEAM=Blue&PLAYERLEVEL=1&SCORE=0&REASON=TimeOut&TIME=59.0" +
        "&data=PL_DTH[idle_P416_FCXTeamDeathMatch:CTR!1]GD_USD[P416:CTR!2;HITS!0;BULLETSFIRED!27;KILLS!0;DUR!49]";

    [Fact]
    public void TheMatchEndEventCountsKillsDeathsTheMatchAndItsResult()
    {
        Playforward();

        // Each client's telemetry session reports its own match; the
        // responder has already worked out whose connection it is.
        Feed(Alice, SendTags(("FPSCLIENT_STOP", HostMatchEnd), ("FPSHOST_STOP", "MATCHID=14600588199604146448&MPMODE=FCXTeamDeathMatch&MAP=pvp_sof12&REASON=TimeOut&TIME=59.0")));
        Feed(Bob, SendTags(("FPSCLIENT_STOP", OtherMatchEnd)));

        Assert.Equal(1u, PlayerStatistics.Get(Alice, "kills"));
        Assert.Equal(1u, PlayerStatistics.Get(Alice, "kills_pvp"));
        Assert.Equal(0u, PlayerStatistics.Get(Alice, "deaths"));
        Assert.Equal(1u, PlayerStatistics.Get(Alice, "match_played"));
        Assert.Equal(1u, PlayerStatistics.Get(Alice, "match_wins"));
        Assert.Equal(0u, PlayerStatistics.Get(Alice, "match_losses"));
        Assert.Equal(59u, PlayerStatistics.Get(Alice, "playtime_pvp"));

        Assert.Equal(0u, PlayerStatistics.Get(Bob, "kills"));
        Assert.Equal(1u, PlayerStatistics.Get(Bob, "deaths"));
        Assert.Equal(1u, PlayerStatistics.Get(Bob, "deaths_pvp"));
        Assert.Equal(1u, PlayerStatistics.Get(Bob, "match_losses"));
        Assert.Equal(59u, PlayerStatistics.Get(Bob, "playtime"));
    }

    [Fact]
    public void KillsAreSummedAcrossWeaponsAndModes()
    {
        var data = PlayerStatistics.ParseData("PL_KL[P416_1_TDM_0x1:CTR!2|KW1911_1_TDM_0x1:CTR!1]PL_DTH[idle_P416_TDM:CTR!3]GD_USD[P416:CTR!2;HITS!5;BULLETSFIRED!8;KILLS!1;DUR!54]");
        Assert.Equal(["PL_KL", "PL_DTH", "GD_USD"], data.Keys);
        Assert.Equal(2, data["PL_KL"].Count);
        Assert.Equal(2u, data["PL_KL"][0].Fields["CTR"]);
        Assert.Equal(3u, data["PL_DTH"].Single().Fields["CTR"]);
        Assert.Equal(8u, data["GD_USD"].Single().Fields["BULLETSFIRED"]);

        Playforward();
        Feed(Alice, SendTags(("FPSCLIENT_STOP", "TEAM=Blue&WINTEAM=Red&TIME=600.0&data=PL_KL[P416_1_TDM_0x1:CTR!2|KW1911_1_TDM_0x1:CTR!1]PL_DTH[idle_P416_TDM:CTR!3]")));
        Assert.Equal(3u, PlayerStatistics.Get(Alice, "kills"));
        Assert.Equal(3u, PlayerStatistics.Get(Alice, "deaths"));
        Assert.Equal(1u, PlayerStatistics.Get(Alice, "match_losses"));
        Assert.Equal(600u, PlayerStatistics.Get(Alice, "playtime"));
    }

    [Fact]
    public void ADrawIsNeitherAWinNorALoss()
    {
        Playforward();
        // Two timed-out matches on 2026-09-15 ended WINTEAM=Draw and were
        // counted as losses for everybody.
        Feed(Alice, SendTags(("FPSCLIENT_STOP", "TEAM=Red&WINTEAM=Draw&PLAYERLEVEL=3&SCORE=0&REASON=TimeOut&TIME=59.0&data=GD_USD[P416:CTR!2;HITS!0;BULLETSFIRED!3;KILLS!0;DUR!20]")));
        Assert.Equal(1u, PlayerStatistics.Get(Alice, "match_played"));
        Assert.Equal(0u, PlayerStatistics.Get(Alice, "match_wins"));
        Assert.Equal(0u, PlayerStatistics.Get(Alice, "match_losses"));
    }

    [Fact]
    public void AMatchPublishesEveryBoardThePublicLeaderboardReads()
    {
        InMemoryProgressStore store = Playforward();
        // 2600 XP is where the player track's rank 4 begins.
        Feed(Alice, SendTags(("FPSCLIENT_STOP", "TEAM=Blue&WINTEAM=Blue&SCORE=2600&TIME=600.0&data=PL_KL[P416_1_TDM_0x1:CTR!5]PL_DTH[idle_P416_TDM:CTR!2]")));

        Assert.Equal(2600, store.Score(Alice, "xp"));
        Assert.Equal(5, store.Score(Alice, "kills"));
        Assert.Equal(2, store.Score(Alice, "deaths"));
        Assert.Equal(1, store.Score(Alice, "match_wins"));
        Assert.Equal(1, store.Score(Alice, "match_played"));
        Assert.Equal(600, store.Score(Alice, "playtime"));
        Assert.Equal(4, store.Score(Alice, PlayerStatistics.LevelBoard));
        // Published even at zero: a player who has played is on every board.
        Assert.Equal(0, store.Score(Alice, "match_losses"));
        Assert.Null(store.Score(Bob, "xp"));
    }

    [Fact]
    public void TheMatchStartAndPerKillTagsCountNothing()
    {
        Playforward();
        // The configuration names PLAYER_KILL, but a match never sends it;
        // counting it as well as the match end would count twice.
        Feed(Alice, SendTags(
            ("FPSCLIENT_START", "MATCHID=1&PLAYERID=&MPMODE=FCXTeamDeathMatch&MAP=pvp_sof12&TEAM=Blue&TIME=114.0"),
            ("PLAYER_KILL", "MATCHID=1&PLAYERID=Alice&VICTIM=Bob&HEADSHOT=1&REASON=KW1911"),
            ("MM_STOP", "NAT=OPEN&RSTOPPLAYERS=2")));
        Assert.Empty(PlayerStatistics.For(Alice));
    }

    [Fact]
    public void TheTelemetryAccountsEventsAreNotCreditedToAnybody()
    {
        Playforward();
        // A match end from a connection with no player behind it has no
        // subject; crediting pid 0 -- or the telemetry account -- would be an
        // invented statistic.
        Feed(Telemetry, SendTags(("FPSCLIENT_STOP", HostMatchEnd)));
        Assert.Equal(0u, PlayerStatistics.Get(Alice, "kills"));
        Assert.Equal(0u, PlayerStatistics.Get(Bob, "match_played"));
    }

    [Fact]
    public void ANamedPlayerIdWinsOverTheConnection()
    {
        Playforward();
        Feed(Alice, SendTags(("FPSCLIENT_STOP", "PLAYERID=Bob&TEAM=Red&WINTEAM=Red&TIME=10.0&data=PL_KL[x:CTR!1]")));
        Assert.Equal(1u, PlayerStatistics.Get(Bob, "kills"));
        Assert.Equal(0u, PlayerStatistics.Get(Alice, "kills"));
    }

    [Fact]
    public void CountersSurviveARestartAndReachThePlayerRow()
    {
        InMemoryProgressStore playforward = Playforward();
        Feed(Alice, SendTags(("FPSCLIENT_STOP", "TEAM=Blue&WINTEAM=Blue&TIME=59.0&data=PL_KL[P416_1_TDM_0x1:CTR!2]PL_DTH[idle_P416_TDM:CTR!1]")));

        // A restart, or Alice's next login on another server of the title.
        OnlineDataStore.Detach();
        OnlineDataStore.Attach(TestTitle.Id, playforward, pid => pid is Alice or Bob);
        Assert.Equal(2u, PlayerStatistics.Get(Alice, "kills"));

        // And the 0x06 row the profile reads carries them, even with no XP yet.
        var parameters = new List<byte>();
        parameters.AddRange(BitConverter.GetBytes(Fc3StatisticDefinitions.StatisticPlayerDataId));
        parameters.AddRange(BitConverter.GetBytes(0u));
        parameters.AddRange(BitConverter.GetBytes(1u));
        parameters.AddRange(Fc3Wire.Column("client_id", Alice).Raw);
        parameters.AddRange(BitConverter.GetBytes(10u));
        parameters.AddRange(BitConverter.GetBytes((ushort)0));
        byte[] body = Fc3OnlineDataService.BuildFetchOnlineDataResult(
            new RmcRequest(Fc3OnlineDataService.ProtocolId, 1, Fc3OnlineDataService.MethodFetchOnlineData, [.. parameters]), Alice);
        int offset = 0;
        Assert.True(OnlineDataRowSet.TryParse(body, ref offset, out OnlineDataRowSet rows));
        List<OnlineDataColumn> row = Assert.Single(rows.Rows);
        Assert.Equal(2u, row.Single(c => c.Name == "kills").NumericValue);
        Assert.Equal(1u, row.Single(c => c.Name == "deaths").NumericValue);
        Assert.Equal(0u, row.Single(c => c.Name == "xp").NumericValue);
    }

    private static List<List<OnlineDataColumn>> Rows(uint pid, uint dataId)
    {
        var parameters = new List<byte>();
        parameters.AddRange(BitConverter.GetBytes(dataId));
        parameters.AddRange(BitConverter.GetBytes(0u));
        parameters.AddRange(BitConverter.GetBytes(1u));
        parameters.AddRange(Fc3Wire.Column("client_id", pid).Raw);
        parameters.AddRange(BitConverter.GetBytes(150u));
        parameters.AddRange(BitConverter.GetBytes((ushort)0));
        byte[] body = Fc3OnlineDataService.BuildFetchOnlineDataResult(
            new RmcRequest(Fc3OnlineDataService.ProtocolId, 1, Fc3OnlineDataService.MethodFetchOnlineData, [.. parameters]), pid);
        int offset = 0;
        Assert.True(OnlineDataRowSet.TryParse(body, ref offset, out OnlineDataRowSet rows));
        return rows.Rows;
    }

    private static uint Value(List<OnlineDataColumn> row, string name) => row.Single(c => c.Name == name).NumericValue;

    [Fact]
    public void TheWeaponIdIsTheCrc32OfTheLowerCaseName()
    {
        // The host's upload of 2026-09-17 carried 359465410 for the STG-90 the
        // telemetry calls STGW-90.
        Assert.Equal(359465410u, Fc3StringId.Of("STGW-90"));
        Assert.Equal(359465410u, Fc3StringId.Of("stgw-90"));
    }

    [Fact]
    public void TheMatchEndYieldsTheWeaponAndTimeSliceRowsTheHostWouldHaveUploaded()
    {
        Playforward();
        Feed(Bob, SendTags(("FPSHOST_START", "MATCHID=14600588199604146448&MPMODE=FCXTeamDeathMatch&MAP=pvp_sof12")));
        Feed(Alice, SendTags(("FPSCLIENT_STOP", HostMatchEnd)));

        List<List<OnlineDataColumn>> weapons = Rows(Alice, 0x08);
        Assert.Equal(2, weapons.Count);
        List<OnlineDataColumn> rifle = weapons.Single(row => Value(row, "weapon_id") == Fc3StringId.Of("p416"));
        Assert.Equal(8u, Value(rifle, "weapon_shots_fired"));
        Assert.Equal(5u, Value(rifle, "weapon_shots_hit"));
        Assert.Equal(1u, Value(rifle, "weapon_kill"));
        Assert.Equal(54u, Value(rifle, "weapon_time"));
        Assert.Equal(100u, Value(rifle, "xp"));
        List<OnlineDataColumn> pistol = weapons.Single(row => Value(row, "weapon_id") == Fc3StringId.Of("kw1911"));
        Assert.Equal(0u, Value(pistol, "weapon_kill"));
        Assert.Equal(7u, Value(pistol, "weapon_shots_fired"));

        List<OnlineDataColumn> slice = Assert.Single(Rows(Alice, 0x07));
        Assert.Equal(["id", "unixtimestamp", "client_id", "match_id", "xp", "tp", "kills", "kills_assist", "kills_headshot",
            "kills_melee", "kills_longrange", "deaths", "teamplay_revives", "gamemode_team", "playtime", "jointime", "leave_reason"],
            slice.Select(c => c.Name));
        // The slice's xp is the match SCORE, as the host's row has it.
        Assert.Equal(750u, Value(slice, "xp"));
        Assert.Equal(750u, PlayerStatistics.Get(Alice, "xp"));
        Assert.Equal(1u, Value(slice, "kills"));
        Assert.Equal(0u, Value(slice, "deaths"));
        Assert.Equal(0u, Value(slice, "gamemode_team"));
        Assert.Equal(59u, Value(slice, "playtime"));
        Assert.NotEqual(0u, Value(slice, "match_id"));
        Assert.All(slice, c => Assert.Equal(new byte[] { 1, 0, 0, 0, 0 }, c.Flags.ToArray()));

        // A second match with the same rifle adds to its row.
        Feed(Alice, SendTags(("FPSCLIENT_STOP", HostMatchEnd)));
        rifle = Rows(Alice, 0x08).Single(row => Value(row, "weapon_id") == Fc3StringId.Of("p416"));
        Assert.Equal(16u, Value(rifle, "weapon_shots_fired"));
        Assert.Equal(2u, Value(rifle, "weapon_kill"));
        Assert.Equal(2, Rows(Alice, 0x07).Count);
    }

    [Fact]
    public void AMatchTheHostUploadedIsNotCountedAgainFromTheTelemetry()
    {
        Playforward();
        // The host's 0x06 delta for Alice landed a second ago; her own
        // FPSCLIENT_STOP for the same match arrives twenty seconds later.
        Fc3MatchLedger.HostReported(TestTitle.Id, Bob, [Alice], null);
        Feed(Alice, SendTags(("FPSCLIENT_STOP", HostMatchEnd)));
        Assert.Equal(0u, PlayerStatistics.Get(Alice, "kills"));
        Assert.Equal(0u, PlayerStatistics.Get(Alice, "match_played"));
        Assert.Empty(Rows(Alice, 0x08));

        // Bob's telemetry, whom the host did not report, still counts.
        Feed(Bob, SendTags(("FPSCLIENT_STOP", OtherMatchEnd)));
        Assert.Equal(1u, PlayerStatistics.Get(Bob, "deaths"));
    }

    [Fact]
    public void TheTelemetryMatchIsFiledUnderTheRowIdTheHostWasGiven()
    {
        Playforward();
        // Bob hosts: his telemetry names the match, then his session stores
        // the match row and is handed its id. Alice's TimeSlice row for that
        // match, built from her telemetry, must carry the same match_id.
        Feed(Bob, SendTags(("FPSHOST_START", "MATCHID=14600588199604146448&MPMODE=FCXTeamDeathMatch&MAP=pvp_sof12")));
        var parameters = new List<byte>();
        parameters.AddRange(BitConverter.GetBytes(0x05u));
        var match = Fc3Wire.RowSet([("match_gamemode", 1u), ("match_time", 60u)]);
        parameters.AddRange(BitConverter.GetBytes(1u));
        parameters.AddRange(BitConverter.GetBytes(2u));
        foreach (OnlineDataColumn column in match.Rows[0]) parameters.AddRange(column.Raw);
        parameters.AddRange([3, 0, (byte)'i', (byte)'d', 0]);
        parameters.AddRange(BitConverter.GetBytes(4u));
        byte[] reply = Fc3OnlineDataService.BuildStoreOnlineDataResult(
            new RmcRequest(Fc3OnlineDataService.ProtocolId, 1, Fc3OnlineDataService.MethodStoreOnlineData, [.. parameters]), Bob, TestTitle.Id);
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(14));

        Feed(Alice, SendTags(("FPSCLIENT_STOP", HostMatchEnd)));
        Assert.Equal(id, Value(Assert.Single(Rows(Alice, 0x07)), "match_id"));
    }
}
