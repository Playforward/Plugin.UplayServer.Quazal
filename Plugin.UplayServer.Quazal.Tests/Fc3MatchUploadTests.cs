using System.Buffers.Binary;
using Plugin.UplayServer.Quazal.Playforward;

namespace Plugin.UplayServer.Quazal.Tests;

/// <summary>
/// The statistics a host uploads after a match, replayed byte for byte:
/// the match row, a TimeSlice row per player, a Weapon row per player and weapon,
/// and the per-player StatisticPlayer delta in mode 8
/// </summary>
[Collection("OnlineDataStore")]
public sealed class Fc3MatchUploadTests : IDisposable
{
    private const uint Host = 2591;
    private const uint Guest = 2643;

    public void Dispose() => OnlineDataStore.Detach();

    private static void Playforward()
    {
        var store = new InMemoryProgressStore(Host);
        store.ResolvePid("host", "dyllan500");
        OnlineDataStore.Detach();
        OnlineDataStore.Attach(TestTitle.Id, store, pid => pid == Host);
    }

    // 106/2 call 134: 0x05 StatisticMatch, return column "id", mode 4.
    private const string MatchStore =
        "0500000001000000070000001c0067616d656d6f64655f6f626a6563746976655f616368696576656400010001010000000000000000000000000000" +
        "00000001000000000f006d617463685f67616d656d6f6465000100010100ecda9199000000000000000000000000010000000013006d617463685f77" +
        "696e6e696e675f7465616d0001000101000200000000000000000000000000000001000000000b006d617463685f74696d650001000101003c000000" +
        "00000000000000000000000001000000001a0067616d656d6f64655f7465616d5f626c75655f706f696e747300010001010000000000000000000000" +
        "0000000000000100000000190067616d656d6f64655f7465616d5f7265645f706f696e74730001000101000000000000000000000000000000000001" +
        "000000000a006d617463685f6d6170000100010100e68525910000000000000000000000000100000000030069640004000000";
    // call 139: 0x07 TimeSlice, two rows (match_id 751), mode 0.
    private const string TimeSliceStore =
        "07000000020000000f0000000a00636c69656e745f69640001010101031f0a0000000000000000000000000000010000000009006d617463685f6964" +
        "000101010100ef020000000000000000000000000000010000000003007870000100010100c800000000000000000000000000000001000000000300" +
        "747000010001010001000000000000000000000000000000010000000006006b696c6c73000100010100010000000000000000000000000000000100" +
        "0000000d006b696c6c735f6173736973740001000101000000000000000000000000000000000001000000000f006b696c6c735f6865616473686f74" +
        "0001000101000000000000000000000000000000000001000000000c006b696c6c735f6d656c65650001000101000000000000000000000000000000" +
        "0000010000000010006b696c6c735f6c6f6e6772616e6765000100010100000000000000000000000000000000000100000000070064656174687300" +
        "010001010000000000000000000000000000000000010000000011007465616d706c61795f7265766976657300010001010000000000000000000000" +
        "00000000000001000000000e0067616d656d6f64655f7465616d0001000101000100000000000000000000000000000001000000000900706c617974" +
        "696d650001000101003b000000000000000000000000000000010000000009006a6f696e74696d650001000101000100000000000000000000000000" +
        "000001000000000d006c656176655f726561736f6e000100010100ffffffff00000000000000000000000001000000000f0000000a00636c69656e74" +
        "5f6964000101010103530a0000000000000000000000000000010000000009006d617463685f6964000101010100ef02000000000000000000000000" +
        "000001000000000300787000010001010000000000000000000000000000000000010000000003007470000100010100000000000000000000000000" +
        "00000000010000000006006b696c6c730001000101000000000000000000000000000000000001000000000d006b696c6c735f617373697374000100" +
        "0101000000000000000000000000000000000001000000000f006b696c6c735f6865616473686f740001000101000000000000000000000000000000" +
        "000001000000000c006b696c6c735f6d656c656500010001010000000000000000000000000000000000010000000010006b696c6c735f6c6f6e6772" +
        "616e67650001000101000000000000000000000000000000000001000000000700646561746873000100010100010000000000000000000000000000" +
        "00010000000011007465616d706c61795f726576697665730001000101000000000000000000000000000000000001000000000e0067616d656d6f64" +
        "655f7465616d0001000101000000000000000000000000000000000001000000000900706c617974696d650001000101003b00000000000000000000" +
        "0000000000010000000009006a6f696e74696d650001000101000100000000000000000000000000000001000000000d006c656176655f726561736f" +
        "6e000100010100000000000000000000000000000000000100000000030069640000000000";
    // call 140: 0x08 Weapon, three rows, mode 0.
    private const string WeaponStore =
        "0800000003000000090000000a00636c69656e745f69640001010101001f0a000000000000000000000000000001000000000a00776561706f6e5f69" +
        "64000101010100c2016d15000000000000000000000000010000000003007870000100010101c8000000000000000000000000000000010000000013" +
        "00776561706f6e5f73686f74735f66697265640001000101010e00000000000000000000000000000001000000001100776561706f6e5f73686f7473" +
        "5f6869740001000101010400000000000000000000000000000001000000000e00776561706f6e5f64616d6167650001000101017600000000000000" +
        "000000000000000001000000000c00776561706f6e5f6b696c6c0001000101010100000000000000000000000000000001000000000e00776561706f" +
        "6e5f72656c6f61640001000101010000000000000000000000000000000001000000000c00776561706f6e5f74696d65000100010101280000000000" +
        "000000000000000000000100000000090000000a00636c69656e745f69640001010101001f0a000000000000000000000000000001000000000a0077" +
        "6561706f6e5f6964000101010100ceb19daf000000000000000000000000010000000003007870000100010101000000000000000000000000000000" +
        "0001000000001300776561706f6e5f73686f74735f66697265640001000101010000000000000000000000000000000001000000001100776561706f" +
        "6e5f73686f74735f6869740001000101010000000000000000000000000000000001000000000e00776561706f6e5f64616d61676500010001010100" +
        "00000000000000000000000000000001000000000c00776561706f6e5f6b696c6c000100010101000000000000000000000000000000000100000000" +
        "0e00776561706f6e5f72656c6f61640001000101010000000000000000000000000000000001000000000c00776561706f6e5f74696d650001000101" +
        "01010000000000000000000000000000000100000000090000000a00636c69656e745f6964000101010100530a000000000000000000000000000001" +
        "000000000a00776561706f6e5f6964000101010100e7a0fe600000000000000000000000000100000000030078700001000101010000000000000000" +
        "000000000000000001000000001300776561706f6e5f73686f74735f6669726564000100010101000000000000000000000000000000000100000000" +
        "1100776561706f6e5f73686f74735f6869740001000101010000000000000000000000000000000001000000000e00776561706f6e5f64616d616765" +
        "0001000101010000000000000000000000000000000001000000000c00776561706f6e5f6b696c6c0001000101010000000000000000000000000000" +
        "000001000000000e00776561706f6e5f72656c6f61640001000101010000000000000000000000000000000001000000000c00776561706f6e5f7469" +
        "6d65000100010101220000000000000000000000000000000100000000030069640000000000";
    // calls 141/142: 0x08 and 0x06 with no rows at all.
    private const string EmptyWeaponStore = "0800000000000000030069640000000000";
    private const string EmptyPlayerStore = "0600000000000000030069640000000000";
    // call 143: 0x06 StatisticPlayer, two delta rows, mode 8.
    private const string PlayerDeltaStore =
        "06000000020000001f0000000a00636c69656e745f69640001010101001f0a0000000000000000000000000000010000000003007870000100010101" +
        "c80000000000000000000000000000000100000000070078705f707670000100010101c8000000000000000000000000000000010000000003007470" +
        "00010001010101000000000000000000000000000000010000000006006b696c6c730001000101010100000000000000000000000000000001000000" +
        "000a006b696c6c735f7076700001000101010100000000000000000000000000000001000000000d006b696c6c735f73747265616b00010001010100" +
        "00000000000000000000000000000001000000000d006b696c6c735f6173736973740001000101010000000000000000000000000000000001000000" +
        "000c006b696c6c735f6d656c65650001000101010000000000000000000000000000000001000000000e006b696c6c735f74616767696e6700010001" +
        "010100000000000000000000000000000000010000000007006465617468730001000101010000000000000000000000000000000001000000000b00" +
        "6465617468735f7076700001000101010000000000000000000000000000000001000000000900737569636964657300010001010100000000000000" +
        "000000000000000000010000000011007465616d706c61795f7265766976657300010001010100000000000000000000000000000000010000000011" +
        "007465616d706c61795f7265766976656400010001010100000000000000000000000000000000010000000014007465616d706c61795f626174746c" +
        "65637279730001000101010000000000000000000000000000000001000000001a007465616d706c61795f6275646479626f6f73745f676976656e00" +
        "01000101010000000000000000000000000000000001000000001d007465616d706c61795f6275646479626f6f73745f726563656976656400010001" +
        "01010000000000000000000000000000000001000000000b006d617463685f77696e7300010001010100000000000000000000000000000000010000" +
        "00000d006d617463685f6c6f737365730001000101010100000000000000000000000000000001000000000d006d617463685f706c61796564000100" +
        "01010101000000000000000000000000000000010000000015006d617463685f77696e5f626f6e75735f706172740001000101010000000000000000" +
        "00000000000000000100000000110067616d656d6f64655f636170747572650001000101010000000000000000000000000000000001000000000f00" +
        "67616d656d6f64655f627265616b000100010101000000000000000000000000000000000100000000110067616d656d6f64655f64657374726f7900" +
        "0100010101000000000000000000000000000000000100000000170067616d656d6f64655f7265736f757263655f7469636b00010001010100000000" +
        "00000000000000000000000001000000000900706c617974696d650001000101013b00000000000000000000000000000001000000000d00706c6179" +
        "74696d655f7076700001000101013b00000000000000000000000000000001000000000800776f6e5f63636500010001010100000000000000000000" +
        "000000000000010000000015007061727469636970617465645f696e5f656f6d6d000100010101010000000000000000000000000000000100000000" +
        "19006d617463685f63686170746572735f636f6d706c657465640001000101010000000000000000000000000000000001000000001f0000000a0063" +
        "6c69656e745f6964000101010100530a0000000000000000000000000000010000000003007870000100010101000000000000000000000000000000" +
        "000100000000070078705f70767000010001010100000000000000000000000000000000010000000003007470000100010101000000000000000000" +
        "00000000000000010000000006006b696c6c730001000101010000000000000000000000000000000001000000000a006b696c6c735f707670000100" +
        "0101010000000000000000000000000000000001000000000d006b696c6c735f73747265616b00010001010100000000000000000000000000000000" +
        "01000000000d006b696c6c735f6173736973740001000101010000000000000000000000000000000001000000000c006b696c6c735f6d656c656500" +
        "01000101010000000000000000000000000000000001000000000e006b696c6c735f74616767696e6700010001010100000000000000000000000000" +
        "000000010000000007006465617468730001000101010100000000000000000000000000000001000000000b006465617468735f7076700001000101" +
        "010100000000000000000000000000000001000000000900737569636964657300010001010100000000000000000000000000000000010000000011" +
        "007465616d706c61795f7265766976657300010001010100000000000000000000000000000000010000000011007465616d706c61795f7265766976" +
        "656400010001010100000000000000000000000000000000010000000014007465616d706c61795f626174746c656372797300010001010100000000" +
        "00000000000000000000000001000000001a007465616d706c61795f6275646479626f6f73745f676976656e00010001010100000000000000000000" +
        "00000000000001000000001d007465616d706c61795f6275646479626f6f73745f726563656976656400010001010100000000000000000000000000" +
        "00000001000000000b006d617463685f77696e730001000101010000000000000000000000000000000001000000000d006d617463685f6c6f737365" +
        "730001000101010100000000000000000000000000000001000000000d006d617463685f706c61796564000100010101010000000000000000000000" +
        "00000000010000000015006d617463685f77696e5f626f6e75735f706172740001000101010000000000000000000000000000000001000000001100" +
        "67616d656d6f64655f636170747572650001000101010000000000000000000000000000000001000000000f0067616d656d6f64655f627265616b00" +
        "0100010101000000000000000000000000000000000100000000110067616d656d6f64655f64657374726f7900010001010100000000000000000000" +
        "0000000000000100000000170067616d656d6f64655f7265736f757263655f7469636b00010001010100000000000000000000000000000000010000" +
        "00000900706c617974696d650001000101013b00000000000000000000000000000001000000000d00706c617974696d655f7076700001000101013b" +
        "00000000000000000000000000000001000000000800776f6e5f63636500010001010100000000000000000000000000000000010000000015007061" +
        "727469636970617465645f696e5f656f6d6d00010001010101000000000000000000000000000000010000000019006d617463685f63686170746572" +
        "735f636f6d706c65746564000100010101000000000000000000000000000000000100000000030069640008000000";

