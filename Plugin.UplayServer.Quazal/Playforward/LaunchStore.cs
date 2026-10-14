using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;

namespace Plugin.UplayServer.Quazal.Playforward;

/// <summary>
/// A launch this server has issued: the key the game's shim proves it holds,
/// and the player it stands for.
/// </summary>
/// <param name="Id">Public. Names the launch on the wire; safe for anyone to see.</param>
/// <param name="Key">
/// Secret. Goes back over the HTTPS request that issued it
/// </param>
/// <param name="ExpiresAt">
/// The latest the shim can prove this launch. It ends sooner if its player
/// goes quiet, see <see cref="PlayforwardSettings.LaunchIdleTimeoutSeconds"/>.
/// </param>
/// <param name="Player">Who a socket that proves this launch logs in as.</param>
internal sealed record IssuedLaunch(string Id, byte[] Key, DateTimeOffset ExpiresAt, ResolvedPlayer Player)
{
    /// <summary>The id as it travels in a datagram.</summary>
    public byte[] IdBytes => Convert.FromHexString(Id);
}

/// <summary>
/// Holds issued launches and the challenges outstanding against them, and
/// decides whether a proof is good.
/// </summary>
internal sealed class LaunchStore
{
    /// <summary>The key's width. 32 bytes: HMAC-SHA256's block-independent full-strength key size.</summary>
    private const int KeyLength = 32;

    /// <summary>
    /// How long a challenge waits for its proof. It covers one HMAC in the
    /// shim and one round trip, so this is generous by orders of magnitude;
    /// it is here to bound how long a HELLO nobody follows up occupies memory.
    /// </summary>
    private static readonly TimeSpan ChallengeTtl = TimeSpan.FromSeconds(30);

    /// <summary>
    /// An issued launch and the last sign its player was still around. The
    /// launch is swapped whole (a refresh replaces its player), so readers
    /// never see half of one.
    /// </summary>
    private sealed class LiveLaunch(IssuedLaunch launch, DateTimeOffset now)
    {
        public IssuedLaunch Launch { get; set; } = launch;
        public ActivityStamp LastActive { get; } = new(now);

        /// <summary>
        /// Guards <see cref="BoundTo"/> and <see cref="LastCounter"/>, which a
        /// KEEP checks and moves together.
        /// </summary>
        public object Gate { get; } = new();

        /// <summary>The socket this launch was last proved from: the only one a KEEP is taken from.</summary>
        public IPEndPoint? BoundTo { get; set; }

        /// <summary>The highest KEEP counter accepted since that proof.</summary>
        public ulong LastCounter { get; set; }
    }

    private readonly ConcurrentDictionary<string, LiveLaunch> _launches = new(StringComparer.Ordinal);

    /// <summary>
    /// One outstanding challenge per launch, the latest HELLO winning. A HELLO
    /// is unauthenticated and a launch id is public, so anything that grew per
    /// HELLO would be memory anyone on the network could fill one slot per
    /// launch is bounded by launches issued, which only a token can do.
    /// </summary>
    private readonly ConcurrentDictionary<string, PendingChallenge> _challenges = new(StringComparer.Ordinal);

    private readonly TimeSpan _launchTtl;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeProvider _time;

    /// <param name="launchTtl">The longest any launch lives.</param>
    /// <param name="idleTimeout">How long a launch lives past the last sign of its player.</param>
    /// <param name="time">The clock; the system's unless a test supplies one.</param>
    public LaunchStore(TimeSpan launchTtl, TimeSpan idleTimeout, TimeProvider? time = null)
    {
        _launchTtl = launchTtl;
        _idleTimeout = idleTimeout;
        _time = time ?? TimeProvider.System;
    }

    /// <param name="From">The socket that said HELLO; the only one whose proof will be accepted.</param>
    private sealed record PendingChallenge(byte[] Challenge, IPEndPoint From, DateTimeOffset ExpiresAt);

    /// <summary>Issues a fresh launch for a player.</summary>
    public IssuedLaunch Issue(ResolvedPlayer player)
    {
        Prune();

        DateTimeOffset now = _time.GetUtcNow();
        var launch = new IssuedLaunch(
            Convert.ToHexString(RandomNumberGenerator.GetBytes(LaunchEnvelope.LaunchIdLength)).ToLowerInvariant(),
            RandomNumberGenerator.GetBytes(KeyLength),
            now + _launchTtl,
            player);
        _launches[launch.Id] = new LiveLaunch(launch, now);
        return launch;
    }

    /// <summary>
    /// Answers a HELLO
    /// </summary>
    public byte[]? Challenge(ReadOnlySpan<byte> launchId, IPEndPoint from)
    {
        Prune();

        string id = Convert.ToHexString(launchId).ToLowerInvariant();
        if (!_launches.TryGetValue(id, out LiveLaunch? live))
            return null;

        byte[] challenge = RandomNumberGenerator.GetBytes(LaunchEnvelope.ChallengeLength);
        _challenges[id] = new PendingChallenge(challenge, from, _time.GetUtcNow() + ChallengeTtl);
        return LaunchEnvelope.Challenge(live.Launch.IdBytes, challenge);
    }

