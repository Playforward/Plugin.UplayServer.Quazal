namespace Plugin.UplayServer.Quazal;

/// <summary>Common minimal Quazal result bodies.</summary>
internal static class Quazal
{
    /// <summary>A list or map with an element count of zero.</summary>
    public static byte[] EmptyList => [0x00, 0x00, 0x00, 0x00];

    /// <summary>
    /// A Quazal string is a uint16 length including the NUL terminator, so the
    /// empty string is a length of one followed by the terminator.
    /// </summary>
    public static byte[] EmptyString => [0x01, 0x00, 0x00];
}
