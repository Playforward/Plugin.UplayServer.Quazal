namespace Plugin.UplayServer.Quazal.Standalone;

/// <summary>
/// How a title is served when Playforward is not behind it: who may log in,
/// and where their PIDs and progress are kept between runs.
/// </summary>
public sealed class StandaloneSettings
{
    /// <summary>
    /// Whether a title with no Playforward half accepts any player name and
    /// keeps what that player builds up on this machine. On by default: a
    /// server with no accounts service is otherwise a LAN server whose
    /// players must each be typed into <see cref="QuazalSettings.Accounts"/>
    /// and whose progress is gone at the next restart. A title Playforward
    /// covers is never served this way, whatever this says.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Where players and progress are kept, one folder per title. Relative
    /// paths are under the working directory; the packaged service may write
    /// only under <c>Database/</c> and <c>Configs/</c>.
    /// </summary>
    public string DataDirectory { get; set; } = "Database/Quazal";

    /// <summary>
    /// The first PID handed to a new player. Clear of the configured
    /// accounts and the LAN range (<see cref="QuazalSettings.DynamicPlayerPidStart"/>)
    /// below it, and of Playforward's allocator (2,000,000) above it, so a
    /// server can be moved between the schemes without two players sharing
    /// a PID.
    /// </summary>
    public uint PidStart { get; set; } = 1_000_000;

    /// <summary>
    /// Refuse a login under a name another address is playing as right now.
    /// The game proves nothing about the name it sends, so this is what
    /// stops a second player taking over the first one's session by typing
    /// the same name; it does not stop them using it once the first is gone.
    /// </summary>
    public bool RefuseNameInUse { get; set; } = true;

    /// <summary>Names nobody may log in as (compared case-insensitively), e.g. an admin's.</summary>
    public List<string> ReservedNames { get; set; } = [];

    internal IEnumerable<string> Validate(QuazalSettings settings)
    {
        if (!Enabled)
            yield break;
        if (string.IsNullOrWhiteSpace(DataDirectory))
            yield return "Standalone.DataDirectory must name a folder";
        if (PidStart <= settings.DynamicPlayerPidStart)
            yield return $"Standalone.PidStart ({PidStart}) must be above DynamicPlayerPidStart ({settings.DynamicPlayerPidStart}), or a LAN pid and a standalone one could be the same player";
        foreach (QuazalAccount account in settings.Accounts ?? [])
        {
            if (account.Pid >= PidStart)
                yield return $"account '{account.Username}' has Pid {account.Pid}, inside the standalone range that starts at Standalone.PidStart ({PidStart})";
        }
    }
}
