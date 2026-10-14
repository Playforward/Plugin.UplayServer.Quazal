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
    }
}
