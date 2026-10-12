namespace Plugin.UplayServer.Quazal;

/// <summary>
/// Quazal's IPToLocation (protocol 45), which FC3 calls once after login with
/// no arguments and answers by uploading its own <c>0x0C PrincipalLocation</c>
/// row (geocode city/region/country, latitude/longitude).
/// </summary>
internal static class IpToLocationService
{
    public const byte ProtocolId = 45;
    public const uint MethodGetLocation = 1;

    public static byte[] BuildGetLocationResult(RmcRequest request)
    {
        Serilog.Log.Information("[quazal] IPToLocation -> United States / US");
        using var body = new MemoryStream();
        TicketGranting.WriteString(body, "United States");
        TicketGranting.WriteString(body, "US");
        TicketGranting.WriteString(body, "US");
        return body.ToArray();
    }
}
