using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using Plugin.UplayServer.Quazal.Playforward;

namespace Plugin.UplayServer.Quazal.Tests;

/// <summary>
/// Mints Ed25519 tokens shaped exactly like Playforward's game-server
/// assertions, so the verifier can be tested against tokens this test controls
/// rather than against a running accounts service.
/// </summary>
internal sealed class PlayforwardTokenFactory
{
    public const string Issuer = "playforward-accounts";
    public const string Audience = "playforward-game-servers:farcry3";

    private readonly Ed25519PrivateKeyParameters _privateKey;

    public PlayforwardTokenFactory()
    {
        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = generator.GenerateKeyPair();
        _privateKey = (Ed25519PrivateKeyParameters)pair.Private;
        PublicKeyBase64Url = Base64UrlEncode(((Ed25519PublicKeyParameters)pair.Public).GetEncoded());
    }

    /// <summary>The public half, encoded the way JWKS presents it.</summary>
    public string PublicKeyBase64Url { get; }

    public string Mint(
        string accountId,
        string displayName = "Far Cry Player",
        string legacyName = "far-cry-player",
        string retroName = "farcryp",
        string scope = "user",
        string? partyRef = null,
        string? issuer = null,
        string? audience = null,
        string algorithm = "EdDSA",
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? notBefore = null,
        string? tokenId = null,
        string? keyId = null)
    {
        var now = DateTimeOffset.UtcNow;
        string payload = JsonSerializer.Serialize(new
        {
            account_id = accountId,
            display_name = displayName,
            legacy_name = legacyName,
            retro_name = retroName,
            scope,
            // Omitted when there is no party, exactly as Playforward omits it:
            // the claim is absent for somebody entering a match alone rather
            // than present and empty.
            party_ref = string.IsNullOrEmpty(partyRef) ? null : partyRef,
            sub = accountId,
            iss = issuer ?? Issuer,
            aud = new[] { audience ?? Audience },
            jti = tokenId ?? Guid.NewGuid().ToString(),
            iat = now.ToUnixTimeSeconds(),
            nbf = (notBefore ?? now).ToUnixTimeSeconds(),
            exp = (expiresAt ?? now.AddMinutes(1)).ToUnixTimeSeconds(),
        }, OmitNulls);

        return Sign(payload, algorithm, keyId);
    }

    /// <summary>
    /// Mints a moderation command, shaped like playforward's
    /// internal/auth/server_command.go.
    /// </summary>
    public string MintCommand(
        string targetAccountId,
        string action = "kick",
        Guid? actionId = null,
        string nativeSessionId = "1-1",
        string reason = "test",
        DateTimeOffset? enforceUntil = null,
        string? issuer = null,
        string? audience = null,
        string algorithm = "EdDSA",
        DateTimeOffset? expiresAt = null)
    {
        var now = DateTimeOffset.UtcNow;
        Guid id = actionId ?? Guid.NewGuid();
        string payload = JsonSerializer.Serialize(new
        {
            action_id = id,
            session_id = Guid.NewGuid(),
            native_session_id = nativeSessionId,
            target_account_id = targetAccountId,
            action,
            reason,
            enforce_until = enforceUntil?.ToUnixTimeSeconds(),
            sub = targetAccountId,
            iss = issuer ?? Issuer,
            aud = new[] { audience ?? Audience },
            jti = id.ToString(),
            iat = now.ToUnixTimeSeconds(),
            nbf = now.ToUnixTimeSeconds(),
            exp = (expiresAt ?? now.AddMinutes(2)).ToUnixTimeSeconds(),
        }, OmitNulls);

        return Sign(payload, algorithm);
    }

    /// <summary>The public half as BouncyCastle parameters, for verifiers that
    /// take a key rather than a configuration block.</summary>
    public Ed25519PublicKeyParameters PublicKey => new(Base64UrlDecode(PublicKeyBase64Url));

    private static readonly JsonSerializerOptions OmitNulls = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>This key's id, derived as Playforward derives one (internal/auth/signingkey.go).</summary>
    public string KeyId => SigningKeys.KeyIdOf(PublicKey);

    /// <summary>A JWKS document publishing this key, shaped like Playforward's.</summary>
    public string Jwks() => JsonSerializer.Serialize(new
    {
        keys = new[] { new { kty = "OKP", crv = "Ed25519", x = PublicKeyBase64Url, use = "sig", alg = "EdDSA", kid = KeyId } },
    });

    /// <param name="keyId">The header's <c>kid</c>; this key's own unless a test names another.</param>
    private string Sign(string payload, string algorithm, string? keyId = null)
    {
        string header = JsonSerializer.Serialize(new
        {
            alg = algorithm,
            kid = keyId ?? KeyId,
        });

        string signingInput =
            Base64UrlEncode(Encoding.UTF8.GetBytes(header)) + "." +
            Base64UrlEncode(Encoding.UTF8.GetBytes(payload));

        byte[] content = Encoding.ASCII.GetBytes(signingInput);
        var signer = new Ed25519Signer();
        signer.Init(forSigning: true, _privateKey);
        signer.BlockUpdate(content, 0, content.Length);

        return signingInput + "." + Base64UrlEncode(signer.GenerateSignature());
    }

    /// <summary>Mints a token and then corrupts its signature.</summary>
    public string MintWithBrokenSignature(string accountId)
    {
        string[] parts = Mint(accountId).Split('.');
        byte[] signature = Base64UrlDecode(parts[2]);
        signature[0] ^= 0xFF;
        return parts[0] + "." + parts[1] + "." + Base64UrlEncode(signature);
    }

    public static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        string standard = value.Replace('-', '+').Replace('_', '/');
        int padding = (4 - (standard.Length % 4)) % 4;
        return Convert.FromBase64String(standard + new string('=', padding));
    }
}
