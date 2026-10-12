using System.Net;
using System.Net.Sockets;
using Plugin.UplayServer.Quazal;
using Plugin.UplayServer.Quazal.Playforward;
using Plugin.UplayServer.Quazal.Serializables;

namespace GameClientTestHarness;

/// <summary>
/// The server's UDP dispatch on a real loopback socket, for the self-test:
/// what <c>UplayQuazalPlugin.OnUdpBytes</c> does, minus ServerApp.
/// </summary>
internal sealed class LoopbackServer : IDisposable
{
    private readonly UdpClient _udp;
    private readonly PlayforwardGateway _gateway;
    private readonly QuazalResponder _responder;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _loop;

    /// <param name="port">The UDP port to listen on; 0 for any free one.</param>
    public LoopbackServer(PlayforwardGateway gateway, QuazalResponder responder, int port = 0)
    {
        _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        _gateway = gateway;
        _responder = responder;
        _loop = Task.Run(ServeAsync);
    }

    public IPEndPoint EndPoint => (IPEndPoint)_udp.Client.LocalEndPoint!;

    private async Task ServeAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await _udp.ReceiveAsync(_stopping.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            byte[]? launchReply = _gateway.HandleLaunchDatagram(received.Buffer, received.RemoteEndPoint, out bool wasLaunch);
            if (wasLaunch)
            {
                if (launchReply is not null)
                    await _udp.SendAsync(launchReply, launchReply.Length, received.RemoteEndPoint);
                continue;
            }

            (int width, var algorithm) = _responder.ResolveDialect(received.RemoteEndPoint, EndPoint.Port, received.Buffer);
            if (!PrudpPacketV0.TryDeserialize(received.Buffer, out PrudpPacketV0 packet, width, algorithm))
                continue;
            foreach (byte[] reply in _responder.Handle(packet, received.RemoteEndPoint, EndPoint.Port, received.Buffer))
                await _udp.SendAsync(reply, reply.Length, received.RemoteEndPoint);
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _udp.Dispose();
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _stopping.Dispose();
    }
}
