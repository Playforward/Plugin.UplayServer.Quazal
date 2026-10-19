namespace GameClientTestHarness;

internal enum HarnessMode { SelfTest, Bridge, LaunchServer, Login, Help }

internal sealed class HarnessOptions
{
    public HarnessMode Mode { get; private set; } = HarnessMode.SelfTest;
    public string Title { get; private set; } = "farcry3";
    public string? PlayforwardUrl { get; private set; }
    public string? Account { get; private set; }
    public string? Password { get; private set; }
    public string Pipe { get; private set; } = "playforward-game-bridge";
    public string? Server { get; private set; }
    /// <summary>The running game server's HTTP base (its /playforward routes), for a pipe-less bridge run.</summary>
    public string? ServerHttp { get; private set; }
    /// <summary>Bridge: prove a launch on the login socket instead of carrying a code.</summary>
    public bool Launch { get; private set; }
    /// <summary>Bridge with --launch: the name to log in as, which the server enforces; what the shim reads from hook.config.</summary>
    public string? Login { get; private set; }

    public static HarnessOptions Parse(string[] args)
    {
        var options = new HarnessOptions();
        int index = 0;

        if (args.Length > 0 && !args[0].StartsWith('-'))
        {
            options.Mode = args[0] switch
            {
                "selftest" => HarnessMode.SelfTest,
                "bridge" => HarnessMode.Bridge,
                "launchserver" => HarnessMode.LaunchServer,
                "login" => HarnessMode.Login,
                _ => HarnessMode.Help,
            };
            index = 1;
        }

        while (index < args.Length)
        {
            // --launch is a flag; everything else takes a value.
            if (args[index] == "--launch")
            {
                options.Launch = true;
                index += 1;
                continue;
            }
            if (index == args.Length - 1)
            {
                options.Mode = HarnessMode.Help;
                return options;
            }
            switch (args[index])
            {
                case "--login": options.Login = args[index + 1]; break;
                case "--playforward-url": options.PlayforwardUrl = args[index + 1]; break;
                case "--account": options.Account = args[index + 1]; break;
                case "--password": options.Password = args[index + 1]; break;
                case "--title": options.Title = args[index + 1]; break;
                case "--pipe": options.Pipe = args[index + 1]; break;
                case "--server": options.Server = args[index + 1]; break;
                case "--server-http": options.ServerHttp = args[index + 1]; break;
                default: options.Mode = HarnessMode.Help; return options;
            }
            index += 2;
        }
        return options;
    }
}