    /// <summary>
    /// Checks a PROOF against the challenge outstanding for its launch. On
    /// success the challenge is consumed and the launch's player and the BOUND
    /// reply come back on any failure, null.
    /// </summary>
    public (ResolvedPlayer Player, byte[] Bound)? Prove(ReadOnlySpan<byte> launchId, IPEndPoint from, ReadOnlySpan<byte> proof)
    {
        Prune();

        string id = Convert.ToHexString(launchId).ToLowerInvariant();
        if (!_launches.TryGetValue(id, out LiveLaunch? live) ||
            !_challenges.TryGetValue(id, out PendingChallenge? pending))
        {
            Log.Warning("[playforward] refused a proof for a launch with no challenge outstanding, from {From}", from);
            return null;
        }

        if (!pending.From.Equals(from))
        {
            Log.Warning("[playforward] refused a proof from {From}: the challenge went to {Challenged}", from, pending.From);
            return null;
        }

        IssuedLaunch launch = live.Launch;
        byte[] expected = LaunchEnvelope.ExpectedProof(launch.Key, launch.IdBytes, pending.Challenge);
        if (!CryptographicOperations.FixedTimeEquals(expected, proof))
        {
            Log.Warning("[playforward] refused a proof from {From} that does not answer the challenge", from);
            return null;
        }

        _challenges.TryRemove(id, out _);
        lock (live.Gate)
        {
            live.BoundTo = from;
            live.LastCounter = 0;
        }
        live.LastActive.Touch(_time.GetUtcNow());
        RetireOthersBoundTo(from, id);
        return (launch.Player, LaunchEnvelope.Bound(launch.Key, launch.IdBytes, pending.Challenge));
    }

    /// <summary>
    /// Checks a KEEP: authenticated with the launch's key, carrying a counter
    /// higher than any before it, and from the socket the launch is bound to.
    /// A launch bound to no socket yet is bound to the first one a good KEEP
    /// comes from: that is how a shim whose server restarted, and so forgot its
    /// binding, binds the game's socket again without needing an answer on it.
    /// On success the launch is kept alive and its player comes back; on any
    /// failure, null.
    /// </summary>
    public ResolvedPlayer? Keep(ReadOnlySpan<byte> launchId, IPEndPoint from, ReadOnlySpan<byte> body)
    {
        Prune();

        string id = Convert.ToHexString(launchId).ToLowerInvariant();
        if (!_launches.TryGetValue(id, out LiveLaunch? live))
            return null;

        IssuedLaunch launch = live.Launch;
        (ulong counter, byte[] mac) = LaunchEnvelope.ReadKeep(body);
        if (!CryptographicOperations.FixedTimeEquals(LaunchEnvelope.ExpectedKeep(launch.Key, launch.IdBytes, counter), mac))
        {
            Log.Debug("[playforward] refused a KEEP from {From} that is not authenticated with its launch's key", from);
            return null;
        }

        bool bindsNow;
        lock (live.Gate)
        {
            if (counter <= live.LastCounter)
            {
                Log.Debug("[playforward] refused a KEEP from {From} replaying counter {Counter}", from, counter);
                return null;
            }
            if (live.BoundTo is not null && !live.BoundTo.Equals(from))
            {
                Log.Debug("[playforward] refused a KEEP from {From}: its launch is bound to {Bound}", from, live.BoundTo);
                return null;
            }
            bindsNow = live.BoundTo is null;
            live.BoundTo = from;
            live.LastCounter = counter;
        }

        live.LastActive.Touch(_time.GetUtcNow());
        if (bindsNow)
            RetireOthersBoundTo(from, id);
        return launch.Player;
    }

