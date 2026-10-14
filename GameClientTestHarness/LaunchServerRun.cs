using System.Net;
using System.Text;
using System.Text.Json;
using Plugin.UplayServer.Quazal;
using Plugin.UplayServer.Quazal.Playforward;

namespace GameClientTestHarness;

/// <summary>
/// Stands the server up on loopback the way the shim meets it: UDP dispatch
/// for the proof and the KEEPs, and HTTP for pre-launch, renew, launch-check
/// and status. Prints the tokens a desktop client's bridge would hand out
/// ("TOKEN &lt;url&gt; &lt;jti&gt; &lt;token&gt;", one per line), then takes the shim
/// through three things in turn, each waiting for the shim to bind again:
/// a renewal; a network drop -- the UDP port gone, with the server running,
/// until the launch idles out and launch-check answers GONE; and a restart,
/// a fresh gateway that remembers nothing. Exits 0 when all three pass. For
/// driving the shim's Windows code under Wine against the real gateway, with
/// neither ServerApp nor a client.
/// </summary>
internal static class LaunchServerRun
{
    /// <summary>How long to wait for each step. Wine startup is the slow part.</summary>
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    /// <summary>
    /// A launch's ceiling for the run: short, so the shim renews it (at three
    /// quarters) within seconds rather than hours.
    /// </summary>
    private const int LaunchTtlSeconds = 4;

    /// <summary>
    /// Shorter than the three seconds between renewals, so a launch whose
    /// KEEPs stop goes quiet past its timeout before a renewal replaces it.
    /// </summary>
    private const int LaunchIdleTimeoutSeconds = 2;

    /// <summary>
    /// Tokens printed: one per launch or renewal, since each is spent once.
    /// The drop costs several, one for each fresh launch that idles out while
    /// the port is still down.
    /// </summary>
    private const int TokenCount = 32;

    public static async Task<int> RunAsync(HarnessOptions options)
    {
        var tokens = new HarnessTokenSource();
        string audience = "playforward-game-servers:" + options.Title;
        string issuer = HarnessTokenSource.Issuer;

        var settings = new QuazalSettings
        {
            Accounts = { new QuazalAccount { Username = "player", Password = "server-side-password", Pid = 101 } },
            Playforward = new PlayforwardSettings
            {
                Enabled = true, PublicKeyBase64 = tokens.PublicKeyBase64Url, Issuer = issuer,
                BaseUrl = "http://playforward.test",
                LaunchTtlSeconds = LaunchTtlSeconds, LaunchIdleTimeoutSeconds = LaunchIdleTimeoutSeconds,
            },
        };

        int port = options.Server is { } s && int.TryParse(s.Split(':')[^1], out int parsed) ? parsed : 0;
        QuazalTitle title = QuazalTitle.ForTests(options.Title, port, port);

        LoopbackServer Listen(PlayforwardGateway gateway, int udpPort) =>
            new(gateway, new QuazalResponder(settings, title.OnEveryPort()), udpPort);

        // One run of the server: its gateway, its UDP port, its boot id.
        Run? Start(int udpPort, string bootId)
        {
            var store = new InMemoryProgressStore();
            PlayforwardGateway? gateway = PlayforwardGateway.Start(settings.Playforward, title.TitleId, audience,
                title.Settings.Playforward.LoginNameMaxLength, store);
            if (gateway is null)
                return null;
            PlayforwardTitles.Register(PlayforwardTitle.ForTests(title, audience, gateway, store));
            return new Run(gateway, Listen(gateway, udpPort), bootId);
        }

        Run? first = Start(port, PlayforwardRoutes.BootId);
        if (first is null)
        {
            Console.Error.WriteLine("the gateway refused to start");
            return 1;
        }
        var server = new Server { Current = first };

        try
        {
            // The game server's HTTP routes, beside its UDP port as ServerApp
            // serves both from one host.
            int udpPort = server.Current.Udp.EndPoint.Port;
            string baseUrl = $"http://127.0.0.1:{udpPort + 1}";
            using var http = new HttpListener();
            http.Prefixes.Add(baseUrl + "/");
            http.Start();
            _ = Task.Run(() => ServeAsync(http, server));

            for (int i = 0; i < TokenCount; i++)
            {
                string tokenId = Guid.NewGuid().ToString();
                string token = tokens.Mint("wine-account", audience, issuer, legacyName: "wine-player", tokenId: tokenId);
                Console.WriteLine($"TOKEN {baseUrl} {tokenId} {token}");
            }
            Console.Out.Flush();
            Console.Error.WriteLine($"launch server on 127.0.0.1:{udpPort} and {baseUrl}; waiting {Wait.TotalSeconds:0}s for a launch to be proved and renewed");

            if (!await WaitAsync(() => server.Current.Gateway.BoundAddresses().Count > 0 && Volatile.Read(ref server.Renewals) > 0))
            {
                Console.Error.WriteLine($"not proved and renewed: launches {server.Launches}, renewals {server.Renewals}");
                return 1;
            }
            Console.Error.WriteLine($"proved and renewed: launches {server.Launches}, renewals {server.Renewals}; dropping the network");

            // A network drop: the game's UDP path gone while the server keeps
            // running, so the launch goes quiet past its idle timeout with no
            // restart to give it away. The shim has to learn it from
            // launch-check, and bind a fresh launch once the path is back.
            int launchesAtDrop = Volatile.Read(ref server.Launches);
            server.Current.Udp.Dispose();
            if (!await WaitAsync(() => Volatile.Read(ref server.Gone) > 0))
            {
                Console.Error.WriteLine("the launch was never reported gone during the drop");
                return 1;
            }
            server.Current = server.Current with { Udp = Listen(server.Current.Gateway, udpPort) };
            if (!await WaitAsync(() => Volatile.Read(ref server.Launches) > launchesAtDrop &&
                                       server.LastPreLaunch is { } fresh && server.Current.Gateway.LaunchSocket(fresh) is not null))
            {
                Console.Error.WriteLine($"not bound again after the drop: launches {server.Launches}, checks answered gone {server.Gone}");
                return 1;
            }
            Console.Error.WriteLine($"bound again after the drop: launches {server.Launches}, checks answered gone {server.Gone}; restarting");

            // A restart: every launch and binding forgotten, a new boot id.
            server.Current.Dispose();
            server.Current = Start(udpPort, Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant())!;
            if (!await WaitAsync(() => server.Current.Gateway.BoundAddresses().Count > 0))
            {
                Console.Error.WriteLine($"not bound again after the restart: launches {server.Launches}");
                return 1;
            }
            Console.Error.WriteLine($"bound again after the restart: {string.Join(", ", server.Current.Gateway.BoundAddresses())}; launches {server.Launches}, renewals {server.Renewals}");
            return 0;
        }
        finally
        {
            server.Current?.Dispose();
        }
    }

