using System.Net;

namespace Plugin.UplayServer.Quazal.Playforward;

/// <summary>
/// The Playforward half of this server, assembled: verify a token, hand back
/// something the game can carry
/// </summary>
internal sealed class PlayforwardGateway : IDisposable
{
    /// <summary>The title this gateway logs players into.</summary>
    public string TitleId { get; }

    private readonly PlayforwardTokenVerifier _verifier;
    private readonly RedeemedTokens _redeemed;
    private readonly CorrelationStore _codes;
    private readonly LaunchStore _launches;
    private readonly PlayerDirectory _players;

    /// <summary>
    /// The blocks in force here, and how a verified moderation command is
    /// applied. Present whether or not this server reports: a block can arrive
    /// as a collected command, or as Playforward's answer at login time.
    /// </summary>
    private readonly SessionEnforcer _enforcer;

    /// <summary>
    /// The outbound half, when this server reports. Null on one that does not,
    /// where the only blocks in force are ones it was told about while running.
    /// </summary>
    private PlayforwardServerClient? _client;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Binding> _bindings =
        new(StringComparer.Ordinal);

    /// <summary>
    /// A player a login key stands for, what traffic shows they are still
    /// there, and when it last did. The player is swapped whole when a claim
    /// changes.
    /// </summary>
    /// <param name="socket">A socket binding's socket: its own traffic keeps it.</param>
    /// <param name="address">An address binding's address: any traffic from it keeps it.</param>
    private sealed class Binding(ResolvedPlayer player, DateTimeOffset now, IPEndPoint? socket = null, IPAddress? address = null)
    {
        public ResolvedPlayer Player { get; set; } = player;
        public ActivityStamp LastActive { get; } = new(now);
        private IPEndPoint? Socket { get; } = socket;
        private IPAddress? Address { get; } = address;

        public bool IsLive(IReadOnlySet<IPEndPoint> liveEndpoints, IReadOnlySet<IPAddress> liveAddresses, IReadOnlySet<uint> livePids) =>
            Socket is not null ? liveEndpoints.Contains(Socket)
            : Address is not null ? liveAddresses.Contains(Address)
            : livePids.Contains(Player.Pid);
    }

    /// <summary>This server's LAN; see <see cref="PlayforwardSettings.LanNetworks"/>.</summary>
    private readonly IPNetwork[] _lanNetworks;

    /// <summary>See <see cref="PlayforwardSettings.BindingIdleTimeoutSeconds"/>.</summary>
    private readonly TimeSpan _bindingIdleTimeout;

    private readonly TimeProvider _time;

    private PlayforwardGateway(string titleId,
        PlayforwardTokenVerifier verifier, RedeemedTokens redeemed, CorrelationStore codes, LaunchStore launches,
        PlayerDirectory players, IPNetwork[] lanNetworks, int codeTtlSeconds, int loginNameMaxLength,
        TimeSpan bindingIdleTimeout, TimeProvider time)
    {
        TitleId = titleId;
        _verifier = verifier;
        _redeemed = redeemed;
        _codes = codes;
        _launches = launches;
        _players = players;
        _lanNetworks = lanNetworks;
        CorrelationCodeTtlSeconds = codeTtlSeconds;
        LoginNameMaxLength = loginNameMaxLength;
        _bindingIdleTimeout = bindingIdleTimeout;
        _time = time;
        _enforcer = new SessionEnforcer(titleId, PidForAccount, ResetProgress, DeletePlayer);
    }

    /// <summary>
    /// Playforward wiped an account's saved progress.
    /// </summary>
    public bool ResetProgress(string accountId)
    {
        uint? pid = _players.FindPid(accountId);
        if (pid is null)
        {
            Log.Information("[playforward] progress reset for {Account}: nothing held here", accountId);
            return true;
        }
        OnlineDataStore.Forget(pid.Value);
        FutureSoldierStatistics.Forget(TitleId, pid.Value);
        Log.Information("[playforward] progress reset for {Account} (pid={Pid}): dropped this session's copy", accountId, pid);
        return true;
    }

    /// <summary>
    /// Playforward forgot an account on this title
    /// </summary>
    public bool DeletePlayer(string accountId)
    {
        uint? pid = _players.FindPid(accountId);
        if (pid is not null)
        {
            OnlineDataStore.Forget(pid.Value);
            FutureSoldierStatistics.Forget(TitleId, pid.Value);
        }
        _players.Forget(accountId);
        _launches.Forget(accountId);
        Unbind(accountId);
        Log.Information("[playforward] {Account} deleted from this title; pid={Pid} retired", accountId, pid?.ToString() ?? "none held");
        return true;
    }

