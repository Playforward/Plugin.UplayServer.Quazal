using System.Buffers.Binary;
using Plugin.UplayServer.Quazal.Playforward;

namespace Plugin.UplayServer.Quazal.Tests;

/// <summary>
/// The Intel service (107): the player's XP, as the game submits it after a
/// match and asks for it at login.
/// </summary>
[Collection("OnlineDataStore")]
public sealed class Fc3IntelServiceTests : IDisposable
{
    private const uint Pid = 2_000_000;

    /// <summary>
    /// The reply a working server sends, byte-accounted: 17-byte header, list A
    /// of five 14-byte boosters, list B of five 14-byte inventory entries, list
    /// C of one 23-byte status entry, list D empty 196 bytes, as captured.
    /// </summary>
    private const int ReplyBytes = 17 + (4 + 5 * 14) + (4 + 5 * 14) + (4 + 23) + 4;

    public void Dispose() => OnlineDataStore.Detach();

    private static InMemoryProgressStore NewPlayforward()
    {
        var store = new InMemoryProgressStore(Pid);
        store.ResolvePid("account-a", "A");
        OnlineDataStore.Detach();
        OnlineDataStore.Attach(TestTitle.Id, store, pid => pid == Pid);
        return store;
    }

    private static RmcRequest AddXp(uint amount)
    {
        var parameters = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(parameters, amount);
        return new RmcRequest(Fc3IntelService.ProtocolId, CallId: 1, Fc3IntelService.MethodAddXp, parameters);
    }

    private static RmcRequest GetIntel() =>
        new(Fc3IntelService.ProtocolId, CallId: 2, Fc3IntelService.MethodGetIntel, []);

