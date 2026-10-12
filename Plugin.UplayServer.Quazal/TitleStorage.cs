using System.Buffers.Binary;
using System.Text;

namespace Plugin.UplayServer.Quazal;

/// <summary>
/// Title Storage (protocol 51). FC3 browses by content type during its online
/// bootstrap, asking for "PatchFileTitleContent".
/// </summary>
internal static class TitleStorage
{
    public const byte ProtocolId = 51;
    public const uint MethodBrowseContentsByType = 2;

    /// <summary>
    /// BrowseContentsByType takes a content-type string plus flag/mask pairs
    /// and returns a list of contents. A replacement server hosts no title
    /// content, so the answer is a well-formed empty list rather than an error:
    /// the generated decoder needs a readable result, and failing the call
    /// leaves the client's patch-content lookup unresolved.
    /// </summary>
    public static byte[] BuildBrowseContentsByTypeResult(RmcRequest request)
    {
        Serilog.Log.Information(
            "[quazal] TitleStorage.BrowseContentsByType({ContentType}) -> 0 contents",
            ReadString(request.Parameters));
        return [0x00, 0x00, 0x00, 0x00];
    }

    /// <summary>Reads the leading Quazal string: uint16 length including the NUL.</summary>
    private static string ReadString(ReadOnlySpan<byte> parameters)
    {
        if (parameters.Length < 2)
            return string.Empty;
        int length = BinaryPrimitives.ReadUInt16LittleEndian(parameters);
        if (length < 1 || length > parameters.Length - 2)
            return string.Empty;
        return Encoding.UTF8.GetString(parameters.Slice(2, length - 1));
    }
}
