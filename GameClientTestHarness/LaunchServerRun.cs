using System.Net;
using Plugin.UplayServer.Quazal;
using Plugin.UplayServer.Quazal.Playforward;

namespace GameClientTestHarness;

/// <summary>
/// Stands the server's UDP dispatch up on a loopback port with one launch
/// issued, prints that launch the way the desktop client's bridge would, and
/// waits for something to prove it. For driving the shim's Windows code
/// under Wine against the real server, with neither ServerApp nor a client.
/// </summary>
internal static class LaunchServerRun
{
    /// <summary>How long to wait for a proof before giving up. Wine startup is the slow part.</summary>
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

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
            },
        };

        int port = options.Server is { } s && int.TryParse(s.Split(':')[^1], out int parsed) ? parsed : 0;
        QuazalTitle title = QuazalTitle.ForTests(options.Title, port, port);
        var store = new InMemoryProgressStore();
        PlayforwardGateway? gateway = PlayforwardGateway.Start(settings.Playforward, title.TitleId, audience,
            title.Settings.Playforward.LoginNameMaxLength, store);
        if (gateway is null)
        {
            Console.Error.WriteLine("the gateway refused to start");
            return 1;
        }
        PlayforwardTitles.Register(PlayforwardTitle.ForTests(title, audience, gateway, store));

        using (gateway)
        {
            using var server = new LoopbackServer(gateway, new QuazalResponder(settings, title.OnEveryPort()), port);

            IssuedLaunch launch = gateway.PreLaunch(tokens.Mint("wine-account", legacyName: "wine-player", audience: audience, issuer: issuer))!;
            Console.Error.WriteLine($"launch server on {server.EndPoint}; launch {launch.Id} issued, waiting {Wait.TotalSeconds:0}s for a proof");
            Console.WriteLine($"LAUNCH {launch.Id} {Convert.ToHexString(launch.Key).ToLowerInvariant()}");
            Console.Out.Flush();

            DateTimeOffset deadline = DateTimeOffset.UtcNow + Wait;
            while (DateTimeOffset.UtcNow < deadline)
            {
                IReadOnlyCollection<string> bound = gateway.BoundAddresses();
                if (bound.Count > 0)
                {
                    Console.Error.WriteLine($"bound: {string.Join(", ", bound)}");
                    return 0;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }

            Console.Error.WriteLine("nothing proved the launch");
            return 1;
        }
    }
}
