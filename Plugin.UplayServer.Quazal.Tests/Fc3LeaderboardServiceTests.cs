using System.Buffers.Binary;
using Plugin.UplayServer.Quazal.Playforward;

namespace Plugin.UplayServer.Quazal.Tests;

/// <summary>
/// The Leaderboard service (109): where the client reads XP from. Requests
/// are the exact bytes captured from the retail client; rows follow the
/// working community server's layout, XP at index 2.
/// </summary>
[Collection("OnlineDataStore")]
public sealed class Fc3LeaderboardServiceTests : IDisposable
{
    private const uint Alice = 2_000_000;
    private const uint Bob = 2_000_001;

    public void Dispose() => OnlineDataStore.Detach();

    private static void Playforward()
    {
        var store = new InMemoryProgressStore(Alice);
        store.ResolvePid("alice", "A");
        store.ResolvePid("bob", "B");
        OnlineDataStore.Detach();
        OnlineDataStore.Attach(TestTitle.Id, store, pid => pid is Alice or Bob);
    }

    // XP reaches the board the way it reaches the level: as a 0x06 delta
    // row's xp (the host's match row, or the player's own decode reward).
    private static void AwardXp(uint pid, uint xp) =>
        PlayerStatistics.Accumulate(pid, [("client_id", pid), ("xp", xp), ("xp_pvp", xp)], "test");

    private static RmcRequest Request(uint method, string hex) =>
        new(Fc3LeaderboardService.ProtocolId, 1, method, Convert.FromHexString(hex));

