namespace Plugin.UplayServer.Quazal.Tests;

/// <summary>
/// Replies pinned to the community-server captures of 2026-09-17: the
/// privileges every account is granted, and the session properties a lobby
/// carries from its creation into search results.
/// </summary>
[Collection("OnlineDataStore")]
public sealed class CommunityCaptureRepliesTests
{
    // 35/1 GetPrivileges as the working server answered the Deck (573 bytes).
    private const string WorkingPrivileges =
        "17000000c00b0000c00b00000d005072656461746f725f47756e00ef030000ef0300000e00554c435f3033302d55706c617900ee030000ee03000008" +
        "00554c435f30323000f0030000f00300000b0042727574616c5f456e6400bb0b0000bb0b00000e00526172655f416e696d616c5f3300bf0b0000bf0b" +
        "0000100048756e74696e675f536b696e735f3300a20f0000a20f00001e00437573746f6d697a61626c655f4d505f546174746f6f735f55706c617900" +
        "d2070000d20700001800437573746f6d697a61626c655f4d505f546174746f6f7300ed030000ed0300000800554c435f30313000be0b0000be0b0000" +
        "100048756e74696e675f536b696e735f3200b90b0000b90b00000e00526172655f416e696d616c5f3100e9030000e903000009004875726b5f303230" +
        "00ba0b0000ba0b00000e00526172655f416e696d616c5f3200bd0b0000bd0b0000100048756e74696e675f536b696e735f3100ec030000ec03000009" +
        "004875726b5f30353000a10f0000a10f000016004578636c75736976655f55706c61795f5468656d6500ea030000ea03000009004875726b5f303330" +
        "00d4070000d407000012004d505f546174746f6f735f456469746f7200bc0b0000bc0b00000e00526172655f416e696d616c5f3400d1070000d10700" +
        "0015004578636c75736976655f53505f546174746f6f7300f1030000f10300000e0048756d696c696174655f456e6400d3070000d30700001000436f" +
        "6c6c6563746f725f4b6e69666500eb030000eb03000009004875726b5f30343000";

    // 42/1 CreateSession as the Deck sent it when hosting (200 bytes).
    private const string CreateSessionRequest =
        "0100000018000000030000001200000004000000000000007d000000e0be00007e0000002d0900006e00000002000000770000000100000075000000" +
        "3c0000006f000000020000007600000000000000700000000200000066000000000000006a0000000200000071000000020000006400000008000000" +
        "72000000020000006b000000020000006c000000020000006500000084d8f7a47c00000023437eb16d000000020000007b0000000000000069000000" +
        "0100000067000000000000007800000005000000";

    [Fact]
    public void EveryAccountIsGrantedTheWorkingServersPrivileges()
    {
        byte[] body = Privileges.BuildGetPrivilegesResult(new RmcRequest(Privileges.ProtocolId, 1, Privileges.MethodGetPrivileges, Convert.FromHexString("0600656e2d555300")));
        byte[] working = Convert.FromHexString(WorkingPrivileges);
        Assert.Equal(working.Length, body.Length);
        static HashSet<string> Entries(byte[] bytes)
        {
            var set = new HashSet<string>();
            int offset = 4;
            while (offset < bytes.Length)
            {
                uint key = BitConverter.ToUInt32(bytes, offset); uint id = BitConverter.ToUInt32(bytes, offset + 4);
                ushort length = BitConverter.ToUInt16(bytes, offset + 8);
                string name = System.Text.Encoding.ASCII.GetString(bytes, offset + 10, length - 1);
                set.Add($"{key}:{id}:{name}");
                offset += 10 + length;
            }
            return set;
        }
        Assert.Equal(Entries(working), Entries(body));
        Assert.Equal(23u, BitConverter.ToUInt32(body, 0));
    }

    private const string WorkingRemoteMonitorInit =
        "0400000012004c6f674f6e6c696e6553657276696365730011004c6f674f6e6c696e6553657373696f6e0015004c6f674f6e6c696e654d617463686d" +
        "616b696e67000e004c6f674f6e6c696e654563686f00";

    [Fact]
    public void TheRemoteMonitorEnablesTheWorkingServersReportTypes()
    {
        byte[] body = RemoteMonitor.BuildInitResult(new RmcRequest(RemoteMonitor.ProtocolId, 56, RemoteMonitor.MethodInit, []));
        Assert.Equal(Convert.FromHexString(WorkingRemoteMonitorInit), body);
    }

    [Fact]
    public void ALobbyCarriesItsCreationPropertiesAndPlayerCountIntoSearchResults()
    {
        const uint Host = 2591;
        byte[] created = GameSession.BuildCreateSessionResult(
            new RmcRequest(GameSession.ProtocolId, 1, GameSession.MethodCreateSession, Convert.FromHexString(CreateSessionRequest)), Host, TestTitle.Id)!;
        uint typeId = BitConverter.ToUInt32(created, 0);
        uint sessionId = BitConverter.ToUInt32(created, 4);
        Assert.Equal(1u, typeId);
        GameSessionStore.AddParticipants(TestTitle.Id, typeId, sessionId, [Host]);

        // The search the other client made, playlist 8, any region.
        byte[] results = GameSession.BuildSearchSessionsResult(
            new RmcRequest(GameSession.ProtocolId, 1, GameSession.MethodSearchSessions, Convert.FromHexString(
                "01000000010000001100000077000000010000006e000000000000006f0000000000000066000000000000007000000000000000" +
                "640000000800000071000000000000006a000000010000006b000000000000007200000000000000" +
                "7c000000" + "23437eb1" + "65000000" + "84d8f7a4" +
                "6c000000000000007b000000000000006d000000000000006700000000000000780000000000000000")), 2643, TestTitle.Id);
        int offset = 0;
        uint count = BitConverter.ToUInt32(results, offset); offset += 4;
        Assert.Equal(1u, count);
        offset += 12;                                                   // type, session, host
        uint urls = BitConverter.ToUInt32(results, offset); offset += 4;
        for (uint i = 0; i < urls; i++) offset += 2 + BitConverter.ToUInt16(results, offset);
        uint properties = BitConverter.ToUInt32(results, offset); offset += 4;
        var found = new Dictionary<uint, int>();
        for (uint i = 0; i < properties; i++, offset += 8)
            found[BitConverter.ToUInt32(results, offset)] = BitConverter.ToInt32(results, offset + 4);
        Assert.Equal(results.Length, offset);

        Assert.Equal(18, found[0x03]);
        Assert.Equal(0, found[0x04]);
        Assert.Equal(48864, found[0x7d]);
        Assert.Equal(2349, found[0x7e]);
        Assert.Equal(1, found[0x05]);
        Assert.Equal(0, found[0x06]);
        Assert.Equal(8, found[0x64]);
        Assert.Equal(26, found.Count);

        GameSessionStore.DeleteSession(TestTitle.Id, typeId, sessionId);
    }
}
