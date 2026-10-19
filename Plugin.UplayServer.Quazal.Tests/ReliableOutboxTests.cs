namespace Plugin.UplayServer.Quazal.Tests;

/// <summary>
/// The server's reliable sends: a long reply goes out a window at a time, an
/// ACK makes room for the next packet, and a packet nobody ACKed goes again.
/// Assassin's Creed Brotherhood's 61-fragment settings file lost its last
/// five fragments in one burst and never recovered them without this.
/// </summary>
public sealed class ReliableOutboxTests
{
    private static List<(ushort, byte[])> Packets(int count) =>
        Enumerable.Range(1, count).Select(n => ((ushort)n, new[] { (byte)n })).ToList();

    private static TimeSpan Elapsed(long from, long to) => TimeSpan.FromMilliseconds(to - from);

    [Fact]
    public void ALongReplyGoesOutAWindowAtATime()
    {
        var outbox = new QuazalResponder.ReliableOutbox();
        List<byte[]> first = outbox.Enqueue(Packets(61), now: 0);

        Assert.Equal(QuazalResponder.SendWindow, first.Count);
        Assert.Equal(61 - QuazalResponder.SendWindow, outbox.WaitingCount);
    }

    [Fact]
    public void EachAckReleasesTheNextPacketInOrder()
    {
        var outbox = new QuazalResponder.ReliableOutbox();
        outbox.Enqueue(Packets(20), now: 0);

        List<byte[]> released = outbox.Acknowledge(1, now: 10);
        Assert.Equal([(byte)(QuazalResponder.SendWindow + 1)], released.Single());
        Assert.Empty(outbox.Acknowledge(1, now: 11));
        Assert.Empty(outbox.Acknowledge(999, now: 12));
    }

    [Fact]
    public void AnUnackedPacketIsSentAgainAndTheRestAreNot()
    {
        var outbox = new QuazalResponder.ReliableOutbox();
        outbox.Enqueue(Packets(3), now: 0);
        outbox.Acknowledge(1, now: 5);
        outbox.Acknowledge(3, now: 5);

        Assert.Empty(outbox.DueForResend(100, Elapsed, out _));
        List<byte[]> again = outbox.DueForResend(600, Elapsed, out int abandoned);
        Assert.Equal([(byte)2], again.Single());
        Assert.Equal(0, abandoned);

        outbox.Acknowledge(2, now: 700);
        Assert.Equal(0, outbox.InFlightCount);
    }

    [Fact]
    public void APacketNeverAckedIsGivenUpOnAndFreesItsPlace()
    {
        var outbox = new QuazalResponder.ReliableOutbox();
        outbox.Enqueue(Packets(QuazalResponder.SendWindow + 1), now: 0);

        long now = 0;
        int abandoned = 0;
        List<byte[]> last = [];
        for (int pass = 0; pass < QuazalResponder.MaxSendAttempts && abandoned == 0; pass++)
        {
            now += 600;
            last = outbox.DueForResend(now, Elapsed, out abandoned);
        }

        Assert.Equal(QuazalResponder.SendWindow, abandoned);
        Assert.Equal([(byte)(QuazalResponder.SendWindow + 1)], last.Single());
    }
}
