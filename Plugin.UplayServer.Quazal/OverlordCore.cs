using System.Buffers.Binary;
using System.Text;

namespace Plugin.UplayServer.Quazal;

/// <summary>
/// Overlord Core protocol (RMC extended protocol id 5003 / 0x138B), the
/// service Ubisoft's Overlord middleware layers on top of RendezVous. Splinter
/// Cell Blacklist and Ghost Recon Future Soldier call it once, right after the
/// secure-connection Register, before they will go online
///
/// The one-byte RMC protocol id is 0x7F (the "extended id follows" sentinel),
/// so every method arrives as ProtocolId 127 with ExtendedProtocolId 5003; the
/// dispatcher keys on <see cref="RmcRequest.ServiceId"/> to separate it, and
/// <see cref="TicketGranting.BuildRmcResponse"/> frames the reply with the same
/// extended id.
/// </summary>
internal static class OverlordCore
{
    public const int ProtocolId = 5003;
    public const uint MethodFetchConfig = 1;

    private static readonly byte[] FetchConfigResult = System.Convert.FromBase64String(
        "LgAAABEAVkVSX1NFUlZFUl9TVEFHRQABCgAAAAAAAAALADI1OTM1MTUwMjUAAQEAAAAAAAAACwAyNjI2MzQ5NzU3AAEBAAAAAAAAAB0ATkNfQ09OTkVDVElPTl9USUNLRVRfVElNRU9VVAACAAAAAAAAJEAiAE5DX0NPTk5FQ1RJT05fSEFORFNIQUtJTkdfVElNRU9VVAACAAAAAAAAFEAKADMxNDQ4Njg3MQABAQAAAAAAAAALADM3NTk2MzQ1NDYAAQEAAAAAAAAAEwBOQ19NQUlOX1BPUlRfUkFOR0UAASAAAAAAAAAACwAyNDQ5MzA0MjA2AAEBAAAAAAAAABEAT1ZFUkxPUkRfVkVSU0lPTgAECAAwLjguMC4wAAsAMjY4NTYxMTI1NgABAQAAAAAAAAALADMzNzYzMzE1MzMAAQEAAAAAAAAAEABWRVJfU0VSVkVSX0NPREUAAb8LAAAAAAAAIwBGSUxFU0VSVklDRV9VTkxPQ0tTX1VQTE9BRF9FTkFCTEVEAAEBAAAAAAAAAAsAMjczOTE4NDA3NQABAQAAAAAAAAALADI5NDI0MzU2MTQAAQEAAAAAAAAAJgBTTl9GUklFTkRDSEFMTEVOR0VTX01BWF9SRUFEX0lOVEVSVkFMAAIAAAAAACCMQBsATkNfQ09OTkVDVElPTl9KT0lOX1RJTUVPVVQAAgAAAAAAAC5ACwAxMDI3NDQ5MTA5AAEBAAAAAAAAAAsANDE3NTc1NjcwOAABAQAAAAAAAAAjAE5DX0NPTk5FQ1RJT05fSU5BQ1RJVklUWV9USFJFU0hPTEQAAgAAAAAAACBAGABGSUxFU0VSVklDRV9BRE1JTl9SRFZJRAABXwQAAAAAAAAbAFNOX1dFRUtMWUNIQUxMRU5HRVNfRU5BQkxFAAEBAAAAAAAAAAsAMTU5Nzk1MzA1NAABAQAAAAAAAAALADIxNTYzODgzOTAAAQEAAAAAAAAACwAzNzg1MTA2NTYwAAEBAAAAAAAAABUAU1RBVFNfV1JJVEVfSU5URVJWQUwAAgAAAAAAAPA/CwAzODM1NTMwMjA3AAEBAAAAAAAAAAsAMTQ5Mjg5MTQ2NAABAQAAAAAAAAAaAFNOX0RBSUxZQ0hBTExFTkdFU19FTkFCTEUAAQEAAAAAAAAACwAyNTA1NzY2MTY2AAEBAAAAAAAAAAkAMTE4NjY1MDkAAQEAAAAAAAAAGwBTTl9GUklFTkRDSEFMTEVOR0VTX0VOQUJMRQABAQAAAAAAAAAkAEZJTEVTRVJWSUNFX1VOTE9DS1NfVVBMT0FEX0lOVEVSVkFMAAIAAAAAACCsQAsAMjUyNDM2MDk4NgABAQAAAAAAAAAKADcyMTc5Nzk3MQABAQAAAAAAAAALADE1MjU2NjYyMjMAAQEAAAAAAAAAGgBDT01NVU5JVFlFVkVOVF9ET1VCTEVDQVNIAAEAAAAAAAAAABwAVVBMQVlfTUFYX1JBTktfTElNSVRFRF9NT0RFAAEFAAAAAAAAAB0AU05fR09ORURBUktDSEFMTEVOR0VTX0VOQUJMRQABAQAAAAAAAAAOAF9PU0RLX1ZFUlNJT04ABA0AMS40LjE2LjMyOTE4AA0ATkNfTUFJTl9QT1JUAAHIMgAAAAAAABgAQ09NTVVOSVRZRVZFTlRfRE9VQkxFWFAAAQAAAAAAAAAACwAzODA0MzY4NTk0AAEBAAAAAAAAAB4ATkNfQ09OTkVDVElPTl9DTE9TSU5HX1RJTUVPVVQAAgAAAAAAAABAIgBOQ19DT05ORUNUSU9OX0VTVEFCTElTSEVEX1RJTUVPVVQAAgAAAAAAACRA");

    /// <summary>
    /// The same map with OVERLORD_VERSION and _OSDK_VERSION replaced, for a
    /// title whose client validates them against different statics. The map
    /// is u32 count then (String, Variant) pairs; every other entry is copied
    /// through byte for byte.
    /// </summary>
    public static byte[] WithVersions(string overlordVersion, string osdkVersion)
    {
        ReadOnlySpan<byte> map = FetchConfigResult;
        using var output = new MemoryStream();
        int offset = 0;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(map);
        offset += 4;
        TicketGranting.WriteU32(output, count);
        for (uint n = 0; n < count; n++)
        {
            ushort keyLength = BinaryPrimitives.ReadUInt16LittleEndian(map[offset..]);
            string key = Encoding.ASCII.GetString(map.Slice(offset + 2, keyLength - 1));
            int keyEnd = offset + 2 + keyLength;
            byte tag = map[keyEnd];
            int valueLength = tag switch
            {
                1 or 2 or 5 or 6 => 8,
                3 => 1,
                4 => 2 + BinaryPrimitives.ReadUInt16LittleEndian(map[(keyEnd + 1)..]),
                _ => throw new InvalidDataException($"unknown Variant tag {tag} under '{key}'"),
            };
            string? replacement = key switch
            {
                "OVERLORD_VERSION" => overlordVersion,
                "_OSDK_VERSION" => osdkVersion,
                _ => null,
            };
            if (replacement is null)
                output.Write(map.Slice(offset, keyEnd + 1 + valueLength - offset));
            else
            {
                TicketGranting.WriteString(output, key);
                output.WriteByte(4);
                TicketGranting.WriteString(output, replacement);
            }
            offset = keyEnd + 1 + valueLength;
        }
        return output.ToArray();
    }

    public static byte[] BuildFetchConfigResult(RmcRequest request)
    {
        Serilog.Log.Information(
            "[quazal] OverlordCore.fetch_config (method {Method}, {Bytes}B params) -> {N}-byte config map",
            request.MethodId, request.Parameters.Length, FetchConfigResult.Length);
        return FetchConfigResult;
    }
}
