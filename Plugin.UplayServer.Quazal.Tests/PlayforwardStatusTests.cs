using System.Text.Json;
using Plugin.UplayServer.Quazal.Playforward;

namespace Plugin.UplayServer.Quazal.Tests;

/// <summary>
/// GET /playforward/status: what a desktop client reads to know a launch
/// would be accepted, without minting or sending a token to find out.
/// </summary>
public sealed class PlayforwardStatusTests
{
    private static readonly Guid ServerId = Guid.Parse("9b2e2e70-b8a0-400d-8088-38f8d3389d31");

    [Fact]
    public void NamesTheServerItsAcceptedKeysAndItsClock()
    {
        var tokens = new PlayforwardTokenFactory();
        var settings = new PlayforwardSettings
        {
            Enabled = true,
            PublicKeyBase64 = tokens.PublicKeyBase64Url,
            Issuer = PlayforwardTokenFactory.Issuer,
            BaseUrl = "http://playforward.test",
        };
        using PlayforwardGateway gateway = PlayforwardGateway.Start(settings, TestTitle.Id, PlayforwardTokenFactory.Audience,
            TestTitle.Quazal.Settings.Playforward.LoginNameMaxLength, new InMemoryProgressStore())!;
        PlayforwardTitle title = PlayforwardTitle.ForTests(TestTitle.Quazal, PlayforwardTokenFactory.Audience, gateway,
            new InMemoryProgressStore(), ServerId);
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

        using JsonDocument status = JsonDocument.Parse(PlayforwardRoutes.StatusDocument([title], now));

        Assert.Equal(now.ToUnixTimeSeconds(), status.RootElement.GetProperty("time").GetInt64());
        JsonElement entry = Assert.Single(status.RootElement.GetProperty("titles").EnumerateArray());
        Assert.Equal(TestTitle.Id, entry.GetProperty("title_id").GetString());
        Assert.Equal(ServerId, entry.GetProperty("server_id").GetGuid());
        // The client compares this with the key id Playforward signs with now.
        Assert.Equal([tokens.KeyId], entry.GetProperty("key_ids").EnumerateArray().Select(id => id.GetString()));
        Assert.Equal(settings.ClockSkewSeconds, entry.GetProperty("clock_skew_seconds").GetInt32());
        Assert.Equal(PlayforwardRoutes.BootId, status.RootElement.GetProperty("boot_id").GetString());
    }

    [Fact]
    public void ALaunchLineIsWhatTheShimParses()
    {
        // The shim reads this inside the game process (UplaySDKShim's
        // launch_envelope parse_launch_line): LAUNCH, the id, the key and the
        // boot id as lowercase hex, and the seconds left, one space apart.
        byte[] key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var launch = new IssuedLaunch("00112233445566778899aabbccddeeff", key, DateTimeOffset.UtcNow.AddHours(8),
            new ResolvedPlayer(new PlayforwardIdentity("account", "Player", "player", "plyr", "user", "", "jti", DateTimeOffset.UtcNow), 2_000_001));

        string line = PlayforwardRoutes.LaunchLine(launch, expiresInSeconds: 28_800);

        Assert.Matches(@"^LAUNCH [0-9a-f]{32} [0-9a-f]{64} [0-9]+ [0-9a-f]{32}\n$", line);
        Assert.Equal($"LAUNCH 00112233445566778899aabbccddeeff {Convert.ToHexString(key).ToLowerInvariant()} 28800 {PlayforwardRoutes.BootId}\n", line);
    }
}
