using Plugin.UplayServer.Quazal.Enums;

namespace Plugin.UplayServer.Quazal;

/// <summary>
/// A local RendezVous principal. The password is server-side configuration;
/// the server never reads a client's upc.json.
/// </summary>
public sealed class QuazalAccount
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public uint Pid { get; set; }

    /// <summary>
    /// Optional pre-derived 16-byte Kerberos source key for non-LoginEx
    /// accounts. FC3 LoginEx always uses the standard KDF with the client's
    /// fixed UbiDummyPwd credential and ignores this compatibility override.
    /// </summary>
    public string? SourceKeyHex { get; set; }

    /// <summary>
    /// The player's Playforward display name, when this account was resolved
    /// from a correlation code. Null for ordinary configured accounts. Carried
    /// so a service that surfaces player names can use the real one rather than
    /// the correlation code that arrived as the login username.
    /// </summary>
    public string? DisplayName { get; set; }

    internal byte[]? ReadSourceKey() => string.IsNullOrEmpty(SourceKeyHex)
        ? null
        : Convert.FromHexString(SourceKeyHex);
}

/// <summary>
/// What the RendezVous transport needs to know about one title beyond what
/// the host's catalog (Configs/Titles.json) already says. Keyed by the
/// title's slug in <see cref="QuazalSettings.Titles"/>; listing a title here
/// is what makes this plugin serve it on the ports the catalog gives it.
/// </summary>
public sealed class QuazalTitleSettings
{
    /// <summary>
    /// UDP port advertised as the secure server in this title's Login
    /// response. A Quazal deployment splits authentication and secure
    /// RendezVous across two endpoints, so this must differ from the port the
    /// client authenticated on
    /// </summary>
    public int SecureServerPort { get; set; }

    /// <summary>
    /// This title's Playforward identity: its own server secret, lobby size
    /// and name width. Consulted only when <see cref="QuazalSettings.Playforward"/>
    /// is enabled; a title with an empty secret then refuses to start rather
    /// than reporting under another title's.
    /// </summary>
    public Playforward.PlayforwardTitleSettings Playforward { get; set; } = new();
}

/// <summary>
/// The RMC service vocabularies this plugin implements. Two titles in one
/// family speak the same services with the same ids; a title in neither
/// needs a table of its own before it can be served. Which family a title
/// is in is catalog data (<c>TitleDescriptor.ProtocolFamily</c>, recorded
/// in the accounts service's admin panel), parsed by name here.
/// </summary>
public enum QuazalProtocolFamily
{
    /// <summary>Not chosen; the title is refused at start rather than guessed at.</summary>
    Unspecified = 0,
    /// <summary>Far Cry 3's pre-Overlord RendezVous services. Catalog slug <c>farcry3</c>.</summary>
    FarCry3,
    /// <summary>The Overlord-era services as Splinter Cell Blacklist speaks them (OSDK 1.4, Overlord 0.8). Catalog slug <c>overlord</c>.</summary>
    Overlord,
    /// <summary>
    /// Ghost Recon Future Soldier: OSDK 1.3.14 with an earlier Overlord and
    /// its own "GR5" extra protocols (ExtendedTracking, GameStorageFileVersion,
    /// GR5Award, MapList, Misc). It shares Blacklist's era, not its ids, so it
    /// is not answered from the Overlord table. Catalog slug <c>futuresoldier</c>.
    /// </summary>
    FutureSoldier,
}

