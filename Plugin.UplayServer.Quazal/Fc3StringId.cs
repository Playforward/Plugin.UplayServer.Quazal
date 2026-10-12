namespace Plugin.UplayServer.Quazal;

/// <summary>
/// Dunia's string ids: the CRC-32 (IEEE, as zlib computes it) of the name in
/// lower case. The <c>weapon_id</c> the host uploads for a weapon is this
/// of the weapon's name
/// </summary>
internal static class Fc3StringId
{
    private static readonly uint[] Table = BuildTable();

    public static uint Of(string name)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in System.Text.Encoding.UTF8.GetBytes(name.ToLowerInvariant()))
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }
}
