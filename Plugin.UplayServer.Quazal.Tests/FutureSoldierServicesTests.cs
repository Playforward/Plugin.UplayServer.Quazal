using System.Buffers.Binary;
using System.Text;
using Plugin.UplayServer.Quazal;
using Xunit;

namespace Plugin.UplayServer.Quazal.Tests;

public sealed class FutureSoldierServicesTests
{
    /// <summary>Reads a Quazal (String, Variant) map into key -> printable value.</summary>
    private static Dictionary<string, string> ReadMap(byte[] map)
    {
        var result = new Dictionary<string, string>();
        int offset = 0;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(map);
        offset += 4;
        for (uint n = 0; n < count; n++)
        {
            ushort keyLength = BinaryPrimitives.ReadUInt16LittleEndian(map.AsSpan(offset));
            string key = Encoding.ASCII.GetString(map, offset + 2, keyLength - 1);
            offset += 2 + keyLength;
            byte tag = map[offset++];
            switch (tag)
            {
                case 4:
                    ushort valueLength = BinaryPrimitives.ReadUInt16LittleEndian(map.AsSpan(offset));
                    result[key] = Encoding.ASCII.GetString(map, offset + 2, valueLength - 1);
                    offset += 2 + valueLength;
                    break;
                case 3:
                    result[key] = map[offset++].ToString();
                    break;
                default:
                    result[key] = Convert.ToHexString(map, offset, 8);
                    offset += 8;
                    break;
            }
        }
        Assert.Equal(map.Length, offset);
        return result;
    }

    [Fact]
    public void TheSettingsMapCarriesFutureSoldiersVersionsAndNothingElseChanges()
    {
        var blacklist = ReadMap(OverlordCore.BuildFetchConfigResult(new RmcRequest(127, CallId: 1, OverlordCore.MethodFetchConfig, [])));
        var futureSoldier = ReadMap(OverlordCore.WithVersions("0.7.0.374915", "1.3.7.0"));

        Assert.Equal("0.7.0.374915", futureSoldier["OVERLORD_VERSION"]);
        Assert.Equal("1.3.7.0", futureSoldier["_OSDK_VERSION"]);
        Assert.Equal("0.8.0.0", blacklist["OVERLORD_VERSION"]);
        Assert.Equal(blacklist.Count, futureSoldier.Count);
        foreach ((string key, string value) in blacklist)
            if (key is not ("OVERLORD_VERSION" or "_OSDK_VERSION"))
                Assert.Equal(value, futureSoldier[key]);
    }
}
