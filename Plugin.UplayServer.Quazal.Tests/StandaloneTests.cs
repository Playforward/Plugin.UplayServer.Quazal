using System.Net;
using Plugin.UplayServer.Quazal.Standalone;

namespace Plugin.UplayServer.Quazal.Tests;

/// <summary>
/// A title served without Playforward: any name is a player, the same name
/// is the same player after a restart, and what they built up is still there.
/// </summary>
[Collection("OnlineDataStore")]
public sealed class StandaloneTests : IDisposable
{
    private const uint PidStart = 1_000_000;
    private static readonly IPEndPoint Alice = new(IPAddress.Parse("203.0.113.10"), 3074);
    private static readonly IPEndPoint Mallory = new(IPAddress.Parse("198.51.100.7"), 3074);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "quazal-standalone-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        LocalTitles.Clear();
        OnlineDataStore.Detach();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private StandaloneSettings Settings(params string[] reserved) => new()
    {
        DataDirectory = _directory,
        PidStart = PidStart,
        ReservedNames = [.. reserved],
    };

    private LocalTitle StartTitle(QuazalTitle title, StandaloneSettings? settings = null)
    {
        LocalTitle local = LocalTitle.Start(settings ?? Settings(), title)!;
        Assert.NotNull(local);
        LocalTitles.Register(local);
        return local;
    }

    [Fact]
    public void AStoreComesBackWholeAfterARestart()
    {
        LocalProgressStore store = LocalProgressStore.Open(_directory, "t", PidStart)!;
        uint alice = store.ResolvePid("name:alice", "Alice")!.Value;
        uint bob = store.ResolvePid("name:bob", "Bob")!.Value;
        Assert.Equal(PidStart, alice);
        Assert.Equal(PidStart + 1, bob);
        Assert.True(store.SaveBlob(alice, "ns", "k", [1, 2, 3]));
        Assert.True(store.SaveScores(alice, new Dictionary<string, long> { ["xp"] = 50 }));
        Assert.True(store.SaveScore(bob, "xp", 70));
        Assert.True(store.SaveTitleBlob("cols", "6", [9]));
        Assert.Equal(1u, store.NextCounter("match"));

        LocalProgressStore reopened = LocalProgressStore.Open(_directory, "t", PidStart)!;
        Assert.Equal(alice, reopened.ResolvePid("name:alice", "Alice"));
        Assert.Equal(PidStart + 2, reopened.ResolvePid("name:carol", "Carol"));
        Assert.Equal([1, 2, 3], reopened.LoadPlayer(alice)!.Single(b => b.Namespace == "ns" && b.Key == "k").Payload);
        Assert.Equal([9], reopened.LoadTitle()!.Single().Payload);
        Assert.Equal(2u, reopened.NextCounter("match"));
        Assert.Equal([bob, alice], reopened.TopScores("xp", 10)!.Select(s => (uint)s.Pid));
        Assert.Equal("Bob", reopened.TopScores("xp", 1)!.Single().DisplayName);
        Assert.Single(reopened.ScoresFor("xp", [alice, PidStart + 2])!);
    }

    [Fact]
    public void AStoreRefusesValuesForPidsItNeverGaveOut()
    {
        LocalProgressStore store = LocalProgressStore.Open(_directory, "t", PidStart)!;
        Assert.False(store.SaveBlob(PidStart, "ns", "k", [1]));
        Assert.Null(store.LoadPlayer(PidStart));
    }

    [Fact]
    public void AnUnreadablePrincipalsFileStopsTheTitleRatherThanReissuingPids()
    {
        LocalProgressStore.Open(_directory, "t", PidStart)!.ResolvePid("name:alice", "Alice");
        File.WriteAllText(Path.Combine(_directory, "t", "principals.json"), "{ not json");
        Assert.Null(LocalProgressStore.Open(_directory, "t", PidStart));
    }

    [Fact]
    public void AnUnreadablePlayerFileIsSetAsideAndThePlayerKeepsTheirPid()
    {
        LocalProgressStore store = LocalProgressStore.Open(_directory, "t", PidStart)!;
        uint alice = store.ResolvePid("name:alice", "Alice")!.Value;
        store.SaveBlob(alice, "ns", "k", [1]);
        string file = Path.Combine(_directory, "t", "players", $"{alice}.json");
        File.WriteAllText(file, "garbage");

        LocalProgressStore reopened = LocalProgressStore.Open(_directory, "t", PidStart)!;
        Assert.Equal(alice, reopened.ResolvePid("name:alice", "Alice"));
        Assert.Empty(reopened.LoadPlayer(alice)!);
        Assert.False(File.Exists(file));
        Assert.Single(Directory.GetFiles(Path.Combine(_directory, "t", "players"), "*.unreadable-*"));
    }

