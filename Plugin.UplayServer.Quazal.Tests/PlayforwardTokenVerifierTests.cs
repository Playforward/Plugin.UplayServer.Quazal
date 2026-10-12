using Plugin.UplayServer.Quazal.Playforward;

namespace Plugin.UplayServer.Quazal.Tests;

/// <summary>
/// The verifier is the entire trust relationship with Playforward: anything it
/// accepts becomes a player on this server. Every test here is a way somebody
/// could try to be someone they are not.
/// </summary>
public class PlayforwardTokenVerifierTests
{
    private const string AccountId = "9d4b2e1a-0000-4000-8000-000000000001";

    private static (PlayforwardTokenVerifier Verifier, PlayforwardTokenFactory Tokens) NewVerifier(
        Action<PlayforwardSettings>? configure = null)
    {
        var tokens = new PlayforwardTokenFactory();
        var settings = new PlayforwardSettings
        {
            Enabled = true,
            PublicKeyBase64 = tokens.PublicKeyBase64Url,
            Issuer = PlayforwardTokenFactory.Issuer,
        };
        configure?.Invoke(settings);

        PlayforwardTokenVerifier? verifier = PlayforwardTokenVerifier.Create(settings, PlayforwardTokenFactory.Audience);
        Assert.NotNull(verifier);
        return (verifier, tokens);
    }

    [Fact]
    public void AcceptsAWellFormedTokenAndReadsItsClaims()
    {
        var (verifier, tokens) = NewVerifier();

        PlayforwardIdentity? identity = verifier.Verify(
            tokens.Mint(AccountId, "Far Cry Player", legacyName: "far-cry-guy", retroName: "fcguy", scope: "moderator"));

        Assert.NotNull(identity);
        Assert.Equal(AccountId, identity.AccountId);
        Assert.Equal("Far Cry Player", identity.DisplayName);
        Assert.Equal("far-cry-guy", identity.LegacyName);
        Assert.Equal("fcguy", identity.RetroName);
        Assert.Equal("moderator", identity.Scope);
        Assert.NotEmpty(identity.TokenId);
    }

    [Fact]
    public void AcceptsTheKeyInEveryEncodingSomebodyMightPasteIt()
    {
        var tokens = new PlayforwardTokenFactory();
        string base64Url = tokens.PublicKeyBase64Url;
        string standard = base64Url.Replace('-', '+').Replace('_', '/');

        // The JWKS shows base64url, unpadded. Somebody re-encoding it by hand
        // may well produce any of these, and a server that silently refuses
        // every token because of a '+' is a bad afternoon.
        foreach (string encoding in new[]
                 {
                     base64Url,
                     base64Url + "=",
                     standard,
                     standard + "=",
                 })
        {
            var verifier = PlayforwardTokenVerifier.Create(new PlayforwardSettings
            {
                Enabled = true,
                PublicKeyBase64 = encoding,
                Issuer = PlayforwardTokenFactory.Issuer,
            }, PlayforwardTokenFactory.Audience);
            Assert.NotNull(verifier);
            Assert.NotNull(verifier.Verify(tokens.Mint(AccountId)));
        }
    }

    [Fact]
    public void FallsBackToDisplayNameWhenTierNamesAreAbsent()
    {
        // A token minted by a Playforward that predates the tier names carries
        // only a display name. The player must still resolve at every cap
        // rather than enforce against an empty tier and be refused.
        var (verifier, tokens) = NewVerifier();
        PlayforwardIdentity? id = verifier.Verify(tokens.Mint(AccountId, "Jason Brody", legacyName: "", retroName: ""));
        Assert.NotNull(id);
        Assert.Equal("Jason Brody", id.NameForCap(32));
        Assert.Equal("Jason Brody", id.NameForCap(16));
        Assert.Equal("Jason Brody", id.NameForCap(8));
    }

    [Fact]
    public void RejectsATokenSignedByAnotherKey()
    {
        var (verifier, _) = NewVerifier();
        var impostor = new PlayforwardTokenFactory();

        // The whole point of the asymmetric key: holding a game server does not
        // let you mint players on it.
        Assert.Null(verifier.Verify(impostor.Mint(AccountId)));
    }

    [Fact]
    public void RejectsATamperedSignature()
    {
        var (verifier, tokens) = NewVerifier();
        Assert.Null(verifier.Verify(tokens.MintWithBrokenSignature(AccountId)));
    }

