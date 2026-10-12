using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Plugin.UplayServer.Quazal.Playforward;

/// <summary>
/// Holds verified identities against the short codes that stand in for them.
/// </summary>
internal sealed class CorrelationStore
{
    /// <summary>
    /// Entropy behind a code. 16 bytes as hex is 32 characters
    /// </summary>
    private const int CodeEntropyBytes = 16;

    private readonly ConcurrentDictionary<string, Entry> _codes = new(StringComparer.Ordinal);

    /// <summary>
    /// Shared with every other endpoint a token can be presented to, so one
    /// token is worth one code 
    /// </summary>
    private readonly RedeemedTokens _redeemed;

    private readonly TimeSpan _ttl;

    public CorrelationStore(TimeSpan ttl, RedeemedTokens redeemed)
    {
        _ttl = ttl;
        _redeemed = redeemed;
    }

    private sealed record Entry(PlayforwardIdentity Identity, DateTimeOffset ExpiresAt);

    /// <summary>
    /// Registers a verified identity and returns the code that stands for it,
    /// or null if this token has already been used.
    /// </summary>
    public string? Register(PlayforwardIdentity identity)
    {
        Prune();

        if (!_redeemed.TryRedeem(identity))
            return null;

        string code = Convert.ToHexString(RandomNumberGenerator.GetBytes(CodeEntropyBytes)).ToLowerInvariant();
        _codes[code] = new Entry(identity, DateTimeOffset.UtcNow + _ttl);

        Log.Information("[playforward] registered {DisplayName} ({AccountId}); code valid for {Ttl}",
            identity.DisplayName, identity.AccountId, _ttl);
        return code;
    }

    /// <summary>
    /// Exchanges a code for the identity behind it, consuming it.
    /// </summary>
    public PlayforwardIdentity? Redeem(string code)
    {
        Prune();

        if (string.IsNullOrWhiteSpace(code) || !_codes.TryRemove(code, out Entry? entry))
            return null;

        if (DateTimeOffset.UtcNow > entry.ExpiresAt)
        {
            Log.Warning("[playforward] refused an expired correlation code");
            return null;
        }

        return entry.Identity;
    }

    /// <summary>Reports whether a string could be one of our codes at all.</summary>
    public static bool LooksLikeCode(string? username) =>
        username is { Length: CodeEntropyBytes * 2 } &&
        username.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>Drops expired codes.</summary>
    private void Prune()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        foreach (var pair in _codes)
        {
            if (now > pair.Value.ExpiresAt)
                _codes.TryRemove(pair.Key, out _);
        }
    }
}
