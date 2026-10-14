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

        Respond(server, 200, JsonSerializer.Serialize(new
        {
            pid = launch.Player.Pid,
            display_name = launch.Player.Identity.DisplayName,
            login_name = gateway.LoginNameFor(launch.Player.Identity),
            launch = new
            {
                id = launch.Id,
                key = Convert.ToBase64String(launch.Key),
                expires_in_seconds = (int)Math.Max(0, (launch.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds),
            },
        }));
        return true;
    }

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
        Respond(server, 200, StatusDocument(PlayforwardTitles.All, DateTimeOffset.UtcNow));
        return true;
    }

    internal const string StatusPath = "/playforward/status";

    /// <summary>The body of <see cref="Status"/>, for these titles at this time.</summary>
    internal static string StatusDocument(IEnumerable<PlayforwardTitle> titles, DateTimeOffset now) =>
        JsonSerializer.Serialize(new
        {
            time = now.ToUnixTimeSeconds(),
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
            Log.Warning("[playforward] refused a plain-HTTP request from {Caller}, which is outside LanNetworks: these routes need TLS from there",
                CallerAddress(server));
            Respond(server, 403, """{"error":"this endpoint requires https from outside the server's local network"}""");
            token = null;
            gateway = null;
            return false;
        }
        return true;
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

    private static string? ReadToken(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("token", out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Respond(ServerSender server, int status, string json)
    {
        server.Response.Clear();
        server.Response.SetBegin(status);
        server.Response.SetHeader("Content-Type", "application/json");
        server.Response.SetBody(json);
        server.SendResponse();
    }
}
