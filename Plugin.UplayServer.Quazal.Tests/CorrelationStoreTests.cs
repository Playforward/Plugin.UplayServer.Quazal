using Plugin.UplayServer.Quazal.Playforward;

namespace Plugin.UplayServer.Quazal.Tests;

public class CorrelationStoreTests
{
    private static PlayforwardIdentity NewIdentity(string? tokenId = null) => new(
        AccountId: "9d4b2e1a-0000-4000-8000-000000000001",
        DisplayName: "Far Cry Player",
        LegacyName: "far-cry-player",
        RetroName: "farcryp",
        Scope: "user",
        PartyRef: "",
        TokenId: tokenId ?? Guid.NewGuid().ToString(),
        ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(1));

    private static CorrelationStore NewStore(int ttlSeconds = 120) =>
        new(TimeSpan.FromSeconds(ttlSeconds), new RedeemedTokens());

    [Fact]
    public void RoundTripsAnIdentityThroughACode()
    {
        var store = NewStore();
        var identity = NewIdentity();

        string? code = store.Register(identity);
        Assert.NotNull(code);

        PlayforwardIdentity? resolved = store.Redeem(code);
        Assert.NotNull(resolved);
        Assert.Equal(identity.AccountId, resolved.AccountId);
        Assert.Equal(identity.DisplayName, resolved.DisplayName);
    }

    [Fact]
    public void ACodeWorksExactlyOnce()
    {
        var store = NewStore();
        string code = store.Register(NewIdentity())!;

        Assert.NotNull(store.Redeem(code));

        // The code crosses a game process and a loopback hop to get here. If a
        // second presentation logged somebody in, a captured code would be
        // worth as much as the token it stands for.
        Assert.Null(store.Redeem(code));
    }

    [Fact]
    public void ATokenCanOnlyBeExchangedOnce()
    {
        var store = NewStore();
        string tokenId = Guid.NewGuid().ToString();

        Assert.NotNull(store.Register(NewIdentity(tokenId)));

        // Playforward puts a jti on these precisely so a server that cannot
        // check session state can still refuse the same token twice. Without
        // this, a token captured in flight mints a second code and logs in
        // beside the real player.
        Assert.Null(store.Register(NewIdentity(tokenId)));
    }

    [Fact]
    public void DistinctTokensGetDistinctCodes()
    {
        var store = NewStore();
        var codes = Enumerable.Range(0, 200).Select(_ => store.Register(NewIdentity())).ToList();

        Assert.DoesNotContain(null, codes);
        Assert.Equal(codes.Count, codes.Distinct().Count());
    }

    [Fact]
    public void RefusesAnExpiredCode()
    {
        var store = NewStore(ttlSeconds: -1);
        string code = store.Register(NewIdentity())!;

        Assert.Null(store.Redeem(code));
    }

    [Fact]
    public void RefusesUnknownCodes()
    {
        var store = NewStore();
        Assert.Null(store.Redeem("00000000000000000000000000000000"));
        Assert.Null(store.Redeem(""));
        Assert.Null(store.Redeem("   "));
    }

    [Fact]
    public void RecognisesItsOwnCodeShape()
    {
        var store = NewStore();
        string code = store.Register(NewIdentity())!;

        // The login path uses this to decide which identity scheme a username
        // belongs to before committing to either, so it must not match the
        // ordinary configured usernames a LAN server uses.
        Assert.True(CorrelationStore.LooksLikeCode(code));

        Assert.False(CorrelationStore.LooksLikeCode(null));
        Assert.False(CorrelationStore.LooksLikeCode(""));
        Assert.False(CorrelationStore.LooksLikeCode("player"));
        Assert.False(CorrelationStore.LooksLikeCode("Tracking"));
        Assert.False(CorrelationStore.LooksLikeCode(code[..^1]), "too short");
        Assert.False(CorrelationStore.LooksLikeCode(code + "a"), "too long");
        Assert.False(CorrelationStore.LooksLikeCode(code.ToUpperInvariant()), "codes are lowercase hex");
    }
}
