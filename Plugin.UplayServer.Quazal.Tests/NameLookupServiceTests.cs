namespace Plugin.UplayServer.Quazal.Tests;

/// <summary>
/// Protocol 29, pids to names, in the shapes the community server answered
/// </summary>
public sealed class NameLookupServiceTests
{
    private static RmcRequest Request(uint method, string hex) =>
        new(NameLookupService.ProtocolId, 1, method, Convert.FromHexString(hex));

    [Fact]
    public void NamesForPidsMatchTheCapturedShape()
    {
        // The retail client asking for one pid, as logged on this server:
        // qlist<uint32> with pid 2000004.
        byte[] reply = NameLookupService.BuildNamesForPidsResult(
            Request(NameLookupService.MethodNamesForPids, "0100000084841E00"), pid => pid == 2_000_004 ? "dyllan501" : pid.ToString());

        // count, then (pid, string) -- the string a uint16 length including the NUL.
        Assert.Equal("01000000" + "84841E00" + "0A00" + Convert.ToHexString("dyllan501\0"u8), Convert.ToHexString(reply));
    }

    [Fact]
    public void DisplayNamesEchoTheCapturedPairShape()
    {
        // The working server's 29/14 reply pairs each username with a display
        // name ("2070" -> "Downtown67"); ours has the real name at both steps.
        byte[] reply = NameLookupService.BuildDisplayNamesForNamesResult(
            Request(NameLookupService.MethodDisplayNamesForNames, "01000000" + "0A00" + Convert.ToHexString("dyllan501\0"u8)));

        Assert.Equal("01000000" + "0A00" + Convert.ToHexString("dyllan501\0"u8) + "0A00" + Convert.ToHexString("dyllan501\0"u8), Convert.ToHexString(reply));
    }

    [Fact]
    public void AStrangerIsNamedByPidLikeTheWorkingServer()
    {
        byte[] reply = NameLookupService.BuildNamesForPidsResult(
            Request(NameLookupService.MethodNamesForPids, "01000000D1070000"), pid => pid.ToString());
        Assert.Equal("01000000" + "D1070000" + "0500" + Convert.ToHexString("2001\0"u8), Convert.ToHexString(reply));
    }
}
