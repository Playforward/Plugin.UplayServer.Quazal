namespace Plugin.UplayServer.Quazal.Tests;

/// <summary>
/// The one title most tests run under. Every session and pid on the server
/// belongs to a title now; a test that is not about titles just needs one to
/// name, and this is it.
/// </summary>
internal static class TestTitle
{
    public const string Id = "farcry3";
    public const int Port = 6570;
    public const int SecurePort = 6571;

    /// <summary>The audience a token for this title carries, as Playforward derives it.</summary>
    public const string Audience = "playforward-game-servers:" + Id;

    public static QuazalTitle Quazal { get; } = QuazalTitle.ForTests(Id, Port, SecurePort);
}
