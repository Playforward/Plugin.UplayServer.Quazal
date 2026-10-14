namespace Plugin.UplayServer.Quazal.Playforward;

/// <summary>
/// One title's whole Playforward half
/// </summary>
internal sealed class PlayforwardTitle : IDisposable
{
    public QuazalTitle Title { get; }
    public string TitleId => Title.TitleId;

    /// <summary>The <c>aud</c> this title's tokens and commands must carry.</summary>
    public string Audience { get; }

    public PlayforwardTitleSettings Settings { get; }

    /// <summary>
    /// The outbound credential. Null only for a test's stand-in, whose store
    /// is in memory and which never reports.
    /// </summary>
    public PlayforwardServerClient? Client { get; }
    public IProgressStore Store { get; }
    public PlayforwardGateway Gateway { get; }

    /// <summary>Null on a title that does not report to the directory.</summary>
    public SessionReporter? Reporter { get; private set; }

    /// <summary>What this title's server is called in the browser.</summary>
    public string ServerLabel =>
        string.IsNullOrWhiteSpace(Settings.ServerLabel) ? Title.Name : Settings.ServerLabel;

    /// <summary>
    /// The registration this title's tokens are minted for, as Playforward's
    /// directory lists it. Null only for a test's stand-in.
    /// </summary>
    public Guid? ServerId { get; }

    private PlayforwardTitle(QuazalTitle title, string audience, PlayforwardTitleSettings settings,
        PlayforwardServerClient? client, IProgressStore store, PlayforwardGateway gateway, Guid? serverId)
    {
        Title = title;
        Audience = audience;
        Settings = settings;
        Client = client;
        Store = store;
        Gateway = gateway;
        ServerId = serverId;
    }

    /// <summary>
    /// A title over a gateway and store a test built itself
    /// </summary>
    internal static PlayforwardTitle ForTests(QuazalTitle title, string audience, PlayforwardGateway gateway, IProgressStore store,
        Guid? serverId = null) =>
        new(title, audience, title.Settings.Playforward ?? new(), client: null, store, gateway, serverId);

    /// <summary>
    /// Brings one title's Playforward half up, or returns null with the
    /// reason logged. The store comes first because a login resolves a PID
    /// through it. Nothing here touches another title's state.
    /// </summary>
    public static PlayforwardTitle? Start(PlayforwardSettings global, QuazalTitle title)
    {
        PlayforwardTitleSettings settings = title.Settings.Playforward ?? new();
        string[] errors = settings.Validate(title.TitleId).ToArray();
        if (errors.Length > 0)
        {
            foreach (string error in errors)
                Log.Error("[playforward] {Title}: {Error}", title, error);
            return null;
        }

        PlayforwardServerClient? client = PlayforwardServerClient.Create(global, settings.ServerSecret, title.TitleId);
        if (client is null)
            return null;

        RegistrationResponse? registration = client.WhoAmIAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (registration is null)
        {
            Log.Warning("[playforward] {Title}: could not confirm which title this server's secret is registered for; continuing unverified", title);
        }
        else if (!string.Equals(registration.TitleId, title.TitleId, StringComparison.OrdinalIgnoreCase))
        {
            Log.Error("[playforward] {Title}: refusing to start -- Titles['{Slug}'].Playforward.ServerSecret is registered for '{Registered}' (server \"{Name}\"), not for this title; a match reported with it would be filed under the wrong game",
                title, title.TitleId, registration.TitleId, registration.Name);
            client.Dispose();
            return null;
        }
        else
        {
            Log.Information("[playforward] {Title}: secret confirmed as server \"{Name}\" ({ServerId}){Address}",
                title, registration.Name, registration.ServerId,
                string.IsNullOrEmpty(registration.PublicUrl) ? "; no public address, so clients are not offered it" : $" at {registration.PublicUrl}");
        }

        Guid? serverId = ServerIdentity(title, settings.ServerId, registration?.ServerId);
        if (serverId is null)
        {
            client.Dispose();
            return null;
        }
        string audience = global.AudienceFor(title.TitleId, serverId.Value);

        var store = new PlayforwardProgressStore(client);
        PlayforwardGateway? gateway = PlayforwardGateway.Start(global, title.TitleId, audience, settings.LoginNameMaxLength, store);
        if (gateway is null)
        {
            client.Dispose();
            return null;
        }
        gateway.AttachClient(client);

        return new PlayforwardTitle(title, audience, settings, client, store, gateway, serverId);
    }

