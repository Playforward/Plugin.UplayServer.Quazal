using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace GameClientTestHarness;

/// <summary>
/// Mints Ed25519 tokens shaped like Playforward's game-server assertions, for
/// the self-test path where no real Playforward is involved.
/// </summary>
internal sealed class HarnessTokenSource
{
    public const string Issuer = "playforward-accounts";

    private readonly Ed25519PrivateKeyParameters _privateKey;

    public HarnessTokenSource()
    {
        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = generator.GenerateKeyPair();
        _privateKey = (Ed25519PrivateKeyParameters)pair.Private;
        PublicKeyBase64Url = Base64Url(((Ed25519PublicKeyParameters)pair.Public).GetEncoded());
    }

    public string PublicKeyBase64Url { get; }

    /// <param name="tokenId">The jti; a fresh one unless the caller needs to know it (a renewal's proof covers it).</param>
    public string Mint(string accountId, string audience, string issuer, string displayName = "Harness Player", string legacyName = "harness-player", string retroName = "harness",
        string? tokenId = null)
    {
        var now = DateTimeOffset.UtcNow;
        string header = JsonSerializer.Serialize(new { alg = "EdDSA", kid = "harness" });
        string payload = JsonSerializer.Serialize(new
        {
            account_id = accountId,
            display_name = displayName,
            legacy_name = legacyName,
            retro_name = retroName,
            scope = "user",
            sub = accountId,
            iss = issuer,
            aud = new[] { audience },
            jti = tokenId ?? Guid.NewGuid().ToString(),
            iat = now.ToUnixTimeSeconds(),
            nbf = now.ToUnixTimeSeconds(),
            exp = now.AddMinutes(1).ToUnixTimeSeconds(),
        });

        string signingInput = Base64Url(Encoding.UTF8.GetBytes(header)) + "." + Base64Url(Encoding.UTF8.GetBytes(payload));
        var signer = new Ed25519Signer();
        signer.Init(forSigning: true, _privateKey);
        byte[] content = Encoding.ASCII.GetBytes(signingInput);
        signer.BlockUpdate(content, 0, content.Length);
        return signingInput + "." + Base64Url(signer.GenerateSignature());
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
