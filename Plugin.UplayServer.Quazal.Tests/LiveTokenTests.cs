using Plugin.UplayServer.Quazal.Playforward;

namespace Plugin.UplayServer.Quazal.Tests;

/// <summary>
/// Verifies a token minted by a real Playforward deployment, rather than by
/// this project's own factory.
/// </summary>
public class LiveTokenTests
{
    [Fact]
    public void VerifiesATokenMintedByARealPlayforward()
    {
        string? key = Environment.GetEnvironmentVariable("PLAYFORWARD_TEST_JWK_X");
        string? token = Environment.GetEnvironmentVariable("PLAYFORWARD_TEST_TOKEN");
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(token),
            "Set PLAYFORWARD_TEST_JWK_X and PLAYFORWARD_TEST_TOKEN to check against a live deployment.");

        var verifier = PlayforwardTokenVerifier.Create(new PlayforwardSettings
        {
            Enabled = true,
            PublicKeyBase64 = key!,
            Issuer = Environment.GetEnvironmentVariable("PLAYFORWARD_TEST_ISSUER") ?? PlayforwardTokenFactory.Issuer,
            // These tokens live about a minute; a human running this by hand
            // will not always beat that.
            ClockSkewSeconds = 3600,
        }, Environment.GetEnvironmentVariable("PLAYFORWARD_TEST_AUDIENCE") ?? PlayforwardTokenFactory.Audience);
        Assert.NotNull(verifier);

        PlayforwardIdentity? identity = verifier.Verify(token!);

        Assert.NotNull(identity);
        Assert.True(Guid.TryParse(identity.AccountId, out _), "account_id should be Playforward's account UUID");
        Assert.NotEmpty(identity.DisplayName);
        Assert.NotEmpty(identity.Scope);
        Assert.True(Guid.TryParse(identity.TokenId, out _), "jti should be the replay nonce Playforward mints");
    }

    /// <summary>
    /// Verifies a moderation command collected from a real Playforward
    /// deployment.
    /// </summary>
    [Fact]
    public void VerifiesACommandIssuedByARealPlayforward()
    {
        string? key = Environment.GetEnvironmentVariable("PLAYFORWARD_TEST_JWK_X");
        string? token = Environment.GetEnvironmentVariable("PLAYFORWARD_TEST_COMMAND");
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(token),
            "Set PLAYFORWARD_TEST_JWK_X and PLAYFORWARD_TEST_COMMAND to check against a live deployment.");

        byte[] raw = SignedJwt.Base64UrlDecode(key!.Trim());
        var verifier = new ServerCommandVerifier(
            new Org.BouncyCastle.Crypto.Parameters.Ed25519PublicKeyParameters(raw),
            Environment.GetEnvironmentVariable("PLAYFORWARD_TEST_ISSUER") ?? PlayforwardTokenFactory.Issuer,
            Environment.GetEnvironmentVariable("PLAYFORWARD_TEST_AUDIENCE") ?? PlayforwardTokenFactory.Audience,
            // A command lives about two minutes; a human running this by hand
            // will not always beat that.
            TimeSpan.FromHours(1));

        ServerCommand? command = verifier.Verify(token!);

        Assert.NotNull(command);
        Assert.NotEqual(Guid.Empty, command.ActionId);
        Assert.True(Guid.TryParse(command.TargetAccountId, out _), "the target should be Playforward's account UUID");
        Assert.Contains(command.Action, new[] { "kick", "ban", "mute", "unmute" });
        // This server's own id for the match, echoed back so it does not need a
        // map from Playforward's id to its own just to act on a command.
        Assert.NotEmpty(command.NativeSessionId);
    }
}
