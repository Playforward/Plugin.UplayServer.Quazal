namespace Plugin.UplayServer.Quazal;

/// <summary>
/// The server's half of PRUDP reliability: reliable DATA this server sends
/// is held until the client acknowledges it, sent again when it is not, and
/// a long reply goes out a window at a time rather than in one burst.
/// <para>
/// Until this existed every reply was fired once and forgotten. That held
/// while replies were a few packets, because the client retransmits its own
/// request when the reply's ACK does not come back. It broke on Assassin's
/// Creed Brotherhood's 58 KB settings file: 61 fragments in one burst
/// overflowed the client's receive buffer under Wine, the last five were
/// dropped, the client had already ACKed the request, and so nothing ever
/// recovered them -- the game sat on its loading screen forever.
/// </para>
/// </summary>
internal sealed partial class QuazalResponder
{
    /// <summary>Unacknowledged reliable packets in flight per session before the rest wait.</summary>
    internal const int SendWindow = 16;

    /// <summary>How long a reliable packet waits for its ACK before it is sent again.</summary>
    internal static readonly TimeSpan RetransmitAfter = TimeSpan.FromMilliseconds(500);

    /// <summary>Sends of one packet before it is given up on.</summary>
    internal const int MaxSendAttempts = 10;

    private static readonly TimeSpan RetransmitInterval = TimeSpan.FromMilliseconds(200);

    private Timer? _retransmit;

    /// <summary>
    /// One session's reliable packets: the ones sent and not yet ACKed, and
    /// the ones still waiting for room in the window. Locked, because the
    /// receive path and the retransmit timer run on different threads.
    /// </summary>
    internal sealed class ReliableOutbox
    {
        private sealed class InFlight
        {
            public required byte[] Bytes { get; init; }
            public long SentAt { get; set; }
            public int Attempts { get; set; } = 1;
        }

        private readonly Lock _gate = new();
        private readonly Dictionary<ushort, InFlight> _inFlight = [];
        private readonly Queue<(ushort Sequence, byte[] Bytes)> _waiting = new();

        public int InFlightCount
        {
            get
            {
                lock (_gate)
                    return _inFlight.Count;
            }
        }

        public int WaitingCount
        {
            get
            {
                lock (_gate)
                    return _waiting.Count;
            }
        }

        /// <summary>Takes packets to send in order; returns those that fit the window now.</summary>
        public List<byte[]> Enqueue(IEnumerable<(ushort Sequence, byte[] Bytes)> packets, long now)
        {
            lock (_gate)
            {
                foreach (var packet in packets)
                    _waiting.Enqueue(packet);
                return Release(now);
            }
        }

        /// <summary>The client ACKed a sequence id; returns what that made room for.</summary>
        public List<byte[]> Acknowledge(ushort sequence, long now)
        {
            lock (_gate)
            {
                if (!_inFlight.Remove(sequence))
                    return [];
                return Release(now);
            }
        }

        /// <summary>
        /// The packets whose ACK is overdue, marked as sent again now. A packet
        /// past <see cref="MaxSendAttempts"/> is given up on, which frees its
        /// place for the ones waiting; those come back in the same list.
        /// </summary>
        public List<byte[]> DueForResend(long now, Func<long, long, TimeSpan> elapsed, out int abandoned)
        {
            abandoned = 0;
            lock (_gate)
            {
                var due = new List<byte[]>();
                foreach ((ushort sequence, InFlight packet) in _inFlight.ToArray())
                {
                    if (elapsed(packet.SentAt, now) < RetransmitAfter)
                        continue;
                    if (packet.Attempts >= MaxSendAttempts)
                    {
                        _inFlight.Remove(sequence);
                        abandoned++;
                        continue;
                    }
                    packet.Attempts++;
                    packet.SentAt = now;
                    due.Add(packet.Bytes);
                }
                if (abandoned > 0)
                    due.AddRange(Release(now));
                return due;
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _inFlight.Clear();
                _waiting.Clear();
            }
        }

        private List<byte[]> Release(long now)
        {
            var send = new List<byte[]>();
            while (_inFlight.Count < SendWindow && _waiting.TryDequeue(out var next))
            {
                _inFlight[next.Sequence] = new InFlight { Bytes = next.Bytes, SentAt = now };
                send.Add(next.Bytes);
            }
            return send;
        }
    }

    /// <summary>Hands this session's reliable reply packets to its outbox; returns the ones to send now.</summary>
    private List<byte[]> SendReliable(Peer peer, List<(ushort Sequence, byte[] Bytes)> packets) =>
        peer.Outbox.Enqueue(packets, _time.GetTimestamp());

    /// <summary>A client ACK of one of this server's reliable packets: releases what was waiting on it.</summary>
    private List<byte[]> AcknowledgeReliable(Peer peer, ushort sequence) =>
        peer.Outbox.Acknowledge(sequence, _time.GetTimestamp());

    private void StartRetransmitting()
    {
        _retransmit ??= new Timer(_ =>
        {
            try
            {
                Retransmit();
            }
            catch (Exception exception)
            {
                Log.Error(exception, "[quazal] the retransmit pass failed; it runs again shortly");
            }
        }, null, RetransmitInterval, RetransmitInterval);
    }

    private void StopRetransmitting()
    {
        _retransmit?.Dispose();
        _retransmit = null;
    }

    /// <summary>One pass: sends again every reliable packet whose ACK is overdue.</summary>
    internal int Retransmit()
    {
        if (Sender is not { } send)
            return 0;
        long now = _time.GetTimestamp();
        int sent = 0;
        foreach (((System.Net.IPEndPoint Remote, int LocalPort, byte SourcePort) key, Peer peer) in _peers)
        {
            List<byte[]> due = peer.Outbox.DueForResend(now, _time.GetElapsedTime, out int abandoned);
            if (abandoned > 0)
                Log.Warning("[quazal] {Remote} on :{Port} never acknowledged {Count} reliable packet(s) after {Attempts} sends; gave up on them",
                    key.Remote, key.LocalPort, abandoned, MaxSendAttempts);
            if (due.Count == 0)
                continue;
            Log.Debug("[quazal] re-sending {Count} unacknowledged packet(s) to {Remote} on :{Port}", due.Count, key.Remote, key.LocalPort);
            foreach (byte[] packet in due)
                send(key.Remote, key.LocalPort, packet);
            sent += due.Count;
        }
        return sent;
    }
}
