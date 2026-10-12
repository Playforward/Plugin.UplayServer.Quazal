using System.Collections.Concurrent;

namespace Plugin.UplayServer.Quazal.Playforward;

/// <summary>
/// Turns a verified moderation command into something the game actually does,
/// and remembers the blocks that have to outlive it.
/// </summary>
internal sealed class SessionEnforcer
{
    /// <summary>Accounts currently barred, and until when. Null means indefinite.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset?> _blocked = new(StringComparer.Ordinal);

    /// <summary>Accounts currently muted, and until when.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset?> _muted = new(StringComparer.Ordinal);

    private readonly PlayerLookup _players;

    /// <summary>
    /// Resolves an account id to the PID this server knows them by, so a
    /// command naming an account can be applied to a session that only speaks
    /// PIDs.
    /// </summary>
    internal delegate uint? PlayerLookup(string accountId);

    /// <summary>
    /// Applies a progress command to an account, reporting whether this
    /// server did its part.
    /// </summary>
    internal delegate bool ProgressAction(string accountId);

    private readonly ProgressAction? _resetProgress;
    private readonly ProgressAction? _deletePlayer;

    /// <summary>
    /// The title whose sessions a removal reaches into. A pid names a
    /// different player in another title, so "remove this pid everywhere"
    /// stops at this title's matches.
    /// </summary>
    private readonly string _titleId;

    public SessionEnforcer(string titleId, PlayerLookup players, ProgressAction? resetProgress = null, ProgressAction? deletePlayer = null)
    {
        _titleId = titleId;
        _players = players;
        _resetProgress = resetProgress;
        _deletePlayer = deletePlayer;
    }

    /// <summary>
    /// Applies one command, reporting whether it can be acknowledged.
    /// </summary>
    public bool Apply(ServerCommand command)
    {
        switch (command.Action)
        {
            case CommandActions.Ban:
                _blocked[command.TargetAccountId] = command.EnforceUntil;
                RemoveFromSessions(command);
                Log.Information("[playforward] ban applied to {Account} until {Until}: {Reason}",
                    command.TargetAccountId, Describe(command.EnforceUntil), command.Reason);
                return true;

            case CommandActions.Kick:
                RemoveFromSessions(command);
                Log.Information("[playforward] kick applied to {Account}: {Reason}",
                    command.TargetAccountId, command.Reason);
                return true;

            case CommandActions.Mute:
                _muted[command.TargetAccountId] = command.EnforceUntil;
                Log.Warning("[playforward] mute recorded for {Account} but Far Cry 3 voice does not pass through this server, so it has no in-game effect: {Reason}",
                    command.TargetAccountId, command.Reason);
                return true;

            case CommandActions.Unmute:
                _muted.TryRemove(command.TargetAccountId, out _);
                Log.Information("[playforward] unmute recorded for {Account}", command.TargetAccountId);
                return true;

            case CommandActions.ResetProgress:
                return ApplyProgress(_resetProgress, command);

            case CommandActions.DeletePlayer:
                RemoveFromSessions(command);
                return ApplyProgress(_deletePlayer, command);

            default:
                Log.Warning("[playforward] no enforcement for action {Action}", command.Action);
                return false;
        }
    }

    /// <summary>
    /// Runs a progress command. Playforward has already wiped the data where
    /// it lives
    /// </summary>
    private static bool ApplyProgress(ProgressAction? action, ServerCommand command)
    {
        if (action is null)
        {
            Log.Warning("[playforward] cannot apply {Action} to {Account}: this server has no progress database",
                command.Action, command.TargetAccountId);
            return false;
        }
        bool applied = action(command.TargetAccountId);
        if (applied)
            Log.Information("[playforward] {Action} applied to {Account}: {Reason}",
                command.Action, command.TargetAccountId, command.Reason);
        return applied;
    }

    /// <summary>
    /// Drops a player from every session this server is running.
    /// </summary>
    private void RemoveFromSessions(ServerCommand command)
    {
        uint? pid = _players(command.TargetAccountId);
        if (pid is null)
        {
            Log.Information("[playforward] {Account} is not logged in here, so there is nothing to remove them from; the block stands for when they return",
                command.TargetAccountId);
            return;
        }

        int removed = GameSessionStore.RemovePlayerEverywhere(_titleId, pid.Value);
        Log.Information("[playforward] removed pid={Pid} from {Count} session(s)", pid.Value, removed);
    }

    /// <summary>
    /// Reports whether an account is barred from joining right now, expiring
    /// the block if it has lapsed.
    /// </summary>
    public bool IsBlocked(string accountId)
    {
        if (!_blocked.TryGetValue(accountId, out DateTimeOffset? until))
            return false;

        if (until is not null && until <= DateTimeOffset.UtcNow)
        {
            _blocked.TryRemove(accountId, out _);
            return false;
        }
        return true;
    }

    /// <summary>Records a block Playforward reported at login time.</summary>
    public void Block(string accountId, DateTimeOffset? until) => _blocked[accountId] = until;

    /// <summary>Whether this account is currently muted. See the caveat in Apply.</summary>
    public bool IsMuted(string accountId) =>
        _muted.TryGetValue(accountId, out DateTimeOffset? until) &&
        (until is null || until > DateTimeOffset.UtcNow);

    private static string Describe(DateTimeOffset? until) =>
        until?.ToString("u") ?? "further notice";
}
