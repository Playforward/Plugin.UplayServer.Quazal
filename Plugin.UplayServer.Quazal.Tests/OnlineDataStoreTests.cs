using System.Text;
using Plugin.UplayServer.Quazal.Playforward;

namespace Plugin.UplayServer.Quazal.Tests;

/// <summary>
/// Online data lives in Playforward and survives this
/// server restarting, or the player landing on another server of the title.
/// </summary>

[Collection("OnlineDataStore")]
public sealed class OnlineDataStoreTests : IDisposable
{
    private const uint PlayforwardPid = 2_000_000;
    private const uint OtherPid = 2_000_001;
    private const uint LanPid = 2000;
    private const uint StatsDataId = 0x0D;

    public void Dispose() => OnlineDataStore.Detach();

    /// <summary>A Playforward with two players on this title.</summary>
    private static InMemoryProgressStore NewPlayforward()
    {
        var store = new InMemoryProgressStore(PlayforwardPid);
        store.ResolvePid("account-a", "A");
        store.ResolvePid("account-b", "B");
        return store;
    }

    /// <summary>Attaches, as the plugin does at start: only Playforward principals are stored.</summary>
    private static void Attach(InMemoryProgressStore store)
    {
        OnlineDataStore.Detach();
        OnlineDataStore.Attach(TestTitle.Id, store, pid => pid is PlayforwardPid or OtherPid);
    }

    /// <summary>A restart, or the player arriving at another server of the title: fresh memory, same Playforward.</summary>
    private static void Restart(InMemoryProgressStore store) => Attach(store);

    private static OnlineDataRowSet RowSet(params (string Name, uint Value)[][] rows) => Fc3Wire.RowSet(rows);

    private static uint Value(OnlineDataRowSet rowSet, int row, string column) => Fc3Wire.Value(rowSet, row, column);

    [Fact]
    public void StoredRowsComeBackOnAnotherServerByteForByte()
    {
        InMemoryProgressStore playforward = NewPlayforward();
        Attach(playforward);
        OnlineDataRowSet uploaded = RowSet(
            [("pid", PlayforwardPid), ("challenge_id", 7), ("value", 42)],
            [("pid", PlayforwardPid), ("challenge_id", 8), ("value", 1)]);
        OnlineDataStore.Store(PlayforwardPid, StatsDataId, uploaded);

        Restart(playforward);

        // Byte-for-byte: the columns carry flag bytes whose layout is not
        // proven, so anything but the client's own bytes coming back would be
        // a reconstruction this plugin has refused to make.
        OnlineDataRowSet? restored = OnlineDataStore.Fetch(PlayforwardPid, StatsDataId);
        Assert.NotNull(restored);
        Assert.Equal(uploaded.ToBytes(), restored.ToBytes());
        Assert.Equal(42u, Value(restored, 0, "value"));
        Assert.Equal(8u, Value(restored, 1, "challenge_id"));
    }

    [Fact]
    public void AClearedSlotStaysClearedAndIsNotNeverStored()
    {
        InMemoryProgressStore playforward = NewPlayforward();
        Attach(playforward);
        OnlineDataStore.Store(PlayforwardPid, StatsDataId, RowSet([("value", 1)]));
        OnlineDataStore.Clear(PlayforwardPid, StatsDataId);

        Restart(playforward);

        // Empty, not null: a fetch on a cleared slot must not fall through to
        // a title seed the way a never-stored one does.
        OnlineDataRowSet? restored = OnlineDataStore.Fetch(PlayforwardPid, StatsDataId);
        Assert.NotNull(restored);
        Assert.Empty(restored.Rows);
        Assert.Null(OnlineDataStore.Fetch(PlayforwardPid, 0x99));
    }

    [Fact]
    public void ALanPidIsHeldInMemoryOnly()
    {
        InMemoryProgressStore playforward = NewPlayforward();
        Attach(playforward);
        OnlineDataStore.Store(LanPid, StatsDataId, RowSet([("value", 1)]));
        Assert.NotNull(OnlineDataStore.Fetch(LanPid, StatsDataId));

        Restart(playforward);

        // A dynamic LAN PID is a join-order number: 2000 tomorrow is whoever
        // connects first, and handing them today's rows would be handing them
        // somebody else's stats. Nothing of theirs went to Playforward.
        Assert.Null(OnlineDataStore.Fetch(LanPid, StatsDataId));
        Assert.Equal(0, playforward.BlobCount);
    }

