using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Text.Json;
using Plugin.UplayServer.Quazal;
using Plugin.UplayServer.Quazal.Playforward;

namespace GameClientTestHarness;

/// <summary>
/// The whole integration in one process
/// </summary>
internal static class SelfTest
{
    public static async Task<int> RunAsync(HarnessOptions options)
    {
        var steps = new Steps();
        Console.WriteLine($"Self-test: a Playforward account logging in to '{options.Title}'.\n");

        var tokens = new HarnessTokenSource();
        string publicKey = tokens.PublicKeyBase64Url;
        string issuer = HarnessTokenSource.Issuer;
        string audience = $"playforward-game-servers:{options.Title}";
        QuazalTitle quazalTitle = QuazalTitle.ForTests(options.Title, 0, 0);
        var title = quazalTitle.OnEveryPort();
        string accountId;
        string token;

        if (options.PlayforwardUrl is { } url)
        {
            try
            {
                (publicKey, issuer) = await FetchKeyAndIssuerAsync(url);
                (accountId, token) = await MintRealTokenAsync(url, options);
                steps.Ok($"minted a real token from Playforward at {url}");
                steps.Info($"account_id={accountId}");
            }
            catch (Exception exception)
            {
                steps.Fail($"could not mint a real token: {exception.Message}");
                return steps.Finish("self-test");
            }
        }
        else
        {
            accountId = Guid.NewGuid().ToString();
            token = tokens.Mint(accountId, audience: audience, issuer: issuer);
            steps.Ok("self-signed a token (no --playforward-url given)");
            steps.Info($"account_id={accountId}");
        }

        var settings = new QuazalSettings
        {
            Accounts = { new QuazalAccount { Username = "player", Password = "server-side-password", Pid = 101 } },
            Playforward = new PlayforwardSettings
            {
                Enabled = true,
                PublicKeyBase64 = publicKey,
                Issuer = issuer,
                BaseUrl = options.PlayforwardUrl ?? "http://playforward.test",
            },
        };
        var playforwardStore = new InMemoryProgressStore();
        steps.Ok("in-memory player-data store standing in for Playforward");

        try
        {
            PlayforwardGateway? gateway = PlayforwardGateway.Start(settings.Playforward, quazalTitle.TitleId, audience,
                quazalTitle.Settings.Playforward.LoginNameMaxLength, playforwardStore);
            if (gateway is null)
            {
                steps.Fail("the Playforward gateway refused to start (see the log above)");
                return steps.Finish("self-test");
            }
            PlayforwardTitles.Register(PlayforwardTitle.ForTests(quazalTitle, audience, gateway, playforwardStore));
            steps.Ok("Playforward gateway started");

            string? code = gateway.PreRegister(token);
            if (code is null)
            {
                steps.Fail("the gateway rejected the token");
                return steps.Finish("self-test");
            }
            steps.Ok("token verified, correlation code issued");
            steps.Info($"code={code}");

            var responder = new QuazalResponder(settings, title);
            uint? pid = LocalLogin.Run(responder, code);
            if (pid is null)
            {
                steps.Fail("LoginEx with the correlation code did not succeed");
                return steps.Finish("self-test");
            }
            steps.Ok($"logged in; server assigned pid={pid}");

            uint? pidReauth = LocalLogin.Run(responder, code);
            if (pidReauth == pid)
                steps.Ok("re-login with the same (now consumed) code keeps the same pid");
            else
                steps.Fail($"re-login did not reuse the session ({pid} then {pidReauth?.ToString() ?? "refused"})");

            if (options.PlayforwardUrl is null)
            {
                gateway.PreBind(tokens.Mint(accountId, legacyName: "sharp-shooter", retroName: "sharp8", audience: audience, issuer: issuer), LocalLogin.SourceAddress);
                uint? pidLegacy = LocalLogin.Run(new QuazalResponder(settings, title), "Sharp Shooter");
                if (pidLegacy == pid)
                    steps.Ok("pre-bound login with the account's legacy-tier name is accepted (canonical match)");
                else
                    steps.Fail($"legacy-tier login did not resolve ({pid} then {pidLegacy?.ToString() ?? "refused"})");

                uint? pidImpostor = LocalLogin.Run(new QuazalResponder(settings, title), "SomeoneElse");
                if (pidImpostor is null)
                    steps.Ok("a pre-bound login claiming a different name is refused (accountability)");
                else
                    steps.Fail($"a mismatched name was NOT refused: got pid={pidImpostor}");

                QuazalTitle retroTitle = QuazalTitle.ForTests("retro-" + options.Title, 0, 0);
                retroTitle.Settings.Playforward.LoginNameMaxLength = 8;
                string retroAudience = $"playforward-game-servers:{retroTitle.TitleId}";
                PlayforwardGateway retroGateway = PlayforwardGateway.Start(settings.Playforward, retroTitle.TitleId, retroAudience,
                    retroTitle.Settings.Playforward.LoginNameMaxLength, playforwardStore)!;
                PlayforwardTitles.Register(PlayforwardTitle.ForTests(retroTitle, retroAudience, retroGateway, playforwardStore));
                retroGateway.PreBind(tokens.Mint(accountId, legacyName: "sharp-shooter", retroName: "sharp8", audience: retroAudience, issuer: issuer), LocalLogin.SourceAddress);
                uint? pidRetro = LocalLogin.Run(new QuazalResponder(settings, retroTitle.OnEveryPort()), "sharp8");
                uint? pidRetroWrong = LocalLogin.Run(new QuazalResponder(settings, retroTitle.OnEveryPort()), "sharp-shooter");
                if (pidRetro == pid && pidRetroWrong is null)
                    steps.Ok("a title with an 8-char field uses the retro name (legacy name refused there)");
                else
                    steps.Fail($"retro-tier selection wrong: retro={pidRetro?.ToString() ?? "refused"}, legacy={pidRetroWrong?.ToString() ?? "refused"}");

                IssuedLaunch? launch = gateway.PreLaunch(tokens.Mint("launch-account", legacyName: "second-player", audience: audience, issuer: issuer));
                bool proved = launch is not null && ProveLaunch(gateway, launch, LocalLogin.SourceEndpoint);
                uint? pidLaunched = proved ? LocalLogin.Run(new QuazalResponder(settings, title), "second-player") : null;
                if (launch is not null && proved && pidLaunched is not null && pidLaunched != pid)
                    steps.Ok($"a launch proved on the game's socket logs in as its own account (pid={pidLaunched}), not the address's (pid={pid})");
                else
                    steps.Fail($"launch path failed: issued={launch is not null} proved={proved} pid={pidLaunched?.ToString() ?? "refused"}");
                var spoofEndpoint = new System.Net.IPEndPoint(LocalLogin.SourceAddress, 62000);
                var spoofResponder = new QuazalResponder(settings, title);
                gateway.PreBind(tokens.Mint("spoof-victim", legacyName: "victim", audience: audience, issuer: issuer), spoofEndpoint.Address);
                uint? victim = LocalLogin.Run(spoofResponder, "victim", forceSignature: null, from: spoofEndpoint);
                uint? spoofed = LocalLogin.Run(spoofResponder, "victim", forceSignature: 0xDEADBEEF, from: spoofEndpoint);
                if (victim is not null && spoofed is null)
                    steps.Ok("the victim logs in, but a spoof of their endpoint that does not echo the session signature is dropped");
                else
                    steps.Fail($"signature check wrong: victim={victim?.ToString() ?? "dropped"} spoofed={spoofed?.ToString() ?? "dropped"}");
                IssuedLaunch? socketLaunch = gateway.PreLaunch(tokens.Mint("socket-account", legacyName: "third-player", audience: audience, issuer: issuer));
                if (socketLaunch is null)
                {
                    steps.Fail("could not issue a launch for the socket run");
                }
                else
                {
                    try
                    {
                        using var server = new LoopbackServer(gateway, new QuazalResponder(settings, title));
                        using var udp = new UdpClient();
                        udp.Connect(server.EndPoint);

                        await LaunchProver.ProveAsync(udp, socketLaunch.IdBytes, socketLaunch.Key, steps.Info);
                        uint? pidSocket = await UdpLogin.LoginAsync(udp, "third-player");
                        if (pidSocket is not null && pidSocket != pid && pidSocket != pidLaunched)
                            steps.Ok($"over a real UDP socket ({udp.Client.LocalEndPoint} -> {server.EndPoint}): HELLO, CHALLENGE, PROOF, BOUND, then LoginEx as pid={pidSocket}");
                        else
                            steps.Fail($"socket run: login after a proved launch answered {pidSocket?.ToString() ?? "refused"}");
                    }
                    catch (Exception exception)
                    {
                        steps.Fail($"socket run failed: {exception.Message}");
                    }
                }
            }

            return steps.Finish("self-test");
        }
        finally
        {
            OnlineDataStore.Detach();
        }
    }

