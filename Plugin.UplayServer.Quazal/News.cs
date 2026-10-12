namespace Plugin.UplayServer.Quazal;

/// <summary>
/// News (protocol 31). FC3 asks for the "ubiNews" channel headers during its
/// online bootstrap.
/// </summary>
internal static class News
{
    public const byte ProtocolId = 31;
    public const uint MethodGetChannels = 1;
    public const uint MethodGetChannelsByTypes = 2;
    public const uint MethodGetSubscribableChannels = 3;
    public const uint MethodGetSubscribedChannels = 5;
    public const uint MethodGetNewsHeaders = 8;
    public const uint MethodGetNewsMessages = 9;
    public const uint MethodGetNumberOfNews = 10;
    public const uint MethodGetNewsHeadersByType = 12;
    public const uint MethodGetNewsMessagesByType = 13;

    /// <summary>
    /// GetNewsMessages takes a list of message ids
    /// </summary>
    public static byte[] BuildGetNewsMessagesResult(RmcRequest request)
    {
        Serilog.Log.Information(
            "[quazal] News.GetNewsMessages -> 0 messages ({Parameters})",
            Convert.ToHexString(request.Parameters));
        return [0x00, 0x00, 0x00, 0x00];
    }

    /// <summary>
    /// GetNewsHeadersByType takes a channel-type string and a result range, and
    /// returns a list of news headers. A replacement server publishes no news,
    /// so the answer is a well-formed empty list.
    /// </summary>
    public static byte[] BuildGetNewsHeadersByTypeResult(RmcRequest request)
    {
        Serilog.Log.Information(
            "[quazal] News.GetNewsHeadersByType -> 0 headers ({Parameters})",
            Convert.ToHexString(request.Parameters));
        return [0x00, 0x00, 0x00, 0x00];
    }

    /// <summary>
    /// The remaining channel/header/message list methods all resolve to
    /// "nothing published". Method 11 (GetChannelByType) is deliberately not
    /// handled: it returns a single NewsChannel structure, which cannot be
    /// fabricated honestly.
    /// </summary>
    public static byte[] BuildEmptyListResult(RmcRequest request, string method)
    {
        Serilog.Log.Information("[quazal] News.{Method} -> 0 entries", method);
        return Quazal.EmptyList;
    }

    /// <summary>GetNumberOfNews returns a bare Uint32.</summary>
    public static byte[] BuildNumberOfNewsResult(RmcRequest request)
    {
        Serilog.Log.Information("[quazal] News.GetNumberOfNews -> 0");
        return [0x00, 0x00, 0x00, 0x00];
    }
}
