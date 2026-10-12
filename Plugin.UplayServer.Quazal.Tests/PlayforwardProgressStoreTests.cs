using System.Net;
using System.Text;
using System.Text.Json;
using Plugin.UplayServer.Quazal.Playforward;

namespace Plugin.UplayServer.Quazal.Tests;

/// <summary>
/// The wire contract with Playforward's player-data endpoints, pinned on this
/// side: the paths, the shapes, and that a payload travels as the game's own
/// bytes out and base64 back.
/// </summary>
public sealed class PlayforwardProgressStoreTests
{
    /// <summary>A Playforward that answers from a script and records what it was asked.</summary>
    private sealed class StubPlayforward : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? ContentType, byte[] Body)> Requests { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage> Answer { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            byte[] body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellation);
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery, request.Content?.Headers.ContentType?.MediaType, body));
            return Answer(request);
        }
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static (PlayforwardProgressStore Store, StubPlayforward Playforward) NewStore()
    {
        var stub = new StubPlayforward();
        var client = PlayforwardServerClient.ForTransport(stub, "http://playforward.test/");
        return (new PlayforwardProgressStore(client), stub);
    }

    [Fact]
    public void AsksWhichTitleItsSecretIsRegisteredFor()
    {
        var stub = new StubPlayforward();
        var client = PlayforwardServerClient.ForTransport(stub, "http://playforward.test/");
        stub.Answer = _ => Json("""{"server_id":"5f1a2b3c-0000-4000-8000-000000000009","title_id":"splinter-cell-blacklist","name":"Test SCBL","region":"lan","public_url":"http://192.0.2.10:80"}""");

        RegistrationResponse? me = client.WhoAmIAsync(CancellationToken.None).GetAwaiter().GetResult();

        Assert.NotNull(me);
        Assert.Equal("splinter-cell-blacklist", me.TitleId);
        Assert.Equal("Test SCBL", me.Name);
        Assert.Equal("http://192.0.2.10:80", me.PublicUrl);
        Assert.Equal(Guid.Parse("5f1a2b3c-0000-4000-8000-000000000009"), me.ServerId);
        var (method, path, _, _) = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Get, method);
        Assert.Equal("/api/v1/gameservers/me", path);

        // Unreachable is "not yet checked", never a mismatch.
        stub.Answer = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        Assert.Null(client.WhoAmIAsync(CancellationToken.None).GetAwaiter().GetResult());
    }

    [Fact]
    public void ResolvesAPrincipalByAccount()
    {
        var (store, playforward) = NewStore();
        playforward.Answer = _ => Json("""{"pid": 2000007}""");

        Assert.Equal(2_000_007u, store.ResolvePid("9d4b2e1a-0000-4000-8000-000000000001", "Jason"));

        var (method, path, contentType, body) = Assert.Single(playforward.Requests);
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("/api/v1/gameservers/principals", path);
        Assert.Equal("application/json", contentType);
        using JsonDocument sent = JsonDocument.Parse(body);
        Assert.Equal("9d4b2e1a-0000-4000-8000-000000000001", sent.RootElement.GetProperty("account_id").GetString());
        Assert.Equal("Jason", sent.RootElement.GetProperty("display_name").GetString());
    }

    [Fact]
    public void AMatchsScoresGoOutAsOneBatchWithTheBoardNamesAsWritten()
    {
        var (store, playforward) = NewStore();
        playforward.Answer = _ => new HttpResponseMessage(HttpStatusCode.NoContent);

        Assert.True(store.SaveScores(2_000_007, new Dictionary<string, long> { ["xp"] = 2600, ["match_wins"] = 3 }));

        var (method, path, contentType, body) = Assert.Single(playforward.Requests);
        Assert.Equal(HttpMethod.Put, method);
        Assert.Equal("/api/v1/gameservers/principals/2000007/scores", path);
        Assert.Equal("application/json", contentType);
        using JsonDocument sent = JsonDocument.Parse(body);
        JsonElement scores = sent.RootElement.GetProperty("scores");
        Assert.Equal(2600, scores.GetProperty("xp").GetInt64());
        // A board name is a key, which the snake_case policy must not touch.
        Assert.Equal(3, scores.GetProperty("match_wins").GetInt64());

        playforward.Answer = _ => new HttpResponseMessage((HttpStatusCode)507);
        Assert.False(store.SaveScores(2_000_007, new Dictionary<string, long> { ["xp"] = 1 }));
    }

    [Fact]
    public void APidThatDoesNotFitThirtyTwoBitsIsRefused()
    {
        var (store, playforward) = NewStore();
        // Quazal principal ids are uint32; a number this title cannot carry
        // must not be truncated into somebody else's.
        playforward.Answer = _ => Json("""{"pid": 4294967296}""");
        Assert.Null(store.ResolvePid("account", "name"));
    }

    [Fact]
    public void BlobsGoOutAsRawBytesAndComeBackFromBase64()
    {
        var (store, playforward) = NewStore();
        byte[] wall = [0x01, 0x00, 0x00, 0x00, 0x03, 0x00, 0xff, 0x7f];

        playforward.Answer = _ => new HttpResponseMessage(HttpStatusCode.NoContent);
        Assert.True(store.SaveBlob(2_000_007, "onlinedata", "0x09", wall));
        var (method, path, contentType, body) = Assert.Single(playforward.Requests);
        Assert.Equal(HttpMethod.Put, method);
        Assert.Equal("/api/v1/gameservers/principals/2000007/blobs/onlinedata/0x09", path);
        Assert.Equal("application/octet-stream", contentType);
        Assert.Equal(wall, body);

        // Playforward answers JSON with the payload base64-encoded, as Go's
        // encoding/json writes a []byte.
        playforward.Answer = _ => Json($$"""
            {"blobs":[{"namespace":"onlinedata","key":"0x09","payload":"{{Convert.ToBase64String(wall)}}","updated_at":"2026-09-13T12:00:00Z"}]}
            """);
        IReadOnlyList<StoredBlob>? loaded = store.LoadPlayer(2_000_007);
        Assert.NotNull(loaded);
        StoredBlob blob = Assert.Single(loaded);
        Assert.Equal("onlinedata", blob.Namespace);
        Assert.Equal("0x09", blob.Key);
        Assert.Equal(wall, blob.Payload);
        Assert.Equal("/api/v1/gameservers/principals/2000007/blobs", playforward.Requests[^1].Path);
    }

    [Fact]
    public void TitleBlobsAndCountersUseTheirOwnPaths()
    {
        var (store, playforward) = NewStore();

        playforward.Answer = _ => new HttpResponseMessage(HttpStatusCode.NoContent);
        Assert.True(store.SaveTitleBlob("onlinedata-columns", "0x09", "msg_id\nblobCrc"u8.ToArray()));
        Assert.Equal("/api/v1/gameservers/title/blobs/onlinedata-columns/0x09", playforward.Requests[^1].Path);

        playforward.Answer = _ => Json("""{"blobs":[]}""");
        Assert.Empty(store.LoadTitle()!);
        Assert.Equal("/api/v1/gameservers/title/blobs", playforward.Requests[^1].Path);

        playforward.Answer = _ => Json("""{"value": 42}""");
        Assert.Equal(42u, store.NextCounter("wall_message_id"));
        Assert.Equal((HttpMethod.Post, "/api/v1/gameservers/counters/wall_message_id/next"),
            (playforward.Requests[^1].Method, playforward.Requests[^1].Path));
    }

    [Fact]
    public void AnUnreachableOrRefusingPlayforwardAnswersNullNotThrows()
    {
        var (store, playforward) = NewStore();

        // The caller's whole policy rests on this: an outage is an answer to
        // handle, never an exception inside the PRUDP handler.
        playforward.Answer = _ => throw new HttpRequestException("connection refused");
        Assert.Null(store.ResolvePid("account", "name"));
        Assert.Null(store.LoadPlayer(1));
        Assert.False(store.SaveBlob(1, "onlinedata", "0x09", [0x00]));
        Assert.Null(store.NextCounter("wall_message_id"));

        playforward.Answer = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        Assert.Null(store.LoadPlayer(1));
        Assert.False(store.SaveBlob(1, "onlinedata", "0x09", [0x00]));
    }
}
