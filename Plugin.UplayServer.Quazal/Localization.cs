namespace Plugin.UplayServer.Quazal;

/// <summary>Localization service used during FC3 player-session startup.</summary>
internal static class Localization
{
    public const byte ProtocolId = 39;
    public const uint MethodSetLocaleCode = 2;

    /// <summary>
    /// SetLocaleCode is a void RMC method. Returning an empty result body still
    /// produces the normal success envelope in <see cref="TicketGranting"/>.
    /// </summary>
    public static byte[] BuildSetLocaleCodeResult(RmcRequest request)
    {
        string locale = request.ReadString() ?? "<invalid>";
        Serilog.Log.Information("[quazal] SetLocaleCode: {Locale}", locale);
        return Array.Empty<byte>();
    }
}
