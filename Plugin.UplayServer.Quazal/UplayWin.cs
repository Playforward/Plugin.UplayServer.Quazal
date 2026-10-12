namespace Plugin.UplayServer.Quazal;

/// <summary>
/// Uplay Win (protocol 49) the Uplay actions/rewards service.
/// </summary>
internal static class UplayWin
{
    public const byte ProtocolId = 49;

    public const uint MethodGetActions = 1;
    public const uint MethodGetActionsCompleted = 2;
    public const uint MethodGetActionsCount = 3;
    public const uint MethodGetActionsCompletedCount = 4;
    public const uint MethodGetRewards = 5;
    public const uint MethodGetRewardsPurchased = 6;
    public const uint MethodUplayWelcome = 7;
    public const uint MethodSetActionsCompleted = 9;
    public const uint MethodGetUserToken = 10;
    public const uint MethodGetVirtualCurrencyUserBalance = 11;
    public const uint MethodGetSectionsByKey = 12;
    public static byte[] BuildEmptyListResult(RmcRequest request, string method)
    {
        Serilog.Log.Information(
            "[quazal] UplayWin.{Method} -> 0 entries ({Parameters})",
            method, Convert.ToHexString(request.Parameters));
        return Quazal.EmptyList;
    }

    /// <summary>Counts and balances are a bare Sint32, zero here.</summary>
    public static byte[] BuildZeroCountResult(RmcRequest request, string method)
    {
        Serilog.Log.Information(
            "[quazal] UplayWin.{Method} -> 0 ({Parameters})",
            method, Convert.ToHexString(request.Parameters));
        return [0x00, 0x00, 0x00, 0x00];
    }

    /// <summary>GetUserToken returns a String; there is no token to hand out.</summary>
    public static byte[] BuildUserTokenResult(RmcRequest request)
    {
        Serilog.Log.Information("[quazal] UplayWin.GetUserToken -> empty token");
        return Quazal.EmptyString;
    }
}