    /// <summary>
    /// The shim's half of the launch exchange, played straight into the gateway
    /// from <paramref name="socket"/>: HELLO, answer the challenge, check BOUND.
    /// </summary>
    private static bool ProveLaunch(PlayforwardGateway gateway, IssuedLaunch launch, IPEndPoint socket)
    {
        byte[]? challengeDatagram = gateway.HandleLaunchDatagram(LaunchEnvelope.Hello(launch.IdBytes), socket, out _);
        if (challengeDatagram is null ||
            !LaunchEnvelope.TryReadReply(challengeDatagram, LaunchEnvelope.Kind.Challenge, launch.IdBytes, out byte[] challenge))
            return false;

        byte[]? bound = gateway.HandleLaunchDatagram(LaunchEnvelope.Proof(launch.Key, launch.IdBytes, challenge), socket, out _);
        return bound is not null &&
            LaunchEnvelope.TryReadReply(bound, LaunchEnvelope.Kind.Bound, launch.IdBytes, out byte[] mac) &&
            mac.AsSpan().SequenceEqual(LaunchEnvelope.Bound(launch.Key, launch.IdBytes, challenge).AsSpan()[^mac.Length..]);
    }

    private static async Task<(string PublicKey, string Issuer)> FetchKeyAndIssuerAsync(string baseUrl)
    {
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        using JsonDocument jwks = JsonDocument.Parse(await http.GetStringAsync("/.well-known/jwks.json"));
        string x = jwks.RootElement.GetProperty("keys")[0].GetProperty("x").GetString()!;
        return (x, HarnessTokenSource.Issuer);
    }

