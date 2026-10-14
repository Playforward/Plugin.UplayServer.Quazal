namespace Plugin.UplayServer.Quazal.Playforward;

/// <summary>
/// The HTTP endpoints this plugin serves: the desktop client presents a
/// Playforward token and gets back something the game can carry 
/// </summary>
internal static class PlayforwardRoutes
{
    /// <summary>
    /// POST /playforward/pre-register  {"token": "&lt;game-server token&gt;"}
    ///   -> 200 {"code": "...", "expires_in_seconds": n}
    /// </summary>
    [HTTP("POST", "/playforward/pre-register")]
    public static bool PreRegister(ServerSender server)
    {
        if (!TryGateway(server, out PlayforwardGateway? gateway, out string? token))
            return true;

        string? code = gateway.PreRegister(token);
        if (code is null)
        {
            Respond(server, 401, """{"error":"token rejected"}""");
            return true;
        }

        Respond(server, 200, JsonSerializer.Serialize(new
        {
            code,
            expires_in_seconds = gateway.CorrelationCodeTtlSeconds,
        }));
        return true;
    }

    /// <summary>
    /// POST /playforward/pre-bind  {"token": "<game-server token>"}
    ///   -> 200 {"pid": n, "display_name": "...", "login_name": "..."}
    ///
    /// Binds the caller's account to the caller's own source address, so the
    /// game logging in from this machine is recognised without the game being
    /// touched at all -- only redirected. The token is the credential; the
    /// address is taken from the connection, never from the body, so a caller
    /// cannot bind an identity onto someone else's address.
    /// </summary>
    [HTTP("POST", "/playforward/pre-bind")]
    public static bool PreBind(ServerSender server)
    {
        if (!TryGateway(server, out PlayforwardGateway? gateway, out string? token))
            return true;

        System.Net.IPAddress? address = CallerAddress(server);
        if (address is null)
        {
            Respond(server, 500, "{\"error\":\"could not determine the caller address\"}");
            return true;
        }

        ResolvedPlayer? player = gateway.PreBind(token, address);
        if (player is null)
        {
            Respond(server, 401, "{\"error\":\"token rejected\"}");
            return true;
        }

        Respond(server, 200, JsonSerializer.Serialize(new
        {
            pid = player.Pid,
            display_name = player.Identity.DisplayName,
            login_name = gateway.LoginNameFor(player.Identity),
        }));
        return true;
    }

    /// <summary>
    /// POST /playforward/pre-launch  {"token": "&lt;game-server token&gt;"}
    ///   -> 200 {"pid": n, "display_name": "...", "login_name": "...",
    ///           "launch": {"id": "&lt;hex&gt;", "key": "&lt;base64&gt;", "expires_in_seconds": n}}
    ///
    /// Issues a launch: the id is public and the key is the secret the game's
    /// shim will prove it holds, on the game's own socket, before the game logs
    /// in. Unlike pre-bind, nothing here binds anything yet -- the binding
    /// happens when the proof arrives, and to the socket it arrives on.
    /// </summary>
    [HTTP("POST", "/playforward/pre-launch")]
    public static bool PreLaunch(ServerSender server)
    {
        if (!TryGateway(server, out PlayforwardGateway? gateway, out string? token))
            return true;

        IssuedLaunch? launch = gateway.PreLaunch(token);
        if (launch is null)
        {
            Respond(server, 401, "{\"error\":\"token rejected\"}");
            return true;
        }

        RespondWithLaunch(server, gateway, launch);
        return true;
    }

    /// <summary>
    /// POST /playforward/renew  {"token": "...", "launch": "&lt;old id, hex&gt;",
    ///                           "proof": "&lt;hex HMAC under the old key&gt;"}
    ///   -> 200, as pre-launch
    ///
    /// </summary>
    [HTTP("POST", "/playforward/renew")]
    public static bool Renew(ServerSender server)
    {
        if (!TryGateway(server, out PlayforwardGateway? gateway, out string? token))
            return true;

        IssuedLaunch? renewed = null;
        if (TryReadHex(server.Request.Body, "launch", out byte[] launchId) && launchId.Length == LaunchEnvelope.LaunchIdLength &&
            TryReadHex(server.Request.Body, "proof", out byte[] proof))
        {
            renewed = gateway.Renew(token, launchId, proof);
        }
        if (renewed is null)
        {
            Respond(server, 401, "{\"error\":\"renewal refused\"}");
            return true;
        }

        RespondWithLaunch(server, gateway, renewed);
        return true;
    }

