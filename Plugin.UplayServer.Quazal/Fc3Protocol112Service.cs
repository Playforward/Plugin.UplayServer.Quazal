namespace Plugin.UplayServer.Quazal;

/// <summary>
/// An FC3-generated service on protocol 112, called with method 1 and no
/// parameters at the tail of the post-login sync chain. Protocol 112 is
/// "Ranking" only in NEX's Nintendo-only range, which does not apply to a
/// Ubisoft Quazal title, so the service is identified here by number alone.
/// </summary>
internal static class Fc3Protocol112Service
{
    public const byte ProtocolId = 112;
    public const uint MethodUnknown1 = 1;

    /// <summary>
    /// The result type is unconfirmed. It is answered with the minimum
    /// well-formed value that every other consumed FC3 result has accepted
    /// </summary>
    public static byte[] BuildUnknown1Result(RmcRequest request)
    {
        Serilog.Log.Information(
            "[quazal] FC3 protocol 112/1 accepted ({ParameterLength} parameter bytes)",
            request.Parameters.Length);
        return [0x00, 0x00, 0x00, 0x00];
    }
}
