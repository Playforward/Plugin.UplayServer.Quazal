using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text;
using System.IO.Pipes;
using Plugin.UplayServer.Quazal.Playforward;
using Plugin.UplayServer.Quazal.Serializables;

namespace GameClientTestHarness;

/// <summary>
/// Stands in for the game's shim against a fully running stack
/// </summary>
internal static class BridgeRun
{
    public static async Task<int> RunAsync(HarnessOptions options)
    {
        var steps = new Steps();
        Console.WriteLine($"Bridge: standing in for the game's shim, title '{options.Title}'{(options.Launch ? ", proving a launch" : "")}.\n");

        if (options.Server is null)
        {
            steps.Fail("--server HOST:PORT is required (the Quazal UDP listener to log in to)");
            return steps.Finish("bridge");
        }
        if (!TryParseEndpoint(options.Server, out IPEndPoint? server))
        {
            steps.Fail($"--server '{options.Server}' is not HOST:PORT");
            return steps.Finish("bridge");
        }

        if (options.Launch)
            return await RunLaunchAsync(options, server!, steps);

        string code;
        try
        {
            if (options.PlayforwardUrl is { } playforward && options.ServerHttp is { } serverHttp)
            {
                (string accountId, string token) = await SelfTest.MintRealTokenAsync(playforward, options);
                steps.Ok($"minted a real game-server token from {playforward} for account {accountId}");
                code = await PreRegisterAsync(serverHttp, token);
                steps.Ok($"{serverHttp}/playforward/pre-register issued a correlation code");
            }
            else
            {
                code = await AskBridgeForCodeAsync(options.Pipe, options.Title);
                steps.Ok($"GameBridge returned a correlation code over pipe '{options.Pipe}'");
            }
            steps.Info($"code={code}");
        }
        catch (Exception exception)
        {
            steps.Fail($"could not get a code: {exception.Message}");
            steps.Info("Is the desktop client running and signed in? Or pass --playforward-url, --account, --password and --server-http to go without it.");
            return steps.Finish("bridge");
        }

        try
        {
            uint? pid = await LoginOverUdpAsync(server!, code);
            if (pid is null)
                steps.Fail("LoginEx over UDP did not return a success with a pid");
            else
                steps.Ok($"logged in to {server}; server assigned pid={pid}");
        }
        catch (Exception exception)
        {
            steps.Fail($"UDP login failed: {exception.Message}");
        }

        return steps.Finish("bridge");
    }

    /// <summary>
    /// The launch shape: get a launch, prove it on the socket, log in by name.
    /// </summary>
    private static async Task<int> RunLaunchAsync(HarnessOptions options, IPEndPoint server, Steps steps)
    {
        byte[] launchId;
        byte[] key;
        string? login = options.Login;
        try
        {
            if (options.PlayforwardUrl is { } playforward && options.ServerHttp is { } serverHttp)
            {
                (string accountId, string token) = await SelfTest.MintRealTokenAsync(playforward, options);
                steps.Ok($"minted a real game-server token from {playforward} for account {accountId}");
                (launchId, key, string loginName) = await PreLaunchAsync(serverHttp, token);
                steps.Ok($"{serverHttp}/playforward/pre-launch issued a launch; the server will enforce the name '{loginName}'");
                login ??= loginName;
            }
            else
            {
                (launchId, key) = await AskBridgeForLaunchAsync(options.Pipe, options.Title);
                steps.Ok($"GameBridge returned a launch over pipe '{options.Pipe}'");
            }
            steps.Info($"launch id={Convert.ToHexString(launchId).ToLowerInvariant()} (the key is not printed)");
        }
        catch (Exception exception)
        {
            steps.Fail($"could not get a launch: {exception.Message}");
            steps.Info("Is the desktop client running and signed in? Or pass --playforward-url, --account, --password and --server-http to go without it.");
            return steps.Finish("bridge");
        }

        if (login is null)
        {
            steps.Fail("--login NAME is required with --launch over the pipe: the server enforces the player's name, and the harness has no hook.config to read it from");
            return steps.Finish("bridge");
        }

        try
        {
            using var udp = new UdpClient();
            udp.Connect(server);

            await LaunchProver.ProveAsync(udp, launchId, key, steps.Info);
            steps.Ok($"launch proved on {udp.Client.LocalEndPoint}: the server bound this socket");

            uint? pid = await UdpLogin.LoginAsync(udp, login);
            if (pid is null)
                steps.Fail($"LoginEx as '{login}' over the proved socket did not return a success with a pid");
            else
                steps.Ok($"logged in to {server} as '{login}'; server assigned pid={pid}");
        }
        catch (Exception exception)
        {
            steps.Fail($"launch or login failed: {exception.Message}");
        }

        return steps.Finish("bridge");
    }

