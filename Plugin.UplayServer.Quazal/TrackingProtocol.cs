using System.Buffers.Binary;
using System.Text;

namespace Plugin.UplayServer.Quazal;

/// <summary>
/// Tracking 3 (protocol 36): the telemetry service. Ubisoft used it for
/// analytics; this server uses it for what the game reports through it during
/// a match
/// </summary>

internal static class TrackingProtocol
{
    public const byte ProtocolId = 0x24;
    public const uint MethodSendTag = 1;
    public const uint MethodSendTagAndUpdateUserInfo = 2;
    public const uint MethodSendUserInfo = 3;
    public const uint MethodGetConfiguration = 4;
    public const uint MethodSendTags = 5;

    /// <summary>The tags a working server asks for, in its order.</summary>
    private static readonly string[] ConfiguredTags =
    [
        "GAME_START", "GAME_STOP", "LINKAPP_VIEW", "UPLAY_START", "UPLAY_STOP", "MM_STOP", "FPSCLIENT_START", "FPSCLIENT_STOP", "LEVEL_START", "LEVEL_STOP", "OBJECTIVE_START", "OBJECTIVE_STOP", "UPLAY_BROWSE", "FPSHOST_START", "FPSHOST_STOP", "MONEY_EARN", "MONEY_SPEND", "SKILLPOINT_SPEND", "XP_EARN", "AWARD_UNLOCK", "PLAYER_DEATH", "GAME_SAVE", "INSTALL_START", "INSTALL_STOP", "MENU_ENTER", "MENU_EXIT", "MENU_OPTIONCHANGE", "MM_RES", "PLAYER_KILL", "PLAYER_SAVED", "UNINSTALL_START", "UNINSTALL_STOP", "VIDEO_START", "VIDEO_STOP", "ABILITY_USED", "CAPTUREPOINT_CHANGE", "CHALLENGE_STOP", "CHASE_STOP", "CLASSCUSTOM_PAGECLOSED", "CONNEXION_STATS", "CRITICAL_START", "CUSTOMCLASS_PAGECLOSED", "DATA_ACTION", "DATA_EARN", "GADGET_USED", "GAMEOP_DONE", "GAME_LOC", "INBOX_PAGEACCESS", "INSIGHT_ACTION", "INTEL_DECODINGCANCELED", "INTEL_DECODINGCOMPLETED", "INTEL_DECODINGSTARTED", "INTEL_EARNED", "INTEL_PAGECLOSED", "ITEM_CRAFT", "ITEM_USE", "KEYINSIGHT_EARNED", "KEYINSIGHT_RECEIVED", "KEYINSIGHT_SENT", "KEYINSIGHT_USED", "MATCHMAKING_UPDATE", "MENU_PASS", "MM_ABORT", "MM_START", "MM_SUCC", "PC_SPECS", "PLAYER_DAMAGE", "PLAYER_POS", "PLAYER_REVIVE", "PLAYER_SPAWN", "PLAYER_STATS", "POPUP_ANSWER", "REWARD_ACTION", "REWARD_ACTIONSELECTED", "SUPPORT_WEAPON", "UPLAY_PASS", "WAIT_REVIVE"
    ];

    /// <summary>GetConfiguration: the list of tags the client should report.</summary>
    public static byte[] BuildGetConfigurationResult()
    {
        using var body = new MemoryStream();
        TicketGranting.WriteU32(body, (uint)ConfiguredTags.Length);
        foreach (string tag in ConfiguredTags)
            TicketGranting.WriteString(body, tag);
        return body.ToArray();
    }

    /// <summary>
    /// SendUserInfo: (TrackingInformation userInfo, uint32 deltaTime) ->
    /// (TrackingInformation userInfo, uint32 trackingID). The working server
    /// answers by echoing the information back with tracking id 0 (community
    /// capture: a 32-byte request, a 32-byte reply). Refusing it made the
    /// client drop and re-open its telemetry session over and over, which
    /// was the trigger for the endpoint-keying bug above.
    /// </summary>
    public static byte[] BuildSendUserInfoResult(RmcRequest request)
    {
        ReadOnlySpan<byte> p = request.Parameters;
        if (p.Length < 4) return new byte[4];
        var body = new byte[p.Length];
        p[..^4].CopyTo(body);                  // userInfo, echoed; the trailing deltaTime becomes
        return body;                           // the trackingID, zero
    }

    /// <summary>One reported event.</summary>
    internal sealed record TrackingEvent(string Tag, IReadOnlyDictionary<string, string> Attributes, uint DeltaTime);

    /// <summary>
    /// SendTags / SendTag: parses the events and hands each to the
    /// statistics. Void result. A tag that does not parse stops the batch
    /// rather than guessing at the rest; what parsed is kept.
    /// </summary>
    public static byte[] BuildSendTagsResult(RmcRequest request, uint pid, Action<uint, TrackingEvent> onEvent)
    {
        ReadOnlySpan<byte> p = request.Parameters;
        int offset = 0;
        uint count = 1;
        if (request.MethodId == MethodSendTags)
        {
            if (!TryReadU32(p, ref offset, out count)) return [];
        }
        for (uint i = 0; i < count && i < 256; i++)
        {
            if (!TryReadU32(p, ref offset, out _)) break;                     // trackingID
            if (!TryReadString(p, ref offset, out string tag)) break;
            if (!TryReadString(p, ref offset, out string attributes)) break;
            if (!TryReadU32(p, ref offset, out uint delta)) break;
            if (request.MethodId == MethodSendTags && !TryReadString(p, ref offset, out _)) break;   // newUserId
            var parsed = Parse(attributes);
            Serilog.Log.Information("[tracking] pid={Pid} {Tag} {Attributes}", pid, tag, attributes);
            onEvent(pid, new TrackingEvent(tag, parsed, delta));
        }
        return [];
    }

    private static Dictionary<string, string> Parse(string attributes)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string pair in attributes.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq < 0) map[pair] = string.Empty;
            else map[pair[..eq]] = pair[(eq + 1)..];
        }
        return map;
    }

    private static bool TryReadU32(ReadOnlySpan<byte> p, ref int offset, out uint value)
    {
        value = 0;
        if (p.Length < offset + 4) return false;
        value = BinaryPrimitives.ReadUInt32LittleEndian(p[offset..]);
        offset += 4;
        return true;
    }

    private static bool TryReadString(ReadOnlySpan<byte> p, ref int offset, out string value)
    {
        value = string.Empty;
        if (p.Length < offset + 2) return false;
        int length = BinaryPrimitives.ReadUInt16LittleEndian(p[offset..]);
        offset += 2;
        if (length == 0) return true;
        if (p.Length < offset + length) return false;
        value = Encoding.UTF8.GetString(p.Slice(offset, length - 1));
        offset += length;
        return true;
    }
}
