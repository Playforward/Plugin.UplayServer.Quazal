namespace Plugin.UplayServer.Quazal;

/// <summary>
/// FC3's protocol 108 post-session notification service. The client submits
/// its principal id after updating lobby presence and does not consume a
/// result; acknowledge it with an ordinary void RMC success.
/// </summary>
internal static class Fc3Protocol108Service
{
    public const byte ProtocolId = 108;
    public const uint MethodPostSessionUpdate = 2;

    /// <summary>
    /// Method 4, sent once after a match as <c>(session id, 0)</c>. A working
    /// server answers five zero bytes
    /// </summary>
    public const uint MethodSessionQuery = 4;
    /// <summary>Method 5, <c>(old session id, new session id)</c> after a MigrateSession; void on the working server.</summary>
    public const uint MethodSessionMigrated = 5;

    public static byte[] BuildVoidResult(RmcRequest request)
    {
        Serilog.Log.Information("[quazal] FC3 protocol 108/{Method} accepted: {Parameters}",
            request.MethodId, Convert.ToHexString(request.Parameters));
        return [];
    }

    public static byte[] BuildSessionQueryResult(RmcRequest request)
    {
        Serilog.Log.Information("[quazal] FC3 protocol 108/4 ({Parameters}) -> 0, false, as the working server answers",
            Convert.ToHexString(request.Parameters));
        return new byte[5];
    }
}
