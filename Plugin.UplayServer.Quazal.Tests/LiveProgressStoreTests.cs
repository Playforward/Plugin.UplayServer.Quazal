using Plugin.UplayServer.Quazal.Playforward;

namespace Plugin.UplayServer.Quazal.Tests;

/// <summary>
/// Drives the player-data store against a real Playforward deployment.
/// </summary>
[Collection("OnlineDataStore")]
public class LiveProgressStoreTests
{
    [Fact]
    public void RoundTripsAPlayerThroughARealPlayforward()
    {
        string? url = Environment.GetEnvironmentVariable("PLAYFORWARD_TEST_URL");
        string? secret = Environment.GetEnvironmentVariable("PLAYFORWARD_TEST_SERVER_SECRET");
        string? accountId = Environment.GetEnvironmentVariable("PLAYFORWARD_TEST_ACCOUNT_ID");
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(accountId),
            "Set PLAYFORWARD_TEST_URL, PLAYFORWARD_TEST_SERVER_SECRET and PLAYFORWARD_TEST_ACCOUNT_ID to check against a live deployment.");

        using PlayforwardServerClient? client = PlayforwardServerClient.Create(new PlayforwardSettings
        {
            Enabled = true,
            BaseUrl = url!,
        }, secret!, TestTitle.Id);
        Assert.NotNull(client);
        var store = new PlayforwardProgressStore(client);

        // Identity: allocated once, stable after.
        uint? pid = store.ResolvePid(accountId!, "Live Test");
        Assert.NotNull(pid);
        Assert.Equal(pid, store.ResolvePid(accountId!, "Live Test"));

        // Progress: the game's bytes, back byte for byte.
        byte[] payload = [0x02, 0x00, 0x00, 0x00, 0xde, 0xad, 0xbe, 0xef, 0x00];
        string key = "0x" + Random.Shared.Next(0x20, 0xff).ToString("X2");
        Assert.True(store.SaveBlob(pid.Value, "onlinedata", key, payload));
        IReadOnlyList<StoredBlob>? blobs = store.LoadPlayer(pid.Value);
        Assert.NotNull(blobs);
        StoredBlob stored = Assert.Single(blobs, blob => blob.Key == key);
        Assert.Equal("onlinedata", stored.Namespace);
        Assert.Equal(payload, stored.Payload);

        // Title state and a counter that only ever goes up.
        Assert.True(store.SaveTitleBlob("onlinedata-columns", "0x09", "msg_id\nblobCrc"u8.ToArray()));
        IReadOnlyList<StoredBlob>? title = store.LoadTitle();
        Assert.NotNull(title);
        Assert.Contains(title, blob => blob.Namespace == "onlinedata-columns" && blob.Key == "0x09");

        uint? first = store.NextCounter("live_test_counter");
        Assert.NotNull(first);
        Assert.Equal(first + 1, store.NextCounter("live_test_counter"));
    }

    /// <summary>
    /// The protocol end: Far Cry 3's own StoreOnlineData and FetchOnlineData,
    /// through the real handlers, with a real Playforward holding the rows.
    /// </summary>
    [Fact]
    public void StoresAndRestoresOnlineDataThroughTheFc3Protocol()
    {
        string? url = Environment.GetEnvironmentVariable("PLAYFORWARD_TEST_URL");
        string? secret = Environment.GetEnvironmentVariable("PLAYFORWARD_TEST_SERVER_SECRET");
        string? accountId = Environment.GetEnvironmentVariable("PLAYFORWARD_TEST_ACCOUNT_ID");
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(accountId),
            "Set PLAYFORWARD_TEST_URL, PLAYFORWARD_TEST_SERVER_SECRET and PLAYFORWARD_TEST_ACCOUNT_ID to check against a live deployment.");

        using PlayforwardServerClient? client = PlayforwardServerClient.Create(new PlayforwardSettings
        {
            Enabled = true, BaseUrl = url!,
        }, secret!, TestTitle.Id);
        Assert.NotNull(client);
        var store = new PlayforwardProgressStore(client);
        uint pid = store.ResolvePid(accountId!, "Protocol Test")!.Value;

        string? capture = Environment.GetEnvironmentVariable("FC3_TEST_ROWSET");
        byte[] rowSet = capture is null
            ? Fc3Wire.RowSet([("pid", pid), ("challenge_id", 7), ("value", 42)], [("pid", pid), ("challenge_id", 8), ("value", 1)]).ToBytes()
            : File.ReadAllBytes(capture);
        const uint statsDataId = 0x0D;

        try
        {
            // The server as the plugin starts it, with this player logged in.
            OnlineDataStore.Detach();
            OnlineDataStore.Attach(TestTitle.Id, store, candidate => candidate == pid);

            // The game uploads. The reply is the assigned-ids row (one column
            // per uploaded row), which is what the client's completion
            // handler insists on.
            byte[] storeReply = Fc3OnlineDataService.BuildStoreOnlineDataResult(Fc3Wire.StoreRequest(statsDataId, rowSet), pid, TestTitle.Id);
            Assert.NotEqual(OnlineDataStore.EmptyRowSet, storeReply);

            // Playforward now holds exactly the bytes the game sent.
            StoredBlob stored = Assert.Single(store.LoadPlayer(pid)!, blob => blob.Namespace == "onlinedata" && blob.Key == $"0x{statsDataId:X2}");
            Assert.Equal(rowSet, stored.Payload);

            // A restart, or the player's next match landing on another
            // server of the title: nothing in memory, then the game asks.
            OnlineDataStore.Detach();
            OnlineDataStore.Attach(TestTitle.Id, store, candidate => candidate == pid);
            byte[] fetched = Fc3OnlineDataService.BuildFetchOnlineDataResult(Fc3Wire.FetchRequest(statsDataId), pid);
            Assert.Equal(rowSet, fetched);

            // And a wall post goes through the completion path: the row is
            // filled out with the harvested columns, its message id drawn
            // from the title-wide counter, and the whole completed set kept.
            byte[] wallReply = Fc3OnlineDataService.BuildStoreOnlineDataResult(
                Fc3Wire.StoreRequest(0x09, Fc3Wire.RowSet([("type", 0u), ("data", 0u)]).ToBytes()), pid, TestTitle.Id);
            Assert.NotEqual(OnlineDataStore.EmptyRowSet, wallReply);
            OnlineDataStore.Detach();
            OnlineDataStore.Attach(TestTitle.Id, store, candidate => candidate == pid);
            OnlineDataRowSet? wall = OnlineDataStore.Fetch(pid, 0x09);
            Assert.NotNull(wall);
            OnlineDataColumn messageId = Assert.Single(wall.Rows).Single(column => column.Name == "msg_id");
            Assert.True(messageId.NumericValue > 0, "the wall row's msg_id should come from Playforward's counter");
        }
        finally
        {
            OnlineDataStore.Detach();
        }
    }
}
