namespace Plugin.UplayServer.Quazal;

/// <summary>
/// Far Cry 3 generated service on protocol 114 (0x72). FC3 calls method 1 with
/// a single string parameter holding its own PID ("1000" in the observed
/// flow), immediately after RegisterEx and before SetLocaleCode.
/// </summary>
internal static class Fc3AccountLinkService
{
    public const byte ProtocolId = 114;
    public const uint MethodSubmitPrincipalId = 1;

    /// <summary>
    /// The call site discards any result, so the reply is the plain RMC
    /// success envelope with an empty method body
    /// </summary>
    public static byte[] BuildSubmitPrincipalIdResult(RmcRequest request)
    {
        Serilog.Log.Information(
            "[quazal] FC3 protocol 114/1 accepted: {Parameters}",
            Convert.ToHexString(request.Parameters));
        return [];
    }
}
