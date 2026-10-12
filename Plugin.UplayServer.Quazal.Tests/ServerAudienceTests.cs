using Plugin.UplayServer.Quazal.Playforward;

namespace Plugin.UplayServer.Quazal.Tests;

/// <summary>
/// Which audience this server accepts. It used to be one per title, so every
/// community's server of a game accepted every other's tokens and commands:
/// the operator of one could replay the tokens of players joining it at
/// another, and log in there as them.
/// </summary>
public sealed class ServerAudienceTests
{
    private static readonly Guid ThisServer = Guid.Parse("6f1c2a4e-0d6b-4f3a-9a52-3b8e2f0c7d11");
    private static readonly Guid AnotherServer = Guid.Parse("0a7e3f55-9c21-4d8e-b1f0-2c4d6e8fa013");

    [Fact]
    public void IsDerivedPerServerExactlyAsPlayforwardMintsIt()
    {
        var settings = new PlayforwardSettings { AudiencePrefix = "playforward-game-servers" };

        Assert.Equal("playforward-game-servers:farcry3:6f1c2a4e-0d6b-4f3a-9a52-3b8e2f0c7d11",
            settings.AudienceFor("farcry3", ThisServer));
        Assert.NotEqual(settings.AudienceFor("farcry3", ThisServer), settings.AudienceFor("farcry3", AnotherServer));
    }

    [Fact]
    public void TakesTheServerIdFromConfigOrFromPlayforward()
    {
        Assert.Equal(ThisServer, PlayforwardTitle.ServerIdentity(TestTitle.Quazal, ThisServer, null));
        Assert.Equal(ThisServer, PlayforwardTitle.ServerIdentity(TestTitle.Quazal, null, ThisServer));
        Assert.Equal(ThisServer, PlayforwardTitle.ServerIdentity(TestTitle.Quazal, ThisServer, ThisServer));
    }

    [Fact]
    public void RefusesToGuessWhichServerItIs()
    {
        // Unknown: accepting any audience would be the replay this prevents.
        Assert.Null(PlayforwardTitle.ServerIdentity(TestTitle.Quazal, null, null));
        // A secret that belongs to a different server than the one configured.
        Assert.Null(PlayforwardTitle.ServerIdentity(TestTitle.Quazal, ThisServer, AnotherServer));
    }
}