    /// <summary>
    /// POST /playforward/launch-check  {"launch": "&lt;id, hex&gt;",
    ///                                  "proof": "&lt;hex HMAC under its key&gt;"}
    ///   -> 200 <c>LIVE</c> or <c>GONE</c> as text/plain, {"live": bool} otherwise
    ///
    /// </summary>
    [HTTP("POST", "/playforward/launch-check")]
    public static bool LaunchCheck(ServerSender server)
    {
        if (!PlayforwardTitles.Any)
        {
            Respond(server, 503, """{"error":"playforward login is not enabled on this server"}""");
            return true;
        }
        if (!PlayforwardTitles.All.Any(title => MayServe(server.Session is NetCoreServer.SslSession, CallerAddress(server), title.Gateway)))
        {
            RefusePlainHttp(server);
            return true;
        }
        if (!TryReadHex(server.Request.Body, "launch", out byte[] launchId) || launchId.Length != LaunchEnvelope.LaunchIdLength ||
            !TryReadHex(server.Request.Body, "proof", out byte[] proof))
        {
            Respond(server, 400, """{"error":"a launch and its proof are required"}""");
            return true;
        }

        // Launch ids are random and per title, so at most one gateway holds it.
        bool live = PlayforwardTitles.All.Any(title => title.Gateway.HoldsLaunch(launchId, proof));
        if (WantsLine(server))
            RespondText(server, 200, live ? "LIVE\n" : "GONE\n");
        else
            Respond(server, 200, JsonSerializer.Serialize(new { live }));
        return true;
    }

