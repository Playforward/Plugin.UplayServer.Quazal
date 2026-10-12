using System.Collections.Concurrent;
using System.Reflection;

namespace Plugin.UplayServer.Quazal;

/// <summary>
/// Title-wide seed data for protocol 106 data ids, loaded from captured wire
/// bytes rather than synthesised.
/// </summary>
internal static class OnlineDataSeed
{
    private static readonly ConcurrentDictionary<uint, OnlineDataRowSet?> _cache = [];

    private static string SeedDirectory
    {
        get
        {
            string? pluginDirectory = Path.GetDirectoryName(
                Assembly.GetExecutingAssembly().Location);
            string root = pluginDirectory is null
                ? AppContext.BaseDirectory
                : Path.GetFullPath(Path.Combine(pluginDirectory, ".."));
            return Path.Combine(root, "Titles", "FarCry3", "onlinedata");
        }
    }

    /// <summary>
    /// The seeded catalogue for a data id, or null if there is no seed file.
    /// Parsed through this plugin's own RowSet reader rather than trusted
    /// blindly, so a corrupt or truncated seed is rejected here instead of
    /// being handed to the client as a malformed reply.
    /// </summary>
    public static OnlineDataRowSet? TryGet(uint dataId) =>
        _cache.GetOrAdd(dataId, Load);

    private static OnlineDataRowSet? Load(uint dataId)
    {
        string path = Path.Combine(SeedDirectory, $"dataid-0x{dataId:X2}.bin");
        if (!File.Exists(path))
            return null;

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException error)
        {
            Serilog.Log.Warning("[quazal] online-data seed 0x{DataId:X2} unreadable: {Message}",
                dataId, error.Message);
            return null;
        }

        int offset = 0;
        if (!OnlineDataRowSet.TryParse(bytes, ref offset, out OnlineDataRowSet rowSet))
        {
            Serilog.Log.Warning(
                "[quazal] online-data seed 0x{DataId:X2} ({Length} bytes) did not parse as a RowSet — ignoring",
                dataId, bytes.Length);
            return null;
        }

        Serilog.Log.Information(
            "[quazal] online-data seed 0x{DataId:X2}: {Rows} row(s), column(s) {Columns} ({Length} bytes from {Path})",
            dataId, rowSet.Rows.Count, string.Join("/", rowSet.ColumnNames), bytes.Length,
            Path.GetFileName(path));
        return rowSet;
    }
}
