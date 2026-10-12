using ModdableWebServer.Interfaces;
using ServerShared.Controllers;
using ServerShared.EventArguments;
using ServerShared.Plugins;
using ServerShared.Server;
using ServerShared.Titles;
using System.Net;
using Plugin.UplayServer.Quazal.Serializables;

namespace Plugin.UplayServer.Quazal;

/// <summary>Attaches the shared RendezVous/PRUDP responder to ServerApp UDP ports.</summary>
public sealed class UplayQuazalPlugin : ServerPlugin<QuazalSettings>
{
    public override uint Priority => 1;
    public override string Name => "UplayServer.Quazal";

    private QuazalResponder? _responder;
    /// <summary>The served titles by listening port; a packet on any other port is not ours.</summary>
    private IReadOnlyDictionary<int, QuazalTitle> _titlesByPort = new Dictionary<int, QuazalTitle>();

    /// <summary>
    /// The UDP listener on each served port, for sending a client something
    /// it did not ask for (a notification). Sent through the listener by
    /// endpoint rather than through a session looked up per client: this
    /// plugin used to keep its own session per endpoint, added for every
    /// datagram before it was parsed and never removed, and a table anybody
    /// can add to with a spoofed source address is memory anybody can fill.
    /// </summary>
    private readonly Dictionary<int, CoreUdpServer> _listeners = [];
    /// <summary>One Playforward half per served title that has one; empty when Playforward is off.</summary>
    private readonly List<Playforward.PlayforwardTitle> _playforward = [];

    public override void Start()
    {
        string[] accountErrors = Config.ValidateAccounts().ToArray();
        if (accountErrors.Length > 0)
        {
            foreach (string error in accountErrors)
                Log.Error("[quazal] invalid account configuration: {Error}", error);
            return;
        }

        string[] peerErrors = Config.ValidatePeerLimits().ToArray();
        if (peerErrors.Length > 0)
        {
            foreach (string error in peerErrors)
                Log.Error("[quazal] invalid session limits: {Error}", error);
            return;
        }

        if (!ResolveTitles())
            return;

        try
        {
            StartPlayforward();
        }
        catch (Exception exception)
        {
            Log.Error(exception, "[playforward] failed to start; continuing with Playforward disabled");
            StopPlayforward();
        }

        Playforward.PartySeating.Enabled = Config.Playforward.SeatPartiesTogether;

        FutureSoldierTitleStorage.Load(Config.FutureSoldierDlcBigPath, Config.FutureSoldierTitleStorageHost ?? Config.PublicHost);
        if (FutureSoldierTitleStorage.Loaded)
            MergeHttpRoutes("title storage");

        _responder = new QuazalResponder(Config, _titlesByPort);
        _responder.Sender = (remote, port, bytes) =>
        {
            if (_listeners.TryGetValue(port, out CoreUdpServer? listener))
                listener.SendAsync(remote, bytes);
            else
                Log.Warning("[quazal] cannot notify {Remote}: no UDP listener on :{Port}", remote, port);
        };
        _responder.StartSweeping();
        CoreUdpSession.OnBytesReceived += OnUdpBytes;
        foreach (QuazalTitle title in _titlesByPort.Values.Distinct())
            Log.Information("[quazal] serving {Title} on UDP {Ports} (secure :{Secure})",
                title, string.Join("/", title.Ports), title.SecureServerPort);
        Log.Information("[quazal] PRUDP V0 listener attached; transport ACKs={Acks}; authenticated secure CONNECT={SecureConnect}; signing identity={Identity}",
            Config.EnableTransportAcks, Config.EnableAuthenticatedSecureConnect,
            _responder.HasSigningIdentity ? "loaded" : "not configured");
    }