    private static uint Slot(byte[] reply, int slot) => BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(slot * 4));

    private static byte[] GetIntel(uint pid) => Fc3IntelService.BuildGetIntelResult(GetIntel(), pid);

    [Fact]
    public void GetIntelReplaysTheWorkingServersBoosterCatalogue()
    {
        NewPlayforward();
        byte[] reply = GetIntel(Pid);

        // 196 bytes, the exact size of the community server's reply.
        Assert.Equal(196, ReplyBytes);
        Assert.Equal(ReplyBytes, reply.Length);

        // Header: a live timestamp, 0, -1, 0, false.
        uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.InRange(Slot(reply, 0), now - 5, now + 5);
        Assert.Equal(0u, Slot(reply, 1));
        Assert.Equal(0xFFFFFFFFu, Slot(reply, 2));
        Assert.Equal(0u, Slot(reply, 3));
        Assert.Equal(0, reply[16]);

        // List A: five boosters, the first 10 minutes long at x2.0.
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(17)));
        Assert.Equal(0, reply[21]);
        Assert.Equal(1, reply[22]);
        Assert.Equal(600u, BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(23)));
        Assert.Equal(7200u, BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(27)));
        Assert.Equal(2.0f, BinaryPrimitives.ReadSingleLittleEndian(reply.AsSpan(31)));

        // And after the timestamp, byte for byte what the community server sent
        // (farcry-lan/captures/deck-hooklog-community-20260914-235901, 107/1 call 41).
        const string captured =
            "00000000ffffffff000000000005000000000158020000201c000000000040010108070000201c0000000000400201100e0000201c" +
            "0000000000400301302a0000201c000000000040040180700000201c00000000004005000000000000000000000000000000000001" +
            "000000000000000100000000000200000000000000020000000000030000000000000003000000000004000000000000000400000000" +
            "000100000001000000000000000000000000ffffffffffffffff000100000000";
        Assert.Equal(captured, Convert.ToHexString(reply.AsSpan(4)).ToLowerInvariant());
    }

    [Fact]
    public void MatchesAccumulateXpAndTheReplyIsEmpty()
    {
        InMemoryProgressStore playforward = NewPlayforward();

        // The 2026-09-14 capture: two matches, 439 then 878, from one client.
        Assert.Empty(Fc3IntelService.BuildAddXpResult(AddXp(439), Pid));
        Assert.Empty(Fc3IntelService.BuildAddXpResult(AddXp(878), Pid));

        // Kept in Playforward, and still there on another server of the title.
        OnlineDataStore.Detach();
        OnlineDataStore.Attach(TestTitle.Id, playforward, pid => pid == Pid);
        StoredBlob xp = Assert.Single(playforward.LoadPlayer(Pid)!, blob => blob.Namespace == "intel" && blob.Key == "xp");
        Assert.Equal(439u + 878u, BinaryPrimitives.ReadUInt32LittleEndian(xp.Payload));
        Fc3IntelService.BuildAddXpResult(AddXp(1), Pid);
        Assert.Equal(1318u, BinaryPrimitives.ReadUInt32LittleEndian(playforward.LoadPlayer(Pid)!.Single(b => b.Key == "xp").Payload));
    }

    [Fact]
    public void FoundDataIsHeldAndADecodeRunsForTheKindsDuration()
    {
        InMemoryProgressStore playforward = NewPlayforward();
        // 107/3 (kind 0, 1) as each finder sent it on 2026-09-17: (0, server time).
        byte[] reply = Fc3IntelService.BuildReportFoundResult(
            new RmcRequest(Fc3IntelService.ProtocolId, 1, Fc3IntelService.MethodReportFound, [0x00, 0x01, 0x00, 0x00, 0x00]), Pid);
        Assert.Equal(8, reply.Length);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(reply));
        uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.InRange(BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(4)), now - 5, now + 5);

        // 107/5 (kind 0, 0, id 3099818090, false) as the Deck started decoding:
        // (start, start + 600, true), the kind's catalogue duration.
        reply = Fc3IntelService.BuildStartDecodingResult(
            new RmcRequest(Fc3IntelService.ProtocolId, 1, Fc3IntelService.MethodStartDecoding, Convert.FromHexString("00000000006a78c3b800")), Pid);
        Assert.Equal(9, reply.Length);
        uint start = BinaryPrimitives.ReadUInt32LittleEndian(reply);
        Assert.Equal(start + 600, BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(4)));
        Assert.Equal(1, reply[8]);

        Fc3IntelService.BuildAddXpResult(AddXp(2547), Pid);
        byte[] intel = Fc3IntelService.BuildGetIntelResult(new RmcRequest(Fc3IntelService.ProtocolId, 1, Fc3IntelService.MethodGetIntel, []), Pid);
        Assert.Equal(219, intel.Length);
        Assert.Equal(2547u, BinaryPrimitives.ReadUInt32LittleEndian(intel.AsSpan(4)));       // header: the XP total
        int inventory = 17 + 4 + 5 * 14 + 4;                                                  // past header, catalogue, count
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(intel.AsSpan(inventory + 9)));  // kind 0: one found
        Assert.Equal(1, intel[inventory + 13]);                                                // ... and decoding
        int decodes = inventory + 5 * 14;
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(intel.AsSpan(decodes)));
        Assert.Equal(start, BinaryPrimitives.ReadUInt32LittleEndian(intel.AsSpan(decodes + 5)));
        Assert.Equal(3099818090u, BinaryPrimitives.ReadUInt32LittleEndian(intel.AsSpan(decodes + 21)));

        Assert.Contains(playforward.LoadPlayer(Pid)!, blob => blob.Namespace == "intel" && blob.Key == "boosters");
    }

    [Fact]
    public void AMalformedAddXpChangesNothing()
    {
        InMemoryProgressStore playforward = NewPlayforward();
        Assert.Empty(Fc3IntelService.BuildAddXpResult(
            new RmcRequest(Fc3IntelService.ProtocolId, 1, Fc3IntelService.MethodAddXp, [0x01, 0x02]), Pid));
        Assert.DoesNotContain(playforward.LoadPlayer(Pid)!, blob => blob.Key == "xp");
    }

    [Fact]
    public void ClaimingDecodedDataClearsTheDecodeAndSaysYes()
    {
        NewPlayforward();
        Fc3IntelService.BuildReportFoundResult(
            new RmcRequest(Fc3IntelService.ProtocolId, 1, Fc3IntelService.MethodReportFound, Convert.FromHexString("0001000000")), Pid);
        Fc3IntelService.BuildStartDecodingResult(
            new RmcRequest(Fc3IntelService.ProtocolId, 1, Fc3IntelService.MethodStartDecoding, Convert.FromHexString("00000000006a78c3b800")), Pid);

        byte[] reply = Fc3IntelService.BuildClaimDecodedResult(
            new RmcRequest(Fc3IntelService.ProtocolId, 1, Fc3IntelService.MethodClaimDecoded, Convert.FromHexString("000000000001")), Pid);
        Assert.Equal([1], reply);

        byte[] intel = Fc3IntelService.BuildGetIntelResult(new RmcRequest(Fc3IntelService.ProtocolId, 1, Fc3IntelService.MethodGetIntel, []), Pid);
        Assert.Equal(196, intel.Length);
    }
}
