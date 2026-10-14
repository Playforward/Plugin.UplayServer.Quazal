namespace Plugin.UplayServer.Quazal.Playforward;

/// <summary>
/// Verifies the short-lived login assertions Playforward mints for this title
/// (<c>POST /api/v1/me/game-token</c>).
/// </summary>
internal sealed class PlayforwardTokenVerifier
{
    private readonly string _issuer;
    private readonly string _audience;
    private readonly TimeSpan _clockSkew;

    private PlayforwardTokenVerifier(SigningKeys keys, string issuer, string audience, TimeSpan clockSkew)
    {
        Keys = keys;
        _issuer = issuer;
        _audience = audience;
        _clockSkew = clockSkew;
    }

    /// <summary>
    /// The keys this verifier checks with, so the command verifier shares them
    /// (and their JWKS) rather than fetching a second copy.
    /// </summary>
    public SigningKeys Keys { get; }

    /// <summary>The <c>aud</c> a token must carry here.</summary>
    public string Audience => _audience;

    /// <summary>How far this server's clock and Playforward's may disagree.</summary>
    public TimeSpan ClockSkew => _clockSkew;

    /// <summary>
    /// Builds a verifier from configuration, or returns null with a logged
    /// reason. A null verifier disables Playforward logins rather than failing
    /// the whole plugin: a LAN server configured the old way must keep working.
    /// </summary>
    /// <param name="settings">The half of the configuration shared by every title.</param>
    /// <param name="audience">
    /// The one title's audience. Per title by construction: a verifier that
    /// accepted another title's audience would let that title's server replay
    /// its players' tokens here.
    /// </param>
    /// <param name="keys">The keys to verify with; built from <paramref name="settings"/> when null (a test supplies its own).</param>
    /// <param name="time">The clock a JWKS refresh is paced by; the system's unless a test supplies one.</param>
    public static PlayforwardTokenVerifier? Create(PlayforwardSettings settings, string audience,
        SigningKeys? keys = null, TimeProvider? time = null)
    {
        if (!settings.Enabled)
            return null;

        if (string.IsNullOrWhiteSpace(settings.Issuer) || string.IsNullOrWhiteSpace(audience))
        {
            Log.Error("[playforward] Issuer and the title's audience are both required: an unchecked audience is the same as no separation between titles");
            return null;
        }

        keys ??= SigningKeys.FromSettings(settings, time ?? TimeProvider.System);
        if (keys is null)
            return null;

        Log.Information("[playforward] token verification enabled: issuer={Issuer} audience={Audience}", settings.Issuer, audience);
        return new PlayforwardTokenVerifier(keys, settings.Issuer, audience, TimeSpan.FromSeconds(settings.ClockSkewSeconds));
    }

    /// <summary>
    /// Verifies a token and returns who it vouches for, or null with a logged
    /// reason.
    /// </summary>
    public PlayforwardIdentity? Verify(string token)
    {
        SignedJwt? jwt = SignedJwt.Verify(token, Keys, "token");
        if (jwt is null || !jwt.HasValidEnvelope(_issuer, _audience, _clockSkew, "token"))
            return null;

        string? accountId = jwt.String("account_id");
        string? tokenId = jwt.String("jti");
        if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(tokenId))
        {
            Log.Warning("[playforward] rejected a token with no account_id or jti");
            return null;
        }

        return new PlayforwardIdentity(
            AccountId: accountId,
            DisplayName: jwt.String("display_name") ?? accountId,
            LegacyName: jwt.String("legacy_name") ?? "",
            RetroName: jwt.String("retro_name") ?? "",
            Scope: jwt.String("scope") ?? "user",
            PartyRef: jwt.String("party_ref") ?? "",
            TokenId: tokenId,
            ExpiresAt: jwt.ReadUnixTime("exp") ?? DateTimeOffset.UtcNow);
    }
}
