namespace Plugin.UplayServer.Quazal;

/// <summary>
/// FC3's protocol 101 post-login presence synchronizer. These operations are
/// requests, not notifications: their results feed the callback that starts
/// the authenticated playlist path.
/// </summary>
internal static class Fc3FriendsPresenceService
{
    public const byte ProtocolId = 101;
    public const uint MethodUpdateState = 1;
    public const uint MethodUpdatePrincipal = 2;
    public const uint MethodPostSessionUpdate = 3;

    /// <summary>
    /// Request: u32 count followed by state ids. Result: count, count × 1,
    /// count, count × 2. The captured ten-state reply was 88 bytes.
    /// </summary>
    public static byte[] BuildUpdateStateResult(RmcRequest request)
    {
        if (!request.TryReadU32(0, out uint count) ||
            count > (uint)((request.Parameters.Length - sizeof(uint)) / sizeof(uint)))
        {
            Serilog.Log.Warning("[quazal] FC3 FriendsPresence.UpdateState has malformed state array: {Parameters}",
                Convert.ToHexString(request.Parameters));
            return [];
        }

        byte[] result = new byte[checked((int)(2 + count + count) * sizeof(uint))];
        Span<byte> bytes = result;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, count);
        int offset = sizeof(uint);
        for (uint index = 0; index < count; index++, offset += sizeof(uint))
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes[offset..], 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes[offset..], count);
        offset += sizeof(uint);
        for (uint index = 0; index < count; index++, offset += sizeof(uint))
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes[offset..], 2);

        Serilog.Log.Information("[quazal] FC3 FriendsPresence.UpdateState accepted {Count} state(s)", count);
        return result;
    }

    /// <summary>Captured result to principal 0xA1F: the four u32s 3, 9, 12, 13.</summary>
    public static byte[] BuildUpdatePrincipalResult(RmcRequest request)
    {
        byte[] result = new byte[4 * sizeof(uint)];
        Span<byte> bytes = result;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, 3);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], 9);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes[8..], 12);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes[12..], 13);
        Serilog.Log.Information("[quazal] FC3 FriendsPresence.UpdatePrincipal accepted: {Parameters}",
            Convert.ToHexString(request.Parameters));
        return result;
    }

    public static byte[] BuildVoidResult(RmcRequest request, string method)
    {
        Serilog.Log.Information("[quazal] FC3 FriendsPresence.{Method} accepted: {Parameters}",
            method, Convert.ToHexString(request.Parameters));
        return [];
    }
}
