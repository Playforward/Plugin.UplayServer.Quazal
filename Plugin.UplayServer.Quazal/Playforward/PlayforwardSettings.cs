using System.Text.Json.Serialization;

namespace Plugin.UplayServer.Quazal.Playforward;

/// <summary>
/// The Playforward identity of one title on this server.
/// </summary>
public sealed class PlayforwardTitleSettings
{
    /// <summary>
    /// This title's own credential, issued once when an admin registered a
    /// server for the title (<c>POST /api/v1/admin/gameservers</c>) and shown
    /// only that once. One secret maps to exactly one <c>title_id</c> there,
    /// which is why it cannot be shared between titles: a match reported on
    /// another title's secret is filed under that title.
    /// </summary>
    public string ServerSecret { get; set; } = string.Empty;

    /// <summary>
    /// The registration id Playforward gave this title's server, shown with its
    /// secret. Tokens and moderation commands are minted for one server, and
    /// this is how the server knows which audience is its own.
    /// </summary>
    public Guid? ServerId { get; set; }

    /// <summary>
    /// What this title's server is called in the browser, and the base of
    /// each match's label. Empty means the title's catalog name.
    /// </summary>
    public string ServerLabel { get; set; } = string.Empty;

    /// <summary>
    /// How many players a match of this title holds, as advertised to the
    /// directory. Per title because it is the game's own lobby size, and
    /// quick match sends people by it.
    /// </summary>
    public int SessionCapacity { get; set; }

    /// <summary>
    /// The number of characters this title keeps of the RendezVous login
    /// username.
    /// </summary>
    public int LoginNameMaxLength { get; set; } = 16;

    /// <summary>
    /// The account whose credentials a Playforward player of this title
    /// inherits. 
    /// </summary>
    public string TemplateAccountUsername { get; set; } = "player";

    public IEnumerable<string> Validate(string titleId)
    {
        string at = $"Titles['{titleId}'].Playforward";
        if (string.IsNullOrWhiteSpace(ServerSecret))
            yield return $"{at}.ServerSecret is required; an admin issues one with POST /api/v1/admin/gameservers for that title";
        if (string.IsNullOrWhiteSpace(TemplateAccountUsername))
            yield return $"{at}.TemplateAccountUsername is required";
        if (LoginNameMaxLength < 1)
            yield return $"{at}.LoginNameMaxLength must be positive";
        if (SessionCapacity is < 1 or > 128)
            yield return $"{at}.SessionCapacity must be between 1 and 128";
    }
}

/// <summary>
/// How this game server talks to Playforward
/// </summary>
public sealed class PlayforwardSettings
{
    /// <summary>Whether players may authenticate with Playforward tokens.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// A pinned Ed25519 public key, raw and base64url (the JWKS <c>"x"</c>
    /// value), for a LAN server with no route to Playforward or no TLS to it.
    /// Leave it empty and the keys are read from Playforward's JWKS at
    /// <see cref="BaseUrl"/>, by key id, which is what lets a signing-key
    /// rotation need nothing done here.
    /// </summary>
    public string PublicKeyBase64 { get; set; } = string.Empty;

    /// <summary>Expected <c>iss</c>. Playforward's default is playforward-accounts.</summary>
    public string Issuer { get; set; } = "playforward-accounts";

    /// <summary>
    /// The audience prefix Playforward mints game-server tokens under.
    /// </summary>
    public string AudiencePrefix { get; set; } = "playforward-game-servers";

    /// <summary>
    /// The <c>aud</c> a token or command for this server of
    /// <paramref name="titleId"/> must carry. Per server rather than per title:
    /// a title's servers are run by different communities, and with one
    /// audience per title each could log in as the other's players.
    /// </summary>
    public string AudienceFor(string titleId, Guid serverId) => $"{AudiencePrefix}:{titleId}:{serverId:D}";

    /// <summary>
    /// Superseded by <see cref="AudiencePrefix"/> and the title's slug. Read
    /// only to name the title a config from before per-title settings served.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Audience { get; set; }

