using System.Collections.Concurrent;

namespace Plugin.UplayServer.Quazal.Playforward;

/// <summary>
/// The tokens this server has already accepted, so the same one is never
/// accepted twice.
/// </summary>
internal sealed class RedeemedTokens
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _spent = new(StringComparer.Ordinal);

    /// <summary>
    /// Records a verified token as spent, or returns false if it already was.
    /// </summary>
    public bool TryRedeem(PlayforwardIdentity identity)
    {
        Prune();

        if (_spent.TryAdd(identity.TokenId, identity.ExpiresAt))
            return true;

        Log.Warning("[playforward] refused a token that has already been redeemed (account {AccountId})", identity.AccountId);
        return false;
    }

    private void Prune()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (var pair in _spent)
        {
            if (now > pair.Value)
                _spent.TryRemove(pair.Key, out _);
        }
    }
}
