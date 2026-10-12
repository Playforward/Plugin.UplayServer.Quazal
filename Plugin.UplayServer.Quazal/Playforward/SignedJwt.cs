using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Plugin.UplayServer.Quazal.Playforward;

/// <summary>
/// One Ed25519-signed JWT from Playforward, parsed and signature-checked.
/// </summary>
internal sealed class SignedJwt
{
    /// <summary>The only algorithm accepted. See the class remarks.</summary>
    private const string ExpectedAlgorithm = "EdDSA";

    private SignedJwt(JsonElement payload) => Payload = payload;

    /// <summary>The verified claims.</summary>
    public JsonElement Payload { get; }

    /// <summary>
    /// Parses a token and verifies its signature, or returns null with a logged
    /// reason. Nothing about the claims is checked here.
    /// </summary>
    /// <param name="token">The compact-serialised JWT.</param>
    /// <param name="publicKey">Playforward's Ed25519 public key.</param>
    /// <param name="what">
    /// What is being verified, for the log line. A refusal is worth being able
    /// to attribute to a login or to a command without reading the code.
    /// </param>
    public static SignedJwt? Verify(string token, Ed25519PublicKeyParameters publicKey, string what)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        string[] parts = token.Split('.');
        if (parts.Length != 3)
        {
            Log.Warning("[playforward] rejected a {What} that is not three dot-separated segments", what);
            return null;
        }

        JsonElement header, payload;
        byte[] signature;
        try
        {
            header = JsonDocument.Parse(Base64UrlDecode(parts[0])).RootElement;
            payload = JsonDocument.Parse(Base64UrlDecode(parts[1])).RootElement;
            signature = Base64UrlDecode(parts[2]);
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            Log.Warning("[playforward] rejected a malformed {What}: {Error}", what, exception.Message);
            return null;
        }

        if (ReadString(header, "alg") != ExpectedAlgorithm)
        {
            Log.Warning("[playforward] rejected a {What} whose alg is not {Expected}", what, ExpectedAlgorithm);
            return null;
        }

        byte[] signedContent = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        var verifier = new Ed25519Signer();
        verifier.Init(forSigning: false, publicKey);
        verifier.BlockUpdate(signedContent, 0, signedContent.Length);
        if (!verifier.VerifySignature(signature))
        {
            Log.Warning("[playforward] rejected a {What} whose signature does not verify against the configured key", what);
            return null;
        }

        return new SignedJwt(payload);
    }

    /// <summary>
    /// Checks issuer, audience and the two time bounds
    /// </summary>
    public bool HasValidEnvelope(string issuer, string audience, TimeSpan clockSkew, string what)
    {
        if (ReadString(Payload, "iss") != issuer)
        {
            Log.Warning("[playforward] rejected a {What} from an unexpected issuer", what);
            return false;
        }

        if (!AudienceMatches(audience))
        {
            Log.Warning("[playforward] rejected a {What} minted for a different audience", what);
            return false;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (ReadUnixTime("exp") is not { } expiresAt || now - clockSkew > expiresAt)
        {
            Log.Warning("[playforward] rejected an expired {What}", what);
            return false;
        }
        if (ReadUnixTime("nbf") is { } notBefore && now + clockSkew < notBefore)
        {
            Log.Warning("[playforward] rejected a {What} that is not valid yet", what);
            return false;
        }

        return true;
    }

    /// <summary>The audience claim is a string or an array of them, per RFC 7519.</summary>
    private bool AudienceMatches(string audience)
    {
        if (!Payload.TryGetProperty("aud", out JsonElement value))
            return false;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() == audience,
            JsonValueKind.Array => value.EnumerateArray()
                .Any(entry => entry.ValueKind == JsonValueKind.String && entry.GetString() == audience),
            _ => false,
        };
    }

    /// <summary>
    /// The audiences a token claims, read without verifying anything.
    /// </summary>
    public static IReadOnlyList<string> PeekAudiences(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return [];
        string[] parts = token.Split('.');
        if (parts.Length != 3)
            return [];
        try
        {
            using JsonDocument payload = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            if (!payload.RootElement.TryGetProperty("aud", out JsonElement value))
                return [];
            return value.ValueKind switch
            {
                JsonValueKind.String => [value.GetString()!],
                JsonValueKind.Array => [.. value.EnumerateArray()
                    .Where(entry => entry.ValueKind == JsonValueKind.String)
                    .Select(entry => entry.GetString()!)],
                _ => [],
            };
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return [];
        }
    }

    /// <summary>A string claim, or null when absent or of another type.</summary>
    public string? String(string name) => ReadString(Payload, name);

    /// <summary>A unix-time claim as an instant, or null.</summary>
    public DateTimeOffset? ReadUnixTime(string name) =>
        Payload.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeSeconds(value.GetInt64())
            : null;

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// base64url, unpadded, as JWT uses throughout
    /// </summary>
    public static byte[] Base64UrlDecode(string value)
    {
        string standard = value.Replace('-', '+').Replace('_', '/').TrimEnd('=');
        int padding = (4 - (standard.Length % 4)) % 4;
        return Convert.FromBase64String(standard + new string('=', padding));
    }
}
