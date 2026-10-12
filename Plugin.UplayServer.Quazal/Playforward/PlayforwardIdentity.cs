namespace Plugin.UplayServer.Quazal.Playforward;

/// <summary>
/// A player, as Playforward vouched for them.
/// </summary>
/// <param name="AccountId">Playforward's account id, and the key the PID map is built on.</param>
/// <param name="DisplayName">The modern-tier name (<=32); used by titles whose field fits it.</param>
/// <param name="LegacyName">The <=15 name for Ubisoft-era caps (Far Cry 3, PSN).</param>
/// <param name="RetroName">The <=8 name for PS2/Wii-era caps.</param>
/// <param name="Scope">Their platform-wide role, for the ban and moderation work that comes later.</param>
/// <param name="PartyRef">
/// The group this player is entering the match as, empty when they are entering
/// alone. It is the entire bridge between a party formed in Playforward and a
/// match in a game nobody may modify
/// </param>
/// <param name="TokenId">The token's jti, kept so the same token cannot be redeemed twice.</param>
/// <param name="ExpiresAt">When the token stops being valid, which bounds how long the jti must be remembered.</param>
internal sealed record PlayforwardIdentity(
    string AccountId,
    string DisplayName,
    string LegacyName,
    string RetroName,
    string Scope,
    string PartyRef,
    string TokenId,
    DateTimeOffset ExpiresAt)
{
    /// <summary>
    /// The name shown in a title whose name field holds <paramref name="cap"/>
    /// characters: the largest tier that fits. Names are non-unique and every
    /// tier is always present, so this is a pure length choice
    /// </summary>
    public string NameForCap(int cap)
    {
        if (cap >= 32)
            return DisplayName;
        if (cap >= 15)
            return FirstNonEmpty(LegacyName, DisplayName);
        return FirstNonEmpty(RetroName, LegacyName, DisplayName);
    }

    private static string FirstNonEmpty(params string[] names)
    {
        foreach (string name in names)
        {
            if (!string.IsNullOrEmpty(name))
                return name;
        }
        return "";
    }
}
