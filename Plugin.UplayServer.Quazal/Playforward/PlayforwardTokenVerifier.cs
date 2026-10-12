using Org.BouncyCastle.Crypto.Parameters;

namespace Plugin.UplayServer.Quazal.Playforward;

/// <summary>
/// Verifies the short-lived login assertions Playforward mints for this title
/// (<c>POST /api/v1/me/game-token</c>).
/// </summary>
internal sealed class PlayforwardTokenVerifier
{
    private readonly Ed25519PublicKeyParameters _publicKey;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly TimeSpan _clockSkew;

    private PlayforwardTokenVerifier(Ed25519PublicKeyParameters publicKey, string issuer, string audience, TimeSpan clockSkew)
    {
        _publicKey = publicKey;
        _issuer = issuer;
        _audience = audience;
        _clockSkew = clockSkew;
    }

    /// <summary>
    /// The key this verifier holds, so the command verifier shares it rather
    /// than decoding the configured value a second time.
    /// </summary>
    public Ed25519PublicKeyParameters PublicKey => _publicKey;

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
    public static PlayforwardTokenVerifier? Create(PlayforwardSettings settings, string audience)
    {
        if (!settings.Enabled)
            return null;

        if (string.IsNullOrWhiteSpace(settings.PublicKeyBase64))
        {
            Log.Error("[playforward] enabled but PublicKeyBase64 is empty: no token can be verified");
            return null;
        }
        if (string.IsNullOrWhiteSpace(settings.Issuer) || string.IsNullOrWhiteSpace(audience))
        {
            Log.Error("[playforward] Issuer and the title's audience are both required: an unchecked audience is the same as no separation between titles");
            return null;
        }

        try
        {
            byte[] raw = SignedJwt.Base64UrlDecode(settings.PublicKeyBase64.Trim());
            if (raw.Length != Ed25519PublicKeyParameters.KeySize)
            {
                Log.Error("[playforward] PublicKeyBase64 is {Length} bytes, expected {Expected}: this must be the raw key from the JWKS \"x\" value, not a PEM or a DER wrapper",
                    raw.Length, Ed25519PublicKeyParameters.KeySize);
                return null;
            }

            var key = new Ed25519PublicKeyParameters(raw);
            Log.Information("[playforward] token verification enabled: issuer={Issuer} audience={Audience}", settings.Issuer, audience);
            return new PlayforwardTokenVerifier(key, settings.Issuer, audience, TimeSpan.FromSeconds(settings.ClockSkewSeconds));
        }
        catch (FormatException exception)
        {
            Log.Error("[playforward] PublicKey is not valid base64 or base64url: {Error}", exception.Message);
            return null;
        }
    }

    /// <summary>
    /// Verifies a token and returns who it vouches for, or null with a logged
    /// reason.
    /// </summary>
    public PlayforwardIdentity? Verify(string token)
    {
        SignedJwt? jwt = SignedJwt.Verify(token, _publicKey, "token");
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