    [Fact]
    public void RejectsATokenMintedForAnotherTitle()
    {
        var (verifier, tokens) = NewVerifier();

        // Playforward derives the audience per title precisely so a token a
        // player minted to join one game cannot be replayed at another title's
        // server by whoever runs it. Not checking this gives that away.
        Assert.Null(verifier.Verify(tokens.Mint(AccountId, audience: "playforward-game-servers:someothertitle")));
    }

    [Fact]
    public void RejectsAnAccountsApiTokenReplayedHere()
    {
        var (verifier, tokens) = NewVerifier();
        Assert.Null(verifier.Verify(tokens.Mint(AccountId, audience: "playforward-accounts")));
    }

    [Fact]
    public void RejectsAnotherIssuer()
    {
        var (verifier, tokens) = NewVerifier();
        Assert.Null(verifier.Verify(tokens.Mint(AccountId, issuer: "not-playforward")));
    }

    [Fact]
    public void RejectsAnExpiredToken()
    {
        var (verifier, tokens) = NewVerifier(settings => settings.ClockSkewSeconds = 0);

        // These tokens are minted to live about a minute. The short life is the
        // only revocation a game server gets, since it cannot reach the
        // accounts database to ask whether a session still exists.
        Assert.Null(verifier.Verify(tokens.Mint(AccountId, expiresAt: DateTimeOffset.UtcNow.AddMinutes(-5))));
    }

    [Fact]
    public void RejectsATokenThatIsNotValidYet()
    {
        var (verifier, tokens) = NewVerifier(settings => settings.ClockSkewSeconds = 0);
        Assert.Null(verifier.Verify(tokens.Mint(AccountId, notBefore: DateTimeOffset.UtcNow.AddMinutes(5))));
    }

    [Fact]
    public void AllowsForClocksThatDisagreeALittle()
    {
        var (verifier, tokens) = NewVerifier(settings => settings.ClockSkewSeconds = 120);

        // Just expired, inside the configured tolerance. A game server and the
        // accounts service are different machines and their clocks drift.
        Assert.NotNull(verifier.Verify(tokens.Mint(AccountId, expiresAt: DateTimeOffset.UtcNow.AddSeconds(-30))));
    }

    [Fact]
    public void RejectsAlgorithmSubstitution()
    {
        var (verifier, tokens) = NewVerifier();

        // The classic JWT failure is a verifier that reads alg out of the token
        // and does as it is told -- "none" skips verification entirely, and
        // "HS256" invites it to treat the public key as a shared secret. The
        // algorithm is pinned before the signature is even examined.
        foreach (string algorithm in new[] { "none", "HS256", "RS256", "ED25519" })
        {
            Assert.Null(verifier.Verify(tokens.Mint(AccountId, algorithm: algorithm)));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-token")]
    [InlineData("only.two")]
    [InlineData("a.b.c.d")]
    [InlineData("!!!.!!!.!!!")]
    public void RejectsMalformedInput(string token)
    {
        var (verifier, _) = NewVerifier();
        Assert.Null(verifier.Verify(token));
    }

    [Fact]
    public void RefusesToStartWithoutAKeyOrAnAudience()
    {
        // A verifier that started without these would accept nothing at best,
        // and everything at worst. Failing to construct is the honest outcome.
        Assert.Null(PlayforwardTokenVerifier.Create(new PlayforwardSettings { Enabled = true }, PlayforwardTokenFactory.Audience));
        Assert.Null(PlayforwardTokenVerifier.Create(new PlayforwardSettings
        {
            Enabled = true,
            PublicKeyBase64 = new PlayforwardTokenFactory().PublicKeyBase64Url,
        }, audience: ""));
    }

    [Fact]
    public void IsOffUnlessEnabled()
    {
        Assert.Null(PlayforwardTokenVerifier.Create(new PlayforwardSettings { Enabled = false }, PlayforwardTokenFactory.Audience));
    }

    [Fact]
    public void RejectsAKeyOfTheWrongLength()
    {
        Assert.Null(PlayforwardTokenVerifier.Create(new PlayforwardSettings
        {
            Enabled = true,
            PublicKeyBase64 = PlayforwardTokenFactory.Base64UrlEncode(new byte[16]),
            Issuer = PlayforwardTokenFactory.Issuer,
        }, PlayforwardTokenFactory.Audience));
    }
}