/// <summary>Local RendezVous listener settings shared by every title.</summary>
public sealed class QuazalSettings
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, QuazalAccount> _playerAccountsByAddress = [];
    private int _nextDynamicPlayerOffset;
    /// <summary>
    /// Local principals accepted by Ticket Granting protocol 10. Tracking is
    /// retained as the default telemetry account; player accounts should be
    /// added explicitly with a distinct PID.
    /// </summary>
    public List<QuazalAccount> Accounts { get; set; } =
    [
        new QuazalAccount
        {
            Username = TicketGranting.TrackingUser,
            Password = TicketGranting.TrackingPassword,
            Pid = TicketGranting.TrackingPid,
        },
    ];

    /// <summary>
    /// FC3's current Uplay path commonly logs every local installation in as
    /// the same configured username (normally "user").  For LAN multiplayer,
    /// make each source IPv4 address a separate principal while retaining that
    /// client's recovered credential key material.
    /// </summary>
    public bool AssignPlayerPidByAddress { get; set; } = true;

    /// <summary>
    /// How this server authenticates players against Playforward. Disabled by
    /// default, so a private LAN server keeps working with local accounts and
    /// no accounts service at all.
    /// </summary>
    public Playforward.PlayforwardSettings Playforward { get; set; } = new();

    /// <summary>First PID allocated to the LAN player identities.</summary>
    public uint DynamicPlayerPidStart { get; set; } = 2000;

    /// <summary>
    /// Milliseconds to hold every reply to a DATA packet (RMC calls) before
    /// sending it; 0 sends at once.
    /// </summary>
    public int ReplyDelayMilliseconds { get; set; }

    /// <summary>
    /// Data ids to advertise in the protocol 106 method 4 (FetchPackages) reply.
    ///
    /// Valid ids come from the name-pointer table in .data, read out of both
    /// shipped builds (2018 at 0x11717a68, 2022 at 0x12186b00 - identical):
    ///   0x00 PlayerClasses      0x08 StatisticWeapon    0x10 WeeklyChallenge
    ///   0x01 Playlists          0x09 WallMessages       0x11 Medals
    ///   0x02 PlayerUnlocks      0x0A WallMessageLikes   0x12 OnlinePlayerProfile
    ///   0x03 PlayerGroups       0x0B PrincipalInfo      0x13 MpNotifications
    ///   0x04 Groups             0x0C PrincipalLocation  0x14 TitleStorage
    ///   0x05 StatisticMatch     0x0D DailyChallenge     0x15 UserStorage
    ///   0x06 StatisticPlayer    0x0E AllPlayerData
    ///   0x07 StatisticTimeSlice 0x0F StatisticChallenge
    /// </summary>
    public List<int> FetchPackageDataIds { get; set; } = [];

    /// <summary>
    /// Diagnostic: drop every protocol 106 (online data) request instead of
    /// answering it, so the client sees the online-data service as absent.
    /// </summary>
    public bool SuppressOnlineData { get; set; }

    /// <summary>
    /// FC3 reference-mode diagnostic: accept the UDP listener only for
    /// stateless NAT-echo/address discovery, then silently drop every PRUDP
    /// session packet (SYN, CONNECT, DATA, and so on).  This mirrors the
    /// current retail path captured from the Deck, where FC3 performs its
    /// NAT probe but never opens a RendezVous session.
    /// </summary>
    public bool BlockPrudpSessions { get; set; }

    /// <summary>
    /// Answer FC3's IPToLocation (45/1) with a country tuple rather than
    /// Core::NotImplemented. The client accepted the tuple in a real run and
    /// stored its PrincipalLocation row from it; off returns the refusal it
    /// also tolerates. See IpToLocationService.
    /// </summary>
    public bool IpToLocationEnabled { get; set; } = true;

    /// <summary>
    /// Diagnostic: fills the leaderboard rows' unproven columns with
    /// distinctive constants 
    /// </summary>
    public bool LeaderboardProbe { get; set; }

    /// <summary>
    /// Build a match's statistics (kills, deaths, the match, weapon usage,
    /// the match-history row) from the telemetry the game reports
    /// (<c>FPSCLIENT_STOP</c>) whenever the host has not uploaded them
    /// itself. The host's own upload always wins
    /// </summary>
    public bool StatisticsFromTracking { get; set; } = true;

    /// <summary>
    /// The maps Ghost Recon Future Soldier's MapList service (121) lists
    /// when a quick-match host asks GetMapList for its playlist
    /// </summary>
    public List<uint> FutureSoldierQuickMatchMaps { get; set; } =
    [
        0x0C25F089, 0x0C283ABE, 0x222A8978, 0x3220F4DE, 0x3220F9DB, 0x3220FC58, 0x322104AF, 0x32210604,
        0xA920C9F0, 0xA9211863, 0xA92118E3, 0xA9211903, 0xA921192E, 0xA9260CFE, 0xAB31491E, 0xBF0D0B78,
    ];

    /// <summary>
    /// Ghost Recon Future Soldier's DLC catalogue
    /// </summary>
    public string? FutureSoldierDlcBigPath { get; set; }

    /// <summary>
    /// The host the client is told to fetch the catalogue from
    /// </summary>
    public string? FutureSoldierTitleStorageHost { get; set; }


    /// <summary>
    /// Checksum arithmetic for peers whose SYN identifies the one-byte NEX V0
    /// trailing checksum (Far Cry 3).
    /// </summary>
    public QuazalChecksumAlgorithm NexChecksumAlgorithm { get; set; }
        = QuazalChecksumAlgorithm.NexByteFold;

    /// <summary>
    /// The titles this plugin serves, keyed by slug. Which ports each one is
    /// reached on comes from the host's catalog (Configs/Titles.json); the
    /// port a packet arrives on is how it is resolved to a title before any
    /// of it is parsed, so a title with no ports there is not served here.
    /// Adding a game is a catalog entry and an entry here, not a fork of
    /// this plugin.
    /// </summary>
    public Dictionary<string, QuazalTitleSettings> Titles { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["farcry3"] = new()
        {
            SecureServerPort = 6571,
            Playforward = new() { SessionCapacity = 14, LoginNameMaxLength = 16 },
        },
        ["splinter-cell-blacklist"] = new()
        {
            SecureServerPort = 6573,
            Playforward = new() { SessionCapacity = 8, LoginNameMaxLength = 16 },
        },
    };

    /// <summary>
    /// Superseded by the catalog's per-title ports. Read only to migrate a
    /// config that predates <see cref="Titles"/>: the title owning these
    /// ports in the catalog gets an entry synthesised from them.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int[]? Ports { get; set; }

    /// <summary>
    /// Sends the transport-level SYN/CONNECT/PING/DATA acknowledgements. This
    /// intentionally stops before encrypted LoginProtocol/RMC handling.
    /// </summary>
    public bool EnableTransportAcks { get; set; } = true;

    /// <summary>
    /// Experimental generic secure-connect probe. On CONNECT, include a
    /// freshly generated P-256 ECK1 public blob in the PRUDP ACK. This
    /// advances CNG-backed clients to their key-import/validation path without
    /// pretending that LoginProtocol/RMC is implemented.
    /// </summary>
    public bool SendSecureConnectProbe { get; set; }

    /// <summary>
    /// Reply to an ECK1 CONNECT with the authenticated P-256 envelope,
    /// when <see cref="RendezVousIdentityFile"/> supplies the same identity
    /// which REST advertised to the game.
    /// </summary>
    public bool EnableAuthenticatedSecureConnect { get; set; } = true;

    /// <summary>
    /// Superseded by each title's <c>RendezVousIdentityFile</c> in the
    /// catalog, where the REST half reads the same value -- the two must
    /// agree on the identity, so it lives once, on the title. Read only to
    /// warn that it is no longer consulted.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? RendezVousIdentityFile { get; set; }

    /// <summary>
    /// Superseded by <see cref="QuazalTitleSettings.SecureServerPort"/>. Read
    /// only to migrate a config that predates <see cref="Titles"/>.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? SecureServerPort { get; set; }

    /// <summary>
    /// The most PRUDP sessions held at once. A game holds up to three (the
    /// authentication session, then the player's and the telemetry account's
    /// on the secure port).
    /// </summary>
    public int MaxPeers { get; set; } = 8;

    /// <summary>
    /// How long a session that has sent a SYN may go without completing its
    /// CONNECT before it is forgotten.
    /// </summary>
    public int PeerHandshakeTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// How long a connected session may be silent before it is forgotten.
    /// </summary>
    public int PeerIdleTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// The most sessions one source address may hold that have not yet logged
    /// in or presented a ticket.
    /// </summary>
    public int MaxUnprovenPeersPerAddress { get; set; } = 8;

    /// <summary>How often idle sessions are forgotten.</summary>
    public int PeerSweepIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// Require every packet after the SYN to echo the connection signature the
    /// server issued in its SYN-ACK, and drop one that does not.
    /// </summary>
    public bool RequireConnectionSignature { get; set; } = true;

    /// <summary>
    /// Address embedded in named-RMC responses that hand the client a follow-up
    /// server to connect to (e.g. LoginWithToken_V1's and Register_V1's result
    /// both end in a prudp:/address=...;port=... URL
    /// </summary>
    public string PublicHost { get; set; } = "127.0.0.1";

    /// <summary>
    /// The account a plain Login (Ticket Granting method 1, not LoginEx) may
    /// sign in as. On a Playforward title that is the telemetry account and
    /// nothing else; see <see cref="IsLocalAccountAllowed"/>.
    /// </summary>
    internal QuazalAccount? FindLoginAccount(QuazalTitle title, string username)
    {
        QuazalAccount? account = FindAccount(username);
        if (account is null || IsLocalAccountAllowed(title, account.Pid))
            return account;

        Serilog.Log.Warning("[playforward] {Title}: refused a Login as local account {User}: only the telemetry account signs in outside Playforward on this title",
            title, username);
        return null;
    }

    /// <summary>
    /// Whether a configured, non-Playforward account may be used on a title.
    /// Always without Playforward
    /// <see cref="IsTelemetryProtocol"/> then confines to telemetry.
    /// </summary>
    internal static bool IsLocalAccountAllowed(QuazalTitle title, uint pid) =>
        !UsesPlayforward(title) || pid == TicketGranting.TrackingPid;

    /// <summary>Whether a title logs its players in through Playforward.</summary>
    internal static bool UsesPlayforward(QuazalTitle title) => PlayforwardFor(title) is not null;

    /// <summary>
    /// Whether the telemetry account may call a protocol on a Playforward
    /// title: signing in, registering its secure connection, and telemetry.
    /// </summary>
    internal static bool IsTelemetryProtocol(int serviceId) =>
        serviceId is TicketGranting.ProtocolId or SecureConnection.ProtocolId or TrackingProtocol.ProtocolId;

    /// <summary>
    /// Whether a call must be refused because it is the telemetry account, on
    /// a Playforward title, reaching past telemetry.
    /// </summary>
    /// <param name="title">The title the connection is for.</param>
    /// <param name="authenticatedPid">Who the connection proved it is, if anyone yet.</param>
    /// <param name="serviceId">The protocol called.</param>
    internal static bool IsConfinedToTelemetry(QuazalTitle title, uint? authenticatedPid, int serviceId) =>
        authenticatedPid == TicketGranting.TrackingPid && UsesPlayforward(title) && !IsTelemetryProtocol(serviceId);

    internal QuazalAccount? FindAccount(string username) =>
        (Accounts ?? []).FirstOrDefault(account =>
            string.Equals(account.Username, username, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether a PID is a Playforward principal, whose online data is kept in
    /// Playforward's player-data store between sessions.
    /// </summary>
    internal static bool IsPlayforwardPid(QuazalTitle title, uint pid) =>
        GatewayFor(title)?.ForPid(pid) is not null;

    /// <summary>
    /// The title's Playforward half, or null when it has none. Every question
    /// about a pid is asked of one title, because Playforward allocates pids
    /// per title from the same starting number.
    /// </summary>
    private static Plugin.UplayServer.Quazal.Playforward.PlayforwardTitle? PlayforwardFor(QuazalTitle title) =>
        Plugin.UplayServer.Quazal.Playforward.PlayforwardTitles.ForTitle(title);

    private static Plugin.UplayServer.Quazal.Playforward.PlayforwardGateway? GatewayFor(QuazalTitle title) =>
        PlayforwardFor(title)?.Gateway;

    /// <summary>
    /// The name a screen should label a pid with: the name this title shows a
    /// Playforward player as (the same one login enforces), a configured
    /// account's username
    /// </summary>
    internal string NameForPid(QuazalTitle title, uint pid)
    {
        if (GatewayFor(title) is { } gateway && gateway.ForPid(pid) is { } bound)
            return gateway.LoginNameFor(bound.Identity);
        return FindAccountByPid(title, pid)?.Username
            ?? Plugin.UplayServer.Quazal.Playforward.PlayerNames.For(title.TitleId, pid)
            ?? pid.ToString();
    }

    internal QuazalAccount? FindAccountByPid(QuazalTitle title, uint pid)
    {
        QuazalAccount? configured =
            (Accounts ?? []).FirstOrDefault(account => account.Pid == pid) ??
            _playerAccountsByAddress.Values.FirstOrDefault(account => account.Pid == pid);
        if (configured is not null && IsLocalAccountAllowed(title, configured.Pid))
            return configured;

        if (PlayforwardFor(title) is { } playforward && playforward.Gateway.ForPid(pid) is { } bound)
            return PlayforwardAccountFor(playforward, bound);

        return null;
    }

    /// <summary>
    /// Returns the configured account unchanged unless this is a player
    /// LoginEx with LAN identity allocation enabled. The derived account uses
    /// the base account's password/source key, which is essential because the
    /// client must be able to validate every Kerberos ticket it receives.
    /// </summary>
    internal QuazalAccount? FindPlayerAccount(QuazalTitle title, string username, System.Net.IPEndPoint source)
    {
        System.Net.IPAddress address = source.Address;
        if (PlayforwardFor(title) is { } playforward)
        {
            Plugin.UplayServer.Quazal.Playforward.PlayforwardGateway gateway = playforward.Gateway;
            if (Plugin.UplayServer.Quazal.Playforward.PlayforwardGateway.LooksLikeCode(username))
                return FindPlayforwardAccount(playforward, username, address);

            if (gateway.ForEndpoint(source) is { } bound)
            {
                int cap = gateway.LoginNameMaxLength;
                string expected = bound.Identity.NameForCap(cap);
                if (Playforward.EnforceLoginName && LoginNameKey(username, cap) != LoginNameKey(expected, cap))
                {
                    Serilog.Log.Warning(
                        "[playforward] {Title}: refused a pre-bound login: claimed name {Claimed} does not match the account's name {Expected} for this title's {Cap}-char field",
                        title, username, expected, cap);
                    return null;
                }
                return PlayforwardAccountFor(playforward, bound);
            }

            Serilog.Log.Warning("[playforward] {Title}: refused a player login as {User} from {Source}: not a Playforward player, and local accounts are off for this title",
                title, username, source);
            return null;
        }

        QuazalAccount? baseAccount = FindAccount(username);
        if (baseAccount is null || !AssignPlayerPidByAddress)
            return baseAccount;

        string addressKey = address.ToString();
        return _playerAccountsByAddress.GetOrAdd(addressKey, key =>
        {
            uint pid = checked(DynamicPlayerPidStart +
                (uint)(Interlocked.Increment(ref _nextDynamicPlayerOffset) - 1));
            var account = new QuazalAccount
            {
                Username = baseAccount.Username,
                Password = baseAccount.Password,
                Pid = pid,
                SourceKeyHex = baseAccount.SourceKeyHex,
            };
            Serilog.Log.Information("[quazal] assigned LAN player {Address} to pid={Pid} (login={Username})",
                key, pid, username);
            return account;
        });
    }

    /// <summary>
    /// Resolves a correlation code to the account the player logs in as.
    /// </summary>
    private static string Canonical(string name)
    {
        Span<char> buffer = name.Length <= 64 ? stackalloc char[name.Length] : new char[name.Length];
        int length = 0;
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c))
                buffer[length++] = char.ToLowerInvariant(c);
        }
        return new string(buffer[..length]);
    }

    /// <summary>
    /// The comparison key for a login name: the game keeps only the first
    /// LoginNameMaxLength characters of the raw username, so both the presented
    /// name and the display name are truncated the same way before being folded
    /// to a canonical key. Without matching the game's truncation, an account
    /// whose display name is longer than the cap would never match what the
    /// game actually sends.
    /// </summary>
    private static string LoginNameKey(string name, int cap)
    {
        string capped = name.Length > cap ? name[..cap] : name;
        return Canonical(capped);
    }

    private QuazalAccount? FindPlayforwardAccount(
        Plugin.UplayServer.Quazal.Playforward.PlayforwardTitle playforward, string code, System.Net.IPAddress address)
    {
        var player = playforward.Gateway.ResolveForLogin(code, address);
        if (player is null)
        {
            Serilog.Log.Warning("[playforward] {Title}: refused a login whose correlation code was unknown, expired or already used, from an address with no prior session",
                playforward.Title);
            return null;
        }
        return PlayforwardAccountFor(playforward, player);
    }

    /// <summary>
    /// Builds the login account for a resolved Playforward player: the title's
    /// template account's credentials with the player's own PID and display
    /// name.
    /// </summary>
    private QuazalAccount? PlayforwardAccountFor(
        Plugin.UplayServer.Quazal.Playforward.PlayforwardTitle playforward,
        Plugin.UplayServer.Quazal.Playforward.ResolvedPlayer player)
    {
        if (Playforward.EnforceBans && playforward.Gateway.IsBanned(player.Identity.AccountId))
        {
            Serilog.Log.Warning("[playforward] {Title}: refused a login from {DisplayName} ({AccountId}): barred from this server",
                playforward.Title, player.Identity.DisplayName, player.Identity.AccountId);
            return null;
        }

        string templateName = playforward.Settings.TemplateAccountUsername;
        QuazalAccount? template = FindAccount(templateName);
        if (template is null)
        {
            Serilog.Log.Error("[playforward] Titles['{TitleId}'].Playforward.TemplateAccountUsername '{Username}' is not in Accounts, so there are no credentials to log {DisplayName} in with",
                playforward.TitleId, templateName, player.Identity.DisplayName);
            return null;
        }

        return new QuazalAccount
        {
            DisplayName = player.Identity.DisplayName,
            Username = template.Username,
            Password = template.Password,
            Pid = player.Pid,
            SourceKeyHex = template.SourceKeyHex,
        };
    }

    internal IEnumerable<string> ValidateAccounts()
    {
        if (Accounts is null || Accounts.Count == 0)
        {
            yield return "Accounts must contain at least one account";
            yield break;
        }

        HashSet<string> usernames = new(StringComparer.OrdinalIgnoreCase);
        HashSet<uint> pids = [];
        foreach (QuazalAccount? account in Accounts)
        {
            if (account is null)
            {
                yield return "Accounts contains a null entry";
                continue;
            }
            if (string.IsNullOrWhiteSpace(account.Username) || account.Username.Contains('\0'))
                yield return "every account Username must be non-empty and contain no NUL";
            else if (!usernames.Add(account.Username))
                yield return $"duplicate account Username '{account.Username}'";
            if (string.IsNullOrEmpty(account.Password))
                yield return $"account '{account.Username}' must have a non-empty Password";
            if (account.Pid == 0)
                yield return $"account '{account.Username}' must have a non-zero Pid";
            else if (!pids.Add(account.Pid))
                yield return $"duplicate account Pid {account.Pid}";
            if (!string.IsNullOrEmpty(account.SourceKeyHex) &&
                (account.SourceKeyHex.Length != 32 || !account.SourceKeyHex.All(Uri.IsHexDigit)))
                yield return $"account '{account.Username}' SourceKeyHex must contain exactly 32 hexadecimal characters";
        }
    }

    /// <summary>What is wrong with the peer table's limits, if anything.</summary>
    internal IEnumerable<string> ValidatePeerLimits()
    {
        if (MaxPeers <= 0)
            yield return "MaxPeers must be positive";
        if (MaxUnprovenPeersPerAddress <= 0)
            yield return "MaxUnprovenPeersPerAddress must be positive";
        if (PeerHandshakeTimeoutSeconds <= 0 || PeerIdleTimeoutSeconds <= 0)
            yield return "PeerHandshakeTimeoutSeconds and PeerIdleTimeoutSeconds must be positive";

        if (PeerSweepIntervalSeconds <= 0 ||
            PeerSweepIntervalSeconds >= PeerHandshakeTimeoutSeconds || PeerSweepIntervalSeconds >= PeerIdleTimeoutSeconds)
            yield return "PeerSweepIntervalSeconds must be positive and shorter than both peer timeouts";

        if (Playforward.Enabled &&
            (Playforward.BindingIdleTimeoutSeconds <= PeerSweepIntervalSeconds ||
             Playforward.LaunchIdleTimeoutSeconds <= PeerSweepIntervalSeconds))
            yield return "Playforward.BindingIdleTimeoutSeconds and LaunchIdleTimeoutSeconds must be longer than PeerSweepIntervalSeconds";
    }
}
