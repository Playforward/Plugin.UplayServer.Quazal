using System.Net;
using Plugin.UplayServer.Quazal.Playforward;

namespace Plugin.UplayServer.Quazal.Tests;

/// <summary>
/// On a Playforward title, a player is a Playforward player or nobody.
/// </summary>
[Collection("OnlineDataStore")]
public sealed class LocalAccountLoginTests
{
    private const uint LocalPid = 2001;
    private static readonly IPEndPoint Stranger = new(IPAddress.Parse("203.0.113.44"), 3074);

    private static readonly QuazalTitle WithPlayforward = QuazalTitle.ForTests("local-accounts-pf", 6590, 6591);
    private static readonly QuazalTitle WithoutPlayforward = QuazalTitle.ForTests("local-accounts-lan", 6592, 6593);

    private static readonly QuazalAccount LocalUser = new() { Username = "user", Password = "a123456", Pid = LocalPid };

    private static QuazalSettings Settings() => new()
    {
        Accounts =
        [
            new QuazalAccount { Username = TicketGranting.TrackingUser, Password = TicketGranting.TrackingPassword, Pid = TicketGranting.TrackingPid },
            LocalUser,
        ],
        // The LAN scheme would otherwise hand "user" a fresh pid per address.
        AssignPlayerPidByAddress = false,
    };

    static LocalAccountLoginTests()
    {
        var tokens = new PlayforwardTokenFactory();
        var store = new InMemoryProgressStore();
        PlayforwardGateway gateway = PlayforwardGateway.Start(new PlayforwardSettings
        {
            Enabled = true,
            PublicKeyBase64 = tokens.PublicKeyBase64Url,
            Issuer = PlayforwardTokenFactory.Issuer,
            BaseUrl = "http://playforward.test",
        }, WithPlayforward.TitleId, PlayforwardTokenFactory.Audience, WithPlayforward.Settings.Playforward.LoginNameMaxLength, store)!;
        PlayforwardTitles.Register(PlayforwardTitle.ForTests(WithPlayforward, PlayforwardTokenFactory.Audience, gateway, store));
    }

    [Fact]
    public void APlayerLoginWithALocalAccountIsRefused()
    {
        QuazalSettings settings = Settings();
        Assert.Null(settings.FindPlayerAccount(WithPlayforward, "user", Stranger));

        // A LAN server with no Playforward still lives on its local accounts.
        Assert.Equal(LocalPid, settings.FindPlayerAccount(WithoutPlayforward, "user", Stranger)!.Pid);
    }

    [Fact]
    public void APlainLoginIsTheTelemetryAccountOrNobody()
    {
        QuazalSettings settings = Settings();
        Assert.Null(settings.FindLoginAccount(WithPlayforward, "user"));
        Assert.Equal(TicketGranting.TrackingPid, settings.FindLoginAccount(WithPlayforward, TicketGranting.TrackingUser)!.Pid);
        Assert.Equal(LocalPid, settings.FindLoginAccount(WithoutPlayforward, "user")!.Pid);
    }

    [Fact]
    public void ATicketForALocalAccountOpensNoSession()
    {
        // The secure CONNECT and RequestTicket resolve the peer by pid, so a
        // local account's pid must be unknown there too.
        QuazalSettings settings = Settings();
        Assert.Null(settings.FindAccountByPid(WithPlayforward, LocalPid));
        Assert.NotNull(settings.FindAccountByPid(WithPlayforward, TicketGranting.TrackingPid));
        Assert.NotNull(settings.FindAccountByPid(WithoutPlayforward, LocalPid));
    }

    [Fact]
    public void TheTelemetryAccountIsConfinedToTelemetry()
    {
        const uint tracking = TicketGranting.TrackingPid;

        // What 254 real telemetry sessions called: sign-in, register, telemetry.
        Assert.False(QuazalSettings.IsConfinedToTelemetry(WithPlayforward, tracking, TicketGranting.ProtocolId));
        Assert.False(QuazalSettings.IsConfinedToTelemetry(WithPlayforward, tracking, SecureConnection.ProtocolId));
        Assert.False(QuazalSettings.IsConfinedToTelemetry(WithPlayforward, tracking, TrackingProtocol.ProtocolId));

        // Anything else -- a game session, a friend list -- is refused.
        Assert.True(QuazalSettings.IsConfinedToTelemetry(WithPlayforward, tracking, GameSession.ProtocolId));

        // A Playforward player is not confined, and neither is anyone on a LAN title.
        Assert.False(QuazalSettings.IsConfinedToTelemetry(WithPlayforward, 2_000_000, GameSession.ProtocolId));
        Assert.False(QuazalSettings.IsConfinedToTelemetry(WithoutPlayforward, tracking, GameSession.ProtocolId));
    }
}
