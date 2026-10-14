using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;

namespace Plugin.UplayServer.Quazal.Playforward;

/// <summary>
/// The public keys Playforward's tokens and commands are verified with, by the
/// key id (<c>kid</c>) each one names in its header.
/// </summary>
internal abstract class SigningKeys
{
    public abstract Ed25519PublicKeyParameters? ForKeyId(string? keyId);

    public abstract IReadOnlyCollection<string> KeyIds { get; }

    public static string KeyIdOf(Ed25519PublicKeyParameters key) =>
        System.Buffers.Text.Base64Url.EncodeToString(SHA256.HashData(key.GetEncoded()));

    public static SigningKeys? FromSettings(PlayforwardSettings settings, TimeProvider time)
    {
        if (!string.IsNullOrWhiteSpace(settings.PublicKeyBase64))
            return PinnedSigningKey.Parse(settings.PublicKeyBase64);
        return JwksSigningKeys.Create(settings, time);
    }
}

/// <summary>
/// One key from configuration, for a LAN server with no route to Playforward.
/// It is used whatever key id a token names, since a pinned key is the whole
/// of what this server trusts; a rotation means updating the setting.
/// </summary>
internal sealed class PinnedSigningKey(Ed25519PublicKeyParameters key) : SigningKeys
{
    public override IReadOnlyCollection<string> KeyIds { get; } = [KeyIdOf(key)];

    public override Ed25519PublicKeyParameters? ForKeyId(string? keyId) => key;

    public static PinnedSigningKey? Parse(string encoded)
    {
        try
        {
            byte[] raw = SignedJwt.Base64UrlDecode(encoded.Trim());
            if (raw.Length != Ed25519PublicKeyParameters.KeySize)
            {
                Log.Error("[playforward] PublicKeyBase64 is {Length} bytes, expected {Expected}: this must be the raw key from the JWKS \"x\" value, not a PEM or a DER wrapper",
                    raw.Length, Ed25519PublicKeyParameters.KeySize);
                return null;
            }
            return new PinnedSigningKey(new Ed25519PublicKeyParameters(raw));
        }
        catch (FormatException exception)
        {
            Log.Error("[playforward] PublicKeyBase64 is not valid base64 or base64url: {Error}", exception.Message);
            return null;
        }
    }
}

