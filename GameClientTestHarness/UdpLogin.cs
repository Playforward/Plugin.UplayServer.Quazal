using System.Net.Sockets;
using Plugin.UplayServer.Quazal.Serializables;

namespace GameClientTestHarness;

/// <summary>
/// A RendezVous login over a real UDP socket: SYN, CONNECT, DATA(LoginEx),
/// reading the reply back off the wire.
/// </summary>
internal static class UdpLogin
{
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Datagrams to read after LoginEx before giving up on a result: the
    /// reply may be preceded by a transport ACK.
    /// </summary>
    private const int RepliesToScan = 4;

    /// <summary>Logs in on a socket the caller holds</summary>
    public static async Task<uint?> LoginAsync(UdpClient udp, string username)
    {
        await SendAsync(udp, LoginExClient.Syn());
        PrudpPacketV0 synAck = await ReceiveAsync(udp);
        uint serverSignature = synAck.ConnectionSignature;

        await SendAsync(udp, LoginExClient.Connect(serverSignature));
        await ReceiveAsync(udp); // CONNECT-ACK

        await SendAsync(udp, LoginExClient.LoginEx(username, serverSignature));

        for (int attempt = 0; attempt < RepliesToScan; attempt++)
        {
            PrudpPacketV0 reply = await ReceiveAsync(udp);
            if (LoginExClient.ReadLoginPid(reply) is { } pid)
                return pid;
        }
        return null;
    }

    private static async Task SendAsync(UdpClient udp, PrudpPacketV0 packet)
    {
        byte[] bytes = LoginExClient.ToBytes(packet);
        await udp.SendAsync(bytes, bytes.Length);
    }

    private static async Task<PrudpPacketV0> ReceiveAsync(UdpClient udp)
    {
        using var timeout = new CancellationTokenSource(ReplyTimeout);
        UdpReceiveResult result = await udp.ReceiveAsync(timeout.Token);
        return LoginExClient.Parse(result.Buffer);
    }
}