    internal static async Task<(string AccountId, string Token)> MintRealTokenAsync(string baseUrl, HarnessOptions options)
    {
        if (options.Account is null || options.Password is null)
            throw new InvalidOperationException("--account and --password are required with --playforward-url");

        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };

        var login = await http.PostAsJsonAsync("/api/v1/auth/login", new
        {
            identifier = options.Account,
            password = options.Password,
            token_delivery = "body",
        });
        login.EnsureSuccessStatusCode();
        string access = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;

        using var me = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        me.Headers.Authorization = new("Bearer", access);
        var meResponse = await http.SendAsync(me);
        meResponse.EnsureSuccessStatusCode();
        string accountId = (await meResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("account_id").GetString()!;

        using var mint = new HttpRequestMessage(HttpMethod.Post, "/api/v1/me/game-token")
        {
            Content = JsonContent.Create(new { title = options.Title }),
        };
        mint.Headers.Authorization = new("Bearer", access);
        var mintResponse = await http.SendAsync(mint);
        if (!mintResponse.IsSuccessStatusCode)
        {
            string body = await mintResponse.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"game-token request returned {(int)mintResponse.StatusCode}: {body}. " +
                "Has this account enabled the title (PUT /api/v1/me/titles/" + options.Title + ")?");
        }
        string token = (await mintResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        return (accountId, token);
    }
}
