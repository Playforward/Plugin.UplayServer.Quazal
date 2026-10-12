using System.Net;
using System.Security.Cryptography;
using Plugin.UplayServer.Quazal.Playforward;

namespace Plugin.UplayServer.Quazal.Tests;

/// <summary>
/// The challenge-response state machine on its own: issue, HELLO, PROOF, and
/// every way a proof is refused.
/// </summary>
public sealed class LaunchStoreTests
{
    private static readonly IPEndPoint Game = new(IPAddress.Parse("203.0.113.10"), 51000);
    private static readonly IPEndPoint Eavesdropper = new(IPAddress.Parse("203.0.113.10"), 51001);

    private static ResolvedPlayer NewPlayer(string accountId = "account-one", string partyRef = "") => new(
        new PlayforwardIdentity(accountId, "Player", "player", "plyr", "user", partyRef,
            TokenId: Guid.NewGuid().ToString(), ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(1)),
        Pid: 2_000_001);

    // Idle as long as the ceiling: these tests are about the exchange, and
    // the two lifetimes have their own in PlayforwardGatewayTests.
    private static LaunchStore NewStore(int ttlSeconds = 60) =>
        new(TimeSpan.FromSeconds(ttlSeconds), TimeSpan.FromSeconds(ttlSeconds));

    /// <summary>What the shim does: answer the challenge with the key it was handed.</summary>
    private static byte[] Answer(IssuedLaunch launch, byte[] challengeDatagram)
    {
        Assert.True(LaunchEnvelope.TryReadReply(challengeDatagram, LaunchEnvelope.Kind.Challenge, launch.IdBytes, out byte[] challenge));
        return LaunchEnvelope.ExpectedProof(launch.Key, launch.IdBytes, challenge);
    }

    [Fact]
    public void AGoodProofFromTheChallengedSocketBindsThePlayer()
    {
        var store = NewStore();
        ResolvedPlayer player = NewPlayer();
        IssuedLaunch launch = store.Issue(player);

        byte[]? challenge = store.Challenge(launch.IdBytes, Game);
        Assert.NotNull(challenge);

        var proved = store.Prove(launch.IdBytes, Game, Answer(launch, challenge));
        Assert.NotNull(proved);
        Assert.Same(player, proved.Value.Player);

        // The go-ahead is authenticated: the shim can check it with the key.
        Assert.True(LaunchEnvelope.TryReadReply(proved.Value.Bound, LaunchEnvelope.Kind.Bound, launch.IdBytes, out _));
    }

    [Fact]
    public void AnUnknownLaunchGetsNoChallenge()
    {
        var store = NewStore();
        Assert.Null(store.Challenge(RandomNumberGenerator.GetBytes(LaunchEnvelope.LaunchIdLength), Game));
    }

    [Fact]
    public void AProofReplayedFromAnotherSocketIsRefused()
    {
        var store = NewStore();
        IssuedLaunch launch = store.Issue(NewPlayer());
        byte[] challenge = store.Challenge(launch.IdBytes, Game)!;
        byte[] proof = Answer(launch, challenge);

        // Somebody on the segment reads the proof off the wire and sends it
        // from their own socket, behind the same address. This is the attack
        // the whole exchange exists to stop.
        Assert.Null(store.Prove(launch.IdBytes, Eavesdropper, proof));

        // And the real game is not punished for it: its own proof still lands.
        Assert.NotNull(store.Prove(launch.IdBytes, Game, proof));
    }

    [Fact]
    public void AProofIsGoodOnceOnly()
    {
        var store = NewStore();
        IssuedLaunch launch = store.Issue(NewPlayer());
        byte[] proof = Answer(launch, store.Challenge(launch.IdBytes, Game)!);

        Assert.NotNull(store.Prove(launch.IdBytes, Game, proof));
        Assert.Null(store.Prove(launch.IdBytes, Game, proof));
    }

    [Fact]
    public void AProofWithTheWrongKeyIsRefused()
    {
        var store = NewStore();
        IssuedLaunch launch = store.Issue(NewPlayer());
        byte[] challengeDatagram = store.Challenge(launch.IdBytes, Game)!;
        LaunchEnvelope.TryReadReply(challengeDatagram, LaunchEnvelope.Kind.Challenge, launch.IdBytes, out byte[] challenge);

        byte[] forged = LaunchEnvelope.ExpectedProof(RandomNumberGenerator.GetBytes(32), launch.IdBytes, challenge);
        Assert.Null(store.Prove(launch.IdBytes, Game, forged));
    }

    [Fact]
    public void AProofForAnEarlierChallengeIsRefused()
    {
        var store = NewStore();
        IssuedLaunch launch = store.Issue(NewPlayer());

        byte[] first = Answer(launch, store.Challenge(launch.IdBytes, Game)!);
        store.Challenge(launch.IdBytes, Game); // a second HELLO replaces the challenge

        Assert.Null(store.Prove(launch.IdBytes, Game, first));
    }

    [Fact]
    public void ALaunchCanBeProvedAgainOnAFreshChallenge()
    {
        // A reconnect: new socket, same launch, same key.
        var store = NewStore();
        IssuedLaunch launch = store.Issue(NewPlayer());
        Assert.NotNull(store.Prove(launch.IdBytes, Game, Answer(launch, store.Challenge(launch.IdBytes, Game)!)));

        var later = new IPEndPoint(Game.Address, 51002);
        Assert.NotNull(store.Prove(launch.IdBytes, later, Answer(launch, store.Challenge(launch.IdBytes, later)!)));
    }

    [Fact]
    public void AnExpiredLaunchCannotBeProved()
    {
        var store = NewStore(ttlSeconds: -1);
        IssuedLaunch launch = store.Issue(NewPlayer());
        Assert.Null(store.Challenge(launch.IdBytes, Game));
    }

    [Fact]
    public void RefreshChangesWhoALaterProofBinds()
    {
        var store = NewStore();
        IssuedLaunch launch = store.Issue(NewPlayer(partyRef: ""));

        // The player joined a party after pressing Play. The client presented
        // a fresh token; the shim knows nothing and still holds the old key.
        store.Refresh(NewPlayer(partyRef: "party-7"));

        var proved = store.Prove(launch.IdBytes, Game, Answer(launch, store.Challenge(launch.IdBytes, Game)!));
        Assert.Equal("party-7", proved!.Value.Player.Identity.PartyRef);
    }

    [Fact]
    public void ForgetEndsEveryLaunchOfTheAccount()
    {
        var store = NewStore();
        IssuedLaunch mine = store.Issue(NewPlayer("account-one"));
        IssuedLaunch theirs = store.Issue(NewPlayer("account-two"));

        store.Forget("account-one");

        Assert.Null(store.Challenge(mine.IdBytes, Game));
        Assert.NotNull(store.Challenge(theirs.IdBytes, Game));
    }

    [Fact]
    public void EveryLaunchIsDistinct()
    {
        var store = NewStore();
        ResolvedPlayer player = NewPlayer();
        IssuedLaunch first = store.Issue(player);
        IssuedLaunch second = store.Issue(player);

        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.Key, second.Key);
        Assert.Equal(32, first.Key.Length);
    }
}
