namespace Plugin.UplayServer.Quazal;

/// <summary>
/// Remote monitoring / log sink (protocol 111). Identified from the method 2
/// payload, which carries `CRemoteMonitorServiceProvider_RdV::InitLog`, the
/// channel name `LogOnlineServices`, and FC3's own build banner
/// ("Far Cry 3 [WIN32] UBI_MASTER 0.1.0").
/// </summary>
internal static class RemoteMonitor
{
    public const byte ProtocolId = 111;
    public const uint MethodInit = 1;
    public const uint MethodLog = 2;

    /// <summary>
    /// The report types a working server enables. Its method 1 result is this
    /// <c>qlist&lt;string&gt;</c> (82 bytes, identical for both clients of the
    /// 2026-09-18 community captures), and the client answers it with
    /// "Report type LogOnlineServices enabled." for each one, then sends its
    /// online-services log through method 2.
    /// </summary>
    private static readonly string[] ReportTypes =
        ["LogOnlineServices", "LogOnlineSession", "LogOnlineMatchmaking", "LogOnlineEcho"];

    /// <summary>Method 1 opens the log channel: the result is the list of enabled report types.</summary>
    public static byte[] BuildInitResult(RmcRequest request)
    {
        Serilog.Log.Information(
            "[quazal] RemoteMonitor init accepted ({ParameterLength} parameter bytes); enabling {Count} report types",
            request.Parameters.Length, ReportTypes.Length);
        var result = new List<byte>(96);
        result.AddRange(BitConverter.GetBytes((uint)ReportTypes.Length));
        foreach (string type in ReportTypes)
        {
            byte[] text = System.Text.Encoding.ASCII.GetBytes(type);
            result.AddRange(BitConverter.GetBytes((ushort)(text.Length + 1)));
            result.AddRange(text);
            result.Add(0);
        }
        return [.. result];
    }

    /// <summary>
    /// Method 2 pushes log records. A replacement server has nothing to say
    /// back, but accepting is more honest than failing a call whose only
    /// purpose is to hand us diagnostics — and the records are worth keeping,
    /// so the human-readable strings are logged.
    /// </summary>
    public static byte[] BuildLogResult(RmcRequest request)
    {
        Serilog.Log.Information("[quazal] RemoteMonitor log: {Text}",
            ExtractText(request.Parameters));
        return Quazal.EmptyList;
    }

    /// <summary>Pulls the printable runs out of a log record blob.</summary>
    private static string ExtractText(ReadOnlySpan<byte> parameters)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (byte value in parameters)
        {
            if (value >= 0x20 && value < 0x7f)
            {
                current.Append((char)value);
                continue;
            }
            if (current.Length >= 6) parts.Add(current.ToString());
            current.Clear();
        }
        if (current.Length >= 6) parts.Add(current.ToString());
        return string.Join(" | ", parts);
    }
}
