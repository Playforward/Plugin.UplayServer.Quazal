using System.Collections.Concurrent;

namespace Plugin.UplayServer.Quazal.Playforward;

/// <summary>
/// The map from a Playforward account to the PID this title knows them by.
/// </summary>
internal sealed class PlayerDirectory
{
    private sealed record Known(uint Pid, string DisplayName);

    private readonly IProgressStore _store;
    private readonly ConcurrentDictionary<string, Known> _byAccount = new(StringComparer.Ordinal);

    public PlayerDirectory(IProgressStore store) => _store = store;

    /// <summary>
    /// Returns the PID for an account, or null if Playforward could not answer
    /// and this process has never resolved the account before. The name
    /// registered with the pid is <paramref name="loginName"/>
    /// </summary>
    public uint? ResolvePid(PlayforwardIdentity identity, string? loginName = null)
    {
        uint? pid = _store.ResolvePid(identity.AccountId, string.IsNullOrEmpty(loginName) ? identity.DisplayName : loginName);
        if (pid is not null)
        {
            _byAccount[identity.AccountId] = new Known(pid.Value, identity.DisplayName);
            return pid;
        }

        if (_byAccount.TryGetValue(identity.AccountId, out Known? known))
        {
            Log.Warning("[playforward] Playforward is unreachable; {DisplayName} ({AccountId}) resolves to pid={Pid} from this session's memory",
                identity.DisplayName, identity.AccountId, known.Pid);
            return known.Pid;
        }

        Log.Error("[playforward] cannot log {DisplayName} ({AccountId}) in: Playforward is unreachable and this server has never resolved them",
            identity.DisplayName, identity.AccountId);
        return null;
    }

    /// <summary>Reverses the map for accounts resolved this session, for logging and moderation.</summary>
    public string? FindAccountId(uint pid)
    {
        foreach ((string accountId, Known known) in _byAccount)
        {
            if (known.Pid == pid)
                return accountId;
        }
        return null;
    }

    /// <summary>The PID an account resolved to this session, if it has.</summary>
    public uint? FindPid(string accountId) =>
        _byAccount.TryGetValue(accountId, out Known? known) ? known.Pid : null;

    /// <summary>
    /// Drops this process's copy of an account's PID, after Playforward has
    /// retired it. The next login asks Playforward again and gets a new one.
    /// </summary>
    public void Forget(string accountId) => _byAccount.TryRemove(accountId, out _);
}
