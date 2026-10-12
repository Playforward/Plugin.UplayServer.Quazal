using System.Collections.Concurrent;

namespace Plugin.UplayServer.Quazal;

/// <summary>
/// What this server knows about each match while it is being reported: the
/// row id it was filed under, which players the host has uploaded statistics
/// for, and who has to be told that their statistics changed.
/// </summary>
internal static class Fc3MatchLedger
{
    /// <summary>NotificationEvent type: "your statistics rows changed" (param1 = the data id).</summary>
    public const uint StatisticsUpdated = 1_003_000;

    /// <summary>How long after a match row this server waits for the host's player rows before telling the players itself.</summary>
    public static TimeSpan HostUploadGrace { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>A host's player rows for a match arrive within a second or two of its match row; anything older belongs to an earlier match.</summary>
    private static readonly TimeSpan HostReportWindow = TimeSpan.FromSeconds(90);

    private static readonly ConcurrentDictionary<string, uint> RowIds = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, uint> Hosts = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<uint, string> StartedByHost = [];
    /// <summary>When each host last uploaded player rows, and when each player was last the subject of such rows.</summary>
    private static readonly ConcurrentDictionary<uint, DateTime> HostUploadedAt = [];
    private static readonly ConcurrentDictionary<uint, DateTime> HostReportedAt = [];

    /// <summary>The host's telemetry announced a match: remember its id against the host.</summary>
    public static void MatchStarted(uint hostPid, string telemetryMatchId)
    {
        if (hostPid == 0 || telemetryMatchId.Length == 0) return;
        StartedByHost[hostPid] = telemetryMatchId;
        Hosts[telemetryMatchId] = hostPid;
    }

    /// <summary>
    /// The host stored the match row and was given <paramref name="rowId"/>:
    /// file it under the match the host's telemetry last started, and see to
    /// it that the players are told once the statistics are in.
    /// </summary>
    public static void MatchStored(string titleId, uint hostPid, uint rowId, GameSession.Notifier? notify)
    {
        DateTime now = DateTime.UtcNow;
        if (StartedByHost.TryGetValue(hostPid, out string? telemetryMatchId))
            RowIds[telemetryMatchId] = rowId;
        if (notify is null) return;
        _ = Task.Delay(HostUploadGrace).ContinueWith(_ =>
        {
            if (HostUploadedAt.TryGetValue(hostPid, out DateTime uploaded) && uploaded >= now)
                return;
            Serilog.Log.Information("[stats] no player rows followed match {Match} from host pid={Host} within {Grace}s; telling the players anyway",
                rowId, hostPid, HostUploadGrace.TotalSeconds);
            TellSessionOf(titleId, hostPid, notify);
        }, TaskScheduler.Default);
    }

    /// <summary>The host uploaded player rows: their subjects need not be counted from telemetry, and every participant re-reads.</summary>
    public static void HostReported(string titleId, uint hostPid, IEnumerable<uint> players, GameSession.Notifier? notify)
    {
        DateTime now = DateTime.UtcNow;
        HostUploadedAt[hostPid] = now;
        foreach (uint player in players)
            HostReportedAt[player] = now;
        if (notify is not null)
            TellSessionOf(titleId, hostPid, notify);
    }

    /// <summary>Whether the host has uploaded this player's rows for the match just played.</summary>
    public static bool HostReportedRecently(uint pid) =>
        HostReportedAt.TryGetValue(pid, out DateTime at) && DateTime.UtcNow - at < HostReportWindow;

    /// <summary>The row id a match is filed under, allocating one when no host stored it.</summary>
    public static uint RowIdFor(string telemetryMatchId) =>
        RowIds.GetOrAdd(telemetryMatchId, _ => OnlineDataStore.NextRowId());

    /// <summary>The host of a match, when its telemetry said so; 0 otherwise.</summary>
    public static uint HostOf(string telemetryMatchId) => Hosts.GetValueOrDefault(telemetryMatchId, 0u);

    /// <summary>Tells one player to re-read its statistics rows.</summary>
    public static bool Tell(uint sourcePid, uint pid, GameSession.Notifier notify) =>
        notify(pid, GameSession.NotificationProtocolId, GameSession.NotificationMethod,
            GameSession.NotificationEvent(sourcePid == 0 ? pid : sourcePid, StatisticsUpdated,
                Fc3StatisticDefinitions.StatisticPlayerDataId, 0, 0));

    /// <summary>Tells every participant of the sessions the host is in, the host included.</summary>
    public static void TellSessionOf(string titleId, uint hostPid, GameSession.Notifier notify)
    {
        var players = new HashSet<uint> { hostPid };
        foreach ((uint typeId, uint sessionId) in GameSessionStore.SessionsContaining(titleId, hostPid))
            foreach (uint participant in GameSessionStore.GetParticipants(titleId, typeId, sessionId))
                players.Add(participant);
        var told = new List<uint>();
        foreach (uint player in players)
            if (Tell(hostPid, player, notify))
                told.Add(player);
        Serilog.Log.Information("[stats] statistics-updated notification from host pid={Host} delivered to [{Players}]",
            hostPid, string.Join(",", told));
    }

    public static void Clear()
    {
        RowIds.Clear();
        Hosts.Clear();
        StartedByHost.Clear();
        HostUploadedAt.Clear();
        HostReportedAt.Clear();
    }
}