/// <summary>
/// Playforward's published keys, fetched from its JWKS and kept by key id, so
/// a signing-key rotation needs nothing done on any game server: the new key
/// is fetched the first time a token names it, and a retired or stolen key
/// drops out on the next refresh.
/// </summary>
internal sealed class JwksSigningKeys : SigningKeys
{
    private const string JwksPath = "/.well-known/jwks.json";

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);

    private static readonly TimeSpan UnknownKeyRefetchInterval = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly Uri _jwks;
    private readonly TimeProvider _time;
    private readonly object _fetchGate = new();

    private volatile IReadOnlyDictionary<string, Ed25519PublicKeyParameters> _keys =
        new Dictionary<string, Ed25519PublicKeyParameters>(StringComparer.Ordinal);
    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSuccess = DateTimeOffset.MinValue;

    private DateTimeOffset _lastUnknownKeyFetch = DateTimeOffset.MinValue;

    internal JwksSigningKeys(HttpClient http, Uri jwks, TimeProvider time)
    {
        _http = http;
        _jwks = jwks;
        _time = time;
    }

    public override IReadOnlyCollection<string> KeyIds
    {
        get
        {
            RefreshIfDue();
            return _keys.Keys.ToArray();
        }
    }

    public override Ed25519PublicKeyParameters? ForKeyId(string? keyId)
    {
        if (string.IsNullOrEmpty(keyId))
        {
            Log.Warning("[playforward] rejected a token naming no key id: every key Playforward signs with has one");
            return null;
        }

        RefreshIfDue();
        if (_keys.TryGetValue(keyId, out Ed25519PublicKeyParameters? key))
            return key;

        if (TryClaimUnknownKeyFetch())
        {
            Fetch(TimeSpan.Zero);
            if (_keys.TryGetValue(keyId, out key))
                return key;
        }

        Log.Warning("[playforward] rejected a token signed with key {KeyId}, which Playforward does not publish", keyId);
        return null;
    }

    /// <summary>Builds the source from settings, or returns null with the reason logged.</summary>
    public static JwksSigningKeys? Create(PlayforwardSettings settings, TimeProvider time)
    {
        if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out Uri? baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttps && !(baseUri.Scheme == Uri.UriSchemeHttp && IsLoopback(baseUri))))
        {
            Log.Error("[playforward] no PublicKeyBase64, and BaseUrl ({BaseUrl}) is not https: the keys tokens are checked with are only fetched over TLS, or from this machine -- pin the key with PublicKeyBase64 on a LAN without it",
                settings.BaseUrl);
            return null;
        }

        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(settings.RequestTimeoutSeconds) };
        var keys = new JwksSigningKeys(http, new Uri(baseUri, JwksPath), time);
        keys.Fetch(TimeSpan.Zero);
        return keys;
    }

    private static bool IsLoopback(Uri uri) =>
        uri.IsLoopback || (IPAddress.TryParse(uri.Host, out IPAddress? address) && IPAddress.IsLoopback(address));

    private bool TryClaimUnknownKeyFetch()
    {
        lock (_fetchGate)
        {
            DateTimeOffset now = _time.GetUtcNow();
            if (now - _lastUnknownKeyFetch < UnknownKeyRefetchInterval)
                return false;
            _lastUnknownKeyFetch = now;
            return true;
        }
    }

    private void RefreshIfDue()
    {
        if (_time.GetUtcNow() - _lastSuccess >= RefreshInterval)
            Fetch(UnknownKeyRefetchInterval);
    }

    private void Fetch(TimeSpan minimumGap)
    {
        lock (_fetchGate)
        {
            DateTimeOffset now = _time.GetUtcNow();
            if (now - _lastAttempt < minimumGap)
                return;
            _lastAttempt = now;

            try
            {
                string body = _http.GetStringAsync(_jwks).GetAwaiter().GetResult();
                Dictionary<string, Ed25519PublicKeyParameters> keys = Parse(body);
                if (keys.Count == 0)
                {
                    Log.Error("[playforward] {Jwks} lists no Ed25519 signing key; keeping the {Count} held", _jwks, _keys.Count);
                    return;
                }
                _keys = keys;
                _lastSuccess = now;
                Log.Information("[playforward] verifying tokens with key(s) {KeyIds} from {Jwks}", string.Join(", ", keys.Keys), _jwks);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
            {
                Log.Warning("[playforward] could not read {Jwks} ({Error}); keeping the {Count} key(s) held", _jwks, exception.Message, _keys.Count);
            }
        }
    }

    /// <summary>
    /// The Ed25519 signing keys in a JWKS document, by key id.
    /// </summary>
    internal static Dictionary<string, Ed25519PublicKeyParameters> Parse(string json)
    {
        var keys = new Dictionary<string, Ed25519PublicKeyParameters>(StringComparer.Ordinal);
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("keys", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
            return keys;

        foreach (JsonElement key in list.EnumerateArray())
        {
            if (Text(key, "kty") != "OKP" || Text(key, "crv") != "Ed25519" ||
                Text(key, "use") is { } use && use != "sig" ||
                Text(key, "alg") is { } alg && alg != "EdDSA" ||
                Text(key, "kid") is not { Length: > 0 } keyId ||
                Text(key, "x") is not { } x)
                continue;

            try
            {
                byte[] raw = SignedJwt.Base64UrlDecode(x);
                if (raw.Length == Ed25519PublicKeyParameters.KeySize)
                    keys[keyId] = new Ed25519PublicKeyParameters(raw);
            }
            catch (FormatException)
            {
                // A key this server cannot read is one it does not trust.
            }
        }
        return keys;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
