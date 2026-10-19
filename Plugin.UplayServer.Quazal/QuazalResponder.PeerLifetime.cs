using System.Net;

namespace Plugin.UplayServer.Quazal;

/// <summary>
/// How long a PRUDP session is kept, and who gets a slot when the table is
/// full.
/// </summary>
internal sealed partial class QuazalResponder
{
    private readonly TimeProvider _time;
    private Timer? _sweep;

    /// <summary>When a refusal was last logged, so a flood is not echoed into the log line for line.</summary>
    private long _lastAdmissionWarning = long.MinValue;

    /// <summary>
    /// Whether a session has proved who it is: logged in, or presented a
    /// ticket. Until then it is something anybody could have sent.
    /// </summary>
    private static bool IsProven(Peer peer) => peer.AuthenticatedPid is not null;

    /// <summary>
    /// Starts forgetting idle sessions every
    /// <see cref="QuazalSettings.PeerSweepIntervalSeconds"/>.
    /// </summary>
    public void StartSweeping()
    {
        StartRetransmitting();
        TimeSpan interval = TimeSpan.FromSeconds(settings.PeerSweepIntervalSeconds);
        _sweep ??= new Timer(_ =>
        {
            // A timer callback that throws ends the process.
            try
            {
                Sweep();
            }
            catch (Exception exception)
            {
                Log.Error(exception, "[quazal] the idle-session sweep failed; it runs again in {Interval}", interval);
            }
        }, null, interval, interval);
    }

    private void StopSweeping()
    {
        _sweep?.Dispose();
        _sweep = null;
    }

    /// <summary>
    /// One pass: forgets idle sessions, then tells each title's Playforward
    /// gateway which players are still here. Returns how many were forgotten.
    /// </summary>
    internal int Sweep()
    {
        int forgotten = ForgetIdlePeers();
        ReportLivePlayers();
        return forgotten;
    }

    /// <summary>
    /// Forgets every session quiet past its timeout: the handshake timeout for
    /// one that never completed (or has closed) its connection, the idle
    /// timeout for one that is connected.
    /// </summary>
    private int ForgetIdlePeers()
    {
        TimeSpan handshake = TimeSpan.FromSeconds(settings.PeerHandshakeTimeoutSeconds);
        TimeSpan idle = TimeSpan.FromSeconds(settings.PeerIdleTimeoutSeconds);
        int forgotten = 0;
        foreach (((IPEndPoint Remote, int LocalPort, byte SourcePort) key, Peer peer) in _peers)
        {
            TimeSpan timeout = peer.Connected ? idle : handshake;
            if (_time.GetElapsedTime(peer.LastHeard) <= timeout ||
                !_peers.TryRemove(new(key, peer)))
                continue;

            // Idle by definition, so nothing is using the keys being wiped.
            ClearSecureState(peer);
            forgotten++;
            Log.Debug("[quazal] forgot {Remote} on :{Port} (virtual port {SourcePort}, pid={Pid}): quiet for over {Timeout}",
                key.Remote, key.LocalPort, key.SourcePort, peer.AuthenticatedPid?.ToString() ?? "none", timeout);
        }
        return forgotten;
    }

    /// <summary>
    /// Tells each Playforward and standalone title which of its players'
    /// games are still talking, so the login bindings and launches they rely on stay, and the
    /// ones nobody is using end; see <see cref="Playforward.PlayforwardGateway.KeepAlive"/>.
    /// </summary>
    private void ReportLivePlayers()
    {
        foreach (QuazalTitle title in _titlesByPort.Values.Distinct())
        {
            if (Standalone.LocalTitles.ForTitle(title) is { } local)
            {
                Dictionary<uint, IPAddress> playing = [];
                foreach (((IPEndPoint Remote, int LocalPort, byte SourcePort) key, Peer peer) in _peers)
                {
                    if (ReferenceEquals(peer.Title, title) && peer.AuthenticatedPid is { } pid)
                        playing[pid] = key.Remote.Address;
                }
                local.KeepAlive(playing);
                continue;
            }
            if (Playforward.PlayforwardTitles.ForTitle(title)?.Gateway is not { } gateway)
                continue;

            HashSet<IPEndPoint> endpoints = [];
            HashSet<uint> pids = [];
            foreach (((IPEndPoint Remote, int LocalPort, byte SourcePort) key, Peer peer) in _peers)
            {
                if (!ReferenceEquals(peer.Title, title))
                    continue;
                endpoints.Add(key.Remote);
                if (peer.AuthenticatedPid is { } pid)
                    pids.Add(pid);
            }
            gateway.KeepAlive(endpoints, pids);
        }
    }

    /// <summary>
    /// Whether a SYN from a new session gets a slot, making room for it if the
    /// table is full.
    /// </summary>
    private bool AdmitPeer(IPEndPoint source)
    {
        int unprovenFromAddress = _peers.Count(entry =>
            !IsProven(entry.Value) && entry.Key.Remote.Address.Equals(source.Address));
        if (unprovenFromAddress >= settings.MaxUnprovenPeersPerAddress)
        {
            WarnRefused("[quazal] ignoring SYN from {Source}: it already has {Count} sessions that have not logged in (MaxUnprovenPeersPerAddress)",
                source, unprovenFromAddress);
            return false;
        }

        if (_peers.Count < settings.MaxPeers)
            return true;
        ForgetIdlePeers();
        if (_peers.Count < settings.MaxPeers)
            return true;

        KeyValuePair<(IPEndPoint Remote, int LocalPort, byte SourcePort), Peer> stalest = _peers
            .Where(entry => !IsProven(entry.Value))
            .OrderBy(entry => entry.Value.Connected)
            .ThenBy(entry => entry.Value.LastHeard)
            .FirstOrDefault();
        if (stalest.Value is null)
        {
            WarnRefused("[quazal] ignoring SYN from {Source}: all {Limit} sessions (MaxPeers) belong to logged-in players",
                source, settings.MaxPeers);
            return false;
        }

        if (_peers.TryRemove(stalest))
        {
            ClearSecureState(stalest.Value);
            Log.Debug("[quazal] displaced {Remote}'s session, which had not logged in, to make room for {Source}",
                stalest.Key.Remote, source);
        }
        return true;
    }

    /// <summary>
    /// Logs a refusal at most once per sweep interval. A flood of SYNs would
    /// otherwise be copied into the log a line per datagram.
    /// </summary>
    private void WarnRefused(string template, IPEndPoint source, int count)
    {
        long now = _time.GetTimestamp();
        long last = Interlocked.Read(ref _lastAdmissionWarning);
        if (last != long.MinValue &&
            _time.GetElapsedTime(last, now) < TimeSpan.FromSeconds(settings.PeerSweepIntervalSeconds))
            return;
        if (Interlocked.CompareExchange(ref _lastAdmissionWarning, now, last) == last)
            Log.Warning(template, source, count);
    }
}
