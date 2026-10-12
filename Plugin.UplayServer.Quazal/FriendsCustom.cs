namespace Plugin.UplayServer.Quazal;

/// <summary>
/// The friends service FC3 uses after the local Uplay friend list is read
/// (protocol 102).
/// </summary>
internal static class FriendsCustom
{
    public const byte ProtocolId = 102;
    public const uint MethodTranslateIds = 3;
    /// <summary>
    /// Method 2: one string, a player's in-game name, answered with their pid
    /// </summary>
    public const uint MethodLookupByName = 2;

    public static byte[] BuildLookupByNameResult(RmcRequest request, Func<string, uint?> resolve)
    {
        string name = string.Empty;
        ReadOnlySpan<byte> p = request.Parameters;
        if (p.Length >= 2)
        {
            ushort length = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(p);
            if (length > 0 && p.Length >= 2 + length)
                name = System.Text.Encoding.UTF8.GetString(p.Slice(2, length - 1));
        }
        uint pid = resolve(name) ?? 0;
        Serilog.Log.Information("[quazal] FriendsCustom.LookupByName {Name} -> pid={Pid}", name, pid);
        var body = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(body, pid);
        return body;
    }

    /// <summary>
    /// The request carries a qlist of account IDs, but FC3's generated response
    /// holder has no result fields and inherits the base response decoder
    /// unchanged. Method 3 therefore returns void, including for an empty input
    /// list.
    /// </summary>
    public static byte[] BuildTranslateIdsResult(RmcRequest request)
    {
        Serilog.Log.Information(
            "[quazal] FriendsCustom 102/3 accepted: {Parameters}",
            Convert.ToHexString(request.Parameters));
        return Array.Empty<byte>();
    }
}
