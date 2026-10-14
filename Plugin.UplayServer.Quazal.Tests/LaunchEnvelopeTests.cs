using System.Security.Cryptography;
using Plugin.UplayServer.Quazal.Playforward;

namespace Plugin.UplayServer.Quazal.Tests;

/// <summary>
/// The wire format the shim has to produce byte for byte, so these pin it.
/// </summary>
public sealed class LaunchEnvelopeTests
{
    private static readonly byte[] LaunchId = RandomNumberGenerator.GetBytes(LaunchEnvelope.LaunchIdLength);
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);
    private static readonly byte[] Challenge = RandomNumberGenerator.GetBytes(LaunchEnvelope.ChallengeLength);

    [Fact]
    public void HelloIsMagicVersionKindAndLaunchId()
    {
        byte[] hello = LaunchEnvelope.Hello(LaunchId);

        // 4 + 1 + 1 + 16: what the shim writes, at the offsets it writes it.
        Assert.Equal(22, hello.Length);
        Assert.Equal("PFWD"u8.ToArray(), hello[..4]);
        Assert.Equal(1, hello[4]);
        Assert.Equal((byte)LaunchEnvelope.Kind.Hello, hello[5]);
        Assert.Equal(LaunchId, hello[6..]);
    }

    [Fact]
    public void ParsesAHelloAndAProof()
    {
        Assert.True(LaunchEnvelope.TryParse(LaunchEnvelope.Hello(LaunchId), out var hello));
        Assert.Equal(LaunchEnvelope.Kind.Hello, hello.Kind);
        Assert.Equal(LaunchId, hello.LaunchId);
        Assert.Empty(hello.Body);

        Assert.True(LaunchEnvelope.TryParse(LaunchEnvelope.Proof(Key, LaunchId, Challenge), out var proof));
        Assert.Equal(LaunchEnvelope.Kind.Proof, proof.Kind);
        Assert.Equal(LaunchId, proof.LaunchId);
        Assert.Equal(LaunchEnvelope.ExpectedProof(Key, LaunchId, Challenge), proof.Body);
    }

    [Fact]
    public void RefusesWhatIsNotOneOfTheShimsMessages()
    {
        // Not ours at all.
        Assert.False(LaunchEnvelope.TryParse("hello"u8, out _));
        Assert.False(LaunchEnvelope.TryParse([], out _));

        // A PRUDP-looking datagram: FC3's SYN opens 0xAF 0xA1.
        Assert.False(LaunchEnvelope.TryParse([0xAF, 0xA1, 0x60, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00], out _));

        // Ours, but a kind only the server sends.
        Assert.False(LaunchEnvelope.TryParse(LaunchEnvelope.Challenge(LaunchId, Challenge), out _));
        Assert.False(LaunchEnvelope.TryParse(LaunchEnvelope.Bound(Key, LaunchId, Challenge), out _));

        // Ours, wrong version.
        byte[] wrongVersion = LaunchEnvelope.Hello(LaunchId);
        wrongVersion[4] = 2;
        Assert.False(LaunchEnvelope.TryParse(wrongVersion, out _));

        // Ours, but a byte long or short: a shim and a server disagreeing on layout.
        Assert.False(LaunchEnvelope.TryParse([.. LaunchEnvelope.Hello(LaunchId), 0], out _));
        Assert.False(LaunchEnvelope.TryParse(LaunchEnvelope.Proof(Key, LaunchId, Challenge)[..^1], out _));
    }

    [Fact]
    public void ProofAndBoundAreDifferentMacsOverTheSameInputs()
    {
        // Domain separation: a captured proof must not double as the server's
        // go-ahead, or the other way round.
        byte[] proof = LaunchEnvelope.ExpectedProof(Key, LaunchId, Challenge);
        Assert.True(LaunchEnvelope.TryReadReply(LaunchEnvelope.Bound(Key, LaunchId, Challenge), LaunchEnvelope.Kind.Bound, LaunchId, out byte[] bound));
        Assert.NotEqual(proof, bound);
    }

    [Fact]
    public void ProofDependsOnEveryInput()
    {
        byte[] proof = LaunchEnvelope.ExpectedProof(Key, LaunchId, Challenge);

        Assert.NotEqual(proof, LaunchEnvelope.ExpectedProof(RandomNumberGenerator.GetBytes(32), LaunchId, Challenge));
        Assert.NotEqual(proof, LaunchEnvelope.ExpectedProof(Key, RandomNumberGenerator.GetBytes(LaunchEnvelope.LaunchIdLength), Challenge));
        Assert.NotEqual(proof, LaunchEnvelope.ExpectedProof(Key, LaunchId, RandomNumberGenerator.GetBytes(LaunchEnvelope.ChallengeLength)));
    }

    [Fact]
    public void RepliesAreReadBackOnlyForTheLaunchTheyName()
    {
        byte[] challenge = LaunchEnvelope.Challenge(LaunchId, Challenge);

        Assert.True(LaunchEnvelope.TryReadReply(challenge, LaunchEnvelope.Kind.Challenge, LaunchId, out byte[] body));
        Assert.Equal(Challenge, body);

        byte[] other = RandomNumberGenerator.GetBytes(LaunchEnvelope.LaunchIdLength);
        Assert.False(LaunchEnvelope.TryReadReply(challenge, LaunchEnvelope.Kind.Challenge, other, out _));
        Assert.False(LaunchEnvelope.TryReadReply(challenge, LaunchEnvelope.Kind.Bound, LaunchId, out _));
    }

    [Fact]
    public void RefusesALaunchIdOfTheWrongWidth()
    {
        Assert.Throws<ArgumentException>(() => LaunchEnvelope.Hello(new byte[LaunchEnvelope.LaunchIdLength - 1]));
    }

    // Fixed vectors, computed with Python's hmac module rather than either
    // implementation, and pinned in UplaySDKShim's tests/launch_envelope_test.cpp
    // too: the shim and this server must agree on these bytes exactly.
    private static readonly byte[] VectorKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] VectorId = Convert.FromHexString("00112233445566778899aabbccddeeff");

    [Fact]
    public void AKeepIsTheVectorBothSidesPin()
    {
        Assert.Equal(
            "50465744010500112233445566778899aabbccddeeff070000000000000070b7ce52f20fadd21f1ed4bee448df4869832050936706d39497fd28a2687e5c",
            Convert.ToHexString(LaunchEnvelope.Keep(VectorKey, VectorId, counter: 7)).ToLowerInvariant());
    }

    [Fact]
    public void ARenewalProofIsTheVectorBothSidesPin()
    {
        Assert.Equal(
            "4ba3d6c3436ccffdd30becf902151dbf5926babe8e177ca3ce9ced96272471ed",
            Convert.ToHexString(LaunchEnvelope.ExpectedRenewal(VectorKey, VectorId, "4b9c2d1e-0000-4000-8000-00000000abcd")).ToLowerInvariant());
    }

    [Fact]
    public void ACheckProofIsTheVectorBothSidesPin()
    {
        Assert.Equal(
            "edce1a06343a503caa67d1602e698bff3eb0b9610cf18b1823833b992cb58ab5",
            Convert.ToHexString(LaunchEnvelope.ExpectedCheck(VectorKey, VectorId)).ToLowerInvariant());
    }
}
