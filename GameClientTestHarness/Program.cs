using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GameClientTestHarness;
using Plugin.UplayServer.Quazal;
using Plugin.UplayServer.Quazal.Playforward;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Warning() // the harness prints its own step log; keep the server's quiet
    .WriteTo.Console()
    .CreateLogger();

var options = HarnessOptions.Parse(args);
return options.Mode switch
{
    HarnessMode.SelfTest => await SelfTest.RunAsync(options),
    HarnessMode.Bridge => await BridgeRun.RunAsync(options),
    HarnessMode.LaunchServer => await LaunchServerRun.RunAsync(options),
    HarnessMode.Login => await PlainLoginRun.RunAsync(options),
    _ => Usage(),
};

static int Usage()
{
    Console.WriteLine("""
        Far Cry 3 login harness — stands in for the patched game.

        selftest   (default) Everything in one process on this machine: start a
                   Playforward gateway, obtain a code, and log in through the
                   real server login path. Proves the integration end to end
                   with no Windows and no running services.

            --playforward-url URL   Mint a real token from a running Playforward
                                    instead of self-signing one. Needs
                                    --account and --password for an account that
                                    has enabled farcry3.
            --account NAME
            --password PW
            --title ID              Default farcry3.

        bridge     Stand in for the game against a fully running stack: ask a
                   real desktop-client GameBridge for a code, then log in over
                   UDP to a running Quazal server.

            --pipe NAME             GameBridge pipe (default playforward-game-bridge).
            --server HOST:PORT      The Quazal UDP listener to log in to.
            --title ID              Default farcry3.
            --launch                Prove a launch on the login socket (what the
                                    shim does) instead of carrying a code, then
                                    log in by name. Needs --login NAME over the
                                    pipe; pipe-less runs take the name from
                                    pre-launch's answer.
            --login NAME            The name to log in as (the server enforces it).

            Without a desktop client, stand in for it too: mint the token
            from Playforward and pre-register it with the game server directly.
            --playforward-url URL   with --account and --password, and
            --server-http URL       the game server's HTTP base, e.g. http://127.0.0.1:80

        login      Log in by name to a running server, as the game does with no
                   Playforward at all (a standalone title, or a LAN account):
                   SYN, CONNECT, LoginEx over a real UDP socket. Prints the pid.

            --server HOST:PORT      The title's authentication port, e.g. 127.0.0.1:6570.
            --login NAME            The name to log in as.

        launchserver  Stand in for the server instead: listen on loopback UDP,
                   issue one launch, print it as the bridge would ("LAUNCH <id>
                   <key>" on stdout), and exit 0 once something proves it. For
                   running the shim's own code under Wine against the real
                   server dispatch -- see UplaySDKShim/tests/run_wine_test.sh.

            --server HOST:PORT      The port to listen on (host ignored; loopback).
        """);
    return 2;
}
