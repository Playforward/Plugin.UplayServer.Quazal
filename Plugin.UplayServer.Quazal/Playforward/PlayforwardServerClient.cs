using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Plugin.UplayServer.Quazal.Playforward;

/// <summary>
/// The calls this server makes *into* Playforward
/// </summary>
internal sealed class PlayforwardServerClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private PlayforwardServerClient(HttpClient http, string baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl;
    }

    /// <summary>
    /// Builds a client, or returns null with a logged reason. Needed whenever
    /// Playforward login is on: the player-data store behind every login and
    /// every saved stat is reached through this, whether or not the server
    /// also reports its matches.
    /// </summary>
    /// <param name="settings">The half of the configuration shared by every title.</param>
    /// <param name="serverSecret">
    /// The one title's credential. One secret is one <c>title_id</c> on
    /// Playforward's side, so this client speaks for exactly that title.
    /// </param>
    /// <param name="titleId">Which title, for the log.</param>
    public static PlayforwardServerClient? Create(PlayforwardSettings settings, string serverSecret, string titleId)
    {
        if (!settings.Enabled)
            return null;

        if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out Uri? baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            Log.Error("[playforward] BaseUrl must be an absolute http(s) URL: {BaseUrl}", settings.BaseUrl);
            return null;
        }
        if (string.IsNullOrWhiteSpace(serverSecret))
        {
            Log.Error("[playforward] {Title}: ServerSecret is required; an admin issues one with POST /api/v1/admin/gameservers", titleId);
            return null;
        }

        var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(settings.RequestTimeoutSeconds),
        };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", serverSecret.Trim());

        Log.Information("[playforward] {Title}: connected to {BaseUrl} (session reporting {Reporting})",
            titleId, baseUri, settings.ReportingEnabled ? "on" : "off");
        return new PlayforwardServerClient(http, baseUri.ToString().TrimEnd('/'));
    }

    /// <summary>A client over a supplied transport, for tests of the wire contract.</summary>
    internal static PlayforwardServerClient ForTransport(HttpMessageHandler handler, string baseUrl) =>
        new(new HttpClient(handler), baseUrl.TrimEnd('/'));

    /// <summary>
    /// Registers or updates one live match, returning Playforward's id for it.
    /// </summary>
    public async Task<Guid?> RegisterSessionAsync(SessionRegistration registration, CancellationToken cancellation)
    {
        RegisterSessionResponse? response = await PostAsync<RegisterSessionResponse>(
            "/api/v1/gameservers/sessions", registration, cancellation).ConfigureAwait(false);
        return response?.SessionId;
    }

    /// <summary>
    /// Replaces a session's roster with exactly these accounts.
    /// </summary>
    public Task<bool> ReconcileRosterAsync(Guid sessionId, IReadOnlyCollection<string> accountIds,
        CancellationToken cancellation) =>
        SendAsync(HttpMethod.Put, $"/api/v1/gameservers/sessions/{sessionId}/roster",
            new RosterPush { AccountIds = accountIds }, cancellation);

    /// <summary>Records that a match is over, clearing every lobby watching it.</summary>
    public Task<bool> EndSessionAsync(Guid sessionId, CancellationToken cancellation) =>
        SendAsync(HttpMethod.Delete, $"/api/v1/gameservers/sessions/{sessionId}", null, cancellation);

    /// <summary>
    /// Collects the moderation commands this server still owes.
    /// </summary>
    public async Task<IReadOnlyList<PendingAction>> PendingActionsAsync(CancellationToken cancellation)
    {
        PendingActionsResponse? response = await GetAsync<PendingActionsResponse>(
            "/api/v1/gameservers/actions", cancellation).ConfigureAwait(false);
        return response?.Actions ?? [];
    }

    /// <summary>Reports that a command was applied in this server's own protocol.</summary>
    public Task<bool> AcknowledgeAsync(Guid actionId, CancellationToken cancellation) =>
        SendAsync(HttpMethod.Post, $"/api/v1/gameservers/actions/{actionId}/ack", null, cancellation);

    /// <summary>
    /// Asks whether an account is currently barred from this server.
    /// </summary>
    public Task<RegistrationResponse?> WhoAmIAsync(CancellationToken cancellation) =>
        GetAsync<RegistrationResponse>("/api/v1/gameservers/me", cancellation);

    public async Task<bool?> IsBannedAsync(string accountId, CancellationToken cancellation)
    {
        BannedResponse? response = await GetAsync<BannedResponse>(
            $"/api/v1/gameservers/players/{Uri.EscapeDataString(accountId)}/banned", cancellation)
            .ConfigureAwait(false);
        return response?.Banned;
    }

    /// <summary>The pid this title knows an account by, allocated on first sight.</summary>
    public async Task<long?> ResolvePrincipalAsync(string accountId, string displayName, CancellationToken cancellation)
    {
        PrincipalResponse? response = await PostAsync<PrincipalResponse>("/api/v1/gameservers/principals",
            new ResolvePrincipalRequest { AccountId = accountId, DisplayName = displayName }, cancellation)
            .ConfigureAwait(false);
        return response?.Pid;
    }

    /// <summary>Everything stored for a player, read once at login.</summary>
    public async Task<IReadOnlyList<StoredBlob>?> LoadPlayerBlobsAsync(uint pid, CancellationToken cancellation)
    {
        BlobsResponse? response = await GetAsync<BlobsResponse>(
            $"/api/v1/gameservers/principals/{pid}/blobs", cancellation).ConfigureAwait(false);
        return response?.Blobs.Select(blob => new StoredBlob(blob.Namespace, blob.Key, blob.Payload)).ToList();
    }

    /// <summary>Stores one value for a player: the raw bytes, as the game sent them.</summary>
    public Task<bool> PutPlayerBlobAsync(uint pid, string ns, string key, byte[] payload, CancellationToken cancellation) =>
        PutBytesAsync($"/api/v1/gameservers/principals/{pid}/blobs/{Uri.EscapeDataString(ns)}/{Uri.EscapeDataString(key)}",
            payload, cancellation);

    /// <summary>Title-wide values every server of this title shares.</summary>
    public async Task<IReadOnlyList<StoredBlob>?> LoadTitleBlobsAsync(CancellationToken cancellation)
    {
        BlobsResponse? response = await GetAsync<BlobsResponse>("/api/v1/gameservers/title/blobs", cancellation)
            .ConfigureAwait(false);
        return response?.Blobs.Select(blob => new StoredBlob(blob.Namespace, blob.Key, blob.Payload)).ToList();
    }

    public Task<bool> PutTitleBlobAsync(string ns, string key, byte[] payload, CancellationToken cancellation) =>
        PutBytesAsync($"/api/v1/gameservers/title/blobs/{Uri.EscapeDataString(ns)}/{Uri.EscapeDataString(key)}",
            payload, cancellation);

    /// <summary>The next value of a title-wide counter, starting at 1.</summary>
    public async Task<long?> NextCounterAsync(string name, CancellationToken cancellation)
    {
        CounterResponse? response = await PostAsync<CounterResponse>(
            $"/api/v1/gameservers/counters/{Uri.EscapeDataString(name)}/next", null, cancellation).ConfigureAwait(false);
        return response?.Value;
    }

    /// <summary>Records a player's number on a board: what a title-wide leaderboard is read from.</summary>
    public Task<bool> PutScoreAsync(uint pid, string board, long score, CancellationToken cancellation) =>
        SendAsync(HttpMethod.Put, $"/api/v1/gameservers/principals/{pid}/scores/{Uri.EscapeDataString(board)}",
            new PutScoreRequest { Score = score }, cancellation);

    /// <summary>
    /// Records several of a player's numbers at once, all or none: what a
    /// match end changes. These are what the public leaderboard on the game's
    /// page ranks, so a page never shows one match's kills beside the
    /// previous match's deaths.
    /// </summary>
    public Task<bool> PutScoresAsync(uint pid, IReadOnlyDictionary<string, long> scores, CancellationToken cancellation) =>
        SendAsync(HttpMethod.Put, $"/api/v1/gameservers/principals/{pid}/scores",
            new PutScoresRequest { Scores = new Dictionary<string, long>(scores) }, cancellation);

    /// <summary>A board's top entries, highest first, over every player of this title.</summary>
    public async Task<IReadOnlyList<StoredScore>?> TopScoresAsync(string board, int limit, CancellationToken cancellation)
    {
        ScoresResponse? response = await GetAsync<ScoresResponse>(
            $"/api/v1/gameservers/title/scores/{Uri.EscapeDataString(board)}?limit={limit}", cancellation).ConfigureAwait(false);
        return response?.Scores.Select(score => new StoredScore(score.Pid, score.Score, score.DisplayName)).ToList();
    }

    /// <summary>The board entries of particular players; those with none are absent.</summary>
    public async Task<IReadOnlyList<StoredScore>?> ScoresForAsync(string board, IReadOnlyCollection<uint> pids, CancellationToken cancellation)
    {
        ScoresResponse? response = await PostAsync<ScoresResponse>(
            $"/api/v1/gameservers/title/scores/{Uri.EscapeDataString(board)}/lookup",
            new ScoresLookupRequest { Pids = pids.Select(pid => (long)pid).ToList() }, cancellation).ConfigureAwait(false);
        return response?.Scores.Select(score => new StoredScore(score.Pid, score.Score, score.DisplayName)).ToList();
    }

    private async Task<bool> PutBytesAsync(string path, byte[] payload, CancellationToken cancellation)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, _baseUrl + path)
            {
                Content = new ByteArrayContent(payload),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using HttpResponseMessage response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
            return await SucceededAsync(response, HttpMethod.Put, path).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            LogFailure(HttpMethod.Put, path, exception);
            return false;
        }
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken cancellation) where T : class
    {
        try
        {
            using HttpResponseMessage response = await _http
                .GetAsync(_baseUrl + path, cancellation).ConfigureAwait(false);
            if (!await SucceededAsync(response, HttpMethod.Get, path).ConfigureAwait(false))
                return null;

            string body = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
            return JsonSerializer.Deserialize<T>(body, JsonOptions);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            LogFailure(HttpMethod.Get, path, exception);
            return null;
        }
    }

    private async Task<T?> PostAsync<T>(string path, object? body, CancellationToken cancellation) where T : class
    {
        try
        {
            using HttpContent? content = body is null ? null : Serialize(body);
            using HttpResponseMessage response = await _http
                .PostAsync(_baseUrl + path, content, cancellation).ConfigureAwait(false);
            if (!await SucceededAsync(response, HttpMethod.Post, path).ConfigureAwait(false))
                return null;

            string responseBody = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
            return JsonSerializer.Deserialize<T>(responseBody, JsonOptions);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            LogFailure(HttpMethod.Post, path, exception);
            return null;
        }
    }

    private async Task<bool> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellation)
    {
        try
        {
            using var request = new HttpRequestMessage(method, _baseUrl + path);
            if (body is not null)
                request.Content = Serialize(body);

            using HttpResponseMessage response = await _http
                .SendAsync(request, cancellation).ConfigureAwait(false);
            return await SucceededAsync(response, method, path).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            LogFailure(method, path, exception);
            return false;
        }
    }

    private static StringContent Serialize(object body) =>
        new(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");

    /// <summary>
    /// Reports whether a response is usable, logging why when it is not: the
    /// cases an operator can act on (a wrong secret, a revoked registration)
    /// as errors, and a single request Playforward will not take from this
    /// server as a warning.
    /// </summary>
    private static async Task<bool> SucceededAsync(HttpResponseMessage response, HttpMethod method, string path)
    {
        if (response.IsSuccessStatusCode)
            return true;
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            Log.Error("[playforward] this server's secret was rejected: check Titles[<title>].Playforward.ServerSecret, or rotate it in the admin panel");
            return false;
        }
        if (response.StatusCode != HttpStatusCode.Forbidden)
        {
            Log.Warning("[playforward] {Method} {Path} answered {Status}", method, path, (int)response.StatusCode);
            return false;
        }

        string? code = await ErrorCodeAsync(response).ConfigureAwait(false);
        if (code == PlayerNotAdmitted)
            Log.Warning("[playforward] {Method} {Path} refused: that player did not join this server through Playforward, so it may not act on them", method, path);
        else if (code == TitleStateVerifiedOnly)
            Log.Warning("[playforward] {Method} {Path} refused: only a verified server may change title-wide state; this one keeps what it learned in memory", method, path);
        else
            Log.Error("[playforward] this server's registration has been revoked; it will not appear in the directory until an admin registers it again");

        return false;
    }

    /// <summary>Playforward's code for a server acting on a player who did not choose it.</summary>
    private const string PlayerNotAdmitted = "player_not_admitted";

    /// <summary>Playforward's code for a community server writing title-wide state.</summary>
    private const string TitleStateVerifiedOnly = "title_state_verified_only";

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        try
        {
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return JsonSerializer.Deserialize<ErrorResponse>(body, JsonOptions)?.Code;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void LogFailure(HttpMethod method, string path, Exception exception) =>
        Log.Debug("[playforward] {Method} {Path} failed: {Error}", method, path, exception.Message);

    public void Dispose() => _http.Dispose();
}

