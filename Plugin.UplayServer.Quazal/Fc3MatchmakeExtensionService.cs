namespace Plugin.UplayServer.Quazal;

/// <summary>
/// Protocol 115, FC3's custom MatchmakeExtension surface
/// </summary>
internal static class Fc3MatchmakeExtensionService
{
    public const byte ProtocolId = 115;
    public const uint MethodUnknown1 = 1;

    public static byte[] BuildUnknown1Result(RmcRequest request)
    {
        request.TryReadU32(0, out uint typeId);
        request.TryReadU32(4, out uint unknown);
        string? tail = request.Parameters.Length > 8
            ? new RmcRequest(request.ProtocolId, request.CallId, request.MethodId,
                request.Parameters[8..]).ReadString()
            : null;
        Serilog.Log.Information(
            "[quazal] MatchmakeExtension 115/1 accepted: typeId={TypeId} unknown={Unknown} tail={Tail} raw={Raw}",
            typeId, unknown, tail, Convert.ToHexString(request.Parameters));
        return [];
    }
}