    [Fact]
    public void AnyNameLogsInAndKeepsItsPidAcrossCaseAndRestarts()
    {
        QuazalTitle title = QuazalTitle.ForTests("standalone-login", 6600, 6601);
        LocalTitle local = StartTitle(title);

        QuazalAccount first = local.Login("Alice", Alice)!;
        Assert.Equal(PidStart, first.Pid);
        Assert.Equal("Alice", first.DisplayName);
        Assert.Equal(PidStart, local.Login("  alice ", Alice)!.Pid);

        LocalTitles.Clear();
        LocalTitle restarted = StartTitle(title);
        Assert.Equal(PidStart, restarted.Login("ALICE", Alice)!.Pid);
        Assert.Equal(PidStart + 1, restarted.Login("Bob", Mallory)!.Pid);
    }

    [Fact]
    public void ANameBeingPlayedElsewhereIsRefusedUntilThatPlayerLeaves()
    {
        LocalTitle local = StartTitle(QuazalTitle.ForTests("standalone-in-use", 6602, 6603));
        uint alice = local.Login("Alice", Alice)!.Pid;

        Assert.Null(local.Login("alice", Mallory));
        // Alice's own reconnect, from any port of her address, is fine.
        Assert.Equal(alice, local.Login("Alice", new IPEndPoint(Alice.Address, 4000))!.Pid);

        local.KeepAlive(new Dictionary<uint, IPAddress> { [alice] = Alice.Address });
        Assert.Null(local.Login("Alice", Mallory));

        local.KeepAlive(new Dictionary<uint, IPAddress>());
        Assert.Equal(alice, local.Login("Alice", Mallory)!.Pid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\u0001name")]
    [InlineData("Admin")]
    public void EmptyControlAndReservedNamesAreRefused(string name)
    {
        LocalTitle local = StartTitle(QuazalTitle.ForTests("standalone-names", 6604, 6605), Settings("admin"));
        Assert.Null(local.Login(name, Alice));
        Assert.Null(local.Login(new string('x', LocalTitle.MaxNameLength + 1), Alice));
    }

    [Fact]
    public void TheLoginPathGivesAStrangerAnAccountOnlyOnAStandaloneTitle()
    {
        QuazalTitle standalone = QuazalTitle.ForTests("standalone-settings", 6606, 6607);
        QuazalTitle lan = QuazalTitle.ForTests("standalone-settings-lan", 6608, 6609);
        StartTitle(standalone);
        var settings = new QuazalSettings();

        QuazalAccount? account = settings.FindPlayerAccount(standalone, "Stranger", Alice);
        Assert.NotNull(account);
        Assert.Equal(PidStart, account.Pid);

        // The ticket and the secure CONNECT find the player by pid.
        Assert.Equal(PidStart, settings.FindAccountByPid(standalone, PidStart)!.Pid);
        Assert.Equal("Stranger", settings.NameForPid(standalone, PidStart));

        Assert.Null(settings.FindPlayerAccount(lan, "Stranger", Alice));
        Assert.Null(settings.FindAccountByPid(lan, PidStart));

        // A configured account keeps its own scheme.
        Assert.Equal(TicketGranting.TrackingPid, settings.FindLoginAccount(standalone, TicketGranting.TrackingUser)!.Pid);
    }

    [Fact]
    public void ProgressIsKeptForStandalonePlayersAndOnlyThem()
    {
        QuazalTitle title = QuazalTitle.ForTests("standalone-stores", 6610, 6611);
        LocalTitle local = StartTitle(title);
        uint alice = local.Login("Alice", Alice)!.Pid;

        Assert.Same(local.Store, PlayerStores.ForTitle(title.TitleId));
        Assert.Same(local.Store, PlayerStores.ForPlayer(title.TitleId, alice));
        Assert.Null(PlayerStores.ForPlayer(title.TitleId, 2000));
        Assert.Null(PlayerStores.ForTitle("not-served"));
    }

    [Fact]
    public void Fc3OnlineDataPersistsThroughTheLocalStore()
    {
        LocalTitle local = StartTitle(TestTitle.Quazal);
        uint alice = local.Login("Alice", Alice)!.Pid;
        OnlineDataStore.Attach(TestTitle.Id, local.Store, pid => PlayerStores.ForPlayer(TestTitle.Id, pid) is not null);
        OnlineDataRowSet uploaded = Fc3Wire.RowSet([("pid", alice), ("challenge_id", 7), ("value", 42)]);
        OnlineDataStore.Store(alice, 0x0D, uploaded);

        // A restart: fresh memory, the same folder.
        OnlineDataStore.Detach();
        LocalTitles.Clear();
        LocalTitle restarted = StartTitle(TestTitle.Quazal);
        OnlineDataStore.Attach(TestTitle.Id, restarted.Store, pid => PlayerStores.ForPlayer(TestTitle.Id, pid) is not null);

        OnlineDataRowSet? restored = OnlineDataStore.Fetch(alice, 0x0D);
        Assert.NotNull(restored);
        Assert.Equal(uploaded.ToBytes(), restored.ToBytes());
    }

    [Fact]
    public void SettingsRefuseAPidRangeThatOverlapsTheOthers()
    {
        var settings = new QuazalSettings { Standalone = new StandaloneSettings { PidStart = 100 } };
        Assert.NotEmpty(settings.Standalone.Validate(settings));
        Assert.Empty(new QuazalSettings().Standalone.Validate(new QuazalSettings()));
    }
}