    [Fact]
    public void LearnedColumnsAreSharedAcrossServers()
    {
        InMemoryProgressStore playforward = NewPlayforward();
        Attach(playforward);
        Assert.Equal(["msg_id", "blobCrc"], OnlineDataStore.Learn(0x09, ["msg_id", "blobCrc", ""]));

        Restart(playforward);

        // The wall consumer drops a row missing any column it looks up by name,
        // and the names come only from what FC3 put on the wire. A server that
        // had to relearn them would silently drop the first wall post after
        // every restart until a query re-taught it.
        Assert.Equal(["blobCrc", "msg_id"], OnlineDataStore.KnownColumns(0x09).Order());
        Assert.Empty(OnlineDataStore.Learn(0x09, ["msg_id"]));
    }

    [Fact]
    public void WallMessageIdsComeFromOneCounterForTheTitle()
    {
        InMemoryProgressStore playforward = NewPlayforward();
        Attach(playforward);
        uint first = OnlineDataStore.NextWallMessageId();
        uint second = OnlineDataStore.NextWallMessageId();
        Assert.Equal(first + 1, second);

        Restart(playforward);

        // FC3 acknowledges wall rows by msg_id. An id reissued after a restart
        // -- or by a second server -- would collide with a row it already read.
        Assert.Equal(second + 1, OnlineDataStore.NextWallMessageId());
    }

    [Fact]
    public void ForgettingAPlayerRereadsPlayforward()
    {
        InMemoryProgressStore playforward = NewPlayforward();
        Attach(playforward);
        OnlineDataStore.Store(PlayforwardPid, StatsDataId, RowSet([("value", 1)]));
        OnlineDataStore.Store(PlayforwardPid, 0x10, RowSet([("value", 2)]));
        OnlineDataStore.Store(OtherPid, StatsDataId, RowSet([("value", 3)]));

        // Playforward reset the player and told this server; the copy goes,
        // and the next fetch finds nothing there. The other player's rows are
        // untouched.
        playforward.ResetProgress("account-a");
        OnlineDataStore.Forget(PlayforwardPid);

        Assert.Null(OnlineDataStore.Fetch(PlayforwardPid, StatsDataId));
        Assert.Null(OnlineDataStore.Fetch(PlayforwardPid, 0x10));
        Assert.Equal(3u, Value(OnlineDataStore.Fetch(OtherPid, StatsDataId)!, 0, "value"));
    }

    [Fact]
    public void AnOutageServesThisSessionsCopyAndKeepsPlaying()
    {
        InMemoryProgressStore playforward = NewPlayforward();
        Attach(playforward);
        OnlineDataStore.Store(PlayforwardPid, StatsDataId, RowSet([("value", 1)]));

        playforward.Reachable = false;

        // A store that could not reach Playforward is still the session's
        // truth: the client wrote it and will read it back.
        OnlineDataStore.Store(PlayforwardPid, StatsDataId, RowSet([("value", 2)]));
        Assert.Equal(2u, Value(OnlineDataStore.Fetch(PlayforwardPid, StatsDataId)!, 0, "value"));

        // A player who could not be loaded is answered with nothing rather
        // than an error, and a wall message id still comes from somewhere.
        Assert.Null(OnlineDataStore.Fetch(OtherPid, StatsDataId));
        uint id = OnlineDataStore.NextWallMessageId();
        Assert.Equal(id + 1, OnlineDataStore.NextWallMessageId());
    }

    [Fact]
    public void WithoutAStoreTheStoreIsInMemoryAsBefore()
    {
        OnlineDataStore.Detach();
        OnlineDataStore.Store(PlayforwardPid, StatsDataId, RowSet([("value", 5)]));
        Assert.Equal(5u, Value(OnlineDataStore.Fetch(PlayforwardPid, StatsDataId)!, 0, "value"));
        Assert.Equal("0x0D", OnlineDataStore.DescribeStored(PlayforwardPid));

        uint first = OnlineDataStore.NextWallMessageId();
        Assert.Equal(first + 1, OnlineDataStore.NextWallMessageId());
    }
}
