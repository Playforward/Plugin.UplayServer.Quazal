namespace Plugin.UplayServer.Quazal.Enums;

/// <summary>
/// How a title's PRUDP V0 trailing checksum is computed
/// </summary>
public enum QuazalChecksumAlgorithm : byte
{
    LegacyWordSum,
    NexByteFold,
    LegacyWordSumTruncated,
}
