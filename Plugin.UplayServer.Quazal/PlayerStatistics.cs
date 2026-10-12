using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace Plugin.UplayServer.Quazal;

/// <summary>
/// The persistent per-player statistics -- the <c>0x06 StatisticPlayer</c>
/// row -- built the way Ubisoft's backend built them: from the host's
/// <c>0x06</c> delta rows, or failing those from the events the game
/// reports through Tracking during a match.
/// </summary>
internal static class PlayerStatistics
{
    private const string Namespace = "stats";
    private const string Key = "player";

    /// <summary>The tag this server counts a match from; every other tag but the host's start is logged and kept for the record only.</summary>
    private const string TagMatchEnd = "FPSCLIENT_STOP";
    /// <summary>The host's telemetry naming the match it is about to store the row for.</summary>
    private const string TagHostStart = "FPSHOST_START";
    private const string SectionKills = "PL_KL";
    private const string SectionDeaths = "PL_DTH";
    /// <summary>Weapon usage: <c>GD_USD[name:CTR!uses;HITS!n;BULLETSFIRED!n;KILLS!n;DUR!seconds|…]</c>.</summary>
    private const string SectionWeapons = "GD_USD";
    /// <summary>XP the game's definitions award a kill (<c>Statistic kills … XP = 100</c>), credited to the weapon that made it.</summary>
    private const uint XpPerKill = 100;

    private static readonly ConcurrentDictionary<uint, ConcurrentDictionary<string, uint>> Counters = [];

    /// <summary>
    /// The columns of the row published to Playforward as score boards, under
    /// their own names: what the public leaderboard on the game's page can
    /// rank players by. An admin chooses which of them the page shows and
    /// what each is labelled (Playforward's title_leaderboards); publishing a
    /// board shows nothing by itself. <c>playtime</c> is in seconds, as the
    /// telemetry's <c>TIME</c> is.
    /// </summary>
    private static readonly string[] PublishedColumns =
        [Fc3IntelService.XpBoard, "kills", "deaths", "match_wins", "match_losses", "match_played", "playtime"];

    /// <summary>
    /// The board the player's rank goes on: the <c>player</c>-track rank
    /// (1-75) the game reports as <c>PLAYERLEVEL</c> and shows at the end of
    /// a match, derived from the XP rather than kept as a column of its own.
    /// </summary>
    internal const string LevelBoard = "level";

    /// <summary>Resolves a player named in an event (pid digits or in-game name) to a pid.</summary>
    internal delegate uint? PlayerResolver(string named);

    /// <summary>The player's counters, loaded from Playforward on first touch.</summary>
    public static IReadOnlyDictionary<string, uint> For(uint pid) => Load(pid);

    public static uint Get(uint pid, string column) =>
        Load(pid).TryGetValue(column, out uint value) ? value : 0;

