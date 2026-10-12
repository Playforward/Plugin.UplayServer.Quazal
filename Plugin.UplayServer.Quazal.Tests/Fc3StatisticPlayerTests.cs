using System.Buffers.Binary;
using Plugin.UplayServer.Quazal.Playforward;

namespace Plugin.UplayServer.Quazal.Tests;

/// <summary>
/// The 0x06 StatisticPlayer row the profile screen reads at login, built from
/// the XP the game submitted, in the exact column set the game defines.
/// </summary>
[Collection("OnlineDataStore")]
public sealed class Fc3StatisticPlayerTests : IDisposable
{
    private const uint Pid = 2_000_000;

    public void Dispose() => OnlineDataStore.Detach();

    private static void Playforward()
    {
        var store = new InMemoryProgressStore(Pid);
        store.ResolvePid("account-a", "A");
        OnlineDataStore.Detach();
        OnlineDataStore.Attach(TestTitle.Id, store, pid => pid == Pid);
    }

    /// <summary>The login-time read: dataId 0x06, no select list, one client_id filter, limit 10, no order.</summary>
    private static RmcRequest StatisticPlayerRead(uint pid)
    {
        var parameters = new List<byte>();
        parameters.AddRange(BitConverter.GetBytes(Fc3StatisticDefinitions.StatisticPlayerDataId));
        parameters.AddRange(BitConverter.GetBytes(0u));                          // select list
        parameters.AddRange(BitConverter.GetBytes(1u));                          // one filter
        parameters.AddRange(Fc3Wire.Column("client_id", pid).Raw);
        parameters.AddRange(BitConverter.GetBytes(10u));                         // limit
        parameters.AddRange(BitConverter.GetBytes((ushort)0));                   // order-by
        return new RmcRequest(Fc3OnlineDataService.ProtocolId, 1, Fc3OnlineDataService.MethodFetchOnlineData, [.. parameters]);
    }

    [Fact]
    public void ThePlayerRowCarriesEveryDefinedColumnWithTheXpTotal()
    {
        Playforward();
        // The row's xp is the sum of the 0x06 deltas (a host's match row, a
        // decode reward), not Intel's total: on the working server a player
        // at 1050 here and 3133 in Intel was level 2 (2026-09-18).
        PlayerStatistics.Accumulate(Pid, [("client_id", Pid), ("xp", 700u), ("xp_pvp", 700u), ("match_played", 1u)], "host");
        PlayerStatistics.Accumulate(Pid, [("client_id", Pid), ("xp", 260u), ("xp_pvp", 260u)], "reward");
        var intel = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(intel, 3133);
        Fc3IntelService.BuildAddXpResult(new RmcRequest(Fc3IntelService.ProtocolId, 1, Fc3IntelService.MethodAddXp, intel), Pid);

        byte[] body = Fc3OnlineDataService.BuildFetchOnlineDataResult(StatisticPlayerRead(Pid), Pid);
        int offset = 0;
        Assert.True(OnlineDataRowSet.TryParse(body, ref offset, out OnlineDataRowSet rows));
        Assert.Equal(body.Length, offset);

        List<OnlineDataColumn> row = Assert.Single(rows.Rows);
        // id, unixtimestamp and the key, then all 38 statistics of the game's
        // PersistentTypes/Player -- the community server's row, column for column.
        Assert.Equal(3 + Fc3StatisticDefinitions.PlayerColumns.Length, row.Count);
        Assert.Equal(["id", "unixtimestamp", "client_id", .. Fc3StatisticDefinitions.PlayerColumns], row.Select(c => c.Name));
        Assert.Equal(Pid, row.Single(c => c.Name == "client_id").NumericValue);
        Assert.Equal(960u, row.Single(c => c.Name == "xp").NumericValue);
        Assert.Equal(960u, row.Single(c => c.Name == "xp_pvp").NumericValue);
        Assert.Equal(1u, row.Single(c => c.Name == "match_played").NumericValue);
        Assert.Equal(0u, row.Single(c => c.Name == "kills").NumericValue);
        // The working server's column order: the coop split before the pvp one.
        Assert.Equal(["xp", "xp_coop", "xp_pvp", "tp", "kills", "kills_coop", "kills_pvp"], row.Skip(3).Take(7).Select(c => c.Name));
        // Every column in the shape a working server serves one: flags 01 00 00 00 00.
        Assert.All(row, c => Assert.Equal(new byte[] { 1, 0, 0, 0, 0 }, c.Flags.ToArray()));
    }

    [Fact]
    public void APlayerWithNoXpStillGetsTheEmptyAnswer()
    {
        Playforward();
        byte[] body = Fc3OnlineDataService.BuildFetchOnlineDataResult(StatisticPlayerRead(Pid), Pid);
        Assert.Equal(OnlineDataStore.EmptyRowSet, body);
    }

    [Fact]
    public void SomebodyElsesRowIsNotSynthesisedFromMyXp()
    {
        Playforward();
        PlayerStatistics.Accumulate(Pid, [("client_id", Pid), ("xp", 100u)], "test");

        // A query filtering on another client_id is answered from storage only.
        byte[] body = Fc3OnlineDataService.BuildFetchOnlineDataResult(StatisticPlayerRead(Pid + 1), Pid);
        Assert.Equal(OnlineDataStore.EmptyRowSet, body);
    }
}