    /// <summary>
    /// Replaces a bound launch with a fresh one for the same socket, before the
    /// old one reaches its ceiling. The shim asks over HTTPS, with a new token
    /// and <see cref="LaunchEnvelope.ExpectedRenewal"/> under the old key, so no
    /// answer has to come back on the game's socket. Returns the new launch, or
    /// null with the reason logged.
    /// </summary>
    /// <param name="player">Who the new token is for; it must be the old launch's account.</param>
    public IssuedLaunch? Renew(ReadOnlySpan<byte> launchId, ResolvedPlayer player, ReadOnlySpan<byte> proof)
    {
        Prune();

        string id = Convert.ToHexString(launchId).ToLowerInvariant();
        if (!_launches.TryGetValue(id, out LiveLaunch? old))
        {
            Log.Warning("[playforward] refused to renew launch {LaunchId}: this server does not hold it", id);
            return null;
        }
        if (!string.Equals(old.Launch.Player.Identity.AccountId, player.Identity.AccountId, StringComparison.Ordinal))
        {
            Log.Warning("[playforward] refused to renew launch {LaunchId} with a token for another account", id);
            return null;
        }
        byte[] expected = LaunchEnvelope.ExpectedRenewal(old.Launch.Key, old.Launch.IdBytes, player.Identity.TokenId);
        if (!CryptographicOperations.FixedTimeEquals(expected, proof))
        {
            Log.Warning("[playforward] refused to renew launch {LaunchId}: the proof is not made with its key", id);
            return null;
        }

        IPEndPoint? socket;
        lock (old.Gate)
            socket = old.BoundTo;
        if (socket is null)
        {
            Log.Warning("[playforward] refused to renew launch {LaunchId}: it was never bound, so there is nothing to carry over", id);
            return null;
        }

        IssuedLaunch renewed = Issue(player);
        LiveLaunch fresh = _launches[renewed.Id];
        lock (fresh.Gate)
            fresh.BoundTo = socket;
        RetireOthersBoundTo(socket, renewed.Id);
        return renewed;
    }

    /// <summary>
    /// Whether this server still holds a launch, asked by the shim holding its
    /// key (<see cref="LaunchEnvelope.ExpectedCheck"/>). A launch can be
    /// forgotten without the server restarting
    /// </summary>
    public bool Holds(ReadOnlySpan<byte> launchId, ReadOnlySpan<byte> proof)
    {
        Prune();

        string id = Convert.ToHexString(launchId).ToLowerInvariant();
        return _launches.TryGetValue(id, out LiveLaunch? live) &&
               CryptographicOperations.FixedTimeEquals(LaunchEnvelope.ExpectedCheck(live.Launch.Key, live.Launch.IdBytes), proof);
    }

    /// <summary>The socket a launch is bound to, or null.</summary>
    public IPEndPoint? BoundSocket(string launchId)
    {
        if (!_launches.TryGetValue(launchId, out LiveLaunch? live))
            return null;
        lock (live.Gate)
            return live.BoundTo;
    }

    /// <summary>
    /// Drops every other launch proved from a socket, once a new one has been:
    /// a renewal replaces the launch it renews, and one game socket carries
    /// one launch.
    /// </summary>
    private void RetireOthersBoundTo(IPEndPoint socket, string keptId)
    {
        foreach ((string id, LiveLaunch live) in _launches)
        {
            if (id == keptId)
                continue;
            bool sameSocket;
            lock (live.Gate)
                sameSocket = socket.Equals(live.BoundTo);
            if (sameSocket)
            {
                _launches.TryRemove(id, out _);
                _challenges.TryRemove(id, out _);
            }
        }
    }

    /// <summary>
    /// Replaces the player behind every live launch of an account, so a proof
    /// made after the player's claims changed binds the current ones
    /// </summary>
    public void Refresh(ResolvedPlayer player)
    {
        DateTimeOffset now = _time.GetUtcNow();
        foreach (LiveLaunch live in _launches.Values)
        {
            if (string.Equals(live.Launch.Player.Identity.AccountId, player.Identity.AccountId, StringComparison.Ordinal))
            {
                live.Launch = live.Launch with { Player = player };
                live.LastActive.Touch(now);
            }
        }
    }

    /// <summary>
    /// Keeps alive every launch of a player whose game is talking to this
    /// server right now, so a game whose desktop client has closed can still
    /// rejoin after a drop.
    /// </summary>
    public void KeepAlive(IReadOnlySet<uint> livePids)
    {
        DateTimeOffset now = _time.GetUtcNow();
        foreach (LiveLaunch live in _launches.Values)
        {
            if (livePids.Contains(live.Launch.Player.Pid))
                live.LastActive.Touch(now);
        }
        Prune();
    }

    /// <summary>Drops every launch of an account, live or not.</summary>
    public void Forget(string accountId)
    {
        foreach ((string id, LiveLaunch live) in _launches)
        {
            if (string.Equals(live.Launch.Player.Identity.AccountId, accountId, StringComparison.Ordinal))
            {
                _launches.TryRemove(id, out _);
                _challenges.TryRemove(id, out _);
            }
        }
    }

    /// <summary>
    /// Drops launches past their ceiling or idle past their timeout, and
    /// challenges nobody answered.
    /// </summary>
    private void Prune()
    {
        DateTimeOffset now = _time.GetUtcNow();

        foreach ((string id, LiveLaunch live) in _launches)
        {
            if (now > live.Launch.ExpiresAt || live.LastActive.IdleLongerThan(_idleTimeout, now))
            {
                _launches.TryRemove(id, out _);
                _challenges.TryRemove(id, out _);
            }
        }
        foreach ((string id, PendingChallenge pending) in _challenges)
        {
            if (now > pending.ExpiresAt)
                _challenges.TryRemove(id, out _);
        }
    }
}