    private static byte[] Store(string hex, uint uploader = Host, GameSession.Notifier? notify = null) =>
        Fc3OnlineDataService.BuildStoreOnlineDataResult(
            new RmcRequest(Fc3OnlineDataService.ProtocolId, 1, Fc3OnlineDataService.MethodStoreOnlineData, Convert.FromHexString(hex)), uploader, TestTitle.Id, notify);

    /// <summary>The working server's push after the host's player rows: src=host, type 1003000, param1=6 (community capture, 2026-09-17).</summary>
    private const string StatisticsUpdatedPush = "1f0a0000f84d0f00060000000000000001000000000000";

    private static (List<(uint Pid, byte Protocol, uint Method, byte[] Body)> Sent, GameSession.Notifier Notify) Notifier()
    {
        var sent = new List<(uint, byte, uint, byte[])>();
        return (sent, (pid, protocol, method, body) => { sent.Add((pid, protocol, method, body)); return true; });
    }

    private static List<List<OnlineDataColumn>> Fetch(uint pid, uint dataId, uint limit = 150, string orderBy = "")
    {
        var parameters = new List<byte>();
        parameters.AddRange(BitConverter.GetBytes(dataId));
        parameters.AddRange(BitConverter.GetBytes(0u));
        parameters.AddRange(BitConverter.GetBytes(1u));
        parameters.AddRange(Fc3Wire.Column("client_id", pid).Raw);
        parameters.AddRange(BitConverter.GetBytes(limit));
        byte[] order = System.Text.Encoding.ASCII.GetBytes(orderBy);
        parameters.AddRange(BitConverter.GetBytes((ushort)(orderBy.Length == 0 ? 0 : order.Length + 1)));
        if (orderBy.Length > 0) { parameters.AddRange(order); parameters.Add(0); }
        byte[] body = Fc3OnlineDataService.BuildFetchOnlineDataResult(
            new RmcRequest(Fc3OnlineDataService.ProtocolId, 1, Fc3OnlineDataService.MethodFetchOnlineData, [.. parameters]), pid);
        int offset = 0;
        Assert.True(OnlineDataRowSet.TryParse(body, ref offset, out OnlineDataRowSet rows));
        return rows.Rows;
    }

