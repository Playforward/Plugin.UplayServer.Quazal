namespace Plugin.UplayServer.Quazal;

/// <summary>Legacy User Storage (protocol 53) methods reached by FC3.</summary>
internal static class UserStorage
{
    public const byte ProtocolId = 53;
    public const uint MethodGetOwnContents = 21;
    public const uint MethodSearchContentsByPlayers = 25;
    public static byte[] BuildGetOwnContentsResult(RmcRequest request)
    {
        Serilog.Log.Information("[quazal] UserStorage.GetOwnContents -> 0 entries ({Parameters})",
            Convert.ToHexString(request.Parameters));
        return Quazal.EmptyList;
    }

    /// <summary>
    /// UploadContent (method 5): the client's own profile document (a 28 KB
    /// <c>&lt;TutorialProfile …&gt;</c> XML in the 2026-09-17 capture, sent by the
    /// PC after a match). A working server answers three uint32s
    /// </summary>
    public const uint MethodUploadContent = 5;

    public static byte[] BuildUploadContentResult(RmcRequest request)
    {
        uint id = OnlineDataStore.NextRowId();
        Serilog.Log.Information("[quazal] UserStorage.UploadContent ({Length} bytes) -> content id {Id}", request.Parameters.Length, id);
        var body = new byte[12];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(body, id);
        return body;
    }

    /// <summary>A replacement server hosts no user content, so the list is empty rather than an error.</summary>
    public static byte[] BuildSearchContentsByPlayersResult(RmcRequest request)
    {
        Serilog.Log.Information("[quazal] UserStorage.SearchContentsByPlayers -> 0 entries ({Parameters})",
            Convert.ToHexString(request.Parameters));
        return Quazal.EmptyList;
    }
}
