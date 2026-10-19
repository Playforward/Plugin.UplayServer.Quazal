using System.Net;
using System.Net.Sockets;

namespace GameClientTestHarness;

/// <summary>
/// A login by name with nothing in front of it: what a game does against a
/// server that has no Playforward, whose shim sends hook.config's
/// login_name and proves no launch.
/// </summary>
internal static class PlainLoginRun
{
    public static async Task<int> RunAsync(HarnessOptions options)
    {
        if (options.Server is null || string.IsNullOrEmpty(options.Login) ||
            !IPEndPoint.TryParse(options.Server, out IPEndPoint? server))
        {
            Console.Error.WriteLine("login needs --server HOST:PORT and --login NAME");
            return 2;
        }

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Connect(server);
        try
        {
            uint? pid = await UdpLogin.LoginAsync(udp, options.Login);
            if (pid is null)
            {
                Console.WriteLine($"FAIL {options.Login}: the server refused the login (see its log)");
                return 1;
            }
            Console.WriteLine($"OK {options.Login} pid={pid} from {udp.Client.LocalEndPoint}");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"FAIL {options.Login}: no reply from {server}");
            return 1;
        }
    }
}