    private static uint Value(List<OnlineDataColumn> row, string name) => row.Single(c => c.Name == name).NumericValue;

    [Fact]
    public void TheMatchStoreIsAnsweredWithItsIdInTheWorkingServersShape()
    {
        Playforward();
        byte[] reply = Store(MatchStore);

        // The working server's 35-byte reply, with its match id 751 where
        // ours goes: a Row of one "id" column, not a RowSet.
        const string working = "0100000003006964000100000000ef0200000000000000000000000000000100000000";
        byte[] expected = Convert.FromHexString(working);
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(14));
        Assert.NotEqual(0u, id);
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(14), id);
        Assert.Equal(expected, reply);

        // Stored under the host with that id and a timestamp.
        List<OnlineDataColumn> match = Assert.Single(OnlineDataStore.Fetch(Host, 0x05)!.Rows);
        Assert.Equal(id, Value(match, "id"));
        Assert.Equal(60u, Value(match, "match_time"));
        Assert.NotEqual(0u, Value(match, "unixtimestamp"));
    }

    [Fact]
    public void EveryPlayersStatisticsAreFiledUnderThatPlayerNotTheHost()
    {
        Playforward();
        Store(MatchStore);
        Assert.Equal(OnlineDataStore.EmptyRowSet, Store(TimeSliceStore));
        Assert.Equal(OnlineDataStore.EmptyRowSet, Store(WeaponStore));
        Assert.Equal(OnlineDataStore.EmptyRowSet, Store(EmptyWeaponStore));
        Assert.Equal(OnlineDataStore.EmptyRowSet, Store(EmptyPlayerStore));
        Assert.Equal(OnlineDataStore.EmptyRowSet, Store(PlayerDeltaStore));

        // The guest's TimeSlice and Weapon rows are read back by the guest's
        // own client_id query, exactly as the login batch asks.
        List<OnlineDataColumn> slice = Assert.Single(Fetch(Guest, 0x07));
        Assert.Equal(751u, Value(slice, "match_id"));
        Assert.Equal(1u, Value(slice, "deaths"));
        Assert.NotEqual(0u, Value(slice, "unixtimestamp"));
        List<OnlineDataColumn> hostSlice = Assert.Single(Fetch(Host, 0x07));
        Assert.Equal(1u, Value(hostSlice, "kills"));
        Assert.Equal(200u, Value(hostSlice, "xp"));

        List<List<OnlineDataColumn>> hostWeapons = Fetch(Host, 0x08);
        Assert.Equal(2, hostWeapons.Count);
        List<OnlineDataColumn> rifle = hostWeapons.Single(row => Value(row, "weapon_id") == 359465410);
        Assert.Equal(14u, Value(rifle, "weapon_shots_fired"));
        Assert.Equal(1u, Value(rifle, "weapon_kill"));
        Assert.Single(Fetch(Guest, 0x08));

        // The 0x06 deltas are the players' counters: the host's kill and
        // loss, the guest's death and loss, both a match played of 59 s.
        Assert.Equal(1u, PlayerStatistics.Get(Host, "kills"));
        Assert.Equal(1u, PlayerStatistics.Get(Host, "match_losses"));
        Assert.Equal(1u, PlayerStatistics.Get(Host, "match_played"));
        Assert.Equal(59u, PlayerStatistics.Get(Host, "playtime_pvp"));
        Assert.Equal(1u, PlayerStatistics.Get(Host, "tp"));
        Assert.Equal(1u, PlayerStatistics.Get(Guest, "deaths"));
        Assert.Equal(0u, PlayerStatistics.Get(Guest, "kills"));
        // The delta's xp is the level's XP (the working server added it too).
        Assert.Equal(200u, PlayerStatistics.Get(Host, "xp"));
    }

    /// <summary>
    /// The login batch asks for the TimeSlice history with <c>limit 5,
    /// orderBy unixtimestamp</c> and a working server answers with at most
    /// five rows, newest first (community capture 2026-09-18, call 147: ids
    /// 1468, 1466, 1462, 1460 for match ids 758, 757, 754, 751). This server
    /// used to send every row it had, oldest first.
    /// </summary>
    [Fact]
    public void TheTimeSliceHistoryIsServedNewestFirstAndNoLongerThanAsked()
    {
        Playforward();
        for (int match = 0; match < 7; match++)
        {
            Fc3OnlineDataService.FileTimeSlice(Guest,
                [Fc3Wire.Column("client_id", Guest), Fc3Wire.Column("match_id", (uint)(700 + match)), Fc3Wire.Column("xp", 100u)],
                "test");
            // Distinct timestamps, as one per match end has.
            OnlineDataRowSet rows = OnlineDataStore.Fetch(Guest, 0x07)!;
            List<OnlineDataColumn> last = rows.Rows[^1];
            int at = last.FindIndex(c => c.Name == "unixtimestamp");
            last[at] = OnlineDataColumn.Served("unixtimestamp", (uint)(1789700000 + match));
            OnlineDataStore.Store(Guest, 0x07, rows);
        }

        List<List<OnlineDataColumn>> history = Fetch(Guest, 0x07, limit: 5, orderBy: "unixtimestamp");
        Assert.Equal(5, history.Count);
        Assert.Equal(new uint[] { 706, 705, 704, 703, 702 }, history.Select(row => Value(row, "match_id")));

        // Without an order-by column (the Weapon query) rows come as filed,
        // and limit 0 is no limit.
        Assert.Equal(7, Fetch(Guest, 0x07, limit: 0).Count);
        Assert.Equal(700u, Value(Fetch(Guest, 0x07, limit: 0)[0], "match_id"));
    }

    [Fact]
    public void ASecondMatchAddsToTheWeaponRowAndTheCounters()
    {
        Playforward();
        Store(MatchStore);
        Store(WeaponStore);
        Store(PlayerDeltaStore);
        Store(MatchStore);
        Store(WeaponStore);
        Store(PlayerDeltaStore);

        List<OnlineDataColumn> rifle = Fetch(Host, 0x08).Single(row => Value(row, "weapon_id") == 359465410);
        Assert.Equal(28u, Value(rifle, "weapon_shots_fired"));
        Assert.Equal(2u, Value(rifle, "weapon_kill"));
        Assert.Equal(2, Fetch(Host, 0x08).Count);
        Assert.Equal(2u, PlayerStatistics.Get(Host, "kills"));
        Assert.Equal(2u, PlayerStatistics.Get(Host, "match_played"));
    }

    /// <summary>
    /// The host's upload chain ends with a reward flush: a 0x06 store in mode
    /// 0 carrying the player's pending reward XP 
    /// </summary>
    [Fact]
    public void TheEmptyRewardFlushLeavesTheStatisticsRowIntact()
    {
        Playforward();
        Store(MatchStore);
        Store(PlayerDeltaStore);
        uint xp = PlayerStatistics.Get(Host, "xp");
        Assert.Equal(200u, xp);

        byte[] flush = Fc3Wire.RowSet([("client_id", Host), ("xp", 0u), ("xp_pvp", 0u),
            ("dailychallenges_task_done", 0u), ("dailychallenges_challenge_done", 0u),
            ("weeklychallenges_task_done", 0u), ("weeklychallenges_challenge_done", 0u)]).ToBytes();
        byte[] reply = Fc3OnlineDataService.BuildStoreOnlineDataResult(Fc3Wire.StoreRequest(0x06, flush), Host, TestTitle.Id);
        Assert.Equal(OnlineDataStore.EmptyRowSet, reply);

        List<List<OnlineDataColumn>> rows = Fetch(Host, 0x06);
        List<OnlineDataColumn> row = Assert.Single(rows);
        Assert.Equal(3 + Fc3StatisticDefinitions.PlayerColumns.Length, row.Count);
        Assert.Equal(200u, Value(row, "xp"));
        Assert.Null(OnlineDataStore.Fetch(Host, 0x06));

        // A stray stored 0x06 row set (from before every 0x06 store
        // accumulated) is ignored, not served.
        OnlineDataStore.Store(Host, 0x06, Fc3Wire.RowSet([("client_id", Host), ("xp", 0u)]));
        row = Assert.Single(Fetch(Host, 0x06));
        Assert.Equal(200u, Value(row, "xp"));
        Assert.Equal(3 + Fc3StatisticDefinitions.PlayerColumns.Length, row.Count);
    }

    [Fact]
    public void TheHostsPlayerRowsMakeEveryParticipantReReadItsStatistics()
    {
        Playforward();
        GameSessionStore.CreateSession(TestTitle.Id, 1, 67794, Host);
        GameSessionStore.AddParticipants(TestTitle.Id, 1, 67794, [Guest]);
        (var sent, GameSession.Notifier notify) = Notifier();

        Store(MatchStore, Host, notify);
        Assert.Empty(sent);
        Store(PlayerDeltaStore, Host, notify);

        // Both players, host included, get the working server's exact bytes.
        Assert.Equal(2, sent.Count);
        Assert.Equal([Host, Guest], sent.Select(n => n.Pid).OrderBy(pid => pid));
        Assert.All(sent, n =>
        {
            Assert.Equal(GameSession.NotificationProtocolId, n.Protocol);
            Assert.Equal(GameSession.NotificationMethod, n.Method);
            Assert.Equal(StatisticsUpdatedPush, Convert.ToHexString(n.Body).ToLowerInvariant());
        });
        GameSessionStore.DeleteSession(TestTitle.Id, 1, 67794);
    }

    [Fact]
    public async Task AHostThatStopsAfterTheMatchRowStillGetsThePlayersToldAfterTheGrace()
    {
        Playforward();
        GameSessionStore.CreateSession(TestTitle.Id, 1, 67795, Host);
        GameSessionStore.AddParticipants(TestTitle.Id, 1, 67795, [Guest]);
        (var sent, GameSession.Notifier notify) = Notifier();
        TimeSpan grace = Fc3MatchLedger.HostUploadGrace;
        Fc3MatchLedger.HostUploadGrace = TimeSpan.FromMilliseconds(100);
        try
        {
            Store(MatchStore, Host, notify);
            Assert.Empty(sent);
            await Task.Delay(500);
            Assert.Equal([Host, Guest], sent.Select(n => n.Pid).OrderBy(pid => pid));
            Assert.All(sent, n => Assert.Equal(StatisticsUpdatedPush, Convert.ToHexString(n.Body).ToLowerInvariant()));
        }
        finally
        {
            Fc3MatchLedger.HostUploadGrace = grace;
            GameSessionStore.DeleteSession(TestTitle.Id, 1, 67795);
        }
    }

    [Fact]
    public void StatisticsRowsAreServedInTheWorkingServersShapeWhateverTheHostUploaded()
    {
        Playforward();
        Store(MatchStore);
        Store(TimeSliceStore);
        Store(WeaponStore);

        // The host uploaded flags 01 01 01 01 03 / 01 00 01 01 01; the working
        // server serves every column as 01 00 00 00 00, id and timestamp first.
        foreach (List<OnlineDataColumn> row in Fetch(Host, 0x07).Concat(Fetch(Host, 0x08)))
        {
            Assert.Equal(["id", "unixtimestamp", "client_id"], row.Take(3).Select(c => c.Name));
            Assert.All(row, c => Assert.Equal(new byte[] { 1, 0, 0, 0, 0 }, c.Flags.ToArray()));
        }
    }

    // 106/3 DeleteOnlineData as the Deck sent it after a match (2026-09-17):
    // dataId 0x13, three where-columns (pid, notification_id,
    // notification_param_0), a trailing u32.
    private const string DismissNotification = "1300000003000000040070696400010001010084841e00000000000000000000000000010000000010006e6f74696669636174696f6e5f6964000100010100e2e38eae000000000000000000000000010000000015006e6f74696669636174696f6e5f706172616d5f3000010001010050c52c90000000000000000000000000010000000000000000";

    [Fact]
    public void DismissingANotificationRemovesTheStoredRowThatMatchesEveryColumn()
    {
        Playforward();
        const uint Deck = 2000004;
        OnlineDataStore.Store(Deck, 0x13, Fc3Wire.RowSet(
            [("pid", Deck), ("notification_id", 2928600034u), ("notification_param_0", 2418853200u)],
            [("pid", Deck), ("notification_id", 2928600034u), ("notification_param_0", 7u)]));

        byte[] reply = Fc3OnlineDataService.BuildDeleteOnlineDataResult(
            new RmcRequest(Fc3OnlineDataService.ProtocolId, 1, Fc3OnlineDataService.MethodDeleteOnlineData, Convert.FromHexString(DismissNotification)), Deck);

        Assert.Equal(OnlineDataStore.EmptyRowSet, reply);
        List<OnlineDataColumn> left = Assert.Single(OnlineDataStore.Fetch(Deck, 0x13)!.Rows);
        Assert.Equal(7u, Value(left, "notification_param_0"));
    }
}
