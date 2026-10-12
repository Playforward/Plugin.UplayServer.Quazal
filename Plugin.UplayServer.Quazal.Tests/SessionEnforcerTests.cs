using Plugin.UplayServer.Quazal.Playforward;

namespace Plugin.UplayServer.Quazal.Tests;

/// <summary>
/// What a verified command actually does to the server.
/// </summary>
[Collection("OnlineDataStore")]
public class SessionEnforcerTests
{
    private readonly PlayforwardTokenFactory _factory = new();

    private ServerCommandVerifier NewVerifier() => new(
        _factory.PublicKey,
        PlayforwardTokenFactory.Issuer,
        PlayforwardTokenFactory.Audience,
        TimeSpan.FromSeconds(30));

    private ServerCommand Command(string account, string action, DateTimeOffset? until = null)
    {
        ServerCommand? command = NewVerifier().Verify(
            _factory.MintCommand(account, action: action, enforceUntil: until));
        Assert.NotNull(command);
        return command;
    }

    /// <summary>An enforcer for somebody who is not logged in on this server.</summary>
    private static SessionEnforcer NobodyLoggedIn() => new(TestTitle.Id, _ => null);

    [Fact]
    public void ABanBlocksTheAccountUntilItLapses()
    {
        var enforcer = NobodyLoggedIn();
        const string account = "9d4b2e1a-0000-4000-8000-000000000001";

        Assert.False(enforcer.IsBlocked(account));

        Assert.True(enforcer.Apply(Command(account, "ban", DateTimeOffset.UtcNow.AddMinutes(15))));
        Assert.True(enforcer.IsBlocked(account));
    }

    [Fact]
    public void ALapsedBanStopsBlocking()
    {
        var enforcer = NobodyLoggedIn();
        const string account = "account-lapsed";

        enforcer.Block(account, DateTimeOffset.UtcNow.AddSeconds(-1));

        Assert.False(enforcer.IsBlocked(account));
    }

    [Fact]
    public void AnIndefiniteBanNeverLapses()
    {
        var enforcer = NobodyLoggedIn();
        const string account = "account-indefinite";

        enforcer.Block(account, until: null);

        Assert.True(enforcer.IsBlocked(account));
    }

    [Fact]
    public void AKickRemovesWithoutBlocking()
    {
        var enforcer = NobodyLoggedIn();
        const string account = "account-kicked";

        Assert.True(enforcer.Apply(Command(account, "kick")));

        // The distinction that makes 'ban' worth having as a separate action:
        // a kick is the ordinary "stop that", and it deliberately leaves the
        // door open.
        Assert.False(enforcer.IsBlocked(account));
    }

    [Fact]
    public void AKickAgainstSomebodyWhoHasAlreadyLeftIsStillApplied()
    {
        // Applied means this server did its part, not that the player noticed.
        // There is nothing left to do and nothing to retry, and answering
        // otherwise would have Playforward hand the command back forever.
        Assert.True(NobodyLoggedIn().Apply(Command("account-gone", "kick")));
    }

    [Fact]
    public void MuteIsRecordedAndUnmuteClearsIt()
    {
        var enforcer = NobodyLoggedIn();
        const string account = "account-muted";

        Assert.True(enforcer.Apply(Command(account, "mute", DateTimeOffset.UtcNow.AddMinutes(10))));
        Assert.True(enforcer.IsMuted(account));

        Assert.True(enforcer.Apply(Command(account, "unmute")));
        Assert.False(enforcer.IsMuted(account));
    }

    [Fact]
    public void RemovalLooksThePlayerUpByAccount()
    {
        // The command names an account; the session speaks PIDs. This is the
        // translation, and it is the server's own -- Playforward never learns a
        // PID.
        string? asked = null;
        var enforcer = new SessionEnforcer(TestTitle.Id, accountId =>
        {
            asked = accountId;
            return 2_000_001u;
        });

        enforcer.Apply(Command("account-here", "kick"));

        Assert.Equal("account-here", asked);
    }

    [Fact]
    public void AResetGoesToTheProgressStoreAndIsAcknowledged()
    {
        string? reset = null;
        var enforcer = new SessionEnforcer(TestTitle.Id, _ => null, resetProgress: account =>
        {
            reset = account;
            return true;
        });

        Assert.True(enforcer.Apply(Command("account-here", "reset_progress")));
        Assert.Equal("account-here", reset);
    }

    [Fact]
    public void ADeleteEndsTheSessionBeforeForgettingThePlayer()
    {
        // The order matters: the session is looked up by account, and once the
        // record is gone the lookup answers nothing. A delete that forgot the
        // player first would leave their session running under a retired PID.
        var order = new List<string>();
        var enforcer = new SessionEnforcer(
            TestTitle.Id,
            account =>
            {
                order.Add("lookup");
                return null;
            },
            deletePlayer: account =>
            {
                order.Add("delete");
                return true;
            });

        Assert.True(enforcer.Apply(Command("account-here", "delete_player")));
        Assert.Equal(["lookup", "delete"], order);
    }

    [Fact]
    public void AProgressCommandIsRefusedWhereThereIsNoDatabase()
    {
        // Not acknowledged, so Playforward keeps handing it back. Saying "done"
        // about somebody's data on a server that could not have done it would
        // be the wrong kind of quiet.
        var enforcer = NobodyLoggedIn();

        Assert.False(enforcer.Apply(Command("account-here", "reset_progress")));
        Assert.False(enforcer.Apply(Command("account-here", "delete_player")));
    }
}