    /// <summary>
    /// Works out which titles this plugin serves and on which ports, and
    /// refuses to start unless every one of those ports has a UDP listener.
    /// Refusing is deliberate: a title whose ports are silently unbound is a
    /// game whose clients time out with nothing in the log naming why.
    /// </summary>
    private bool ResolveTitles()
    {
        if (QuazalTitles.MigrateLegacyListener(Config, TitleRegistry.All, out IReadOnlyList<string> notes))
        {
            foreach (string note in notes)
                Log.Warning("[quazal] config migration: {Note}", note);
            SaveConfig();
        }

        _titlesByPort = QuazalTitles.Resolve(Config, TitleRegistry.All, out IReadOnlyList<string> problems);
        if (problems.Count > 0)
        {
            foreach (string problem in problems)
                Log.Error("[quazal] invalid title configuration: {Problem}", problem);
            return false;
        }

        _listeners.Clear();
        foreach (int port in _titlesByPort.Keys)
        {
            if (ServerController.Servers.FirstOrDefault(s => s.Port == port && s.IsUdp)?.Server is CoreUdpServer listener)
                _listeners[port] = listener;
        }

        var unbound = _titlesByPort
            .Where(entry => !_listeners.ContainsKey(entry.Key))
            .ToArray();
        if (unbound.Length > 0)
        {
            foreach ((int port, QuazalTitle title) in unbound)
                Log.Error("[quazal] no UDP server registered for port {Port}, which {Title} needs — add it to ServerAppSettings.json with UseUDP=true",
                    port, title);
            return false;
        }
        return true;
    }

    /// <summary>
    /// Brings up the Playforward half of every served title: for each, the
    /// client (the credential every call rides on), the player-data store
    /// behind it, the login gateway.
    /// </summary>
    private void StartPlayforward()
    {
        if (!Config.Playforward.Enabled)
            return;

        if (Config.Playforward.MigrateLegacyTitleValues(Config.Titles, out IReadOnlyList<string> notes))
        {
            foreach (string note in notes)
                Log.Warning("[playforward] config migration: {Note}", note);
            SaveConfig();
        }

        string[] errors = Config.Playforward.Validate().ToArray();
        if (errors.Length > 0)
        {
            foreach (string error in errors)
                Log.Error("[playforward] {Error}", error);
            return;
        }

        foreach (QuazalTitle title in _titlesByPort.Values.Distinct())
        {
            Playforward.PlayforwardTitle? playforward = Playforward.PlayforwardTitle.Start(Config.Playforward, title);
            if (playforward is null)
            {
                Log.Error("[playforward] {Title} has no working Playforward identity (see above); its players log in locally", title);
                continue;
            }
            _playforward.Add(playforward);
            Playforward.PlayforwardTitles.Register(playforward);
        }
        if (_playforward.Count == 0)
        {
            Log.Error("[playforward] no title could be brought up (see above); continuing with Playforward disabled");
            return;
        }

        AttachOnlineData();
        MergePlayforwardRoutes();
        StartSessionReporting();
    }

    /// <summary>
    /// Puts the online-data store (Far Cry 3's protocol 106) behind the
    /// player-data store of the title that speaks it. The store is keyed by
    /// pid alone, so it can serve one title
    /// </summary>
    private void AttachOnlineData()
    {
        Playforward.PlayforwardTitle[] farCry3 = _playforward
            .Where(p => p.Title.ProtocolFamily == QuazalProtocolFamily.FarCry3)
            .ToArray();
        if (farCry3.Length == 0)
            return;
        if (farCry3.Length > 1)
        {
            Log.Error("[playforward] {Count} titles speak Far Cry 3's online-data protocol but the store can hold one; saved progress is attached to {Title} only",
                farCry3.Length, farCry3[0].Title);
        }
        Playforward.PlayforwardTitle owner = farCry3[0];
        OnlineDataStore.Attach(owner.TitleId, owner.Store, pid => QuazalSettings.IsPlayforwardPid(owner.Title, pid));
    }

    /// <summary>
    /// Publishes this assembly's [HTTP] routes onto every configured HTTP
    /// server, the same way the REST plugin does.
    /// </summary>
    private void MergePlayforwardRoutes()
    {
        int merged = MergeHttpRoutes("pre-registration");
        if (merged == 0)
            Log.Error("[playforward] no HTTP server is configured, so the desktop client has nowhere to pre-register: add one to ServerAppSettings.json");
    }

    private bool _routesMerged;

