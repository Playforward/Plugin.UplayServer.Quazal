using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Plugin.UplayServer.Quazal.Playforward;

namespace Plugin.UplayServer.Quazal.Tests;

/// <summary>
/// Verifying by key id from Playforward's JWKS. A game server used to verify
/// with one key pinned in its config, so rotating Playforward's signing key
/// stopped every game server until each one was reconfigured by hand.
/// </summary>
public sealed class JwksSigningKeysTests
{
    private static readonly Uri Jwks = new("https://playforward.test/.well-known/jwks.json");
    private const string AccountId = "9d4b2e1a-0000-4000-8000-000000000001";

    private readonly PlayforwardTokenFactory _current = new();
    private readonly PlayforwardTokenFactory _next = new();
    private readonly FakeTimeProvider _time = new();
    private readonly FakeJwks _playforward = new();

    private PlayforwardTokenVerifier NewVerifier() =>
        PlayforwardTokenVerifier.Create(new PlayforwardSettings
        {
            Enabled = true,
            Issuer = PlayforwardTokenFactory.Issuer,
            BaseUrl = "https://playforward.test",
        }, PlayforwardTokenFactory.Audience, new JwksSigningKeys(new HttpClient(_playforward), Jwks, _time))!;

    /// <summary>The JWKS Playforward publishes while it signs with these keys.</summary>
    private static string Publishing(params PlayforwardTokenFactory[] keys) =>
        "{\"keys\":[" + string.Join(",", keys.Select(key => key.Jwks()[9..^2])) + "]}";

    [Fact]
    public void AcceptsATokenSignedWithAPublishedKey()
    {
        _playforward.Document = _current.Jwks();

        Assert.NotNull(NewVerifier().Verify(_current.Mint(AccountId)));
    }

    [Fact]
    public void ARotationIsPickedUpTheFirstTimeATokenNamesTheNewKey()
    {
        _playforward.Document = _current.Jwks();
        PlayforwardTokenVerifier verifier = NewVerifier();
        Assert.NotNull(verifier.Verify(_current.Mint(AccountId)));

        // Playforward rotates: it publishes both, and signs with the new one.
        _playforward.Document = Publishing(_current, _next);
        Assert.NotNull(verifier.Verify(_next.Mint(AccountId)));
        Assert.Equal(2, _playforward.Requests);
    }

    [Fact]
    public void AKeyPlayforwardStopsPublishingStopsVerifyingAfterTheNextRefresh()
    {
        _playforward.Document = Publishing(_current, _next);
        PlayforwardTokenVerifier verifier = NewVerifier();
        Assert.NotNull(verifier.Verify(_current.Mint(AccountId)));

        // The old key is retired -- or was stolen, and pulled.
        _playforward.Document = _next.Jwks();
        _time.Advance(TimeSpan.FromHours(1));

        Assert.Null(verifier.Verify(_current.Mint(AccountId)));
        Assert.NotNull(verifier.Verify(_next.Mint(AccountId)));
    }

    [Fact]
    public void TokensNamingInventedKeysCannotMakeItFetchOncePerToken()
    {
        _playforward.Document = _current.Jwks();
        PlayforwardTokenVerifier verifier = NewVerifier();
        Assert.NotNull(verifier.Verify(_current.Mint(AccountId)));

        for (int i = 0; i < 100; i++)
            Assert.Null(verifier.Verify(_current.Mint(AccountId, keyId: "invented-" + i)));

        // The first unknown id may fetch; the rest wait out the gap.
        Assert.Equal(2, _playforward.Requests);
        _time.Advance(TimeSpan.FromSeconds(31));
        Assert.Null(verifier.Verify(_current.Mint(AccountId, keyId: "invented-again")));
        Assert.Equal(3, _playforward.Requests);
    }

    [Fact]
    public void AFailedRefreshKeepsTheKeysItHeld()
    {
        // Playforward briefly unreachable must not refuse every login.
        _playforward.Document = _current.Jwks();
        PlayforwardTokenVerifier verifier = NewVerifier();
        Assert.NotNull(verifier.Verify(_current.Mint(AccountId)));

        _playforward.Failing = true;
        _time.Advance(TimeSpan.FromHours(1));

        Assert.NotNull(verifier.Verify(_current.Mint(AccountId)));
    }

    [Fact]
    public void ATokenNamingNoKeyIdIsRefused()
    {
        _playforward.Document = _current.Jwks();

        Assert.Null(NewVerifier().Verify(_current.Mint(AccountId, keyId: "")));
    }

    [Fact]
    public void ReadsOnlyEd25519SigningKeys()
    {
        string document = """
            {"keys":[
              {"kty":"RSA","kid":"rsa","n":"x","e":"AQAB"},
              {"kty":"OKP","crv":"X25519","kid":"exchange","x":"AAAA"},
              {"kty":"OKP","crv":"Ed25519","kid":"for-encryption","use":"enc","x":"AAAA"},
              {"kty":"OKP","crv":"Ed25519","x":"AAAA"},
              {"kty":"OKP","crv":"Ed25519","kid":"short","x":"AAAA"}
            ]}
            """;
        Assert.Empty(JwksSigningKeys.Parse(document));

        Assert.Equal([_current.KeyId], JwksSigningKeys.Parse(_current.Jwks()).Keys);
    }

    [Theory]
    [InlineData("http://playforward.test", false)]
    [InlineData("http://192.168.1.179:8080", false)]
    [InlineData("http://127.0.0.1:1", true)]
    [InlineData("https://playforward.test", true)]
    public void FetchesTheJwksOnlyOverTlsOrFromThisMachine(string baseUrl, bool allowed)
    {
        // A key slipped into the JWKS on the network would sign in as anybody.
        var settings = new PlayforwardSettings { Enabled = true, BaseUrl = baseUrl, RequestTimeoutSeconds = 1 };

        Assert.Equal(allowed, JwksSigningKeys.Create(settings, _time) is not null);
    }

    [Fact]
    public void APinnedKeyIsUsedWhateverKeyIdATokenNames()
    {
        // The LAN case: one key from configuration is all the server trusts.
        var pinned = new PinnedSigningKey(_current.PublicKey);

        Assert.Same(pinned.ForKeyId("anything"), pinned.ForKeyId(null));
        Assert.Equal([_current.KeyId], pinned.KeyIds);
    }

    /// <summary>Playforward's JWKS endpoint, serving whatever the test sets.</summary>
    private sealed class FakeJwks : HttpMessageHandler
    {
        public string Document { get; set; } = """{"keys":[]}""";
        public bool Failing { get; set; }
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.Equal(Jwks, request.RequestUri);
            return Task.FromResult(Failing
                ? new HttpResponseMessage(HttpStatusCode.BadGateway)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Document, Encoding.UTF8, "application/json") });
        }
    }
}
