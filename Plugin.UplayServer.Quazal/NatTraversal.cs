using System.Net;

namespace Plugin.UplayServer.Quazal;

/// <summary>
/// NAT traversal (protocol 3). A client joining a session sends
/// <c>RequestProbeInitiationExt(targetList, stationToProbe)</c>: the host's
/// station urls (<c>udp:/address=…;port=2346;PID=…;RVCID=…</c>, public and
/// private) and its own public url. The server's part is to turn round and
/// call <c>InitiateProbe(stationToProbe)</c> on each target, which is what
/// makes the host fire probe packets at the joiner
/// </summary>
internal static class NatTraversal
{
    public const byte ProtocolId = 3;
    public const uint MethodRequestProbeInitiation = 1;
    public const uint MethodInitiateProbe = 2;
    public const uint MethodRequestProbeInitiationExt = 3;
    public const uint MethodReportNatTraversalResult = 4;
    public const uint MethodReportNatProperties = 5;

    /// <summary>
    /// Sends an RMC request to the connected player a target url names
    /// </summary>
    public delegate bool TargetNotifier(uint pid, uint rvcid, IPAddress? address, byte protocolId, uint methodId, byte[] parameters);

    /// <summary>
    /// RequestProbeInitiationExt: forwards <c>InitiateProbe(stationToProbe)</c>
    /// to every distinct target the request names, then returns void.
    /// </summary>
    public static byte[] BuildRequestProbeInitiationExtResult(RmcRequest request, uint caller, TargetNotifier notify)
    {
        IReadOnlyList<string> targets = SecureConnection.ReadStationUrls(request.Parameters);
        string? stationToProbe = ReadTrailingString(request.Parameters, targets);
        if (string.IsNullOrEmpty(stationToProbe))
        {
            Serilog.Log.Warning("[quazal] NatTraversal.RequestProbeInitiationExt from pid={Pid}: empty stationToProbe -- this client has not learned its public station from the NAT-detect echo; nobody probed",
                caller);
            return [];
        }

        byte[] initiateProbe = BuildInitiateProbeParameters(stationToProbe);
        var probed = new HashSet<string>();
        foreach (string target in targets)
        {
            uint pid = ReadUrlU32(target, "PID");
            uint rvcid = ReadUrlU32(target, "RVCID");
            IPAddress? address = ReadUrlAddress(target);
            if (pid != 0 && pid == caller)
                continue;
            string key = pid != 0 ? $"pid:{pid}" : rvcid != 0 ? $"rvcid:{rvcid}" : $"addr:{address}";
            if (!probed.Add(key))
                continue;
            bool sent = notify(pid, rvcid, address, ProtocolId, MethodInitiateProbe, initiateProbe);
            Serilog.Log.Information("[quazal] NatTraversal: pid={Pid} asks {Target} to probe {Station} -> InitiateProbe {Outcome}",
                caller, target, stationToProbe, sent ? "sent" : "NOT sent (target not connected here)");
        }
        return [];
    }

    /// <summary>The methods a client reports with, all void: the result and its NAT properties.</summary>
    public static byte[] BuildVoidResult(RmcRequest request, string name)
    {
        Serilog.Log.Information("[quazal] NatTraversal.{Method} ({Length} parameter bytes) -> void", name, request.Parameters.Length);
        return [];
    }

    /// <summary>InitiateProbe's one parameter: the StationURL to probe.</summary>
    public static byte[] BuildInitiateProbeParameters(string stationToProbe)
    {
        using var body = new MemoryStream();
        TicketGranting.WriteString(body, stationToProbe);
        return body.ToArray();
    }

    /// <summary>The string after a List&lt;StationURL&gt; whose entries were already read.</summary>
    private static string? ReadTrailingString(byte[] parameters, IReadOnlyList<string> urls)
    {
        int offset = 4;
        foreach (string url in urls)
            offset += 2 + System.Text.Encoding.UTF8.GetByteCount(url) + 1;
        if (offset + 2 > parameters.Length)
            return null;
        int length = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(parameters.AsSpan(offset));
        if (length < 1 || offset + 2 + length > parameters.Length)
            return null;
        return System.Text.Encoding.UTF8.GetString(parameters, offset + 2, length - 1);
    }

    /// <summary>A numeric <c>key=value</c> field of a station url, or 0.</summary>
    public static uint ReadUrlU32(string url, string key)
    {
        string? value = ReadUrlField(url, key);
        return value is not null && uint.TryParse(value, out uint parsed) ? parsed : 0;
    }

    /// <summary>The <c>address=</c> of a station url, or null.</summary>
    public static IPAddress? ReadUrlAddress(string url) =>
        ReadUrlField(url, "address") is { } value && IPAddress.TryParse(value, out IPAddress? address) ? address : null;

    private static string? ReadUrlField(string url, string key)
    {
        int start = url.IndexOf('/') + 1;
        foreach (string field in url[start..].Split(';'))
        {
            int eq = field.IndexOf('=');
            if (eq > 0 && string.Equals(field[..eq], key, StringComparison.OrdinalIgnoreCase))
                return field[(eq + 1)..];
        }
        return null;
    }
}