    /// <summary>
    /// Which registered server this is
    /// </summary>
    internal static Guid? ServerIdentity(QuazalTitle title, Guid? configured, Guid? registered)
    {
        if (configured is { } setting && registered is { } reported && setting != reported)
        {
            Log.Error("[playforward] {Title}: refusing to start -- Titles['{Slug}'].Playforward.ServerId is {Configured}, but its ServerSecret belongs to server {Registered}",
                title, title.TitleId, setting, reported);
            return null;
        }
        Guid? serverId = configured ?? registered;
        if (serverId is null || serverId == Guid.Empty)
        {
            Log.Error("[playforward] {Title}: Playforward identity is off -- this server's registration id is unknown, because Playforward could not be reached to read it; set Titles['{Slug}'].Playforward.ServerId (shown when the server was registered) so the title can start without it",
                title, title.TitleId);
            return null;
        }
        return serverId;
    }

    /// <summary>
    /// Starts the half that runs outward
    /// </summary>
    public void StartReporting(PlayforwardSettings global)
    {
        if (Client is null)
        {
            Log.Error("[playforward] {Title} has no client to report through", Title);
            return;
        }

        var commands = new ServerCommandVerifier(
            Gateway.Verifier.Keys,
            global.Issuer,
            Audience,
            TimeSpan.FromSeconds(global.ClockSkewSeconds));

        Reporter = new SessionReporter(Client, commands, Gateway.Enforcer, Gateway, global, this);
        Reporter.Start();
    }

    public void Dispose()
    {
        Reporter?.Dispose();
        Reporter = null;
        Client?.Dispose();
        Gateway.Dispose();
    }
}

/// <summary>
/// The titles with Playforward identity on this server, by slug and by token
/// audience. Static for the reason the gateway's <c>Current</c> used to be:
/// the HTTP routes and the GameSession handlers are static methods with no
/// instance to be handed, so they look their title up here.
/// </summary>
internal static class PlayforwardTitles
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, PlayforwardTitle> _byTitleId =
        new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyCollection<PlayforwardTitle> All => _byTitleId.Values.ToArray();

    /// <summary>Whether any title on this server has Playforward identity.</summary>
    public static bool Any => !_byTitleId.IsEmpty;

    public static void Register(PlayforwardTitle title) => _byTitleId[title.TitleId] = title;

    public static void Clear() => _byTitleId.Clear();

    public static PlayforwardTitle? ForTitle(string titleId) => _byTitleId.GetValueOrDefault(titleId);

    /// <summary>The title a peer belongs to, or null when it has no Playforward half.</summary>
    public static PlayforwardTitle? ForTitle(QuazalTitle title) => ForTitle(title.TitleId);

    /// <summary>
    /// The title whose audience a token claims. The claim is unverified and
    /// only chooses which verifier runs; that verifier still requires it.
    /// </summary>
    public static PlayforwardTitle? ForToken(string token)
    {
        foreach (string audience in SignedJwt.PeekAudiences(token))
        {
            PlayforwardTitle? match = _byTitleId.Values.FirstOrDefault(t =>
                string.Equals(t.Audience, audience, StringComparison.Ordinal));
            if (match is not null)
                return match;
        }
        return null;
    }

    /// <summary>Wakes a title's reporter after a session changed, when it has one.</summary>
    public static void Nudge(string titleId) => ForTitle(titleId)?.Reporter?.Nudge();
}
