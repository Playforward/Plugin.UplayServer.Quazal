using System.Net.Sockets;
using Plugin.UplayServer.Quazal.Playforward;

namespace GameClientTestHarness;

/// <summary>
/// The shim's half of the launch exchange, over a real UDP socket
/// </summary>
internal static class LaunchProver
{
    /// <summary>How long to wait for each reply before saying HELLO again.</summary>
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// HELLO retries before giving up
    /// </summary>
    private const int Attempts = 3;

    /// <summary>Proves the launch on <paramref name="udp"/>, or throws with the step that failed.</summary>
    public static async Task ProveAsync(UdpClient udp, byte[] launchId, byte[] key, Action<string> log)
    {
        for (int attempt = 1; attempt <= Attempts; attempt++)
        {
            byte[] hello = LaunchEnvelope.Hello(launchId);
            await udp.SendAsync(hello, hello.Length);
            log($"HELLO ({hello.Length} bytes), attempt {attempt}");

            byte[]? challengeDatagram = await ReceiveAsync(udp);
            if (challengeDatagram is null)
                continue;
            if (!LaunchEnvelope.TryReadReply(challengeDatagram, LaunchEnvelope.Kind.Challenge, launchId, out byte[] challenge))
                throw new InvalidOperationException($"the server answered HELLO with {challengeDatagram.Length} bytes that are not a CHALLENGE for this launch");
            log($"CHALLENGE ({challengeDatagram.Length} bytes)");

            byte[] proof = LaunchEnvelope.Proof(key, launchId, challenge);
            await udp.SendAsync(proof, proof.Length);
            log($"PROOF ({proof.Length} bytes)");

            byte[]? boundDatagram = await ReceiveAsync(udp);
            if (boundDatagram is null)
                throw new InvalidOperationException("the server did not answer the PROOF: wrong key, or a challenge it no longer holds (it logs which)");
            if (!LaunchEnvelope.TryReadReply(boundDatagram, LaunchEnvelope.Kind.Bound, launchId, out byte[] mac))
                throw new InvalidOperationException($"the server answered PROOF with {boundDatagram.Length} bytes that are not a BOUND for this launch");

            LaunchEnvelope.TryReadReply(LaunchEnvelope.Bound(key, launchId, challenge), LaunchEnvelope.Kind.Bound, launchId, out byte[] expected);
            if (!mac.AsSpan().SequenceEqual(expected))
                throw new InvalidOperationException("the BOUND reply's MAC is wrong: it was not produced with this launch's key");

            log($"BOUND ({boundDatagram.Length} bytes), MAC verified");
            return;
        }

        throw new TimeoutException($"no CHALLENGE after {Attempts} HELLOs: is Playforward login enabled on that server, and is this launch still live?");
    }

    private static async Task<byte[]?> ReceiveAsync(UdpClient udp)
    {
        using var timeout = new CancellationTokenSource(ReplyTimeout);
        try
        {
            return (await udp.ReceiveAsync(timeout.Token)).Buffer;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