    /// <summary>
    /// Tolerance for the two clocks disagreeing. Small on purpose: these tokens
    /// are minted to live about a minute, so a generous skew would be a large
    /// fraction of the only revocation they have.
    /// </summary>
    public int ClockSkewSeconds { get; set; } = 30;

    /// <summary>
    /// How long a correlation code stays redeemable. It travels from the
    /// desktop client into a game process that is already launching, so this
    /// only has to cover a slow machine.
    /// </summary>
    public int CorrelationCodeTtlSeconds { get; set; } = 120;

    /// <summary>
    /// The longest a launch stays provable after the desktop client is issued
    /// it, however much it is used.
    /// </summary>
    public int LaunchTtlSeconds { get; set; } = 8 * 60 * 60;

    /// <summary>
    /// How long a launch stays provable with no sign that its player is still
    /// around: neither the desktop client refreshing it nor the player's game
    /// talking to this server.
    /// </summary>
    public int LaunchIdleTimeoutSeconds { get; set; } = 10 * 60;

    /// <summary>
    /// How long a login binding 
    /// </summary>
    public int BindingIdleTimeoutSeconds { get; set; } = 3 * 60;

    /// <summary>Superseded by <see cref="PlayforwardTitleSettings.TemplateAccountUsername"/>; read only to migrate.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TemplateAccountUsername { get; set; }

    /// <summary>
    /// Require a pre-bound login's RendezVous username to match the account's
    /// verified display name, and refuse it otherwise.
    /// </summary>
    public bool EnforceLoginName { get; set; } = true;

    /// <summary>
    /// The networks, in CIDR notation, this server treats as its LAN. Two
    /// things are allowed only from them: a pre-bind binding an address, and
    /// the Playforward HTTP routes answering over plain HTTP. Empty turns both
    /// off, so players need a launch proof or a code, over TLS.
    /// </summary>
    public List<string> LanNetworks { get; set; } =
    [
        "127.0.0.0/8",
        "10.0.0.0/8",
        "172.16.0.0/12",
        "192.168.0.0/16",
        "::1/128",
        "fc00::/7",
        "fe80::/10",
    ];

    /// <summary>Superseded by <see cref="PlayforwardTitleSettings.LoginNameMaxLength"/>; read only to migrate.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LoginNameMaxLength { get; set; }

    public bool ReportingEnabled { get; set; }

    /// <summary>Where Playforward is, e.g. <c>https://accounts.example.com</c>.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Superseded by <see cref="PlayforwardTitleSettings.ServerSecret"/>; read only to migrate.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServerSecret { get; set; }

    /// <summary>
    /// How often the reporter pushes the whole picture. A change nudges it
    /// sooner, so this is the ceiling on staleness rather than the usual delay.
    /// </summary>
    public int ReportIntervalSeconds { get; set; } = 15;

    /// <summary>How long one call to Playforward may take before it is left to
    /// the next pass.</summary>
    public int RequestTimeoutSeconds { get; set; } = 10;

    /// <summary>Superseded by <see cref="PlayforwardTitleSettings.ServerLabel"/>; read only to migrate.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServerLabel { get; set; }

    /// <summary>Superseded by <see cref="PlayforwardTitleSettings.SessionCapacity"/>; read only to migrate.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SessionCapacity { get; set; }

    /// <summary>
    /// Whether a player barred from this server is refused at login.
    /// </summary>
    public bool EnforceBans { get; set; } = true;

    /// <summary>
    /// Whether party members are steered into the same match.
    /// </summary>
    public bool SeatPartiesTogether { get; set; } = true;