/// <summary>One match, as this server describes it to the directory.</summary>
internal sealed class SessionRegistration
{
    /// <summary>
    /// This server's own id for the match, opaque to Playforward. 
    /// </summary>
    public required string NativeSessionId { get; init; }

    /// <summary>What a player sees in the browser.</summary>
    public string Label { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;
    public string Map { get; init; } = string.Empty;
    public required int Capacity { get; init; }
    public bool Joinable { get; init; } = true;
}

internal sealed class RegisterSessionResponse
{
    public Guid SessionId { get; init; }
    public string NativeSessionId { get; init; } = string.Empty;
}

internal sealed class RosterPush
{
    public required IReadOnlyCollection<string> AccountIds { get; init; }
}

internal sealed class PendingActionsResponse
{
    public List<PendingAction> Actions { get; init; } = [];
}

/// <summary>
/// One command waiting to be enforced. Everything except <see cref="Token"/> is
/// for logging
/// </summary>
internal sealed class PendingAction
{
    public Guid ActionId { get; init; }
    public string NativeSessionId { get; init; } = string.Empty;
    public string Token { get; init; } = string.Empty;
}

internal sealed class BannedResponse
{
    public bool Banned { get; init; }
}

/// <summary>What <c>GET /api/v1/gameservers/me</c> says about the caller's registration.</summary>
internal sealed class RegistrationResponse
{
    public Guid ServerId { get; init; }
    public string TitleId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string PublicUrl { get; init; } = string.Empty;

