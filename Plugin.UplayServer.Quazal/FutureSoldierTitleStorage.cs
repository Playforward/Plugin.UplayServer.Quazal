using System.IO.Compression;
using System.Security.Cryptography;
using Serilog;

namespace Plugin.UplayServer.Quazal;

/// <summary>
/// Ghost Recon Future Soldier's title storage (51): the one content the game
/// browses for, <c>GameConfigContent</c>, is the DLC catalogue 
/// that the working server hands out over HTTP on every login.
/// Without it the client runs on its built-in content table: search attribute
/// 115 is 0 instead of 3, and a quick-match host asks MapList for the
/// base-content map pool (0x4012) rather than its playlist's (0x1C012) 
/// the one MapList answer the working server was never seen giving.
/// </summary>
internal static class FutureSoldierTitleStorage
{
    public const uint MethodDownloadContent = 4;

    /// <summary>The path the working server serves the gzip under; the client asks for it verbatim.</summary>
    public const string DownloadPath = "/pc/title_storage/dlc.big.gz";

    public const string ContentType = "GameConfigContent";
    public const uint ContentId = 5;
    public const string ContentName = "GameConfigFile.cfg";
    public const string ContentDescription = "dlc.big v2";

    private static byte[]? _gzip;
    private static string _sha256 = string.Empty;
    private static string _host = string.Empty;

    /// <summary>Whether a catalogue is loaded and will be offered.</summary>
    public static bool Loaded => _gzip is not null;

    /// <summary>Reads, hashes and gzips the catalogue; nothing is offered if the path is unset or unreadable.</summary>
    public static void Load(string? dlcBigPath, string host)
    {
        _gzip = null;
        _host = host;
        if (string.IsNullOrWhiteSpace(dlcBigPath))
        {
            Log.Information("[quazal] Future Soldier title storage: no FutureSoldierDlcBigPath configured; GameConfigContent is not offered");
            return;
        }
        try
        {
            byte[] raw = File.ReadAllBytes(dlcBigPath);
            _sha256 = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();
            using var packed = new MemoryStream();
            using (var gzip = new GZipStream(packed, CompressionLevel.Optimal, leaveOpen: true))
                gzip.Write(raw);
            _gzip = packed.ToArray();
            Log.Information("[quazal] Future Soldier title storage: {Path} ({Raw} bytes, sha256 {Sha}) served as http://{Host}{DownloadPath} ({Gzip} bytes)",
                dlcBigPath, raw.Length, _sha256, host, DownloadPath, _gzip.Length);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "[quazal] Future Soldier title storage: could not read {Path}; GameConfigContent is not offered", dlcBigPath);
        }
    }

    /// <summary>For tests: a catalogue of known size and hash without a file.</summary>
    internal static void LoadForTests(byte[] gzip, string sha256, string host)
    {
        _gzip = gzip;
        _sha256 = sha256;
        _host = host;
    }

    public static byte[] BuildBrowseContentsByTypeResult(RmcRequest request)
    {
        string? contentType = request.ReadString();
        if (!Loaded || !string.Equals(contentType, ContentType, StringComparison.Ordinal))
            return TitleStorage.BuildBrowseContentsByTypeResult(request);
        Log.Information("[quazal] TitleStorage.BrowseContentsByType({ContentType}) -> 1 content: {Name} ({Description}, {Size} bytes)",
            contentType, ContentName, ContentDescription, _gzip!.Length);
        return BuildBrowseResult((uint)_gzip.Length, _sha256);
    }

    internal static byte[] BuildBrowseResult(uint gzipSize, string sha256)
    {
        using var content = new MemoryStream();
        TicketGranting.WriteU32(content, ContentId);
        TicketGranting.WriteString(content, ContentName);
        TicketGranting.WriteString(content, ContentDescription);
        TicketGranting.WriteU32(content, gzipSize);
        TicketGranting.WriteU32(content, 0);
        TicketGranting.WriteString(content, ContentType);
        TicketGranting.WriteU32(content, 1);
        TicketGranting.WriteU32(content, 6);
        TicketGranting.WriteString(content, sha256);
        byte[] inner = content.ToArray();

        using var body = new MemoryStream();
        TicketGranting.WriteU32(body, 1);
        TicketGranting.WriteString(body, ContentType);       // AnyDataHolder type name
        TicketGranting.WriteU32(body, (uint)inner.Length + 4);
        TicketGranting.WriteU32(body, (uint)inner.Length);
        body.Write(inner);
        return body.ToArray();
    }

    public static byte[]? BuildDownloadContentResult(RmcRequest request)
    {
        request.TryReadU32(0, out uint contentId);
        if (!Loaded || contentId != ContentId)
        {
            Log.Warning("[quazal] TitleStorage.DownloadContent({ContentId}): nothing to download (loaded={Loaded})", contentId, Loaded);
            return null;
        }
        Log.Information("[quazal] TitleStorage.DownloadContent({ContentId}) -> http://{Host}{Path}", contentId, _host, DownloadPath);
        return BuildDownloadResult(_host);
    }

    internal static byte[] BuildDownloadResult(string host)
    {
        using var body = new MemoryStream();
        TicketGranting.WriteString(body, "http://");
        TicketGranting.WriteString(body, host);
        TicketGranting.WriteString(body, DownloadPath);
        return body.ToArray();
    }

    /// <summary>GET /pc/title_storage/dlc.big.gz: the catalogue, as the client fetches it after DownloadContent.</summary>
    [HTTP("GET", DownloadPath)]
    public static bool Download(ServerSender server)
    {
        server.Response.Clear();
        if (_gzip is null)
        {
            server.Response.SetBegin(404);
            server.Response.SetBody("no GameConfigContent is configured on this server");
        }
        else
        {
            server.Response.SetBegin(200);
            server.Response.SetHeader("Content-Type", "application/gzip");
            server.Response.SetBody(_gzip);
            Log.Information("[quazal] Future Soldier title storage: served dlc.big.gz ({Bytes} bytes)", _gzip.Length);
        }
        server.SendResponse();
        return true;
    }
}