    /// <summary>
    /// Merges this assembly's [HTTP] routes onto every configured HTTP
    /// server, once; the whole assembly goes in together, so whichever
    /// feature asks first publishes them all. Returns the server count.
    /// </summary>
    private int MergeHttpRoutes(string feature)
    {
        int servers = 0;
        foreach (var server in ServerController.Servers)
        {
            if (server.Server is not IHttpServer http)
                continue;
            if (!_routesMerged)
                http.HTTPAttributeToMethods.Merge(System.Reflection.Assembly.GetExecutingAssembly());
            servers++;
        }
        if (servers == 0)
        {
            Log.Error("[quazal] no HTTP server is configured for the {Feature} endpoint: add one to ServerAppSettings.json", feature);
            return 0;
        }
        if (!_routesMerged)
            Log.Information("[quazal] HTTP routes published on {Count} HTTP server(s) (first asked for by {Feature})", servers, feature);
        _routesMerged = true;
        return servers;
    }

    /// <summary>
    /// Starts the half of Playforward that runs outward: registering this
    /// server's matches, pushing their rosters, and collecting the moderation
    /// commands to enforce.
    /// </summary>
    private void StartSessionReporting()
    {
        if (!Config.Playforward.ReportingEnabled)
        {
            Log.Information("[playforward] session reporting is off: this server will not appear in the directory and no moderator can act on a match here");
            return;
        }

        foreach (Playforward.PlayforwardTitle playforward in _playforward)
            playforward.StartReporting(Config.Playforward);
    }

    private void StopPlayforward()
    {
        Playforward.PlayforwardTitles.Clear();
        foreach (Playforward.PlayforwardTitle playforward in _playforward)
            playforward.Dispose();
        _playforward.Clear();
        OnlineDataStore.Detach();
    }

    public override void Stop()
    {
        CoreUdpSession.OnBytesReceived -= OnUdpBytes;
        _responder?.Dispose();
        _responder = null;
        StopPlayforward();
    }

    private void OnUdpBytes(object? sender, SessionBytesReceivedEventArgs e)
    {
        if (_responder is null || sender is not CoreUdpSession session ||
            !_titlesByPort.TryGetValue(session.Server.Port, out QuazalTitle? title) ||
            session.EndPoint is not IPEndPoint source)
            return;
        if (Playforward.PlayforwardTitles.ForTitle(title)?.Gateway is { } gateway)
        {
            byte[]? launchReply = gateway.HandleLaunchDatagram(e.Data.Span, source, out bool wasLaunch);
            if (wasLaunch)
            {
                if (launchReply is not null)
                    session.SendAsync(launchReply);
                return;
            }
        }
        if (QuazalResponder.TryCreateNatDetectReply(e.Data.Span, source, out byte[] natReply))
        {
            session.SendAsync(natReply);
            Log.Information("[quazal] NAT-detect reply -> {Source} ({Length} bytes)",
                source, natReply.Length);
            return;
        }

        (int width, var algorithm) = _responder.ResolveDialect(source, session.Server.Port, e.Data.Span);
        if (!PrudpPacketV0.TryDeserialize(e.Data.Span, out PrudpPacketV0 packet, width, algorithm))
        {
            Log.Debug("[quazal] ignored {Length}-byte non-V0 datagram from {Source} (tried {Width}-byte checksum) wire={Wire}",
                e.Data.Length, source, width, Convert.ToHexString(e.Data.Span));
            return;
        }

        Log.Information("[quazal] {Source} {Packet}", source, packet);
        try
        {
            IReadOnlyList<byte[]> replies = _responder.Handle(packet, source, session.Server.Port, e.Data.Span);
            int delay = Config.ReplyDelayMilliseconds;
            if (delay > 0 && packet.PacketType == Enums.PacketType.Data && replies.Count > 0)
            {
                byte[][] held = [.. replies];
                _ = Task.Run(async () =>
                {
                    await Task.Delay(delay);
                    foreach (byte[] reply in held) session.SendAsync(reply);
                });
                return;
            }
            foreach (byte[] reply in replies)
            {
                session.SendAsync(reply);
                Log.Debug("[quazal] ACK {Type} seq={Sequence} -> {Source} ({Length} bytes) wire={Wire}",
                    packet.PacketType, packet.SequenceId, source, reply.Length,
                    Convert.ToHexString(reply));
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[quazal] handler failed for {Source}; keeping UDP listener alive", source);
        }
    }
}
