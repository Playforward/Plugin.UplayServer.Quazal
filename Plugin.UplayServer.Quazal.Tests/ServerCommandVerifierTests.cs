using Plugin.UplayServer.Quazal.Playforward;

namespace Plugin.UplayServer.Quazal.Tests;

/// <summary>
/// The command verifier is the reason a kick can be trusted at all.
/// </summary>
public class ServerCommandVerifierTests
{
    private readonly PlayforwardTokenFactory _factory = new();

    private ServerCommandVerifier NewVerifier(string? audience = null) => new(
        new PinnedSigningKey(_factory.PublicKey),
        PlayforwardTokenFactory.Issuer,
        audience ?? PlayforwardTokenFactory.Audience,
        TimeSpan.FromSeconds(30));

    [Fact]
    public void AcceptsAGenuineCommand()
    {
        var actionId = Guid.NewGuid();
        var until = DateTimeOffset.UtcNow.AddMinutes(30);

        ServerCommand? command = NewVerifier().Verify(_factory.MintCommand(
            "9d4b2e1a-0000-4000-8000-000000000001",
            action: "ban",
            actionId: actionId,
            nativeSessionId: "3-17",
            reason: "abusive in voice chat",
            enforceUntil: until));

        Assert.NotNull(command);
        Assert.Equal(actionId, command.ActionId);
        Assert.Equal("ban", command.Action);
        Assert.Equal("3-17", command.NativeSessionId);
        Assert.Equal("9d4b2e1a-0000-4000-8000-000000000001", command.TargetAccountId);
        Assert.Equal("abusive in voice chat", command.Reason);
        // The duration travels in the command rather than being this server's
        // to choose: this server holds the block and Playforward holds the
        // record, and the two disagreeing would make the record wrong about
        // something a person is entitled to appeal.
        Assert.Equal(until.ToUnixTimeSeconds(), command.EnforceUntil?.ToUnixTimeSeconds());
    }

    [Fact]
    public void RefusesACommandSignedByAnotherKey()
    {
        var impostor = new PlayforwardTokenFactory();

        // The whole point: a compromised proxy, a misconfigured relay or an
        // operator's own tooling can all put bytes in front of this server, and
        // none of them holds Playforward's key.
        Assert.Null(NewVerifier().Verify(impostor.MintCommand("account-1")));
    }

    [Fact]
    public void RefusesACommandForAnotherTitlesServers()
    {
        // Playforward derives the audience per title precisely so a command
        // minted for one title cannot be enforced at another's server by
        // whoever operates it.
        Assert.Null(NewVerifier().Verify(
            _factory.MintCommand("account-1", audience: "playforward-game-servers:someothertitle")));
    }

    [Fact]
    public void RefusesACommandFromAnotherIssuer()
    {
        Assert.Null(NewVerifier().Verify(_factory.MintCommand("account-1", issuer: "not-playforward")));
    }

    [Fact]
    public void RefusesAnExpiredCommand()
    {
        // Commands are collected within seconds of being issued. One that stays
        // valid for a day is a replay waiting to happen against whoever next
        // holds that account.
        Assert.Null(NewVerifier().Verify(_factory.MintCommand(
            "account-1", expiresAt: DateTimeOffset.UtcNow.AddMinutes(-5))));
    }

    [Fact]
    public void RefusesACommandClaimingAnUnsignedAlgorithm()
    {
        // The classic JWT failure: a verifier that reads the algorithm out of
        // the token and does what it says.
        Assert.Null(NewVerifier().Verify(_factory.MintCommand("account-1", algorithm: "none")));
        Assert.Null(NewVerifier().Verify(_factory.MintCommand("account-1", algorithm: "HS256")));
    }

    [Fact]
    public void RefusesAnActionItDoesNotUnderstand()
    {
        // Guessing at an unknown instruction is how a future "warn" becomes an
        // accidental removal. A refused command is also not acknowledged, so
        // Playforward hands it back rather than it disappearing silently.
        Assert.Null(NewVerifier().Verify(_factory.MintCommand("account-1", action: "vaporise")));
    }

    [Theory]
    [InlineData("kick")]
    [InlineData("ban")]
    [InlineData("mute")]
    [InlineData("unmute")]
    public void AcceptsEveryActionItCanEnforce(string action)
    {
        Assert.NotNull(NewVerifier().Verify(_factory.MintCommand("account-1", action: action)));
    }

    [Fact]
    public void AKickCarriesNoExpiry()
    {
        ServerCommand? command = NewVerifier().Verify(_factory.MintCommand("account-1", action: "kick"));

        Assert.NotNull(command);
        Assert.Null(command.EnforceUntil);
    }

    [Theory]
    [InlineData("reset_progress")]
    [InlineData("delete_player")]
    public void AcceptsTheProgressActions(string action)
    {
        // The signed channel is how Playforward reaches into a server it does
        // not run, and "wipe this account's data here" belongs on it for the
        // same reason a kick does: it must provably have come from Playforward.
        ServerCommand? command = NewVerifier().Verify(_factory.MintCommand("account-1", action: action));

        Assert.NotNull(command);
        Assert.Equal(action, command.Action);
        Assert.Equal("account-1", command.TargetAccountId);
    }
}