    public IEnumerable<string> Validate()
    {
        if (!Enabled)
        {
            if (ReportingEnabled)
                yield return "Playforward.ReportingEnabled requires Playforward.Enabled: there are no accounts to report without it";
            yield break;
        }

        if (string.IsNullOrWhiteSpace(PublicKeyBase64) && string.IsNullOrWhiteSpace(BaseUrl))
            yield return "Playforward.BaseUrl is required when Playforward is enabled: it is where the keys tokens are checked with are read from, unless PublicKeyBase64 pins one";
        if (string.IsNullOrWhiteSpace(Issuer))
            yield return "Playforward.Issuer is required";
        if (string.IsNullOrWhiteSpace(AudiencePrefix) || AudiencePrefix.Contains(':'))
            yield return "Playforward.AudiencePrefix is required and carries no ':' -- the title's slug is appended after one";
        if (CorrelationCodeTtlSeconds <= 0)
            yield return "Playforward.CorrelationCodeTtlSeconds must be positive";
        if (LaunchTtlSeconds <= 0)
            yield return "Playforward.LaunchTtlSeconds must be positive";
        if (LaunchIdleTimeoutSeconds <= 0 || LaunchIdleTimeoutSeconds > LaunchTtlSeconds)
            yield return "Playforward.LaunchIdleTimeoutSeconds must be positive and no longer than LaunchTtlSeconds";
        if (BindingIdleTimeoutSeconds <= 0)
            yield return "Playforward.BindingIdleTimeoutSeconds must be positive";
        foreach (string network in LanNetworks ?? [])
        {
            if (!System.Net.IPNetwork.TryParse(network, out _))
                yield return $"Playforward.LanNetworks entry '{network}' is not a CIDR network";
        }

        if (string.IsNullOrWhiteSpace(BaseUrl))
            yield return "Playforward.BaseUrl is required: a player's PID and saved progress live in Playforward";
        if (RequestTimeoutSeconds <= 0)
            yield return "Playforward.RequestTimeoutSeconds must be positive";

        if (ReportingEnabled && ReportIntervalSeconds <= 0)
            yield return "Playforward.ReportIntervalSeconds must be positive";
    }

    /// <summary>
    /// Carries the per-title values a config from before
    /// <see cref="PlayforwardTitleSettings"/> holds here into the block of the
    /// title it was serving -- the one its <see cref="Audience"/> names, or
    /// the only Quazal title when there is one. Clears them so the caller can
    /// save the migrated shape. Returns what it did, for the log.
    /// </summary>
    public bool MigrateLegacyTitleValues(IReadOnlyDictionary<string, QuazalTitleSettings> titles, out IReadOnlyList<string> notes)
    {
        List<string> found = [];
        notes = found;
        bool hasLegacy = Audience is not null || ServerSecret is not null || ServerLabel is not null
                         || SessionCapacity is not null || LoginNameMaxLength is not null || TemplateAccountUsername is not null;
        if (!hasLegacy)
            return false;

        string? titleId = null;
        if (Audience is { } audience && audience.StartsWith(AudiencePrefix + ":", StringComparison.Ordinal))
            titleId = audience[(AudiencePrefix.Length + 1)..];
        titleId ??= titles.Count == 1 ? titles.Keys.First() : null;

        if (titleId is null || !titles.TryGetValue(titleId, out QuazalTitleSettings? title))
        {
            found.Add($"Playforward carried per-title values (Audience {Audience ?? "unset"}) but no matching entry exists under Titles; set Titles[<title>].Playforward by hand");
        }
        else
        {
            PlayforwardTitleSettings into = title.Playforward ??= new();
            if (ServerSecret is { } secret) into.ServerSecret = secret;
            if (ServerLabel is { } label) into.ServerLabel = label;
            if (SessionCapacity is { } capacity) into.SessionCapacity = capacity;
            if (LoginNameMaxLength is { } width) into.LoginNameMaxLength = width;
            if (TemplateAccountUsername is { } template) into.TemplateAccountUsername = template;
            found.Add($"moved Playforward's ServerSecret/ServerLabel/SessionCapacity/LoginNameMaxLength/TemplateAccountUsername into Titles['{titleId}'].Playforward");
        }

        Audience = null;
        ServerSecret = null;
        ServerLabel = null;
        SessionCapacity = null;
        LoginNameMaxLength = null;
        TemplateAccountUsername = null;
        return true;
    }
}