    /// <summary>
    /// Identifies this run of the server. Launches and bindings live in memory,
    /// so a different boot id means both were lost. The shim learns that from
    /// <see cref="LaunchCheck"/>, which also covers a launch forgotten without a
    /// restart; the boot id stays on the status page and the launch line for
    /// the logs and the desktop client.
    /// </summary>
    internal static readonly string BootId =
        Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(BootIdLength)).ToLowerInvariant();

    /// <summary>As wide as a launch id: random, and never the same twice.</summary>
    private const int BootIdLength = LaunchEnvelope.LaunchIdLength;

    /// <summary>
    /// Answers a launch, as JSON for the desktop client, or as one line for a
    /// shim that asks for <c>text/plain</c>:
    /// <c>LAUNCH &lt;id hex&gt; &lt;key hex&gt; &lt;expires in seconds&gt; &lt;boot id&gt;</c>.
    /// The line is what the shim reads inside the game process, where a JSON
    /// parser would be one more thing to carry for four fields.
    /// </summary>
    private static void RespondWithLaunch(ServerSender server, PlayforwardGateway gateway, IssuedLaunch launch)
    {
        int expiresIn = (int)Math.Max(0, (launch.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds);
        if (WantsLine(server))
        {
            RespondText(server, 200, LaunchLine(launch, expiresIn));
            return;
        }

        Respond(server, 200, JsonSerializer.Serialize(new
        {
            pid = launch.Player.Pid,
            display_name = launch.Player.Identity.DisplayName,
            login_name = gateway.LoginNameFor(launch.Player.Identity),
            boot_id = BootId,
            launch = new
            {
                id = launch.Id,
                key = Convert.ToBase64String(launch.Key),
                expires_in_seconds = expiresIn,
            },
        }));
    }

    /// <summary>The one-line form of a launch; see <see cref="RespondWithLaunch"/>.</summary>
    /// <param name="bootId">This run's, unless a harness standing in for a restarted server names another.</param>
    internal static string LaunchLine(IssuedLaunch launch, int expiresInSeconds, string? bootId = null) =>
        $"LAUNCH {launch.Id} {Convert.ToHexString(launch.Key).ToLowerInvariant()} {expiresInSeconds} {bootId ?? BootId}\n";

    /// <summary>Whether the caller asked for the one-line answers a shim reads.</summary>
    private static bool WantsLine(ServerSender server) =>
        server.Headers.TryGetValue("accept", out string? accept) &&
        accept.Contains("text/plain", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// GET /playforward/status
    ///   -> 200 {"time": unix seconds, "titles": [{"title_id": "...", "server_id": "...",
    ///           "key_ids": ["..."], "clock_skew_seconds": n}]}
    ///
    /// </summary>
    [HTTP("GET", StatusPath)]
    public static bool Status(ServerSender server)
    {
        if (!PlayforwardTitles.Any)
        {
            Respond(server, 503, """{"error":"playforward login is not enabled on this server"}""");
            return true;
        }
        if (WantsLine(server))
            RespondText(server, 200, $"BOOT {BootId}\n");
        else
            Respond(server, 200, StatusDocument(PlayforwardTitles.All, DateTimeOffset.UtcNow));
        return true;
    }

    internal const string StatusPath = "/playforward/status";

    /// <summary>The body of <see cref="Status"/>, for these titles at this time.</summary>
    internal static string StatusDocument(IEnumerable<PlayforwardTitle> titles, DateTimeOffset now) =>
        JsonSerializer.Serialize(new
        {
            time = now.ToUnixTimeSeconds(),
            boot_id = BootId,
            titles = titles.Select(title => new
            {
                title_id = title.TitleId,
                server_id = title.ServerId,
                key_ids = title.Gateway.Verifier.Keys.KeyIds,
                clock_skew_seconds = (int)title.Gateway.Verifier.ClockSkew.TotalSeconds,
            }),
        });

    /// <summary>
    /// Reads the token and picks the gateway of the title it is for, answering
    /// the request itself when there is nothing to hand it to. The title is
    /// chosen by the audience the token claims; the gateway then verifies the
    /// token, audience included, so a claim for the wrong title gets exactly
    /// as far as that title's verifier and is refused there.
    /// </summary>
    /// <returns>Whether a gateway and a token are in hand; false means a response was already sent.</returns>
    private static bool TryGateway(ServerSender server,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PlayforwardGateway? gateway,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? token)
    {
        gateway = null;
        token = null;
        if (!PlayforwardTitles.Any)
        {
            Respond(server, 503, """{"error":"playforward login is not enabled on this server"}""");
            return false;
        }

        token = ReadToken(server.Request.Body);
        if (string.IsNullOrWhiteSpace(token))
        {
            Respond(server, 400, """{"error":"a token is required"}""");
            return false;
        }

        gateway = PlayforwardTitles.ForToken(token)?.Gateway;
        if (gateway is null)
        {
            Log.Warning("[playforward] rejected a token minted for no title served here ({Audiences})",
                string.Join(", ", SignedJwt.PeekAudiences(token)));
            Respond(server, 401, """{"error":"token rejected"}""");
            return false;
        }

        if (!MayServe(server.Session is NetCoreServer.SslSession, CallerAddress(server), gateway))
        {
            RefusePlainHttp(server);
            token = null;
            gateway = null;
            return false;
        }
        return true;
    }

    private static void RefusePlainHttp(ServerSender server)
    {
        Log.Warning("[playforward] refused a plain-HTTP request from {Caller}, which is outside LanNetworks: these routes need TLS from there",
            CallerAddress(server));
        Respond(server, 403, """{"error":"this endpoint requires https from outside the server's local network"}""");
    }

    /// <summary>
    /// Whether a request may be answered: over TLS always, over plain HTTP
    /// only from the server's LAN.
    /// </summary>
    internal static bool MayServe(bool overTls, System.Net.IPAddress? caller, PlayforwardGateway gateway) =>
        overTls || (caller is not null && gateway.IsOnLan(caller));

    /// <summary>The caller's source IP, taken from the connection, not a header.</summary>
    private static System.Net.IPAddress? CallerAddress(ServerSender server) =>
        (server.Session.Socket.RemoteEndPoint as System.Net.IPEndPoint)?.Address;

    private static string? ReadToken(string? body) => ReadString(body, "token");

    /// <summary>One string field of a JSON body, or null.</summary>
    private static string? ReadString(string? body, string name)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>One hex field of a JSON body, decoded; false if absent or not hex.</summary>
    private static bool TryReadHex(string? body, string name, out byte[] bytes)
    {
        bytes = [];
        if (ReadString(body, name) is not { } hex)
            return false;
        try
        {
            bytes = Convert.FromHexString(hex);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void Respond(ServerSender server, int status, string json) =>
        RespondWith(server, status, "application/json", json);

    private static void RespondText(ServerSender server, int status, string text) =>
        RespondWith(server, status, "text/plain; charset=utf-8", text);

    private static void RespondWith(ServerSender server, int status, string contentType, string body)
    {
        server.Response.Clear();
        server.Response.SetBegin(status);
        server.Response.SetHeader("Content-Type", contentType);
        server.Response.SetBody(body);
        server.SendResponse();
    }
}