    /// <summary>Drops every binding that resolves to an account.</summary>
    private void Unbind(string accountId)
    {
        foreach ((string key, Binding binding) in _bindings)
        {
            if (string.Equals(binding.Player.Identity.AccountId, accountId, StringComparison.Ordinal))
                _bindings.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Releases a player's cached progress once nothing binds them any more.
    /// A player is on one server at a time, so a binding ending is the moment
    /// this server stops being the one holding their rows.
    /// </summary>
    private void ReleaseIfUnbound(uint pid)
    {
        if (ForPid(pid) is not null)
            return;
        OnlineDataStore.Forget(pid);
        FutureSoldierStatistics.Forget(TitleId, pid);
    }

    /// <summary>Binds an address to a player; see <see cref="Bind"/>.</summary>
    private void BindAddress(IPAddress address, ResolvedPlayer player) =>
        Bind(AddressKey(address), new Binding(player, _time.GetUtcNow(), address: address));

    /// <summary>Binds a socket to a player; see <see cref="Bind"/>.</summary>
    private void BindSocket(IPEndPoint socket, ResolvedPlayer player) =>
        Bind(EndpointKey(socket), new Binding(player, _time.GetUtcNow(), socket: socket));

    /// <summary>
    /// Binds a key to a player, afresh, releasing whoever it stood for before.
    /// </summary>
    private void Bind(string key, Binding binding)
    {
        Binding? previous = _bindings.GetValueOrDefault(key);
        _bindings[key] = binding;
        if (previous is not null && previous.Player.Pid != binding.Player.Pid)
            ReleaseIfUnbound(previous.Player.Pid);
    }

    /// <summary>
    /// Binds a redeemed code, replacing any earlier code's binding from the
    /// same address: a fresh code from one machine is a new sign-in, and the
    /// one before it has stopped being that machine's player.
    /// </summary>
    private void BindCode(IPAddress address, string key, ResolvedPlayer player)
    {
        string addressPrefix = $"{CodeKeyPrefix}{address}/";
        List<ResolvedPlayer> replaced = [];
        foreach (string existing in _bindings.Keys)
        {
            if (existing != key && existing.StartsWith(addressPrefix, StringComparison.Ordinal) &&
                _bindings.TryRemove(existing, out Binding? previous))
                replaced.Add(previous.Player);
        }

        _bindings[key] = new Binding(player, _time.GetUtcNow());
        foreach (ResolvedPlayer previous in replaced)
        {
            if (previous.Pid != player.Pid)
                ReleaseIfUnbound(previous.Pid);
        }
    }

    /// <summary>
    /// Replaces the identity behind every binding of an account, keeping the PID.
    /// </summary>
    private void Rebind(ResolvedPlayer player)
    {
        foreach (Binding binding in _bindings.Values)
        {
            if (string.Equals(binding.Player.Identity.AccountId, player.Identity.AccountId, StringComparison.Ordinal))
                binding.Player = player;
        }
    }

    /// <summary>
    /// Keeps alive what the players this server can hear right now depend on,
    /// and ends every binding and launch that nobody has shown a sign of life
    /// for. Called by the responder on its sweep.
    /// </summary>
    /// <param name="liveEndpoints">The sockets this title's games are talking from.</param>
    /// <param name="livePids">The players logged in on those games.</param>
    public void KeepAlive(IReadOnlySet<IPEndPoint> liveEndpoints, IReadOnlySet<uint> livePids)
    {
        DateTimeOffset now = _time.GetUtcNow();
        HashSet<IPAddress> liveAddresses = liveEndpoints.Select(endpoint => endpoint.Address).ToHashSet();

        List<ResolvedPlayer> ended = [];
        foreach ((string key, Binding binding) in _bindings)
        {
            if (binding.IsLive(liveEndpoints, liveAddresses, livePids))
                binding.LastActive.Touch(now);
            else if (binding.LastActive.IdleLongerThan(_bindingIdleTimeout, now) &&
                     _bindings.TryRemove(new KeyValuePair<string, Binding>(key, binding)))
                ended.Add(binding.Player);
        }

        foreach (ResolvedPlayer player in ended)
        {
            Log.Information("[playforward] {DisplayName} (pid={Pid}) has been quiet for {Timeout}; their login binding has ended",
                player.Identity.DisplayName, player.Pid, _bindingIdleTimeout);
            ReleaseIfUnbound(player.Pid);
        }

        _launches.KeepAlive(livePids);
    }

    private static string AddressKey(IPAddress address) => address.ToString();

    private const string CodeKeyPrefix = "code:";

    /// <summary>
    /// A redeemed code's binding: the address and the code together, so a
    /// re-login has to present the code it redeemed, from where it redeemed it.
    /// </summary>
    private static string CodeKey(IPAddress address, string code) => $"{CodeKeyPrefix}{address}/{code}";

    /// <summary>
    /// Whether an address is on this server's LAN
    /// </summary>
    public bool IsOnLan(IPAddress address)
    {
        IPAddress candidate = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return _lanNetworks.Any(network => network.Contains(candidate));
    }

    /// <summary>
    /// "ip:port" ("[ip]:port" for IPv6), which can never collide with a bare
    /// address key.
    /// </summary>
    private static string EndpointKey(IPEndPoint endpoint) => endpoint.ToString();

    /// <summary>
    /// How many characters of a login name this title keeps, and so which of an
    /// account's name tiers it is known by here.
    /// </summary>
    public int LoginNameMaxLength { get; }

    /// <summary>
    /// The name this title will actually show a player by: the largest tier
    /// that fits its login field.
    /// </summary>
    public string LoginNameFor(PlayforwardIdentity identity) => identity.NameForCap(LoginNameMaxLength);

    /// <summary>The token verifier, so the reporter can share its key.</summary>
    public PlayforwardTokenVerifier Verifier => _verifier;

    /// <summary>The blocks in force here, and how commands are applied.</summary>
    public SessionEnforcer Enforcer => _enforcer;

    /// <summary>
    /// Gives the gateway the outbound client, so a login can ask Playforward
    /// whether an account is barred rather than relying only on commands this
    /// server happened to be running to collect.
    /// </summary>
    public void AttachClient(PlayforwardServerClient client) => _client = client;

    /// <summary>
    /// The PID this server knows an account by, or null when they are not
    /// logged in here.
    /// </summary>
    private uint? PidForAccount(string accountId)
    {
        foreach (Binding binding in _bindings.Values)
        {
            if (string.Equals(binding.Player.Identity.AccountId, accountId, StringComparison.Ordinal))
                return binding.Player.Pid;
        }
        return null;
    }

    /// <summary>
    /// Whether an account is barred from this server right now.
    /// </summary>
    public bool IsBanned(string accountId)
    {
        if (_enforcer.IsBlocked(accountId))
            return true;

        if (_client is null)
            return false;

        bool? banned = _client.IsBannedAsync(accountId, CancellationToken.None)
            .GetAwaiter().GetResult();
        if (banned == true)
        {
            _enforcer.Block(accountId, until: null);
            return true;
        }
        return false;
    }

    /// <summary>How long an issued correlation code stays redeemable, for the
    /// client to size its own retry against.</summary>
    public int CorrelationCodeTtlSeconds { get; }

    /// <summary>
    /// Builds the gateway from configuration, or returns null with the reason
    /// already logged. A null gateway means Playforward login is off and the
    /// server behaves exactly as it did before -- which is what a private LAN
    /// server with no accounts service wants.
    /// </summary>
    /// <param name="settings">The half of the Playforward configuration shared by every title.</param>
    /// <param name="titleId">The title this gateway logs players into.</param>
    /// <param name="audience">The <c>aud</c> a token for that title must carry.</param>
    /// <param name="loginNameMaxLength">How many characters of a login name the title keeps.</param>
    /// <param name="store">
    /// Playforward's player-data store for the title, where the account-to-PID
    /// map and the online data live so that every server of this title sees
    /// the same player. Required: a PID that differed between servers or
    /// restarts would lose the player their stats.
    /// </param>
    /// <param name="time">The clock bindings and launches expire by; the system's unless a test supplies one.</param>
    public static PlayforwardGateway? Start(PlayforwardSettings settings, string titleId, string audience,
        int loginNameMaxLength, IProgressStore store, TimeProvider? time = null)
    {
        time ??= TimeProvider.System;
        string[] errors = settings.Validate().ToArray();
        if (errors.Length > 0)
        {
            foreach (string error in errors)
                Log.Error("[playforward] {Error}", error);
            return null;
        }

        PlayforwardTokenVerifier? verifier = PlayforwardTokenVerifier.Create(settings, audience);
        if (verifier is null)
            return null;

        var players = new PlayerDirectory(store);
        var redeemed = new RedeemedTokens();

        return new PlayforwardGateway(
            titleId,
            verifier,
            redeemed,
            new CorrelationStore(TimeSpan.FromSeconds(settings.CorrelationCodeTtlSeconds), redeemed),
            new LaunchStore(TimeSpan.FromSeconds(settings.LaunchTtlSeconds),
                TimeSpan.FromSeconds(settings.LaunchIdleTimeoutSeconds), time),
            players,
            (settings.LanNetworks ?? []).Select(IPNetwork.Parse).ToArray(),
            settings.CorrelationCodeTtlSeconds,
            loginNameMaxLength,
            TimeSpan.FromSeconds(settings.BindingIdleTimeoutSeconds),
            time);
    }

    /// <summary>
    /// Verifies a token and returns the correlation code that stands in for it,
    /// or null if the token was not acceptable.
    /// </summary>
    public string? PreRegister(string token)
    {
        PlayforwardIdentity? identity = _verifier.Verify(token);
        return identity is null ? null : _codes.Register(identity);
    }

    /// <summary>
    /// Verifies a token and binds the account it names to a source address, so
    /// a game logging in from that address is recognised as this player without
    /// the correlation code ever having to travel through the game.
    /// </summary>
    public ResolvedPlayer? PreBind(string token, IPAddress address)
    {
        ResolvedPlayer? player = Redeem(token);
        if (player is null)
            return null;
        if (IsOnLan(address))
            BindAddress(address, player);
        else
            Log.Information("[playforward] not binding {Address} to {AccountId}: address login is only allowed from LanNetworks, so this game must prove a launch",
                address, player.Identity.AccountId);

        Rebind(player);
        _launches.Refresh(player);

        Log.Information("[playforward] pre-bound {DisplayName} ({AccountId}) to {Address} as pid={Pid}, party {PartyRef}",
            player.Identity.DisplayName, player.Identity.AccountId, address, player.Pid,
            string.IsNullOrEmpty(player.Identity.PartyRef) ? "none" : player.Identity.PartyRef);
        return player;
    }

    /// <summary>
    /// Verifies a token and issues a launch for the player it names: a public
    /// id and a secret key, which the desktop client hands the game's shim so
    /// it can prove, on the game's own socket, that it is this player's game.
    /// </summary>
    public IssuedLaunch? PreLaunch(string token)
    {
        ResolvedPlayer? player = Redeem(token);
        if (player is null)
            return null;

        IssuedLaunch launch = _launches.Issue(player);
        _launches.Refresh(player);
        Rebind(player);

        Log.Information("[playforward] issued launch {LaunchId} to {DisplayName} ({AccountId}) as pid={Pid}, party {PartyRef}",
            launch.Id, player.Identity.DisplayName, player.Identity.AccountId, player.Pid,
            string.IsNullOrEmpty(player.Identity.PartyRef) ? "none" : player.Identity.PartyRef);
        return launch;
    }

    /// <summary>
    /// Handles a datagram from a game's shim, if that is what it is: answers
    /// a HELLO with a challenge, and a good PROOF by binding the socket it came
    /// from and answering BOUND. Returns the reply to send, or null for no
    /// reply 
    /// </summary>
    /// <param name="datagram">The raw UDP payload, before any PRUDP parsing.</param>
    /// <param name="source">Where it came from: the socket a successful proof binds.</param>
    /// <param name="recognised">Whether the datagram was one of the shim's at all, so the caller can stop looking.</param>
    public byte[]? HandleLaunchDatagram(ReadOnlySpan<byte> datagram, IPEndPoint source, out bool recognised)
    {
        recognised = LaunchEnvelope.TryParse(datagram, out LaunchEnvelope.Message message);
        if (!recognised)
            return null;

        switch (message.Kind)
        {
            case LaunchEnvelope.Kind.Hello:
                byte[]? challenge = _launches.Challenge(message.LaunchId, source);
                if (challenge is null)
                    Log.Debug("[playforward] ignored a HELLO from {Source} for a launch this server has not issued", source);
                return challenge;

            case LaunchEnvelope.Kind.Proof:
                if (_launches.Prove(message.LaunchId, source, message.Body) is not var (player, bound))
                    return null;

                BindSocket(source, player);
                Log.Information("[playforward] {DisplayName} ({AccountId}) proved their launch from {Source}; bound as pid={Pid}, party {PartyRef}",
                    player.Identity.DisplayName, player.Identity.AccountId, source, player.Pid,
                    string.IsNullOrEmpty(player.Identity.PartyRef) ? "none" : player.Identity.PartyRef);
                return bound;

            default:
                return null;
        }
    }

    /// <summary>
    /// Verifies a token, refuses one already spent, and resolves the player's
    /// PID. Shared by everything a token can be presented to.
    /// </summary>
    private ResolvedPlayer? Redeem(string token)
    {
        PlayforwardIdentity? identity = _verifier.Verify(token);
        if (identity is null || !_redeemed.TryRedeem(identity))
            return null;

        uint? pid = _players.ResolvePid(identity, LoginNameFor(identity));
        return pid is null ? null : new ResolvedPlayer(identity, pid.Value);
    }

    /// <summary>
    /// The player a key stands for, or null
    /// </summary>
    private ResolvedPlayer? Bound(string key) =>
        _bindings.TryGetValue(key, out Binding? binding) &&
        !binding.LastActive.IdleLongerThan(_bindingIdleTimeout, _time.GetUtcNow())
            ? binding.Player
            : null;

    /// <summary>Returns the account bound to an address, or null.</summary>
    public ResolvedPlayer? ForAddress(IPAddress address) => Bound(AddressKey(address));

    /// <summary>
    /// Returns the account a login from this socket is, or null
    /// </summary>
    public ResolvedPlayer? ForEndpoint(IPEndPoint endpoint) =>
        Bound(EndpointKey(endpoint)) ?? ForAddress(endpoint.Address);

    /// <summary>
    /// The addresses and sockets that currently have a binding, for a refusal
    /// to name.
    /// </summary>
    public IReadOnlyCollection<string> BoundAddresses() =>
        _bindings.Keys.Where(key => !key.StartsWith(CodeKeyPrefix, StringComparison.Ordinal)).ToArray();

    /// <summary>
    /// Returns a bound player by their assigned PID, or null.
    /// </summary>
    public ResolvedPlayer? ForPid(uint pid)
    {
        DateTimeOffset now = _time.GetUtcNow();
        foreach (Binding binding in _bindings.Values)
        {
            if (binding.Player.Pid == pid && !binding.LastActive.IdleLongerThan(_bindingIdleTimeout, now))
                return binding.Player;
        }
        return null;
    }

    /// <summary>
    /// Resolves a correlation code to the player it stands for, allocating this
    /// server's PID for them if they have never been seen here before.
    /// </summary>
    public ResolvedPlayer? Resolve(string code)
    {
        PlayforwardIdentity? identity = _codes.Redeem(code);
        if (identity is null)
            return null;

        uint? pid = _players.ResolvePid(identity, LoginNameFor(identity));
        if (pid is null)
            return null;
        Log.Information("[playforward] {DisplayName} ({AccountId}) logging in as pid={Pid}",
            identity.DisplayName, identity.AccountId, pid);
        return new ResolvedPlayer(identity, pid.Value);
    }

    /// <summary>
    /// Resolves a login by its correlation-code username and source address,
    /// binding the two on first success and reusing the binding afterwards.
    /// </summary>
    public ResolvedPlayer? ResolveForLogin(string code, IPAddress address)
    {
        string key = CodeKey(address, code);

        if (Resolve(code) is { } resolved)
        {
            BindCode(address, key, resolved);
            return resolved;
        }

        if (Bound(key) is { } bound)
        {
            Log.Information("[playforward] reusing the code's session binding for {Address}: {DisplayName} pid={Pid}",
                address, bound.Identity.DisplayName, bound.Pid);
            return bound;
        }

        return null;
    }

    /// <summary>Reports whether a username could be a correlation code.</summary>
    public static bool LooksLikeCode(string? username) => CorrelationStore.LooksLikeCode(username);

    public void Dispose()
    {
        _bindings.Clear();
    }
}

/// <summary>A verified player and the PID this server knows them by.</summary>
internal sealed record ResolvedPlayer(PlayforwardIdentity Identity, uint Pid);
