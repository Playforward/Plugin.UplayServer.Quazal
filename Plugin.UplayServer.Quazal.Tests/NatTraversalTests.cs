using System.Net;
using Plugin.UplayServer.Quazal;
using Xunit;

namespace Plugin.UplayServer.Quazal.Tests;

/// <summary>
/// RequestProbeInitiationExt as the warehouse GRFS capture (2026-09-20) sent
/// it: the joiner (pid 6554, public 173.73.140.54) names the host's public and
/// private urls and its own station; the server must ask the host to probe it.
/// </summary>
public sealed class NatTraversalTests
{
    private const string WithPid =
        "0200000038007564703a2f616464726573733d32342e33322e3134342e37383b706f72743d323334363b5049443d363432373b52564349443d393136310038007564703a2f616464726573733d3139322e3136382e302e31333b706f72743d323334363b5049443d363432373b52564349443d393136310030007564703a2f616464726573733d3137332e37332e3134302e35343b706f72743d323334363b52564349443d3931373100";

    private const string WithoutPid =
        "020000002e007564703a2f616464726573733d3139322e3136382e312e333b706f72743d323334363b52564349443d39313639002e007564703a2f616464726573733d36382e35382e36392e35393b706f72743d323334363b52564349443d393136390030007564703a2f616464726573733d3137332e37332e3134302e35343b706f72743d323334363b52564349443d3931373100";

    private static RmcRequest Request(string hex) =>
        new(NatTraversal.ProtocolId, CallId: 95, NatTraversal.MethodRequestProbeInitiationExt, Convert.FromHexString(hex));

    [Fact]
    public void ForwardsInitiateProbeOncePerTargetPid()
    {
        var sent = new List<(uint Pid, uint Rvcid, IPAddress? Address, byte Protocol, uint Method, byte[] Parameters)>();
        byte[] result = NatTraversal.BuildRequestProbeInitiationExtResult(Request(WithPid), caller: 6554,
            (pid, rvcid, address, protocol, method, parameters) => { sent.Add((pid, rvcid, address, protocol, method, parameters)); return true; });

        Assert.Empty(result);
        var call = Assert.Single(sent);   // public + private url of one host -> one InitiateProbe
        Assert.Equal(6427u, call.Pid);
        Assert.Equal(9161u, call.Rvcid);
        Assert.Equal(IPAddress.Parse("24.32.144.78"), call.Address);
        Assert.Equal(NatTraversal.ProtocolId, call.Protocol);
        Assert.Equal(NatTraversal.MethodInitiateProbe, call.Method);
        Assert.Equal(NatTraversal.BuildInitiateProbeParameters("udp:/address=173.73.140.54;port=2346;RVCID=9171"), call.Parameters);
        // StationURL wire form: u16 length including NUL, chars, NUL.
        Assert.Equal(0x30, call.Parameters[0]);
        Assert.Equal(0, call.Parameters[^1]);
    }

    [Fact]
    public void FallsBackToRvcidWhenTargetHasNoPid()
    {
        var sent = new List<(uint Pid, uint Rvcid, IPAddress? Address)>();
        NatTraversal.BuildRequestProbeInitiationExtResult(Request(WithoutPid), caller: 6554,
            (pid, rvcid, address, _, _, _) => { sent.Add((pid, rvcid, address)); return true; });

        // Private + public url of one RVCID -> one InitiateProbe, keyed on it.
        var call = Assert.Single(sent);
        Assert.Equal(0u, call.Pid);
        Assert.Equal(9169u, call.Rvcid);
        Assert.Equal(IPAddress.Parse("192.168.1.3"), call.Address);
    }

    [Fact]
    public void NeverAsksTheCallerToProbeItself()
    {
        int calls = 0;
        NatTraversal.BuildRequestProbeInitiationExtResult(Request(WithPid), caller: 6427, (_, _, _, _, _, _) => { calls++; return true; });
        Assert.Equal(0, calls);
    }

    [Fact]
    public void RegisterPublicUrlIsWordedLikeTheWorkingServers()
    {
        // FC3 community and GRFS warehouse both: prudp:/address=A;port=P;sid=S;type=3
        Assert.Equal("prudp:/address=173.73.140.54;port=2347;sid=15;type=3",
            SecureConnection.BuildPublicUrl("173.73.140.54", 2347, 15));
        byte[] reply = SecureConnection.BuildRegisterResult(0x23d3, "prudp:/address=173.73.140.54;port=2347;sid=15;type=3");
        Assert.Equal("01000100D3230000350070727564703A2F616464726573733D3137332E37332E3134302E35343B706F72743D323334373B7369643D31353B747970653D3300",
            Convert.ToHexString(reply));
    }



    /// <summary>
    /// The NAT-detect echo, byte for byte from both community-server captures.
    /// This is how a client learns its own public game station; the token
    /// length differs per title, so it comes from the probe.
    /// </summary>
    [Theory]
    // Future Soldier, conf.longweep.net 2026-09-23: 2-byte token a502/a503.
    [InlineData("71710500000000000000a5028e", "173.73.140.54", 2346,
                "a5027564703a2f616464726573733d3137332e37332e3134302e35343b706f72743d3233343600")]
    [InlineData("71710500000000000000a5038f", "173.73.140.54", 2346,
                "a5037564703a2f616464726573733d3137332e37332e3134302e35343b706f72743d3233343600")]
    // Far Cry 3, live Ubisoft 2026-08-26: 15-byte token. Must not regress.
    [InlineData("71710500000000000000010333000000000000000080000003a1", "173.73.140.54", 18313,
                "0103330000000000000000800000037564703a2f616464726573733d3137332e37332e3134302e35343b706f72743d313833313300")]
    public void NatDetectEchoAnswersWithTheObservedStation(string probeHex, string address, int port, string expectedHex)
    {
        bool ok = QuazalResponder.TryCreateNatDetectReply(
            Convert.FromHexString(probeHex), new IPEndPoint(IPAddress.Parse(address), port), out byte[] reply);

        Assert.True(ok);
        Assert.Equal(expectedHex.ToUpperInvariant(), Convert.ToHexString(reply));
    }

    [Fact]
    public void NatDetectIgnoresDatagramsThatAreNotTheProbe()
    {
        // A normal PRUDP packet (not the 0x71/0x71 NATEcho envelope).
        Assert.False(QuazalResponder.TryCreateNatDetectReply(
            Convert.FromHexString("3f313252279ae64b7300000d"), new IPEndPoint(IPAddress.Loopback, 2346), out _));
        // Header only, no token.
        Assert.False(QuazalResponder.TryCreateNatDetectReply(
            Convert.FromHexString("717105000000000000008e"[..22]), new IPEndPoint(IPAddress.Loopback, 2346), out _));
    }
}
