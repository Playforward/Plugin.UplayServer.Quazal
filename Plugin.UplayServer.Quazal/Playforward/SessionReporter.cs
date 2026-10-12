using System.Collections.Concurrent;

namespace Plugin.UplayServer.Quazal.Playforward;

/// <summary>
/// Mirrors this server's live matches into Playforward's session directory, and
/// collects the moderation commands that come back.
/// </summary>
internal sealed class SessionReporter : IDisposable
{
    private readonly PlayforwardServerClient _client;
    private readonly ServerCommandVerifier _commands;
    private readonly SessionEnforcer _enforcer;
    private readonly PlayforwardGateway _gateway;
    private readonly PlayforwardSettings _settings;
    /// <summary>
    /// The title this reporter speaks for
    /// </summary>
    private readonly PlayforwardTitle _title;

    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _wake = new(0);
    private Task? _loop;

    /// <summary>
    /// Playforward's session id for each of this server's own session keys.
    /// </summary>
    private readonly ConcurrentDictionary<string, Guid> _registered = new(StringComparer.Ordinal);

    public SessionReporter(PlayforwardServerClient client, ServerCommandVerifier commands,
        SessionEnforcer enforcer, PlayforwardGateway gateway, PlayforwardSettings settings, PlayforwardTitle title)
    {
        _client = client;
        _commands = commands;
        _enforcer = enforcer;
        _gateway = gateway;
        _settings = settings;
        _title = title;
    }

    /// <summary>Starts the loop.</summary>
    public void Start()
    {
        _loop = Task.Run(RunAsync);
    }

    /// <summary>
    /// Asks for a pass sooner than the timer would. Called when a session or
    /// its roster changes, so a lobby view updates promptly without the loop
    /// having to run constantly.
    /// </summary>
    public void Nudge()
    {
        if (_wake.CurrentCount == 0)
            _wake.Release();
    }

    private async Task RunAsync()
    {
        var interval = TimeSpan.FromSeconds(_settings.ReportIntervalSeconds);
        Log.Information("[playforward] {Title}: session reporter running every {Interval}s", _title.Title, _settings.ReportIntervalSeconds);

        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                await PassAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                Log.Error(exception, "[playforward] session reporting pass failed; continuing");
            }

            try
            {
                await _wake.WaitAsync(interval, _stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One full pass: report everything, then collect what came back.</summary>
    private async Task PassAsync(CancellationToken cancellation)
    {
        IReadOnlyList<SessionSnapshot> sessions = GameSessionStore.Snapshot(_title.TitleId);
        var stillLive = new HashSet<string>(StringComparer.Ordinal);

        foreach (SessionSnapshot session in sessions)
        {
            string nativeId = NativeIdFor(session);
            stillLive.Add(nativeId);
            await ReportAsync(session, nativeId, cancellation).ConfigureAwait(false);
        }

        await EndVanishedSessionsAsync(stillLive, cancellation).ConfigureAwait(false);
        await CollectCommandsAsync(cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// This server's own id for a match: its Quazal (type, session) key.
    /// </summary>
    private static string NativeIdFor(SessionSnapshot session) => $"{session.TypeId}-{session.SessionId}";

    private async Task ReportAsync(SessionSnapshot session, string nativeId, CancellationToken cancellation)
    {
        if (!_registered.TryGetValue(nativeId, out Guid sessionId))
        {
            Guid? registered = await _client.RegisterSessionAsync(new SessionRegistration
            {
                NativeSessionId = nativeId,
                Label = LabelFor(session),
                Mode = ModeFor(session),
                Capacity = _title.Settings.SessionCapacity,
                Joinable = true,
            }, cancellation).ConfigureAwait(false);

            if (registered is null)
                return;

            sessionId = registered.Value;
            _registered[nativeId] = sessionId;
            Log.Information("[playforward] {Title}: registered session {Native} as {SessionId}", _title.Title, nativeId, sessionId);
        }
        var accountIds = new List<string>(session.Participants.Count);
        foreach (uint pid in session.Participants)
        {
            if (_gateway.ForPid(pid) is { } player)
                accountIds.Add(player.Identity.AccountId);
        }

        await _client.ReconcileRosterAsync(sessionId, accountIds, cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// Tells Playforward about matches this server no longer has.
    /// </summary>
    private async Task EndVanishedSessionsAsync(HashSet<string> stillLive, CancellationToken cancellation)
    {
        foreach ((string nativeId, Guid sessionId) in _registered.ToArray())
        {
            if (stillLive.Contains(nativeId))
                continue;

            if (await _client.EndSessionAsync(sessionId, cancellation).ConfigureAwait(false))
            {
                _registered.TryRemove(nativeId, out _);
                Log.Information("[playforward] session {Native} ended", nativeId);
            }
        }
    }

    /// <summary>Collects, verifies and applies whatever this server owes.</summary>
    private async Task CollectCommandsAsync(CancellationToken cancellation)
    {
        IReadOnlyList<PendingAction> pending = await _client.PendingActionsAsync(cancellation).ConfigureAwait(false);
        foreach (PendingAction action in pending)
        {
            ServerCommand? command = _commands.Verify(action.Token);
            if (command is null)
            {
                Log.Warning("[playforward] refused command {ActionId}: it did not verify", action.ActionId);
                continue;
            }

            if (!_enforcer.Apply(command))
                continue;

            await _client.AcknowledgeAsync(command.ActionId, cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// What a player sees in the browser for this match.
    /// </summary>
    private string LabelFor(SessionSnapshot session) =>
        session.Properties.TryGetValue(Fc3SessionProperties.Playlist, out int playlist)
            ? $"{_title.ServerLabel} — playlist {playlist}"
            : _title.ServerLabel;

    private static string ModeFor(SessionSnapshot session) =>
        session.Properties.TryGetValue(Fc3SessionProperties.Playlist, out int playlist)
            ? playlist.ToString()
            : string.Empty;

    public void Dispose()
    {
        _stopping.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Shutting down; a loop that ended badly has nothing left to affect.
        }
        _stopping.Dispose();
        _wake.Dispose();
    }
}

/// <summary>
/// The Far Cry 3 GameSession property ids that have been confirmed from real
/// captures. Named here rather than inline so an unconfirmed id cannot quietly
/// acquire a meaningful-sounding name.
/// </summary>
internal static class Fc3SessionProperties
{
    public const uint Playlist = 0x64;
}