    private static List<List<uint>> Rows(byte[] body)
    {
        int offset = 0;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset)); offset += 4;
        var rows = new List<List<uint>>();
        for (uint i = 0; i < count; i++)
        {
            uint columns = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset)); offset += 4;
            var row = new List<uint>();
            for (uint c = 0; c < columns; c++) { row.Add(BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset))); offset += 4; }
            rows.Add(row);
        }
        Assert.Equal(body.Length, offset);
        return rows;
    }

    private static string Pid(uint pid) => Convert.ToHexString(BitConverter.GetBytes(pid)).ToLowerInvariant();

    [Fact]
    public void APlayersOwnEntryCarriesTheirXpAtIndexTwo()
    {
        Playforward();
        AwardXp(Alice, 471);
        AwardXp(Alice, 439);

        // 109/3 as the retail client sends it for its own pid on board 0:
        // board, 0, [pid], 0, limit 10, true (2026-09-15 capture, pid swapped).
        byte[] body = Fc3LeaderboardService.BuildGetByPidsResult(
            Request(Fc3LeaderboardService.MethodGetByPids, "0000000000000000" + "01000000" + Pid(Alice) + "00000000" + "0a000000" + "01"));

        List<uint> row = Assert.Single(Rows(body));
        Assert.Equal([Alice, 0u, 910u, 0u, 0u], row);   // index 1 stays zero; the client shows 910 XP as level 2 itself
    }

    [Fact]
    public void AStrangerHasARowOfZeroXp()
    {
        Playforward();
        byte[] body = Fc3LeaderboardService.BuildGetByPidsResult(
            Request(Fc3LeaderboardService.MethodGetByPids, "0300000000000000" + "01000000" + Pid(Bob) + "00000000" + "0a000000" + "01"));

        // Board 3 rows are six wide on the working server.
        List<uint> row = Assert.Single(Rows(body));
        Assert.Equal([Bob, 0u, 0u, 0u, 0u, 0u], row);
    }

    [Fact]
    public void TheGlobalBoardRanksPlayersThisServerHasNeverSeen()
    {
        var store = new InMemoryProgressStore(Alice);
        store.ResolvePid("alice", "A");
        store.ResolvePid("bob", "B");
        const uint Carol = 2_000_002;
        store.ResolvePid("carol", "Carol");
        OnlineDataStore.Detach();
        // Only Alice is on this server; Bob and Carol earned their XP on
        // another server of the title, or last week, and are offline.
        OnlineDataStore.Attach(TestTitle.Id, store, pid => pid is Alice);
        AwardXp(Alice, 3992);
        store.SaveScore(Bob, Fc3IntelService.XpBoard, 6052);
        store.SaveScore(Carol, Fc3IntelService.XpBoard, 17_527_305);

        byte[] body = Fc3LeaderboardService.BuildGetTopResult(
            Request(Fc3LeaderboardService.MethodGetTop, "000000000000000000000000" + "0a000000" + "01"), [Alice]);
        List<List<uint>> rows = Rows(body);
        Assert.Equal(3, rows.Count);
        Assert.Equal([Carol, 0u, 17_527_305u, 0u, 0u], rows[0]);
        Assert.Equal([Bob, 0u, 6052u, 0u, 0u], rows[1]);
        Assert.Equal([Alice, 0u, 3992u, 0u, 0u], rows[2]);

        // And the name lookup the client makes for each row has their names.
        Assert.Equal("Carol", Plugin.UplayServer.Quazal.Playforward.PlayerNames.For(TestTitle.Id, Carol));
        Assert.Equal("B", new QuazalSettings().NameForPid(TestTitle.Quazal, Bob));

        // A match against an offline player's entry ranks them too.
        body = Fc3LeaderboardService.BuildGetForPlayersResult(
            Request(Fc3LeaderboardService.MethodGetForPlayers, "02000000" + Pid(Alice) + Pid(Bob) + "0a000000"));
        Assert.Equal(6052u, Rows(body)[1][2]);

        // Playforward unreachable: the board is whoever this server holds.
        // (A different limit, so the brief cache of the read above is not hit.)
        store.Reachable = false;
        body = Fc3LeaderboardService.BuildGetTopResult(
            Request(Fc3LeaderboardService.MethodGetTop, "000000000000000000000000" + "05000000" + "01"), [Alice]);
        Assert.Equal([Alice, 0u, 3992u, 0u, 0u], Assert.Single(Rows(body)));
    }

    [Fact]
    public void AfterAMatchEveryParticipantIsAnswered()
    {
        Playforward();
        AwardXp(Alice, 100);
        AwardXp(Bob, 250);

        // 109/10 as captured on this server after a match: [2 pids], 10.
        byte[] body = Fc3LeaderboardService.BuildGetForPlayersResult(
            Request(Fc3LeaderboardService.MethodGetForPlayers, "02000000" + Pid(Bob) + Pid(Alice) + "0a000000"));

        List<List<uint>> rows = Rows(body);
        Assert.Equal(2, rows.Count);
        // Five columns whatever the trailing 10, the XP at index 2.
        Assert.Equal([Bob, 0u, 250u, 0u, 0u], rows[0]);
        Assert.Equal([Alice, 0u, 100u, 0u, 0u], rows[1]);
    }

    [Fact]
    public void TheTopBoardRanksThePlayersThisServerHoldsByXp()
    {
        Playforward();
        AwardXp(Alice, 100);
        AwardXp(Bob, 250);

        // 109/1 as captured: board 0, 0, 0, limit 10, true.
        byte[] body = Fc3LeaderboardService.BuildGetTopResult(
            Request(Fc3LeaderboardService.MethodGetTop, "000000000000000000000000" + "0a000000" + "01"), [Alice, Bob]);

        List<List<uint>> rows = Rows(body);
        Assert.Equal([Bob, Alice], rows.Select(row => row[0]));
        Assert.Equal(250u, rows[0][2]);
    }

    [Fact]
    public void TheRegionalBoardTakesTheLimitFromTheSevenWordRequest()
    {
        Playforward();
        AwardXp(Alice, 100);

        // 109/2 as logged on this server: board 0, 0, "US" packed in a word,
        // three zero words, limit 10, true.
        byte[] body = Fc3LeaderboardService.BuildGetTopResult(
            Request(Fc3LeaderboardService.MethodGetRegionalTop, "0000000000000000005553000000000000000000000000000A00000001"), [Alice]);
        Assert.Equal([Alice, 0u, 100u, 0u, 0u], Assert.Single(Rows(body)));
    }

    // The five XP totals the game was watched at (2026-09-15/16): what it
    // showed on screen, and the PLAYERLEVEL it reported in telemetry.
    [Theory]
    [InlineData(480u, 1u, 1u)]
    [InlineData(960u, 2u, 2u)]
    [InlineData(1849u, 3u, 3u)]
    [InlineData(2236u, 4u, 3u)]
    [InlineData(3236u, 5u, 4u)]
    [InlineData(48_000u, 20u, 18u)]
    [InlineData(17_527_305u, 20u, 75u)]
    public void TheLevelShownAndTheRankReportedFollowTheGamesOwnTables(uint xp, uint shown, uint reported)
    {
        Assert.Equal(shown, Fc3RankTable.DisplayLevelFor(xp));
        Assert.Equal(reported, Fc3RankTable.RankFor(xp));
        Assert.Equal(75, Fc3RankTable.MaxRank);
        Assert.Equal(20, Fc3RankTable.MaxLevel);
    }

    [Fact]
    public void PerPlayerRowsCarryTheXpAndNoRank()
    {
        Playforward();
        AwardXp(Alice, 3236);
        byte[] body = Fc3LeaderboardService.BuildGetForPlayersResult(
            Request(Fc3LeaderboardService.MethodGetForPlayers, "01000000" + Pid(Alice) + "0A000000"));
        Assert.Equal([Alice, 0u, 3236u, 0u, 0u], Assert.Single(Rows(body)));
    }

    [Fact]
    public void TheCountMethodsMirrorTheWorkingServer()
    {
        byte[] body = Fc3LeaderboardService.BuildCountResult(Request(Fc3LeaderboardService.MethodUnknown9, "0000000000000000" + "01000000" + Pid(Alice) + "01"));
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(body));
    }
}
