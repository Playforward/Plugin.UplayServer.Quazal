using Plugin.UplayServer.Quazal.Playforward;

namespace Plugin.UplayServer.Quazal.Tests;

/// <summary>
/// The account-to-PID map, which is Playforward's: this server asks for a
/// PID and keeps a copy for the session.
/// </summary>
public sealed class PlayerDirectoryTests
{
    private const uint PidStart = 2_000_000;

    private static PlayforwardIdentity Identity(string accountId, string displayName = "Player") => new(
        AccountId: accountId,
        DisplayName: displayName,
        LegacyName: "far-cry-player",
        RetroName: "farcryp",
        Scope: "user",
        PartyRef: "",
        TokenId: Guid.NewGuid().ToString(),
        ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(1));

    [Fact]
    public void ResolvesThroughTheStoreAndKeepsThePidStable()
    {
        var store = new InMemoryProgressStore(PidStart);
        var directory = new PlayerDirectory(store);

        uint a = directory.ResolvePid(Identity("account-a"))!.Value;
        uint b = directory.ResolvePid(Identity("account-b"))!.Value;
        Assert.Equal(PidStart, a);
        Assert.NotEqual(a, b);

        // Same account, same number -- and a display-name change moves nothing.
        Assert.Equal(a, directory.ResolvePid(Identity("account-a", "New Name")));
        Assert.Equal("account-a", directory.FindAccountId(a));
        Assert.Equal(a, directory.FindPid("account-a"));
        Assert.Null(directory.FindAccountId(999_999));
    }

    [Fact]
    public void TwoServersOfOneTitleSeeTheSamePlayer()
    {
        // The whole reason the map moved out of this process: behind quick
        // match a player lands on whichever server has room, and their stats
        // are keyed by PID.
        var playforward = new InMemoryProgressStore(PidStart);
        var first = new PlayerDirectory(playforward);
        var second = new PlayerDirectory(playforward);

        Assert.Equal(first.ResolvePid(Identity("account-a")), second.ResolvePid(Identity("account-a")));
    }

    [Fact]
    public void AnOutageReusesThisSessionsPidButCannotInventOne()
    {
        var store = new InMemoryProgressStore(PidStart);
        var directory = new PlayerDirectory(store);
        uint known = directory.ResolvePid(Identity("account-a"))!.Value;

        store.Reachable = false;

        // A LAN party keeps going: somebody who logged in before the outage
        // logs in again from memory.
        Assert.Equal(known, directory.ResolvePid(Identity("account-a")));

        // A stranger cannot be given a number this server made up, because
        // Playforward's number would differ and their stats would split.
        Assert.Null(directory.ResolvePid(Identity("account-b")));
    }

    [Fact]
    public void ForgettingAnAccountMakesTheNextLoginAskAgain()
    {
        var store = new InMemoryProgressStore(PidStart);
        var directory = new PlayerDirectory(store);
        uint retired = directory.ResolvePid(Identity("account-a"))!.Value;

        // Playforward deleted the player; this server drops its copy.
        store.DeletePlayer("account-a");
        directory.Forget("account-a");
        Assert.Null(directory.FindPid("account-a"));

        // And the next login is a new player on a fresh, higher number.
        uint fresh = directory.ResolvePid(Identity("account-a"))!.Value;
        Assert.True(fresh > retired);
    }
}
