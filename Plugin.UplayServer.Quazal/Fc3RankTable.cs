namespace Plugin.UplayServer.Quazal;

/// <summary>
/// Far Cry 3's multiplayer rank tables, as the game defines them: the XP at
/// which each rank begins.
/// </summary>
internal static class Fc3RankTable
{
    /// <summary>XP required for rank <c>index + 1</c>.</summary>
    private static readonly uint[] XpForRank =
    [
              0,     800,    1600,    2600,    3900,    5500,    7300,    9500,   12000,   14700,
          17700,   21100,   24700,   28600,   32800,   37200,   42000,   47100,   52400,   58100,
          64000,   70200,   76700,   83500,   90600,   97900,  105600,  113500,  121800,  130300,
         139100,  148200,  157600,  167300,  177300,  187500,  198100,  208900,  220000,  231500,
         243200,  255100,  267400,  280000,  292900,  306000,  319500,  333200,  347200,  361500,
         376100,  391000,  406200,  421600,  437400,  453400,  469700,  486300,  503300,  520500,
         537900,  555700,  573800,  592100,  610800,  629700,  648900,  668400,  688200,  708300,
         728700,  749300,  770300,  791500,  813100,
    ];

    /// <summary>The <c>primary</c> track: XP required for displayed level <c>index + 1</c>.</summary>
    private static readonly uint[] XpForLevel =
    [
            0,   500,  1100,  1900,  3000,  4300,  5900,  7700,  9700, 12000,
        14500, 17300, 20300, 23500, 27000, 30700, 34700, 38900, 43300, 48000,
    ];

    public static int MaxRank => XpForRank.Length;
    public static int MaxLevel => XpForLevel.Length;

    /// <summary>The <c>player</c>-track rank an XP total has reached, 1 to 75 -- the game's <c>PLAYERLEVEL</c>.</summary>
    public static uint RankFor(uint xp) => Reached(XpForRank, xp);

    /// <summary>The level the game displays for an XP total, 1 to 20 (the <c>primary</c> track).</summary>
    public static uint DisplayLevelFor(uint xp) => Reached(XpForLevel, xp);

    private static uint Reached(uint[] thresholds, uint xp)
    {
        int rank = 1;
        while (rank < thresholds.Length && xp >= thresholds[rank])
            rank++;
        return (uint)rank;
    }
}