    /// <summary>
    /// The GameBridge's original request: the bare title in, the code out.
    /// </summary>
    private static Task<string> AskBridgeForCodeAsync(string pipeName, string title) =>
        AskBridgeAsync(pipeName, title);

    /// <summary>
    /// The launch request on the same pipe: "launch &lt;title&gt;" in,
    /// "LAUNCH &lt;id hex&gt; &lt;key hex&gt;" out. Exactly what the shim parses.
    /// </summary>
    private static async Task<(byte[] Id, byte[] Key)> AskBridgeForLaunchAsync(string pipeName, string title)
    {
        string line = await AskBridgeAsync(pipeName, "launch " + title);
        string[] words = line.Split(' ');
        if (words.Length != 3 || words[0] != "LAUNCH")
            throw new InvalidOperationException($"expected 'LAUNCH <id> <key>', got '{line}'");

        byte[] id = Convert.FromHexString(words[1]);
        byte[] key = Convert.FromHexString(words[2]);
        if (id.Length != LaunchEnvelope.LaunchIdLength)
            throw new InvalidOperationException($"launch id is {id.Length} bytes, expected {LaunchEnvelope.LaunchIdLength}");
        return (id, key);
    }

    /// <summary>
    /// Speaks the GameBridge's one-line protocol: write a request and a
    /// newline, read one line back -- the answer, or "ERROR &lt;reason&gt;".
    /// </summary>
    private static async Task<string> AskBridgeAsync(string pipeName, string request)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
        await pipe.ConnectAsync(5000);

        await pipe.WriteAsync(Encoding.UTF8.GetBytes(request + "\n"));
        await pipe.FlushAsync();

        string line = await ReadLineAsync(pipe);
        if (line.StartsWith("ERROR ", StringComparison.Ordinal))
            throw new InvalidOperationException(line["ERROR ".Length..]);
        return line;
    }

    /// <summary>
    /// The desktop client's one call to the game server: the token in, a code out.
    /// </summary>
    private static async Task<string> PreRegisterAsync(string serverHttp, string token)
    {
        using var http = new HttpClient { BaseAddress = new Uri(serverHttp) };
        using var content = new StringContent(JsonSerializer.Serialize(new { token }), Encoding.UTF8, "application/json");
        var response = await http.PostAsync("/playforward/pre-register", content);
        string body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"pre-register answered {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("code").GetString()!;
    }

    /// <summary>The desktop client's pre-launch call: the token in, a launch out.</summary>
    private static async Task<(byte[] Id, byte[] Key, string LoginName)> PreLaunchAsync(string serverHttp, string token)
    {
        using var http = new HttpClient { BaseAddress = new Uri(serverHttp) };
        using var content = new StringContent(JsonSerializer.Serialize(new { token }), Encoding.UTF8, "application/json");
        var response = await http.PostAsync("/playforward/pre-launch", content);
        string body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"pre-launch answered {(int)response.StatusCode}: {body}");

        JsonElement root = JsonDocument.Parse(body).RootElement;
        JsonElement launch = root.GetProperty("launch");
        return (
            Convert.FromHexString(launch.GetProperty("id").GetString()!),
            Convert.FromBase64String(launch.GetProperty("key").GetString()!),
            root.GetProperty("login_name").GetString()!);
    }

    private static async Task<string> ReadLineAsync(Stream stream)
    {
        var builder = new StringBuilder();
        var one = new byte[1];
        while (await stream.ReadAsync(one) == 1)
        {
            if (one[0] == (byte)'\n')
                break;
            builder.Append((char)one[0]);
        }
        return builder.ToString().TrimEnd('\r');
    }

    /// <summary>A login on a fresh socket, for the code shape.</summary>
    private static async Task<uint?> LoginOverUdpAsync(IPEndPoint server, string username)
    {
        using var udp = new UdpClient();
        udp.Connect(server);
        return await UdpLogin.LoginAsync(udp, username);
    }

    private static bool TryParseEndpoint(string value, out IPEndPoint? endpoint)
    {
        endpoint = null;
        string[] parts = value.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[1], out int port))
            return false;
        if (!IPAddress.TryParse(parts[0], out IPAddress? address))
        {
            IPAddress[] resolved = Dns.GetHostAddresses(parts[0]);
            if (resolved.Length == 0)
                return false;
            address = resolved[0];
        }
        endpoint = new IPEndPoint(address, port);
        return true;
    }
}
