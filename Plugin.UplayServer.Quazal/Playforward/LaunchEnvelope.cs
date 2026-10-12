using System.Security.Cryptography;
using System.Text;

namespace Plugin.UplayServer.Quazal.Playforward;

/// <summary>
/// The datagrams a game's shim exchanges with this server, on the game's own
/// UDP socket, to prove which launch it is before the game logs in.
/// </summary>
internal static class LaunchEnvelope
{
    /// <summary>
    /// Leads every message. Chosen so no PRUDP V0 packet starts this way: PRUDP
    /// opens with two virtual-port bytes whose high nibbles are stream types,
    /// and 'P' 'F' (0x50, 0x46) would be a NAT stream talking to an SBMGMT
    /// stream, a pairing no client sends. Checked before PRUDP parsing anyway.
    /// </summary>
    private static readonly byte[] Magic = "PFWD"u8.ToArray();

    /// <summary>Bumped if the layout ever changes, so a shim and a server disagreeing is a clean refusal.</summary>
    private const byte Version = 1;

    private const int MagicOffset = 0;
    private const int VersionOffset = MagicOffset + 4;
    private const int KindOffset = VersionOffset + 1;
    private const int LaunchIdOffset = KindOffset + 1;

    /// <summary>The raw bytes of a launch id, as the shim carries it.</summary>
    public const int LaunchIdLength = 16;

    /// <summary>
    /// A challenge. 32 bytes: the width of the HMAC that answers it, and far
    /// past any chance of one being issued twice in a server's lifetime.
    /// </summary>
    public const int ChallengeLength = 32;

    /// <summary>HMAC-SHA256 output.</summary>
    public const int MacLength = HMACSHA256.HashSizeInBytes;

    private const int HeaderLength = LaunchIdOffset + LaunchIdLength;
    private const int BodyOffset = HeaderLength;

    /// <summary>Domain separation, so a proof can never be mistaken for a bound reply or anything else keyed the same way.</summary>
    private static readonly byte[] ProofPrefix = Encoding.ASCII.GetBytes("playforward-launch-proof");
    private static readonly byte[] BoundPrefix = Encoding.ASCII.GetBytes("playforward-launch-bound");

    public enum Kind : byte
    {
        Hello = 1,
        Challenge = 2,
        Proof = 3,
        Bound = 4,
    }

    /// <summary>A parsed inbound message: what it is, which launch it names, and its body.</summary>
    /// <param name="Body">Empty for HELLO; the proof for PROOF.</param>
    public readonly record struct Message(Kind Kind, byte[] LaunchId, byte[] Body);

    /// <summary>
    /// Reads a datagram as one of the shim's messages, or returns false for
    /// anything else -- including one of our own outbound kinds, which a
    /// client has no business sending.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> datagram, out Message message)
    {
        message = default;

        if (datagram.Length < HeaderLength ||
            !datagram.Slice(MagicOffset, Magic.Length).SequenceEqual(Magic) ||
            datagram[VersionOffset] != Version)
        {
            return false;
        }

        var kind = (Kind)datagram[KindOffset];
        int bodyLength = kind switch
        {
            Kind.Hello => 0,
            Kind.Proof => MacLength,
            _ => -1,
        };
        if (bodyLength < 0 || datagram.Length != HeaderLength + bodyLength)
            return false;

        message = new Message(
            kind,
            datagram.Slice(LaunchIdOffset, LaunchIdLength).ToArray(),
            datagram.Slice(BodyOffset, bodyLength).ToArray());
        return true;
    }

    /// <summary>The CHALLENGE for a launch.</summary>
    public static byte[] Challenge(ReadOnlySpan<byte> launchId, ReadOnlySpan<byte> challenge) =>
        Build(Kind.Challenge, launchId, challenge);

    /// <summary>The BOUND reply, proving to the shim that this server holds the launch key.</summary>
    public static byte[] Bound(ReadOnlySpan<byte> key, ReadOnlySpan<byte> launchId, ReadOnlySpan<byte> challenge) =>
        Build(Kind.Bound, launchId, Mac(key, BoundPrefix, launchId, challenge));

    /// <summary>What a shim holding <paramref name="key"/> must answer <paramref name="challenge"/> with.</summary>
    public static byte[] ExpectedProof(ReadOnlySpan<byte> key, ReadOnlySpan<byte> launchId, ReadOnlySpan<byte> challenge) =>
        Mac(key, ProofPrefix, launchId, challenge);

    /// <summary>
    /// The HELLO a shim sends. Here for the test harness and the tests; the
    /// real sender is C++ inside the game process.
    /// </summary>
    public static byte[] Hello(ReadOnlySpan<byte> launchId) =>
        Build(Kind.Hello, launchId, ReadOnlySpan<byte>.Empty);

    /// <summary>The PROOF a shim sends. Same reason as <see cref="Hello"/>.</summary>
    public static byte[] Proof(ReadOnlySpan<byte> key, ReadOnlySpan<byte> launchId, ReadOnlySpan<byte> challenge) =>
        Build(Kind.Proof, launchId, ExpectedProof(key, launchId, challenge));

    /// <summary>Reads the body of an outbound CHALLENGE or BOUND, for a client that receives one.</summary>
    public static bool TryReadReply(ReadOnlySpan<byte> datagram, Kind expected, ReadOnlySpan<byte> launchId, out byte[] body)
    {
        body = [];
        int bodyLength = expected switch
        {
            Kind.Challenge => ChallengeLength,
            Kind.Bound => MacLength,
            _ => -1,
        };
        if (bodyLength < 0 || datagram.Length != HeaderLength + bodyLength ||
            !datagram.Slice(MagicOffset, Magic.Length).SequenceEqual(Magic) ||
            datagram[VersionOffset] != Version ||
            datagram[KindOffset] != (byte)expected ||
            !datagram.Slice(LaunchIdOffset, LaunchIdLength).SequenceEqual(launchId))
        {
            return false;
        }
        body = datagram.Slice(BodyOffset, bodyLength).ToArray();
        return true;
    }

    private static byte[] Build(Kind kind, ReadOnlySpan<byte> launchId, ReadOnlySpan<byte> body)
    {
        if (launchId.Length != LaunchIdLength)
            throw new ArgumentException($"a launch id is {LaunchIdLength} bytes", nameof(launchId));

        byte[] datagram = new byte[HeaderLength + body.Length];
        Magic.CopyTo(datagram, MagicOffset);
        datagram[VersionOffset] = Version;
        datagram[KindOffset] = (byte)kind;
        launchId.CopyTo(datagram.AsSpan(LaunchIdOffset));
        body.CopyTo(datagram.AsSpan(BodyOffset));
        return datagram;
    }

    private static byte[] Mac(ReadOnlySpan<byte> key, byte[] prefix, ReadOnlySpan<byte> launchId, ReadOnlySpan<byte> challenge)
    {
        byte[] input = new byte[prefix.Length + launchId.Length + challenge.Length];
        prefix.CopyTo(input, 0);
        launchId.CopyTo(input.AsSpan(prefix.Length));
        challenge.CopyTo(input.AsSpan(prefix.Length + launchId.Length));
        return HMACSHA256.HashData(key, input);
    }
}
