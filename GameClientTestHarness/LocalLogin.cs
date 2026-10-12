using System.Net;
using Plugin.UplayServer.Quazal;
using Plugin.UplayServer.Quazal.Serializables;

namespace GameClientTestHarness;

/// <summary>
/// Drives a LoginEx through a responder in-process
/// </summary>
internal static class LocalLogin
{
    private static readonly IPEndPoint Source = new(IPAddress.Loopback, 61000);
    private const int LocalPort = 1;

    /// <summary>The address logins come from, so a pre-bind can target it.</summary>
    public static IPAddress SourceAddress => Source.Address;

    /// <summary>The socket logins come from, so a launch can be proved on it.</summary>
    public static IPEndPoint SourceEndpoint => Source;

    public static uint? Run(QuazalResponder responder, string correlationCode)
        => Run(responder, correlationCode, null, Source);

    /// <param name="forceSignature">
    /// When set, the LoginEx DATA carries this instead of the server's real
    /// connection signature
    /// </param>
    /// <param name="from">
    /// The endpoint the login appears to come from.
    /// </param>
    public static uint? Run(QuazalResponder responder, string correlationCode, uint? forceSignature, IPEndPoint from)
    {
        PrudpPacketV0 syn = LoginExClient.Syn();
        var synReplies = responder.Handle(syn, from, LocalPort, LoginExClient.ToBytes(syn));
        if (synReplies.Count == 0)
            return null;
        uint serverSignature = LoginExClient.Parse(synReplies[0]).ConnectionSignature;

        PrudpPacketV0 connect = LoginExClient.Connect(serverSignature);
        responder.Handle(connect, from, LocalPort, LoginExClient.ToBytes(connect));

        PrudpPacketV0 data = LoginExClient.LoginEx(correlationCode, serverSignature, forceSignature ?? serverSignature);
        var replies = responder.Handle(data, from, LocalPort, LoginExClient.ToBytes(data));

        foreach (byte[] reply in replies)
        {
            if (LoginExClient.ReadLoginPid(LoginExClient.Parse(reply)) is { } pid)
                return pid;
        }
        return null;
    }
}
