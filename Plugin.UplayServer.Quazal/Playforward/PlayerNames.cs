using System.Collections.Concurrent;

namespace Plugin.UplayServer.Quazal.Playforward;

/// <summary>
/// The display names of players this server has seen on a Playforward board
/// but does not hold in memory 
/// </summary>
internal static class PlayerNames
{
    private static readonly ConcurrentDictionary<(string TitleId, uint Pid), string> Names = [];

    public static void Remember(string titleId, long pid, string name)
    {
        if (pid is < 1 or > uint.MaxValue || string.IsNullOrEmpty(name)) return;
        Names[(titleId, (uint)pid)] = name;
    }

    public static string? For(string titleId, uint pid) =>
        Names.TryGetValue((titleId, pid), out string? name) ? name : null;

    /// <summary>The reverse: a pid seen under this name on a board, for a friend lookup by name.</summary>
    public static uint? PidFor(string titleId, string name)
    {
        foreach (((string knownTitle, uint pid), string known) in Names)
        {
            if (string.Equals(knownTitle, titleId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(known, name, StringComparison.OrdinalIgnoreCase))
                return pid;
        }
        return null;
    }

    public static void Clear() => Names.Clear();
}
