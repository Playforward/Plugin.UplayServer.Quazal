namespace Plugin.UplayServer.Quazal;

/// <summary>
/// Far Cry 3's multiplayer statistic definitions, as the game itself defines
/// them: the column sets of the persistent statistic tables the online-data
/// service (protocol 106) keys by <c>client_id</c>.
/// </summary>
internal static class Fc3StatisticDefinitions
{
    /// <summary>Protocol 106 data id of the persistent per-player table.</summary>
    public const uint StatisticPlayerDataId = 0x06;

    /// <summary>The column the persistent tables are keyed and queried by.</summary>
    public const string ClientIdColumn = "client_id";

    /// <summary><c>PersistentTypes/Player</c>: the <c>0x06 StatisticPlayer</c> row.</summary>
    public static readonly string[] PlayerColumns =
    [
        "xp", "xp_coop", "xp_pvp", "tp",
        "kills", "kills_coop", "kills_pvp", "kills_streak", "kills_assist", "kills_melee", "kills_tagging",
        "deaths", "deaths_coop", "deaths_pvp", "suicides",
        "teamplay_revives", "teamplay_revived", "teamplay_battlecrys", "teamplay_buddyboost_given", "teamplay_buddyboost_received",
        "match_wins", "match_losses", "match_played", "match_win_bonus_part",
        "gamemode_capture", "gamemode_break", "gamemode_destroy", "gamemode_resource_tick",
        "playtime", "playtime_coop", "playtime_pvp",
        "dailychallenges_task_done", "dailychallenges_challenge_done", "weeklychallenges_task_done", "weeklychallenges_challenge_done",
        "won_cce", "participated_in_eomm", "match_chapters_completed",
    ];

    /// <summary><c>PersistentTypes/Match</c>: the <c>0x05 StatisticMatch</c> row (plus <c>unixtimestamp</c>).</summary>
    public static readonly string[] MatchColumns =
    [
        "gamemode_objective_achieved", "match_gamemode", "match_winning_team", "match_time", "unixtimestamp",
        "gamemode_team_blue_points", "gamemode_team_red_points", "match_map",
    ];

    /// <summary><c>PersistentTypes/TimeSlice</c>: the <c>0x07 StatisticTimeSlice</c> row, one per player per match.</summary>
    public static readonly string[] TimeSliceColumns =
    [
        "xp", "tp", "kills", "kills_assist", "kills_headshot", "kills_melee", "kills_longrange", "deaths",
        "teamplay_revives", "gamemode_team", "playtime", "jointime", "unixtimestamp", "leave_reason",
    ];

    /// <summary><c>PersistentTypes/Weapon</c>: the <c>0x08 StatisticWeapon</c> row, one per player per weapon.</summary>
    public static readonly string[] WeaponColumns =
    [
        "xp", "weapon_shots_fired", "weapon_shots_hit", "weapon_damage", "weapon_kill", "weapon_reload", "weapon_time", "value",
    ];
}
