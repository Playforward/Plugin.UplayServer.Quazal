using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Plugin.UplayServer.Quazal.Standalone;

/// <summary>
/// One title served without Playforward: any name the game logs in with is a
/// player, given the same PID every time it comes back, with its progress in
/// a <see cref="LocalProgressStore"/>.
/// <para>
/// The name is all there is. These games send a fixed stand-in password, so
/// nothing in the login proves who typed the name; what this does guarantee
/// is that two people cannot be one player at once (see
/// <see cref="StandaloneSettings.RefuseNameInUse"/>).
/// </para>
/// </summary>
internal sealed class LocalTitle
{
    /// <summary>Longest name accepted; the games cap theirs well below this.</summary>
    internal const int MaxNameLength = 64;

    private readonly StandaloneSettings _settings;
    private readonly HashSet<string> _reserved;

    /// <summary>Which address each PID is being played from right now, as the last sweep saw it.</summary>
    private readonly ConcurrentDictionary<uint, IPAddress> _inUse = [];

    public QuazalTitle Title { get; }
    public LocalProgressStore Store { get; }
    public string TitleId => Title.TitleId;

    private LocalTitle(QuazalTitle title, LocalProgressStore store, StandaloneSettings settings)
    {
        Title = title;
        Store = store;
        _settings = settings;
        _reserved = new HashSet<string>((settings.ReservedNames ?? []).Select(Canonical), StringComparer.Ordinal);
    }

    public static LocalTitle? Start(StandaloneSettings settings, QuazalTitle title, TimeProvider? time = null)
    {
        LocalProgressStore? store = LocalProgressStore.Open(settings.DataDirectory, title.TitleId, settings.PidStart, time);
        return store is null ? null : new LocalTitle(title, store, settings);
    }

    /// <summary>
    /// The account a name logs in as from an address, allocating its PID the
    /// first time the name is seen; null, with the reason logged, when the
    /// name is refused.
    /// </summary>
    public QuazalAccount? Login(string username, IPEndPoint source)
    {
        string name = username.Trim();
        if (name.Length == 0 || name.Length > MaxNameLength || name.Any(char.IsControl))
        {
            Log.Warning("[standalone] {Title}: refused a login from {Source}: the name is empty, too long or has control characters", Title, source);
            return null;
        }
        string key = Canonical(name);
        if (key.Length == 0 || _reserved.Contains(key))
        {
            Log.Warning("[standalone] {Title}: refused a login as {Name} from {Source}: that name is reserved", Title, name, source);
            return null;
        }

        string accountId = AccountIdFor(key);
        if (_settings.RefuseNameInUse && Store.FindPid(accountId) is { } existing &&
            _inUse.TryGetValue(existing, out IPAddress? playing) && !playing.Equals(source.Address))
        {
            Log.Warning("[standalone] {Title}: refused a login as {Name} from {Source}: pid={Pid} is being played from {Other} right now",
                Title, name, source, existing, playing);
            return null;
        }

        uint? pid = Store.ResolvePid(accountId, name);
        if (pid is null)
        {
            Log.Error("[standalone] {Title}: could not give {Name} a PID (see above); refused the login", Title, name);
            return null;
        }
        _inUse[pid.Value] = source.Address;
        Log.Information("[standalone] {Title}: {Name} (pid={Pid}) logging in from {Source}", Title, name, pid.Value, source);
        return AccountFor(pid.Value, name);
    }

    /// <summary>The account behind a PID this title gave out, for the ticket and secure CONNECT checks.</summary>
    public QuazalAccount? AccountForPid(uint pid) =>
        Store.NameFor(pid) is { } name ? AccountFor(pid, name) : null;

    public bool IsPlayer(uint pid) => Store.Knows(pid);

    /// <summary>The PID a name already has here, without creating one.</summary>
    public uint? FindPid(string name) =>
        string.IsNullOrWhiteSpace(name) ? null : Store.FindPid(AccountIdFor(Canonical(name)));

    public string? NameFor(uint pid) => Store.NameFor(pid);

    /// <summary>
    /// Takes the sweep's view of who is still connected: a PID nobody is
    /// playing any more is free to log in from anywhere again.
    /// </summary>
    public void KeepAlive(IReadOnlyDictionary<uint, IPAddress> live)
    {
        foreach (uint pid in _inUse.Keys)
        {
            if (!live.ContainsKey(pid))
                _inUse.TryRemove(pid, out _);
        }
        foreach ((uint pid, IPAddress address) in live)
        {
            if (Store.Knows(pid))
                _inUse[pid] = address;
        }
    }

    private static QuazalAccount AccountFor(uint pid, string name) => new()
    {
        Username = name,
        DisplayName = name,
        // LoginEx seals its ticket with the stand-in password the game
        // itself uses, so this is never a secret anybody chose.
        Password = TicketGranting.PlayerTicketPassword,
        Pid = pid,
    };

    internal static string AccountIdFor(string canonicalName) => "name:" + canonicalName;

    /// <summary>A name as compared: Unicode-normalised, case-folded, outer whitespace dropped.</summary>
    internal static string Canonical(string name) =>
        name.Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant();
}

/// <summary>The titles served without Playforward, by slug. Static for the same reason as <see cref="Playforward.PlayforwardTitles"/>.</summary>
internal static class LocalTitles
{
    private static readonly ConcurrentDictionary<string, LocalTitle> _byTitleId = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyCollection<LocalTitle> All => _byTitleId.Values.ToArray();

    public static void Register(LocalTitle title) => _byTitleId[title.TitleId] = title;

    public static void Clear() => _byTitleId.Clear();

    public static LocalTitle? ForTitle(string titleId) => _byTitleId.GetValueOrDefault(titleId);

    public static LocalTitle? ForTitle(QuazalTitle title) => ForTitle(title.TitleId);
}

/// <summary>
/// Which store keeps a title's players: Playforward's when the title has a
/// Playforward half, else this machine's when it is served standalone. The
/// services that persist anything ask here, so they need not know which.
/// </summary>
internal static class PlayerStores
{
    /// <summary>The title's store, for title-wide values, counters and boards; null when it has none.</summary>
    public static Playforward.IProgressStore? ForTitle(string titleId) =>
        (Playforward.IProgressStore?)Playforward.PlayforwardTitles.ForTitle(titleId)?.Store
        ?? LocalTitles.ForTitle(titleId)?.Store;

    /// <summary>
    /// The store a player's progress is kept in, or null for a player whose
    /// progress lives only as long as this run (a configured account, a LAN
    /// pid assigned by address).
    /// </summary>
    public static Playforward.IProgressStore? ForPlayer(string titleId, uint pid)
    {
        if (Playforward.PlayforwardTitles.ForTitle(titleId) is { } playforward)
            return playforward.Gateway.ForPid(pid) is null ? null : playforward.Store;
        return LocalTitles.ForTitle(titleId) is { } local && local.IsPlayer(pid) ? local.Store : null;
    }

    /// <summary>Where a title's progress goes, for log lines.</summary>
    public static string Describe(string titleId) =>
        Playforward.PlayforwardTitles.ForTitle(titleId) is not null ? "Playforward" : "the local store";
}