    /// <summary>
    /// "community" or "verified". A community server's scores go to unranked
    /// boards and it may not change title-wide state; empty from a
    /// Playforward older than community hosting, which trusted every server.
    /// </summary>
    public string Trust { get; init; } = string.Empty;
}

internal sealed class ErrorResponse
{
    public string? Code { get; init; }
}

internal sealed class ResolvePrincipalRequest
{
    public required string AccountId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
}

internal sealed class PrincipalResponse
{
    public long Pid { get; init; }
}

internal sealed class BlobsResponse
{
    public List<BlobEntry> Blobs { get; init; } = [];
}

/// <summary>One value as Playforward serialises it; the payload travels as base64.</summary>
internal sealed class BlobEntry
{
    public string Namespace { get; init; } = string.Empty;
    public string Key { get; init; } = string.Empty;
    public byte[] Payload { get; init; } = [];
}

internal sealed class CounterResponse
{
    public long Value { get; init; }
}

internal sealed class PutScoreRequest
{
    public long Score { get; init; }
}

internal sealed class PutScoresRequest
{
    /// <summary>Board name to number. Keys go out as written: the naming policy renames properties, not dictionary keys.</summary>
    public Dictionary<string, long> Scores { get; init; } = [];
}

internal sealed class ScoresLookupRequest
{
    public List<long> Pids { get; init; } = [];
}

internal sealed class ScoresResponse
{
    public List<ScoreEntry> Scores { get; init; } = [];
}

internal sealed class ScoreEntry
{
    public long Pid { get; init; }
    public long Score { get; init; }
    public string DisplayName { get; init; } = string.Empty;
}
