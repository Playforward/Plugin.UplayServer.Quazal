using System.Net;
using Plugin.UplayServer.Quazal.Playforward;

namespace Plugin.UplayServer.Quazal.Tests;

/// <summary>
/// Layer 2 of the party design: friends who grouped up in Playforward land in
/// the same Far Cry 3 match.
/// </summary>
[Collection("OnlineDataStore")]
public sealed class PartySeatingTests : IDisposable
{
    private const string PartyRef = "6f2a1c40-0000-4000-8000-00000000aaaa";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "pf-party-" + Guid.NewGuid().ToString("N"));

    private readonly PlayforwardTokenFactory _tokens = new();
    private PlayforwardGateway? _gateway;

    public void Dispose()
    {
        _gateway?.Dispose();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private PlayforwardGateway Gateway()
    {
        var settings = new PlayforwardSettings
        {
            Enabled = true,
            PublicKeyBase64 = _tokens.PublicKeyBase64Url,
            Issuer = PlayforwardTokenFactory.Issuer,
            BaseUrl = "http://playforward.test",
        };
        PlayforwardGateway? gateway = PlayforwardGateway.Start(settings, TestTitle.Id, PlayforwardTokenFactory.Audience,
            TestTitle.Quazal.Settings.Playforward.LoginNameMaxLength, new InMemoryProgressStore());
        Assert.NotNull(gateway);
        _gateway = gateway;
        return gateway;
    }

    /// <summary>
    /// Pre-binds one player, which is the path proven with a real FC3 session,
    /// and returns the PID this server knows them by.
    /// </summary>
    private uint Bind(PlayforwardGateway gateway, string accountId, string address, string? partyRef)
    {
        ResolvedPlayer? player = gateway.PreBind(
            _tokens.Mint(accountId, partyRef: partyRef), IPAddress.Parse(address));
        Assert.NotNull(player);
        return player.Pid;
    }

    private static (uint SessionId, uint HostPid, IReadOnlyList<string> HostUrls,
        IReadOnlyDictionary<uint, int> Properties) Session(uint sessionId, uint hostPid) =>
        (sessionId, hostPid, Array.Empty<string>(), new Dictionary<uint, int>());

    [Fact]
    public void SeatsASearcherIntoTheirPartysSession()
    {
        PlayforwardGateway gateway = Gateway();
        uint host = Bind(gateway, "account-host", "10.0.0.1", PartyRef);
        uint joiner = Bind(gateway, "account-joiner", "10.0.0.2", PartyRef);

        var candidates = new[]
        {
            Session(sessionId: 10, hostPid: 999),   // a stranger's lobby
            Session(sessionId: 11, hostPid: host),  // the party's
        };

        var steered = PartySeating.Steer(gateway, enabled: true, joiner, candidates);

        // Narrowed to one, not merely reordered. Far Cry 3 picks from the list
        // itself, and a list whose *first* entry is the party's session is a
        // list where the game may still take the second -- putting one member
        // in a stranger's lobby while the rest of their party waits.
        Assert.Single(steered);
        Assert.Equal(11u, steered[0].SessionId);
    }

    [Fact]
    public void LeavesASearcherWithNoPartyAlone()
    {
        PlayforwardGateway gateway = Gateway();
        uint host = Bind(gateway, "account-host", "10.0.0.1", PartyRef);
        uint alone = Bind(gateway, "account-alone", "10.0.0.3", partyRef: null);

        var candidates = new[] { Session(10, 999), Session(11, host) };

        // Somebody entering a match alone gets the ordinary result. Most
        // players most of the time.
        Assert.Equal(2, PartySeating.Steer(gateway, enabled: true, alone, candidates).Count);
    }

    [Fact]
    public void LeavesADifferentPartyAlone()
    {
        PlayforwardGateway gateway = Gateway();
        uint host = Bind(gateway, "account-host", "10.0.0.1", PartyRef);
        uint other = Bind(gateway, "account-other", "10.0.0.4", "a-different-party");

        var candidates = new[] { Session(10, 999), Session(11, host) };

        Assert.Equal(2, PartySeating.Steer(gateway, enabled: true, other, candidates).Count);
    }

    [Fact]
    public void LeavesTheSearchAloneWhenNobodyInThePartyIsHostingYet()
    {
        PlayforwardGateway gateway = Gateway();
        uint joiner = Bind(gateway, "account-joiner", "10.0.0.2", PartyRef);

        var candidates = new[] { Session(10, 999) };

        // Better than returning nothing: this member joins an ordinary match
        // and the party converges on whichever session one of them ends up in,
        // rather than everybody staring at an empty browser.
        Assert.Single(PartySeating.Steer(gateway, enabled: true, joiner, candidates));
    }

    [Fact]
    public void DoesNotSeatSomebodyIntoTheirOwnSession()
    {
        PlayforwardGateway gateway = Gateway();
        uint host = Bind(gateway, "account-host", "10.0.0.1", PartyRef);

        var candidates = new[] { Session(10, 999), Session(11, host) };

        // The host searching would otherwise be steered at themselves, which is
        // not a match anybody can join.
        Assert.Equal(2, PartySeating.Steer(gateway, enabled: true, host, candidates).Count);
    }

    [Fact]
    public void DoesNothingWhenSteeringIsSwitchedOff()
    {
        PlayforwardGateway gateway = Gateway();
        uint host = Bind(gateway, "account-host", "10.0.0.1", PartyRef);
        uint joiner = Bind(gateway, "account-joiner", "10.0.0.2", PartyRef);

        var candidates = new[] { Session(10, 999), Session(11, host) };

        Assert.Equal(2, PartySeating.Steer(gateway, enabled: false, joiner, candidates).Count);
    }

    [Fact]
    public void DoesNothingOnAServerWithNoPlayforwardIdentity()
    {
        var candidates = new[] { Session(10, 999) };

        // A server running only local LAN accounts has no gateway at all, and
        // must behave exactly as it did before any of this existed.
        Assert.Single(PartySeating.Steer(gateway: null, enabled: true, 101u, candidates));
    }

    [Fact]
    public void LeavesALocalLanAccountAlone()
    {
        PlayforwardGateway gateway = Gateway();
        uint host = Bind(gateway, "account-host", "10.0.0.1", PartyRef);

        var candidates = new[] { Session(10, 999), Session(11, host) };

        // A PID this server allocated for a configured local account has no
        // Playforward identity behind it and therefore no party. A server can
        // run both kinds at once.
        Assert.Equal(2, PartySeating.Steer(gateway, enabled: true, 101u, candidates).Count);
    }
}
