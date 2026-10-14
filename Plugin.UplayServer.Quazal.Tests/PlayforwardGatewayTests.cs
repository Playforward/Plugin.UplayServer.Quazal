using Microsoft.Extensions.Time.Testing;
using Plugin.UplayServer.Quazal.Playforward;

namespace Plugin.UplayServer.Quazal.Tests;

/// <summary>
/// The whole server-side path: a token arrives, a code goes out, the game comes
/// back with that code, and a player with a stable PID logs in.
/// </summary>
[Collection("OnlineDataStore")]
public sealed class PlayforwardGatewayTests : IDisposable
{
    private const string AccountId = "9d4b2e1a-0000-4000-8000-000000000001";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "pf-gateway-" + Guid.NewGuid().ToString("N"));

    private readonly PlayforwardTokenFactory _tokens = new();

    /// <summary>The Playforward this test's servers share, standing in for the real one.</summary>
    private readonly InMemoryProgressStore _playforward = new();

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private PlayforwardGateway NewGateway(Action<PlayforwardSettings>? configure = null)
    {
        PlayforwardSettings settings = Settings();
        configure?.Invoke(settings);

        PlayforwardGateway? gateway = Start(settings, _playforward);
        Assert.NotNull(gateway);
        return gateway;
    }

    /// <summary>The gateway for the test title, as the plugin would start it.</summary>
    private static PlayforwardGateway? Start(PlayforwardSettings settings, IProgressStore store) =>
        PlayforwardGateway.Start(settings, TestTitle.Id, PlayforwardTokenFactory.Audience,
            TestTitle.Quazal.Settings.Playforward.LoginNameMaxLength, store);

    private PlayforwardSettings Settings() => new()
    {
        Enabled = true,
        PublicKeyBase64 = _tokens.PublicKeyBase64Url,
        Issuer = PlayforwardTokenFactory.Issuer,
        BaseUrl = "http://playforward.test",
    };

    [Fact]
    public void TakesATokenAndProducesAPlayer()
    {
        using PlayforwardGateway gateway = NewGateway();

        string? code = gateway.PreRegister(_tokens.Mint(AccountId, "Far Cry Player"));
        Assert.NotNull(code);

        // What the game sends as its username is this code, and this is the
        // moment it becomes a player: verified identity, and the PID this
        // server will know them by from now on.
        ResolvedPlayer? player = gateway.Resolve(code);
        Assert.NotNull(player);
        Assert.Equal(AccountId, player.Identity.AccountId);
        Assert.Equal("Far Cry Player", player.Identity.DisplayName);
        Assert.True(player.Pid > 0);
    }

    [Fact]
    public void TheSameAccountComesBackToTheSamePid()
    {
        using PlayforwardGateway gateway = NewGateway();

        uint first = gateway.Resolve(gateway.PreRegister(_tokens.Mint(AccountId))!)!.Pid;
        uint second = gateway.Resolve(gateway.PreRegister(_tokens.Mint(AccountId))!)!.Pid;

        // Their online data hangs off this. A player who came back with a
        // different PID would come back as a stranger.
        Assert.Equal(first, second);
    }

    [Fact]
    public void DifferentAccountsGetDifferentPids()
    {
        using PlayforwardGateway gateway = NewGateway();

        uint one = gateway.Resolve(gateway.PreRegister(_tokens.Mint("account-one"))!)!.Pid;
        uint two = gateway.Resolve(gateway.PreRegister(_tokens.Mint("account-two"))!)!.Pid;

        Assert.NotEqual(one, two);
    }

    [Fact]
    public void RefusesATokenThatWasNotMintedForThisTitle()
    {
        using PlayforwardGateway gateway = NewGateway();

        Assert.Null(gateway.PreRegister(_tokens.Mint(AccountId, audience: "playforward-game-servers:someothertitle")));
    }

    [Fact]
    public void RefusesToPreRegisterTheSameTokenTwice()
    {
        using PlayforwardGateway gateway = NewGateway();
        string token = _tokens.Mint(AccountId);

        Assert.NotNull(gateway.PreRegister(token));

        // A token captured in flight must not mint a second code and log in
        // beside the real player. This is what the jti is for.
        Assert.Null(gateway.PreRegister(token));
    }

    [Fact]
    public void ACodeCannotBeUsedTwice()
    {
        using PlayforwardGateway gateway = NewGateway();
        string code = gateway.PreRegister(_tokens.Mint(AccountId))!;

        Assert.NotNull(gateway.Resolve(code));
        Assert.Null(gateway.Resolve(code));
    }

    [Fact]
    public void RefusesToStartOnNonsensicalConfiguration()
    {
        // Code expiry itself is covered in CorrelationStoreTests, where the
        // clock can be pushed around. What belongs here is that the gateway
        // will not start at all with settings that could never work: a server
        // that came up and then refused every login is far worse to diagnose
        // than one that says why it did not come up.
        PlayforwardSettings noTtl = Settings();
        noTtl.CorrelationCodeTtlSeconds = 0;
        Assert.Null(Start(noTtl, _playforward));

        // And, now that a player's PID and progress live in Playforward, a
        // server with no way to reach it cannot log anybody in either.
        PlayforwardSettings noBaseUrl = Settings();
        noBaseUrl.BaseUrl = "";
        Assert.Null(Start(noBaseUrl, _playforward));

        // The per-title half is validated by the title: no template account
        // to inherit credentials from, no credential of the title's own.
        Assert.Contains(new PlayforwardTitleSettings { ServerSecret = "s", SessionCapacity = 14, TemplateAccountUsername = "" }.Validate("t"),
            p => p.Contains("TemplateAccountUsername"));
        Assert.Contains(new PlayforwardTitleSettings { ServerSecret = "", SessionCapacity = 14 }.Validate("t"),
            p => p.Contains("ServerSecret"));
    }

    [Fact]
    public void ALegacyConfigMovesItsPerTitleValuesOntoTheTitleItsAudienceNames()
    {
        // A config from before per-title settings carried one audience, one
        // secret and one lobby size at the top level. They belong to the
        // title the audience names, and nothing is guessed when it names none.
        var titles = new Dictionary<string, QuazalTitleSettings>(StringComparer.OrdinalIgnoreCase)
        {
            [TestTitle.Id] = new() { Playforward = new() { SessionCapacity = 14 } },
            ["splinter-cell-blacklist"] = new() { Playforward = new() { SessionCapacity = 8 } },
        };
        var settings = new PlayforwardSettings
        {
            Audience = PlayforwardTokenFactory.Audience,
            ServerSecret = "old-secret",
            ServerLabel = "Old label",
            SessionCapacity = 12,
            LoginNameMaxLength = 10,
            TemplateAccountUsername = "tpl",
        };

        Assert.True(settings.MigrateLegacyTitleValues(titles, out var notes));

        PlayforwardTitleSettings moved = titles[TestTitle.Id].Playforward;
        Assert.Equal("old-secret", moved.ServerSecret);
        Assert.Equal("Old label", moved.ServerLabel);
        Assert.Equal(12, moved.SessionCapacity);
        Assert.Equal(10, moved.LoginNameMaxLength);
        Assert.Equal("tpl", moved.TemplateAccountUsername);
        Assert.Equal(string.Empty, titles["splinter-cell-blacklist"].Playforward.ServerSecret);
        Assert.Null(settings.Audience);
        Assert.Null(settings.ServerSecret);
        Assert.Contains(notes, n => n.Contains(TestTitle.Id));

        // Nothing legacy left: a second pass is a no-op.
        Assert.False(settings.MigrateLegacyTitleValues(titles, out _));

        // An audience naming no configured title is reported, not guessed at.
        var stray = new PlayforwardSettings { Audience = "playforward-game-servers:nowhere", ServerSecret = "x" };
        Assert.True(stray.MigrateLegacyTitleValues(titles, out notes));
        Assert.Contains(notes, n => n.Contains("no matching entry"));
        Assert.Equal("old-secret", titles[TestTitle.Id].Playforward.ServerSecret);
    }

    [Fact]
    public void RefusesAnInventedCode()
    {
        using PlayforwardGateway gateway = NewGateway();
        Assert.Null(gateway.Resolve("ffffffffffffffffffffffffffffffff"));
    }

    [Fact]
    public void ReLoginFromTheSameAddressReusesTheBindingAfterTheCodeIsConsumed()
    {
        using PlayforwardGateway gateway = NewGateway();
        var address = System.Net.IPAddress.Parse("203.0.113.7");

        string code = gateway.PreRegister(_tokens.Mint(AccountId))!;

        // First login redeems the code and binds it to the address.
        ResolvedPlayer? first = gateway.ResolveForLogin(code, address);
        Assert.NotNull(first);
        Assert.Equal(AccountId, first.Identity.AccountId);

        // The game re-sends the same (now consumed) code on a later login.
        // Without the address binding this would be refused and the session
        // would drop; with it, the player stays who they were.
        ResolvedPlayer? again = gateway.ResolveForLogin(code, address);
        Assert.NotNull(again);
        Assert.Equal(AccountId, again.Identity.AccountId);
        Assert.Equal(first.Pid, again.Pid);
    }

    [Fact]
    public void AConsumedCodeFromADifferentAddressIsStillRefused()
    {
        using PlayforwardGateway gateway = NewGateway();
        string code = gateway.PreRegister(_tokens.Mint(AccountId))!;

        Assert.NotNull(gateway.ResolveForLogin(code, System.Net.IPAddress.Parse("203.0.113.7")));

        // The binding is to the address that first redeemed it. A captured code
        // replayed from somewhere else gets nothing -- it is worth only the one
        // initial resolve, from the machine that legitimately had it.
        Assert.Null(gateway.ResolveForLogin(code, System.Net.IPAddress.Parse("198.51.100.9")));
    }

    [Fact]
    public void AFreshCodeFromTheSameAddressRebindsToTheNewIdentity()
    {
        using PlayforwardGateway gateway = NewGateway();
        var address = System.Net.IPAddress.Parse("203.0.113.7");

        string firstCode = gateway.PreRegister(_tokens.Mint("account-one"))!;
        Assert.Equal("account-one", gateway.ResolveForLogin(firstCode, address)!.Identity.AccountId);

        // A different account signs in from the same machine. The new code
        // redeems and replaces the binding, rather than being shadowed by the
        // old one.
        string secondCode = gateway.PreRegister(_tokens.Mint("account-two"))!;
        Assert.Equal("account-two", gateway.ResolveForLogin(secondCode, address)!.Identity.AccountId);
    }

    [Fact]
    public void AMadeUpCodeFromAnAddressWithAPlayerIsRefused()
    {
        using PlayforwardGateway gateway = NewGateway();
        var sharedAddress = System.Net.IPAddress.Parse("100.64.12.34");

        string code = gateway.PreRegister(_tokens.Mint(AccountId))!;
        Assert.NotNull(gateway.ResolveForLogin(code, sharedAddress));

        // Somebody else behind the same carrier NAT sends a code-shaped name
        // they made up. It used to fall back to whoever was bound to the
        // address and log them in as the victim.
        Assert.Null(gateway.ResolveForLogin("ffffffffffffffffffffffffffffffff", sharedAddress));

        // Nor does a made-up code reach a LAN pre-bind: that binding answers
        // a login by name, not by code.
        var lan = System.Net.IPAddress.Parse("192.168.1.50");
        Assert.NotNull(gateway.PreBind(_tokens.Mint("account-lan"), lan));
        Assert.Null(gateway.ResolveForLogin("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", lan));
    }

    [Fact]
    public void AFreshCodeReleasesTheCodeBeforeIt()
    {
        using PlayforwardGateway gateway = NewGateway();
        var address = System.Net.IPAddress.Parse("203.0.113.8");

        string firstCode = gateway.PreRegister(_tokens.Mint("account-one"))!;
        uint firstPid = gateway.ResolveForLogin(firstCode, address)!.Pid;
        string secondCode = gateway.PreRegister(_tokens.Mint("account-two"))!;
        Assert.NotNull(gateway.ResolveForLogin(secondCode, address));

        // The machine signed in as someone else; the first player is no longer
        // on this server and their code no longer logs anybody in.
        Assert.Null(gateway.ForPid(firstPid));
        Assert.Null(gateway.ResolveForLogin(firstCode, address));
    }

    [Fact]
    public void APreBindFromASharedPublicAddressLogsNobodyIn()
    {
        using PlayforwardGateway gateway = NewGateway();

        // Carrier-grade NAT and an ordinary public address: thousands of
        // strangers can be behind either.
        foreach (string shared in new[] { "100.64.12.34", "203.0.113.60", "::ffff:203.0.113.61", "2001:db8::1" })
        {
            var address = System.Net.IPAddress.Parse(shared);
            ResolvedPlayer? bound = gateway.PreBind(_tokens.Mint(AccountId), address);

            // The client still gets its answer, and the player still exists...
            Assert.NotNull(bound);

            // ...but a game from that address is nobody until it proves a launch.
            Assert.Null(gateway.ForEndpoint(new System.Net.IPEndPoint(address, 3074)));
            Assert.Null(gateway.ForPid(bound.Pid));
        }
        Assert.Empty(gateway.BoundAddresses());
    }

    [Fact]
    public void AddressLoginFollowsTheConfiguredNetworks()
    {
        var lan = System.Net.IPAddress.Parse("192.168.1.70");
        var ipv4OnDualStack = System.Net.IPAddress.Parse("::ffff:192.168.1.71");

        using (PlayforwardGateway defaults = NewGateway())
        {
            Assert.NotNull(defaults.PreBind(_tokens.Mint(AccountId), lan));
            Assert.NotNull(defaults.ForAddress(lan));
            Assert.NotNull(defaults.PreBind(_tokens.Mint(AccountId), ipv4OnDualStack));
            Assert.NotNull(defaults.ForAddress(ipv4OnDualStack));
        }

        // An operator whose LAN is a dorm turns it off entirely.
        using PlayforwardGateway none = NewGateway(settings => settings.LanNetworks = []);
        Assert.NotNull(none.PreBind(_tokens.Mint(AccountId), lan));
        Assert.Null(none.ForAddress(lan));

        // And a network that is not one refuses to start rather than guessing.
        PlayforwardSettings typo = Settings();
        typo.LanNetworks = ["192.168.1.0/33"];
        Assert.Null(Start(typo, _playforward));
    }

    [Fact]
    public void PlainHttpIsAnsweredOnlyFromTheLan()
    {
        // The routes carry a game token in and a code or launch key out, and
        // they merge onto port 80 too. Across the internet in cleartext,
        // anyone on the path could take either.
        using PlayforwardGateway gateway = NewGateway();
        var lan = System.Net.IPAddress.Parse("192.168.1.179");
        var internet = System.Net.IPAddress.Parse("203.0.113.80");
        var carrierNat = System.Net.IPAddress.Parse("100.64.1.2");

        Assert.True(PlayforwardRoutes.MayServe(overTls: false, lan, gateway));
        Assert.True(PlayforwardRoutes.MayServe(overTls: false, System.Net.IPAddress.Loopback, gateway));
        Assert.False(PlayforwardRoutes.MayServe(overTls: false, internet, gateway));
        Assert.False(PlayforwardRoutes.MayServe(overTls: false, carrierNat, gateway));
        Assert.False(PlayforwardRoutes.MayServe(overTls: false, caller: null, gateway));

        // TLS is fine from anywhere.
        Assert.True(PlayforwardRoutes.MayServe(overTls: true, internet, gateway));

        // With no LAN configured, plain HTTP answers nobody.
        using PlayforwardGateway none = NewGateway(settings => settings.LanNetworks = []);
        Assert.False(PlayforwardRoutes.MayServe(overTls: false, lan, none));
    }

    [Fact]
    public void PreBindLetsAnyUsernameFromThatAddressResolveToTheAccount()
    {
        using PlayforwardGateway gateway = NewGateway();
        var address = System.Net.IPAddress.Parse("192.168.1.20");

        // The desktop client vouches for this machine ahead of the game
        // connecting -- the game itself is never touched, only redirected.
        ResolvedPlayer? bound = gateway.PreBind(_tokens.Mint(AccountId), address);
        Assert.NotNull(bound);
        Assert.Equal(AccountId, bound.Identity.AccountId);

        // The unmodified game then logs in with whatever username it sends, and
        // is recognised by its address alone.
        ResolvedPlayer? seen = gateway.ForAddress(address);
        Assert.NotNull(seen);
        Assert.Equal(bound.Pid, seen.Pid);
    }

    [Fact]
    public void PreBindKeepsThePidStableForTheAccount()
    {
        using PlayforwardGateway gateway = NewGateway();
        uint viaCode = gateway.Resolve(gateway.PreRegister(_tokens.Mint(AccountId))!)!.Pid;

        // However the account is established -- a code login or a pre-bind --
        // the PID it lands on is the same, because the directory keys it by
        // account, not by how it arrived.
        uint viaBind = gateway.PreBind(_tokens.Mint(AccountId), System.Net.IPAddress.Parse("203.0.113.21"))!.Pid;
        Assert.Equal(viaCode, viaBind);
    }

    [Fact]
    public void PreBindRejectsABadToken()
    {
        using PlayforwardGateway gateway = NewGateway();
        var impostor = new PlayforwardTokenFactory();
        Assert.Null(gateway.PreBind(impostor.Mint(AccountId), System.Net.IPAddress.Parse("203.0.113.22")));
    }

    [Fact]
    public void ForPidRecognisesABoundPlayersPid()
    {
        using PlayforwardGateway gateway = NewGateway();
        var address = System.Net.IPAddress.Parse("192.168.1.30");

        ResolvedPlayer bound = gateway.PreBind(_tokens.Mint(AccountId), address)!;

        // The secure CONNECT that follows LoginEx identifies the peer by the PID
        // in its ticket and refuses an unknown one. A Playforward PID is not in
        // the configured accounts, so ForPid is what keeps the session alive
        // past login.
        ResolvedPlayer? byPid = gateway.ForPid(bound.Pid);
        Assert.NotNull(byPid);
        Assert.Equal(AccountId, byPid.Identity.AccountId);

        Assert.Null(gateway.ForPid(999999)); // an unknown pid is not a Playforward player
    }

    [Fact]
    public void ForAddressReturnsNothingForAnUnboundAddress()
    {
        using PlayforwardGateway gateway = NewGateway();
        Assert.Null(gateway.ForAddress(System.Net.IPAddress.Parse("203.0.113.99")));
    }

    // -----------------------------------------------------------------
    // Launches: the shim proves who it is on the game's own socket
    // -----------------------------------------------------------------

    private static readonly System.Net.IPEndPoint GameSocket = new(System.Net.IPAddress.Parse("203.0.113.40"), 51000);

    /// <summary>
    /// Plays the shim's half of the exchange against the gateway, from
    /// <paramref name="socket"/>, and returns whether the server said BOUND.
    /// </summary>
    private static bool ProveLaunch(PlayforwardGateway gateway, IssuedLaunch launch, System.Net.IPEndPoint socket)
    {
        byte[]? challengeDatagram = gateway.HandleLaunchDatagram(LaunchEnvelope.Hello(launch.IdBytes), socket, out bool recognised);
        Assert.True(recognised);
        if (challengeDatagram is null)
            return false;
        Assert.True(LaunchEnvelope.TryReadReply(challengeDatagram, LaunchEnvelope.Kind.Challenge, launch.IdBytes, out byte[] challenge));

        byte[]? bound = gateway.HandleLaunchDatagram(LaunchEnvelope.Proof(launch.Key, launch.IdBytes, challenge), socket, out recognised);
        Assert.True(recognised);
        if (bound is null)
            return false;

        // The shim checks the go-ahead with its key before it lets the game log in.
        Assert.True(LaunchEnvelope.TryReadReply(bound, LaunchEnvelope.Kind.Bound, launch.IdBytes, out byte[] mac));
        Assert.True(LaunchEnvelope.TryReadReply(LaunchEnvelope.Bound(launch.Key, launch.IdBytes, challenge), LaunchEnvelope.Kind.Bound, launch.IdBytes, out byte[] expected));
        Assert.Equal(expected, mac);
        return true;
    }

    [Fact]
    public void AProvedLaunchBindsTheSocketItWasProvedFrom()
    {
        using PlayforwardGateway gateway = NewGateway();

        IssuedLaunch? launch = gateway.PreLaunch(_tokens.Mint(AccountId, "Far Cry Player"));
        Assert.NotNull(launch);
        Assert.Equal(AccountId, launch.Player.Identity.AccountId);

        // Nothing is bound yet: issuing is not binding.
        Assert.Null(gateway.ForEndpoint(GameSocket));

        Assert.True(ProveLaunch(gateway, launch, GameSocket));

        // LoginEx from that socket is this player, with the same PID the
        // launch was issued for.
        ResolvedPlayer? seen = gateway.ForEndpoint(GameSocket);
        Assert.NotNull(seen);
        Assert.Equal(launch.Player.Pid, seen.Pid);
        Assert.Equal(AccountId, seen.Identity.AccountId);
        Assert.NotNull(gateway.ForPid(seen.Pid));

        // But the address alone is not: another game from this address is not
        // automatically this player, which is the whole point.
        Assert.Null(gateway.ForAddress(GameSocket.Address));
    }

    [Fact]
    public void TwoPlayersBehindOneAddressAreTwoBindings()
    {
        using PlayforwardGateway gateway = NewGateway();
        var first = new System.Net.IPEndPoint(GameSocket.Address, 51000);
        var second = new System.Net.IPEndPoint(GameSocket.Address, 51001);

        IssuedLaunch one = gateway.PreLaunch(_tokens.Mint("account-one"))!;
        IssuedLaunch two = gateway.PreLaunch(_tokens.Mint("account-two"))!;
        Assert.True(ProveLaunch(gateway, one, first));
        Assert.True(ProveLaunch(gateway, two, second));

        // The LAN-meetup case: one public address, and each game is still
        // its own player.
        Assert.Equal("account-one", gateway.ForEndpoint(first)!.Identity.AccountId);
        Assert.Equal("account-two", gateway.ForEndpoint(second)!.Identity.AccountId);
        Assert.NotEqual(gateway.ForEndpoint(first)!.Pid, gateway.ForEndpoint(second)!.Pid);
    }

    [Fact]
    public void ASocketBindingWinsOverTheAddressBinding()
    {
        using PlayforwardGateway gateway = NewGateway();

        // One machine on a LAN pre-bound by address the old way, another
        // behind the same address proved a launch. The proof is the more
        // specific claim.
        var lanSocket = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("192.168.1.40"), 51000);
        gateway.PreBind(_tokens.Mint("account-by-address"), lanSocket.Address);
        IssuedLaunch launch = gateway.PreLaunch(_tokens.Mint("account-by-socket"))!;
        Assert.True(ProveLaunch(gateway, launch, lanSocket));

        Assert.Equal("account-by-socket", gateway.ForEndpoint(lanSocket)!.Identity.AccountId);

        // A different socket behind that address still falls back to the address binding.
        var other = new System.Net.IPEndPoint(lanSocket.Address, 51009);
        Assert.Equal("account-by-address", gateway.ForEndpoint(other)!.Identity.AccountId);

        // ForSocket names only the proof, so a log can tell the two apart.
        Assert.Equal("account-by-socket", gateway.ForSocket(lanSocket)!.Identity.AccountId);
        Assert.Null(gateway.ForSocket(other));
    }

    [Fact]
    public void AForgedProofBindsNothing()
    {
        using PlayforwardGateway gateway = NewGateway();
        IssuedLaunch launch = gateway.PreLaunch(_tokens.Mint(AccountId))!;

        // The launch id was read off the wire; the key was not.
        var forged = launch with { Key = new byte[launch.Key.Length] };
        Assert.False(ProveLaunch(gateway, forged, GameSocket));
        Assert.Null(gateway.ForEndpoint(GameSocket));
    }

    [Fact]
    public void ADatagramThatIsNotTheShimsIsLeftForPrudp()
    {
        using PlayforwardGateway gateway = NewGateway();
        Assert.Null(gateway.HandleLaunchDatagram([0xAF, 0xA1, 0x60, 0, 0, 0, 0, 0, 0, 0, 0, 0], GameSocket, out bool recognised));
        Assert.False(recognised);
    }

    [Fact]
    public void PreLaunchRejectsABadToken()
    {
        using PlayforwardGateway gateway = NewGateway();
        var impostor = new PlayforwardTokenFactory();
        Assert.Null(gateway.PreLaunch(impostor.Mint(AccountId)));
    }

    [Fact]
    public void ATokenIsGoodForOneUseAcrossEveryEndpoint()
    {
        using PlayforwardGateway gateway = NewGateway();

        // One token, spent at pre-launch, is refused at pre-bind and
        // pre-register -- a capture must not be a second login by another door.
        string token = _tokens.Mint(AccountId);
        Assert.NotNull(gateway.PreLaunch(token));
        Assert.Null(gateway.PreBind(token, GameSocket.Address));
        Assert.Null(gateway.PreRegister(token));

        // And pre-bind, which used to accept a token twice, no longer does.
        string another = _tokens.Mint(AccountId);
        Assert.NotNull(gateway.PreBind(another, GameSocket.Address));
        Assert.Null(gateway.PreBind(another, GameSocket.Address));
    }

    [Fact]
    public void AFreshLaunchUpdatesAPlayerAlreadyIn()
    {
        using PlayforwardGateway gateway = NewGateway();

        IssuedLaunch launch = gateway.PreLaunch(_tokens.Mint(AccountId, partyRef: ""))!;
        Assert.True(ProveLaunch(gateway, launch, GameSocket));
        Assert.Equal("", gateway.ForEndpoint(GameSocket)!.Identity.PartyRef);

        // The player joins a party mid-session. The client presents a fresh
        // token; the game and its shim are none the wiser and stay bound.
        IssuedLaunch again = gateway.PreLaunch(_tokens.Mint(AccountId, partyRef: "party-7"))!;

        ResolvedPlayer? bound = gateway.ForEndpoint(GameSocket);
        Assert.NotNull(bound);
        Assert.Equal("party-7", bound.Identity.PartyRef);
        Assert.Equal(launch.Player.Pid, bound.Pid);

        // And the original key still works on a reconnect, binding the new claims.
        var reconnected = new System.Net.IPEndPoint(GameSocket.Address, 51003);
        Assert.True(ProveLaunch(gateway, launch, reconnected));
        Assert.Equal("party-7", gateway.ForEndpoint(reconnected)!.Identity.PartyRef);
        Assert.NotEqual(launch.Id, again.Id);
    }

    [Fact]
    public void APreBindWithAFreshTokenAlsoUpdatesAProvedGame()
    {
        using PlayforwardGateway gateway = NewGateway();
        IssuedLaunch launch = gateway.PreLaunch(_tokens.Mint(AccountId, partyRef: ""))!;
        Assert.True(ProveLaunch(gateway, launch, GameSocket));

        // The client's readiness loop keeps pre-binding by address, and does
        // not know this machine's game proved a launch. Its next fresh token,
        // carrying the party, must still reach the socket binding.
        gateway.PreBind(_tokens.Mint(AccountId, partyRef: "party-9"), GameSocket.Address);

        Assert.Equal("party-9", gateway.ForEndpoint(GameSocket)!.Identity.PartyRef);
        Assert.Equal(launch.Player.Pid, gateway.ForEndpoint(GameSocket)!.Pid);
    }

    [Fact]
    public void DeletingAPlayerEndsTheirLaunchesAndSocketBindings()
    {
        using PlayforwardGateway gateway = NewGateway();
        IssuedLaunch launch = gateway.PreLaunch(_tokens.Mint(AccountId))!;
        Assert.True(ProveLaunch(gateway, launch, GameSocket));

        Assert.True(gateway.DeletePlayer(AccountId));

        Assert.Null(gateway.ForEndpoint(GameSocket));
        Assert.False(ProveLaunch(gateway, launch, new System.Net.IPEndPoint(GameSocket.Address, 51004)));
    }

    // -----------------------------------------------------------------
    // Bindings and launches end when their player goes quiet
    // -----------------------------------------------------------------

    private static readonly System.Net.IPAddress LanMachine = System.Net.IPAddress.Parse("192.168.1.60");

    private PlayforwardGateway NewGateway(FakeTimeProvider time)
    {
        PlayforwardGateway? gateway = PlayforwardGateway.Start(Settings(), TestTitle.Id, PlayforwardTokenFactory.Audience,
            TestTitle.Quazal.Settings.Playforward.LoginNameMaxLength, _playforward, time);
        Assert.NotNull(gateway);
        return gateway;
    }

    private static readonly TimeSpan BindingIdle = TimeSpan.FromSeconds(new PlayforwardSettings().BindingIdleTimeoutSeconds);
    private static readonly TimeSpan LaunchIdle = TimeSpan.FromSeconds(new PlayforwardSettings().LaunchIdleTimeoutSeconds);
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    private static readonly HashSet<System.Net.IPEndPoint> NoSockets = [];
    private static readonly HashSet<uint> NoPlayers = [];

    private static HashSet<System.Net.IPEndPoint> Sockets(params System.Net.IPEndPoint[] live) => [.. live];
    private static HashSet<uint> Players(params uint[] live) => [.. live];

    [Fact]
    public void AnAddressBindingEndsWhenItsPlayerHasGone()
    {
        // The DHCP case: the machine that pre-bound this address leaves, the
        // address goes to somebody else, and they must not arrive as the
        // player who left -- which used to hold until a restart.
        var time = new FakeTimeProvider();
        using PlayforwardGateway gateway = NewGateway(time);
        gateway.PreBind(_tokens.Mint(AccountId, "Far Cry Player"), LanMachine);

        time.Advance(BindingIdle - OneSecond);
        Assert.NotNull(gateway.ForAddress(LanMachine));

        time.Advance(2 * OneSecond);
        Assert.Null(gateway.ForAddress(LanMachine));

        // And the sweep takes it out of the table, not only out of lookups.
        gateway.KeepAlive(NoSockets, NoPlayers);
        Assert.Empty(gateway.BoundAddresses());
    }

    [Fact]
    public void AnAddressBindingLastsWhileItsGameOrClientIsThere()
    {
        var time = new FakeTimeProvider();
        using PlayforwardGateway gateway = NewGateway(time);
        gateway.PreBind(_tokens.Mint(AccountId), LanMachine);

        // The game talking from that address keeps it...
        var game = new System.Net.IPEndPoint(LanMachine, 3074);
        for (int i = 0; i < 3; i++)
        {
            time.Advance(BindingIdle - OneSecond);
            gateway.KeepAlive(Sockets(game), NoPlayers);
        }
        Assert.NotNull(gateway.ForAddress(LanMachine));

        // ...and so does the desktop client pre-binding it again.
        for (int i = 0; i < 3; i++)
        {
            time.Advance(BindingIdle - OneSecond);
            gateway.PreBind(_tokens.Mint(AccountId), LanMachine);
        }
        Assert.NotNull(gateway.ForAddress(LanMachine));
    }

    [Fact]
    public void ASocketBindingLivesOnItsGamesTrafficAlone()
    {
        // The carrier-NAT case: once the game on this public socket has gone,
        // the next customer handed the socket must not arrive as its player.
        var time = new FakeTimeProvider();
        using PlayforwardGateway gateway = NewGateway(time);
        IssuedLaunch launch = gateway.PreLaunch(_tokens.Mint(AccountId))!;
        Assert.True(ProveLaunch(gateway, launch, GameSocket));

        time.Advance(BindingIdle - OneSecond);
        gateway.KeepAlive(Sockets(GameSocket), Players(launch.Player.Pid));
        time.Advance(BindingIdle - OneSecond);
        Assert.NotNull(gateway.ForEndpoint(GameSocket));

        // The desktop client keeps re-binding from wherever it is, but that
        // says nothing about this socket.
        time.Advance(2 * OneSecond);
        gateway.PreBind(_tokens.Mint(AccountId), GameSocket.Address);
        gateway.KeepAlive(NoSockets, NoPlayers);
        Assert.Null(gateway.ForEndpoint(GameSocket));
    }

    [Fact]
    public void ALaunchNobodyIsUsingStopsBeingProvable()
    {
        var time = new FakeTimeProvider();
        using PlayforwardGateway gateway = NewGateway(time);
        IssuedLaunch launch = gateway.PreLaunch(_tokens.Mint(AccountId))!;

        // It used to stay good for eight hours whatever happened.
        time.Advance(LaunchIdle + OneSecond);
        Assert.False(ProveLaunch(gateway, launch, GameSocket));
    }

    [Fact]
    public void ALaunchLastsWhileItsClientOrGameIsThereUpToItsCeiling()
    {
        var time = new FakeTimeProvider();
        using PlayforwardGateway gateway = NewGateway(time);
        IssuedLaunch launch = gateway.PreLaunch(_tokens.Mint(AccountId))!;

        // The client's readiness tick refreshes it...
        time.Advance(LaunchIdle - OneSecond);
        gateway.PreBind(_tokens.Mint(AccountId), GameSocket.Address);
        // ...and so does the player's game talking to this server, after the
        // client has closed.
        time.Advance(LaunchIdle - OneSecond);
        gateway.KeepAlive(NoSockets, Players(launch.Player.Pid));
        time.Advance(LaunchIdle - OneSecond);
        Assert.True(ProveLaunch(gateway, launch, GameSocket));

        // But never past LaunchTtlSeconds, however busy.
        time.SetUtcNow(launch.ExpiresAt + OneSecond);
        gateway.KeepAlive(NoSockets, Players(launch.Player.Pid));
        Assert.False(ProveLaunch(gateway, launch, new System.Net.IPEndPoint(GameSocket.Address, 51009)));
    }

    // -----------------------------------------------------------------
    // A long session: KEEP holds the binding, renewal outlives the ceiling
    // -----------------------------------------------------------------

    /// <summary>How often the shim sends a KEEP (docs/planned/shim-launch-over-tls.md).</summary>
    private static readonly TimeSpan KeepInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Sends one KEEP from <paramref name="socket"/>. It is never answered:
    /// once the game is running, a reply on its socket would reach the game.
    /// </summary>
    private static void Keep(PlayforwardGateway gateway, IssuedLaunch launch, System.Net.IPEndPoint socket, ulong counter)
    {
        byte[]? reply = gateway.HandleLaunchDatagram(LaunchEnvelope.Keep(launch.Key, launch.IdBytes, counter), socket, out bool recognised);
        Assert.True(recognised);
        Assert.Null(reply);
    }

    /// <summary>What the shim does at three quarters of a launch's life, over HTTPS.</summary>
    private IssuedLaunch? Renew(PlayforwardGateway gateway, IssuedLaunch old)
    {
        string tokenId = Guid.NewGuid().ToString();
        return gateway.Renew(_tokens.Mint(AccountId, tokenId: tokenId), old.IdBytes,
            LaunchEnvelope.ExpectedRenewal(old.Key, old.IdBytes, tokenId));
    }

    [Fact]
    public void AnHourOfSinglePlayerKeepsItsBindingOnKeepsAlone()
    {
        // The game sends this server nothing while the player is in the
        // campaign, so the socket binding used to end after three minutes and
        // the next online login was nobody.
        var time = new FakeTimeProvider();
        using PlayforwardGateway gateway = NewGateway(time);
        IssuedLaunch launch = gateway.PreLaunch(_tokens.Mint(AccountId))!;
        Assert.True(ProveLaunch(gateway, launch, GameSocket));

        for (ulong counter = 1; counter <= 60; counter++)
        {
            time.Advance(KeepInterval);
            gateway.KeepAlive(NoSockets, NoPlayers);
            Keep(gateway, launch, GameSocket, counter);
            Assert.NotNull(gateway.ForEndpoint(GameSocket));
        }
    }

    [Fact]
    public void AKeepRestoresABindingThatLapsed()
    {
        // A laptop asleep, a network that dropped for a few minutes: the
        // binding ended, but the launch is still good and only its game can
        // send a KEEP for it.
        var time = new FakeTimeProvider();
        using PlayforwardGateway gateway = NewGateway(time);
        IssuedLaunch launch = gateway.PreLaunch(_tokens.Mint(AccountId))!;
        Assert.True(ProveLaunch(gateway, launch, GameSocket));

        time.Advance(BindingIdle + OneSecond);
        gateway.KeepAlive(NoSockets, NoPlayers);
        Assert.Null(gateway.ForEndpoint(GameSocket));

        Keep(gateway, launch, GameSocket, counter: 1);
        Assert.Equal(AccountId, gateway.ForEndpoint(GameSocket)!.Identity.AccountId);
    }

    [Fact]
    public void ADayLongSessionStaysBoundAcrossItsRenewals()
    {
        // Eight hours is a launch's ceiling. The shim renews at three
        // quarters of it over HTTPS -- a new token, and a proof made with the
        // old key -- and keeps the new launch from then on. Nothing is
        // answered on the game's socket.
        var time = new FakeTimeProvider();
        using PlayforwardGateway gateway = NewGateway(time);
        IssuedLaunch launch = gateway.PreLaunch(_tokens.Mint(AccountId))!;
        Assert.True(ProveLaunch(gateway, launch, GameSocket));
        TimeSpan renewAfter = (launch.ExpiresAt - time.GetUtcNow()) * 3 / 4;
        DateTimeOffset renewAt = time.GetUtcNow() + renewAfter;
        TimeSpan session = TimeSpan.FromHours(24);
        DateTimeOffset end = time.GetUtcNow() + session;
        ulong counter = 0;
        int renewals = 0;

        while (time.GetUtcNow() < end)
        {
            time.Advance(KeepInterval);
            gateway.KeepAlive(NoSockets, NoPlayers);
            if (time.GetUtcNow() >= renewAt)
            {
                IssuedLaunch? renewed = Renew(gateway, launch);
                Assert.NotNull(renewed);
                // The launch it replaced is gone: one socket, one launch.
                Assert.Null(Renew(gateway, launch));
                launch = renewed;
                counter = 0;
                renewAt = time.GetUtcNow() + renewAfter;
                renewals++;
            }
            Keep(gateway, launch, GameSocket, ++counter);
            Assert.Equal(AccountId, gateway.ForEndpoint(GameSocket)!.Identity.AccountId);
        }

        // Every six hours, the last landing on the day's final minute.
        Assert.Equal((int)(session / renewAfter), renewals);
    }

    [Fact]
    public void AfterARestartAFreshLaunchsKeepBindsTheSocketAgain()
    {
        // Launches and bindings live in memory, so a restart forgets both. The
        // shim notices the boot id change over HTTPS, acquires a fresh launch,
        // and binds the game's socket with its first KEEP.
        IssuedLaunch launch;
        using (PlayforwardGateway before = NewGateway())
        {
            launch = before.PreLaunch(_tokens.Mint(AccountId))!;
            Assert.True(ProveLaunch(before, launch, GameSocket));
        }

        using PlayforwardGateway after = NewGateway();
        Keep(after, launch, GameSocket, counter: 2);
        Assert.Null(after.ForEndpoint(GameSocket));

        IssuedLaunch fresh = after.PreLaunch(_tokens.Mint(AccountId))!;
        Keep(after, fresh, GameSocket, counter: 1);
        Assert.Equal(AccountId, after.ForEndpoint(GameSocket)!.Identity.AccountId);
    }

    [Fact]
    public void IsNotRunningWhenDisabled()
    {
        // A private LAN server with no accounts service must keep working
        // exactly as it did, on local accounts alone.
        Assert.Null(Start(new PlayforwardSettings { Enabled = false }, _playforward));
    }
}
