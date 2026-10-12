using System.Buffers.Binary;
using System.Text;

namespace Plugin.UplayServer.Quazal;

/// <summary>
/// Secure Connection (protocol 11), served on the secure connection once the
/// Kerberos CONNECT has completed. The client opens with Register, handing over
/// the station urls it can be reached on.
/// </summary>
internal static class SecureConnection
{
    public const byte ProtocolId = 0x0B;
    public const uint MethodRegister = 1;
    public const uint MethodRegisterEx = 4;

    private const uint ResultSuccess = 0x00010001;

    /// <summary>
    /// First Rendez-Vous connection id handed out. Every Register gets its
    /// own: the working servers do (FC3 community 0x111b then 0x111c for one
    /// client's two connections; GRFS warehouse 0x23d3/0x23d4), and the id is
    /// what a peer stamps into its station urls as <c>RVCID</c> so other
    /// players' NAT-traversal requests can name it. One shared id (the old
    /// 0x00580001, an FC3-era assumption) made every player's url the same.
    /// </summary>
    public const uint FirstConnectionId = 0x1000;

    /// <summary>Reads a List&lt;StationURL&gt;: a count followed by strings.</summary>
    public static IReadOnlyList<string> ReadStationUrls(ReadOnlySpan<byte> parameters)
    {
        var urls = new List<string>();
        if (parameters.Length < 4)
            return urls;

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(parameters);
        int offset = 4;
        for (uint n = 0; n < count; n++)
        {
            if (offset + 2 > parameters.Length)
                break;
            int length = BinaryPrimitives.ReadUInt16LittleEndian(parameters[offset..]);
            offset += 2;
            if (length < 1 || offset + length > parameters.Length)
                break;
            urls.Add(Encoding.UTF8.GetString(parameters.Slice(offset, length - 1)));
            offset += length;
        }
        return urls;
    }

    /// <summary>
    /// Register result: the result code, the connection id the client should
    /// stamp into its station urls, and the public url the server sees it on.
    /// </summary>
    public static byte[] BuildRegisterResult(uint connectionId, string publicUrl)
    {
        using var body = new MemoryStream();
        TicketGranting.WriteU32(body, ResultSuccess);
        TicketGranting.WriteU32(body, connectionId);
        TicketGranting.WriteString(body, publicUrl);
        return body.ToArray();
    }

    /// <summary>
    /// Builds the public station url for a peer: the address and port the
    /// datagram actually arrived from, marked <c>type=3</c> (public) exactly
    /// as the working servers word it (<c>prudp:/address=173.73.140.54;
    /// port=2347;sid=15;type=3</c>). The type is what the client keys on to
    /// learn its own public address; without it Future Soldier registered an
    /// address-less "PID=…;RVCID=…" url and asked hosts to probe an empty
    /// station. The stream id is taken from the client's own urls so the
    /// reply stays in its addressing scheme.
    /// </summary>
    public static string BuildPublicUrl(string address, int port, int streamId)
        => $"prudp:/address={address};port={port};sid={streamId};type=3";

    /// <summary>Extracts the sid a client used, defaulting to 15.</summary>
    public static int ReadStreamId(IReadOnlyList<string> urls)
    {
        foreach (string url in urls)
        {
            int index = url.IndexOf("sid=", StringComparison.Ordinal);
            if (index < 0)
                continue;
            int end = url.IndexOf(';', index);
            string value = end < 0 ? url[(index + 4)..] : url[(index + 4)..end];
            if (int.TryParse(value, out int sid))
                return sid;
        }
        return 15;
    }
}