    private static async Task<bool> WaitAsync(Func<bool> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + Wait;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
        return false;
    }

    private sealed record Run(PlayforwardGateway Gateway, LoopbackServer Udp, string BootId) : IDisposable
    {
        public void Dispose()
        {
            Udp.Dispose();
            Gateway.Dispose();
        }
    }

    /// <summary>The run being served, swapped whole by a restart, and what it has answered.</summary>
    private sealed class Server
    {
        public volatile Run Current = null!;
        public int Launches;
        public int Renewals;
        public int Gone;

        /// <summary>The id of the newest launch pre-launch issued, not counting renewals.</summary>
        public volatile string? LastPreLaunch;
    }

    /// <summary>
    /// The routes the shim calls, answered as PlayforwardRoutes answers a shim
    /// that asks for text/plain, through the same gateway and line format.
    /// </summary>
    private static async Task ServeAsync(HttpListener http, Server server)
    {
        while (http.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await http.GetContextAsync();
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            string body = await new StreamReader(context.Request.InputStream, Encoding.UTF8).ReadToEndAsync();
            Run run = server.Current;
            (int status, string text) = (context.Request.HttpMethod, context.Request.Url!.AbsolutePath) switch
            {
                ("POST", "/playforward/pre-launch") => PreLaunched(server, run, run.Gateway.PreLaunch(Field(body, "token") ?? "")),
                ("POST", "/playforward/renew") => Launched(run,
                    run.Gateway.Renew(Field(body, "token") ?? "", Hex(Field(body, "launch")), Hex(Field(body, "proof"))), ref server.Renewals),
                ("POST", "/playforward/launch-check") => Checked(server,
                    run.Gateway.HoldsLaunch(Hex(Field(body, "launch")), Hex(Field(body, "proof")))),
                ("GET", "/playforward/status") => (200, $"BOOT {run.BootId}\n"),
                _ => (404, "not found\n"),
            };

            byte[] bytes = Encoding.UTF8.GetBytes(text);
            context.Response.StatusCode = status;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    private static (int, string) PreLaunched(Server server, Run run, IssuedLaunch? launch)
    {
        if (launch is not null)
            server.LastPreLaunch = launch.Id;
        return Launched(run, launch, ref server.Launches);
    }

    private static (int, string) Checked(Server server, bool live)
    {
        if (!live)
            Interlocked.Increment(ref server.Gone);
        return (200, live ? "LIVE\n" : "GONE\n");
    }

    private static (int, string) Launched(Run run, IssuedLaunch? launch, ref int count)
    {
        if (launch is null)
            return (401, "{\"error\":\"token rejected\"}");
        Interlocked.Increment(ref count);
        int expiresIn = (int)Math.Max(0, (launch.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds);
        return (200, PlayforwardRoutes.LaunchLine(launch, expiresIn, run.BootId));
    }

    private static string? Field(string json, string name)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(name, out JsonElement value) ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static byte[] Hex(string? text)
    {
        try
        {
            return text is null ? [] : Convert.FromHexString(text);
        }
        catch (FormatException)
        {
            return [];
        }
    }
}