    /// <summary>
    /// Applies one tracking event reported by <paramref name="reporter"/>.
    /// Returns the player whose statistics changed, or 0 when none did. With
    /// <paramref name="countMatches"/> off only the host's match start is
    /// noted (it names the match the host's row belongs to); no match is
    /// counted from telemetry.
    /// </summary>
    public static uint Apply(uint reporter, TrackingProtocol.TrackingEvent e, PlayerResolver resolve, bool countMatches = true)
    {
        if (e.Tag == TagHostStart)
        {
            Fc3MatchLedger.MatchStarted(reporter, e.Attributes.GetValueOrDefault("MATCHID", ""));
            return 0;
        }
        if (e.Tag != TagMatchEnd || !countMatches) return 0;
        uint player = Subject(reporter, e, resolve);
        if (player == 0)
        {
            Serilog.Log.Warning("[stats] {Tag} from a connection with no player behind it; not counted", e.Tag);
            return 0;
        }
        if (Fc3MatchLedger.HostReportedRecently(player))
        {
            Serilog.Log.Information("[stats] pid={Pid}: the host uploaded this match's statistics; the telemetry is not counted again", player);
            return 0;
        }

        var data = ParseData(e.Attributes.GetValueOrDefault("data", ""));
        uint kills = Total(data, SectionKills);
        uint deaths = Total(data, SectionDeaths);
        uint score = 0;
        if (e.Attributes.TryGetValue("SCORE", out string? scoreText))
            uint.TryParse(scoreText, NumberStyles.Integer, CultureInfo.InvariantCulture, out score);
        if (score > 0) { Add(player, "xp", score); Add(player, "xp_pvp", score); }
        if (kills > 0) { Add(player, "kills", kills); Add(player, "kills_pvp", kills); }
        if (deaths > 0) { Add(player, "deaths", deaths); Add(player, "deaths_pvp", deaths); }

        Add(player, "match_played", 1);
        string team = e.Attributes.GetValueOrDefault("TEAM", "");
        string winner = e.Attributes.GetValueOrDefault("WINTEAM", "");
        bool won = false;
        if (team.Length > 0 && winner.Length > 0 && !string.Equals(winner, "Draw", StringComparison.OrdinalIgnoreCase))
        {
            won = string.Equals(team, winner, StringComparison.OrdinalIgnoreCase);
            Add(player, won ? "match_wins" : "match_losses", 1);
        }
        uint seconds = 0;
        if (e.Attributes.TryGetValue("TIME", out string? time) &&
            double.TryParse(time, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && parsed > 0)
        {
            seconds = (uint)parsed;
            Add(player, "playtime", seconds);
            Add(player, "playtime_pvp", seconds);
        }

        FileWeapons(player, data);
        FileTimeSlice(player, e.Attributes.GetValueOrDefault("MATCHID", ""), score, kills, deaths, team, seconds);
        return player;
    }

    /// <summary>
    /// The match's Weapon rows from its <c>GD_USD</c> usage: one per weapon
    /// the player used, keyed by the game's id for it -- the CRC-32 of the
    /// weapon's name in lower case, which is how <c>STGW-90</c> in the
    /// telemetry is <c>weapon_id</c> 359465410 in the host's upload
    /// (2026-09-17). Shots, hits, kills and time are the telemetry's; the XP
    /// is the kills' worth. Damage and reloads are not reported and stay 0.
    /// </summary>
    private static void FileWeapons(uint player, IReadOnlyDictionary<string, List<(string Key, Dictionary<string, uint> Fields)>> data)
    {
        if (!data.TryGetValue(SectionWeapons, out var weapons)) return;
        foreach ((string name, Dictionary<string, uint> fields) in weapons)
        {
            if (name.Length == 0) continue;
            uint kills = fields.GetValueOrDefault("KILLS");
            var row = new List<OnlineDataColumn>
            {
                OnlineDataColumn.Served(Fc3StatisticDefinitions.ClientIdColumn, player),
                OnlineDataColumn.Served("weapon_id", Fc3StringId.Of(name)),
                OnlineDataColumn.Served("xp", kills * XpPerKill),
                OnlineDataColumn.Served("weapon_shots_fired", fields.GetValueOrDefault("BULLETSFIRED")),
                OnlineDataColumn.Served("weapon_shots_hit", fields.GetValueOrDefault("HITS")),
                OnlineDataColumn.Served("weapon_damage", 0),
                OnlineDataColumn.Served("weapon_kill", kills),
                OnlineDataColumn.Served("weapon_reload", 0),
                OnlineDataColumn.Served("weapon_time", fields.GetValueOrDefault("DUR")),
            };
            Fc3OnlineDataService.FileWeapon(player, row, $"telemetry ({name})");
        }
    }

    /// <summary>
    /// The match's TimeSlice row: what the host would have uploaded, from
    /// what the telemetry says. The XP is the match SCORE, as the host's row
    /// has it; the team is 0 for Blue and 1 for Red; the columns the
    /// telemetry says nothing about (assists, headshots, revives) stay 0.
    /// </summary>
    private static void FileTimeSlice(uint player, string telemetryMatchId, uint score, uint kills, uint deaths, string team, uint seconds)
    {
        if (telemetryMatchId.Length == 0) return;
        uint matchId = Fc3MatchLedger.RowIdFor(telemetryMatchId);
        var row = new List<OnlineDataColumn>
        {
            OnlineDataColumn.Served(Fc3StatisticDefinitions.ClientIdColumn, player),
            OnlineDataColumn.Served("match_id", matchId),
            OnlineDataColumn.Served("xp", score),
            OnlineDataColumn.Served("tp", 0),
            OnlineDataColumn.Served("kills", kills),
            OnlineDataColumn.Served("kills_assist", 0),
            OnlineDataColumn.Served("kills_headshot", 0),
            OnlineDataColumn.Served("kills_melee", 0),
            OnlineDataColumn.Served("kills_longrange", 0),
            OnlineDataColumn.Served("deaths", deaths),
            OnlineDataColumn.Served("teamplay_revives", 0),
            OnlineDataColumn.Served("gamemode_team", string.Equals(team, "Red", StringComparison.OrdinalIgnoreCase) ? 1u : 0u),
            OnlineDataColumn.Served("playtime", seconds),
            OnlineDataColumn.Served("jointime", 1),
            OnlineDataColumn.Served("leave_reason", 0),
        };
        Fc3OnlineDataService.FileTimeSlice(player, row, "telemetry");
    }

    /// <summary>Sum of a section's <c>CTR</c> fields across its keys.</summary>
    private static uint Total(IReadOnlyDictionary<string, List<(string Key, Dictionary<string, uint> Fields)>> data, string section)
    {
        uint total = 0;
        if (data.TryGetValue(section, out var entries))
            foreach ((_, Dictionary<string, uint> fields) in entries)
                total += fields.GetValueOrDefault("CTR");
        return total;
    }

    /// <summary>
    /// Parses the <c>data</c> field: <c>NAME[entry|entry]NAME[…]</c>, an
    /// entry being <c>key:FIELD!value;FIELD!value</c>. Anything malformed
    /// ends the section rather than being guessed at.
    /// </summary>
    internal static Dictionary<string, List<(string Key, Dictionary<string, uint> Fields)>> ParseData(string data)
    {
        var sections = new Dictionary<string, List<(string, Dictionary<string, uint>)>>(StringComparer.Ordinal);
        int at = 0;
        while (at < data.Length)
        {
            int open = data.IndexOf('[', at);
            if (open < 0) break;
            int close = data.IndexOf(']', open);
            if (close < 0) break;
            string name = data[at..open];
            var entries = new List<(string, Dictionary<string, uint>)>();
            foreach (string entry in data[(open + 1)..close].Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = entry.IndexOf(':');
                if (colon < 0) continue;
                var fields = new Dictionary<string, uint>(StringComparer.Ordinal);
                foreach (string field in entry[(colon + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    int bang = field.IndexOf('!');
                    if (bang > 0 && uint.TryParse(field[(bang + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint value))
                        fields[field[..bang]] = value;
                }
                entries.Add((entry[..colon], fields));
            }
            sections[name] = entries;
            at = close + 1;
        }
        return sections;
    }

    /// <summary>
    /// Adds a <c>0x06 StatisticPlayer</c> delta row (mode 8, "add to the
    /// row") to one player's counters, XP included: the host's per-player
    /// match rows, and the reward row the client stores for itself when it
    /// claims decoded data. Returns whether anything changed.
    /// </summary>
    public static bool Accumulate(uint pid, IEnumerable<(string Column, uint Value)> delta, string source)
    {
        var counters = Load(pid);
        var applied = new List<string>();
        foreach ((string column, uint value) in delta)
        {
            if (value == 0 || column is "client_id") continue;
            uint total = counters.AddOrUpdate(column, value, (_, current) => current + value);
            applied.Add($"{column} +{value} -> {total}");
        }
        if (applied.Count == 0) return false;
        Persist(pid, counters);
        Serilog.Log.Information("[stats] pid={Pid} statistics from {Source}: {Applied}", pid, source, string.Join(", ", applied));
        return true;
    }

    /// <summary>The XP of the player's row -- what the game turns into the level.</summary>
    public static uint Xp(uint pid) => Get(pid, "xp");

    /// <summary>Forgets a player's cached counters; the next touch reloads them.</summary>
    public static void Forget(uint pid) => Counters.TryRemove(pid, out _);

    public static void Clear() => Counters.Clear();

    /// <summary>
    /// The PLAYERID an event names, when it resolves; otherwise the player
    /// behind the reporting connection -- 0 when there is none, in which
    /// case the event has no subject and is not counted.
    /// </summary>
    private static uint Subject(uint reporter, TrackingProtocol.TrackingEvent e, PlayerResolver resolve) =>
        e.Attributes.TryGetValue("PLAYERID", out string? named) && named.Length > 0 && resolve(named) is { } pid ? pid : reporter;

    private static void Add(uint pid, string column, uint amount)
    {
        var counters = Load(pid);
        uint total = counters.AddOrUpdate(column, amount, (_, current) => current + amount);
        Persist(pid, counters);
        Serilog.Log.Information("[stats] pid={Pid} {Column} +{Amount} -> {Total}", pid, column, amount, total);
    }

    private static ConcurrentDictionary<string, uint> Load(uint pid) =>
        Counters.GetOrAdd(pid, _ =>
        {
            var loaded = new ConcurrentDictionary<string, uint>(StringComparer.Ordinal);
            byte[]? stored = OnlineDataStore.GetBlob(pid, Namespace, Key);
            if (stored is null) return loaded;
            try
            {
                foreach ((string column, uint value) in JsonSerializer.Deserialize<Dictionary<string, uint>>(stored) ?? [])
                    loaded[column] = value;
            }
            catch (JsonException exception)
            {
                Serilog.Log.Warning("[stats] pid={Pid}: stored statistics did not parse ({Error}); starting over", pid, exception.Message);
            }
            return loaded;
        });

    private static void Persist(uint pid, ConcurrentDictionary<string, uint> counters)
    {
        OnlineDataStore.PutBlob(pid, Namespace, Key, JsonSerializer.SerializeToUtf8Bytes(counters.ToDictionary()));
        OnlineDataStore.PutScores(pid, PublishedScores(counters));
    }

    /// <summary>
    /// The numbers a player's counters publish: every published column (zero
    /// when nothing has counted it yet, so a player who has played is on each
    /// board) and the rank their XP has reached.
    /// </summary>
    internal static Dictionary<string, long> PublishedScores(IReadOnlyDictionary<string, uint> counters)
    {
        var scores = PublishedColumns.ToDictionary(column => column, column => (long)counters.GetValueOrDefault(column), StringComparer.Ordinal);
        scores[LevelBoard] = Fc3RankTable.RankFor(counters.GetValueOrDefault(Fc3IntelService.XpBoard));
        return scores;
    }
}
