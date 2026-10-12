using System.Buffers.Binary;
using System.Net;
using System.Text;
using Plugin.UplayServer.Quazal;
using Plugin.UplayServer.Quazal.Playforward;
using Xunit;

namespace Plugin.UplayServer.Quazal.Tests;

/// <summary>
/// Splinter Cell Blacklist's Player Stats (55), driven with the requests two
/// retail clients (a PC and a Steam Deck, pids 2000000 and 2000001) sent this
/// server during a Spies vs Mercs match on 2026-09-21.
/// </summary>
public sealed class BlacklistStatisticsTests : IDisposable
{
    private const string Title = "blacklist";
    private const uint Deck = 2000001;
    private const uint Pc = 2000000;

    private const string DeckLoginWrite =
        "050000000100000001000000000000000100000088000000010100000000000000020000000100000000000000030000008C00000001C80A0000000000" +
        "00910000000110E90100000000009200000001A0D70000000000001500000001000000000000000400000078000000010100000000000000790000000101" +
        "000000000000007E000000010600000000000000B2000000010100000000000000170000000100000000000000020000009A0000000106000000000000" +
        "009D00000001010000000000000020000000010000000000000003000000A6000000016400000000000000A8000000010600000000000000A90000000101" +
        "00000000000000";

    private const string DeckRound1 =
        "040000000A00000001000000000000000B0000006400000001020000000000000068000000011A0000000000000069000000010B000000000000006B0000" +
        "0001040000000000000078000000010100000000000000CC000000010300000000000000D3000000019B01000000000000D4000000019B01000000000000" +
        "D6000000010100000000000000DA00000001D804000000000000DB00000001D8040000000000000B0000000100000094000000040000006400000001020000" +
        "000000000068000000011A0000000000000069000000010B000000000000006B0000000104000000000000000C00000001000000CB000000010000006D00" +
        "00000101000000000000000D000000010000000000000005000000EE000000010200000000000000030100000102000000000000000A0100000101000000" +
        "000000001201000001010000000000000017010000010100000000000000";

    private const string DeckRound2 =
        "030000000A0000000100000000000000060000006400000001010000000000000068000000010D0000000000000069000000010400000000000000D30000" +
        "00014202000000000000D4000000014202000000000000D60000000101000000000000000B0000000100000084000000030000006400000001010000000000" +
        "000068000000010D00000000000000690000000104000000000000000D000000010000000000000002000000EE00000001010000000000000012010000010100" +
        "000000000000";

    private const string DeckMatchEnd =
        "02000000020000000100000000000000030000008C00000001C80A000000000000910000000110E90100000000009200000001A0D70000000000000A0000" +
        "00010000000000000009000000790000000101000000000000007A0000000101000000000000007E00000001F6030000000000007F00000001C80A000000" +
        "0000008100000001A0D7000000000000C7000000010200000000000000E200000001C80A000000000000E300000001A0D7000000000000E6000000010200" +
        "000000000000";

    private const string PcLoginRead =
        "0100000080841E000300000015000000010000000000000001000000010000007E000000000000001A000000010000000000000001000000010000007E00" +
        "0000000000000A0000000100000000000000010000000A00000064000000650000007A0000007C000000C8000000C9000000CA000000CB000000CC000000CD" +
        "00000000000000";

    public void Dispose()
    {
        BlacklistStatistics.Clear();
        PlayforwardTitles.Clear();
    }

    private static RmcRequest Request(uint method, string hex) =>
        new((byte)BlacklistServices.PlayerStats.ProtocolId, 1, method, Convert.FromHexString(hex));

    private static void Write(uint pid, string hex) =>
        Assert.Empty(BlacklistServices.PlayerStats.BuildWriteStatsResult(Request(1, hex), pid, Title)!);

    private static string Read(uint pid, uint board, uint context, params uint[] statIds)
    {
        var body = new MemoryStream();
        TicketGranting.WriteU32(body, 1);
        TicketGranting.WriteU32(body, pid);
        TicketGranting.WriteU32(body, 1);
        TicketGranting.WriteU32(body, board);
        TicketGranting.WriteU32(body, 1);
        TicketGranting.WriteU32(body, context);
        TicketGranting.WriteU32(body, 1);
        TicketGranting.WriteU32(body, (uint)statIds.Length);
        foreach (uint statId in statIds)
            TicketGranting.WriteU32(body, statId);
        TicketGranting.WriteU32(body, 0);
        return Convert.ToHexString(body.ToArray());
    }

    private sealed record StatSet(uint Pid, string Name, Dictionary<uint, object> Stats);
    private sealed record Result(uint Board, uint Context, uint ResetFrequency, List<StatSet> Sets, Dictionary<uint, object> Defaults);

    /// <summary>Decodes a QList&lt;StatboardResult&gt; back into what the client would see.</summary>
    private static List<Result> Results(byte[] body)
    {
        int offset = 0;
        uint U32() { uint v = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset)); offset += 4; return v; }
        object Variant()
        {
            byte tag = body[offset++];
            switch (tag)
            {
                case 1: long i = BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(offset)); offset += 8; return i;
                case 2: double d = BinaryPrimitives.ReadDoubleLittleEndian(body.AsSpan(offset)); offset += 8; return d;
                default: throw new InvalidDataException($"tag {tag}");
            }
        }
        Dictionary<uint, object> Stats()
        {
            var stats = new Dictionary<uint, object>();
            uint count = U32();
            for (uint n = 0; n < count; n++)
            {
                uint id = U32();
                stats[id] = Variant();
            }
            return stats;
        }

        var results = new List<Result>();
        uint resultCount = U32();
        for (uint r = 0; r < resultCount; r++)
        {
            uint board = U32(), context = U32(), reset = U32();
            var sets = new List<StatSet>();
            uint setCount = U32();
            for (uint s = 0; s < setCount; s++)
            {
                uint pid = U32();
                ushort nameLength = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(offset));
                string name = Encoding.UTF8.GetString(body, offset + 2, nameLength - 1);
                offset += 2 + nameLength;
                offset += 8;   // submitted time
                sets.Add(new StatSet(pid, name, Stats()));
            }
            results.Add(new Result(board, context, reset, sets, Stats()));
        }
        Assert.Equal(body.Length, offset);
        return results;
    }

    private static List<Result> ReadBack(string hex) =>
        Results(BlacklistServices.PlayerStats.BuildReadStatsByPlayersResult(Request(2, hex), pid => $"player{pid}", Title)!);

    [Fact]
    public void RoundWritesAddUpToKillsWinsAndLossesInEveryContextWritten()
    {
        Write(Deck, DeckRound1);
        Write(Deck, DeckRound2);
        Write(Deck, DeckMatchEnd);

        Result overall = Assert.Single(ReadBack(Read(Deck, 10, 0, 100, 101, 122, 124, 126, 211)));
        StatSet deck = Assert.Single(overall.Sets);
        Assert.Equal(Deck, deck.Pid);
        Assert.Equal("player2000001", deck.Name);
        Assert.Equal(3L, deck.Stats[100]);        // 2 + 1 kills
        Assert.Equal(0L, deck.Stats[101]);        // never died: the default
        Assert.Equal(1L, deck.Stats[122]);        // one win
        Assert.Equal(0L, deck.Stats[124]);
        Assert.Equal(1014L, deck.Stats[126]);     // the map, not 2 x 1014
        Assert.Equal(411L + 578L, deck.Stats[211]);

        Assert.Equal(2L, Assert.Single(Assert.Single(ReadBack(Read(Deck, 11, 148, 100))).Sets).Stats[100]);
        Assert.Equal(1L, Assert.Single(Assert.Single(ReadBack(Read(Deck, 11, 132, 100))).Sets).Stats[100]);
    }

    [Fact]
    public void TheLoginSnapshotReplacesRatherThanAdds()
    {
        Write(Deck, DeckLoginWrite);
        Write(Deck, DeckLoginWrite);
        Write(Deck, DeckMatchEnd);   // carries the same board 2 snapshot again

        Result cash = Assert.Single(ReadBack(Read(Deck, 2, 0, 140, 145, 146)));
        StatSet deck = Assert.Single(cash.Sets);
        Assert.Equal(2760L, deck.Stats[140]);
        Assert.Equal(125200L, deck.Stats[145]);
        Assert.Equal(55200L, deck.Stats[146]);
    }

    [Fact]
    public void TheCapturedLoginReadAnswersEveryQueryWithDefaultsAndOnlyTheRowsThatExist()
    {
        Write(Pc, DeckRound2);   // the PC now has a board 10 row; nothing on 21 or 26

        List<Result> results = ReadBack(PcLoginRead);
        Assert.Equal(3, results.Count);
        Assert.Equal([21u, 26u, 10u], results.Select(r => r.Board));
        Assert.All(results, r => Assert.Equal(0u, r.Context));
        Assert.All(results, r => Assert.Equal(1u, r.ResetFrequency));

        Assert.Empty(results[0].Sets);
        Assert.Equal(0L, Assert.Single(results[0].Defaults).Value);
        Assert.Equal(126u, Assert.Single(results[0].Defaults).Key);
        Assert.Empty(results[1].Sets);

        StatSet pc = Assert.Single(results[2].Sets);
        Assert.Equal(Pc, pc.Pid);
        Assert.Equal([100u, 101u, 122u, 124u, 200u, 201u, 202u, 203u, 204u, 205u], pc.Stats.Keys);
        Assert.Equal(1L, pc.Stats[100]);
        Assert.Equal(10, results[2].Defaults.Count);
        Assert.All(results[2].Defaults.Values, v => Assert.Equal(0L, v));
    }

    [Fact]
    public void AnEmptyContextListReadsContextZeroAndOtherPlayersRowsAreListedToo()
    {
        Write(Deck, DeckRound1);
        Write(Pc, DeckRound2);

        string read = "02000000" + Hex(Pc) + Hex(Deck) + "01000000" + "0A000000" + "00000000" + "01000000" + "01000000" + "64000000" + "00000000";
        Result result = Assert.Single(ReadBack(read));
        Assert.Equal(0u, result.Context);
        Assert.Equal(2, result.Sets.Count);
        Assert.Equal(1L, result.Sets.Single(s => s.Pid == Pc).Stats[100]);
        Assert.Equal(2L, result.Sets.Single(s => s.Pid == Deck).Stats[100]);
    }

    [Fact]
    public void ADoubleStatIsCarriedAsOne()
    {
        Write(Deck, "010000000A000000010000000000000002000000E1000000010300000000000000E500000002000000E0899DD83F");
        StatSet deck = Assert.Single(Assert.Single(ReadBack(Read(Deck, 10, 0, 225, 229))).Sets);
        Assert.Equal(3L, deck.Stats[225]);
        Assert.Equal(0.38461539149284363, (double)deck.Stats[229], 12);
    }

    [Fact]
    public void AMalformedWriteOrReadIsRefused()
    {
        Assert.Null(BlacklistServices.PlayerStats.BuildWriteStatsResult(Request(1, "0100000015000000"), Deck, Title));
        Assert.Null(BlacklistServices.PlayerStats.BuildReadStatsByPlayersResult(Request(2, "0100000080841E00"), _ => "", Title));
    }

    /// <summary>
    /// A Playforward player's rows outlive this server: written through on
    /// every write, loaded again on the first read after the copy here is
    /// dropped
    /// </summary>
    [Fact]
    public void APlayforwardPlayersStatsSurviveARestart()
    {
        var tokens = new PlayforwardTokenFactory();
        var store = new InMemoryProgressStore();
        using PlayforwardGateway gateway = PlayforwardGateway.Start(new PlayforwardSettings
        {
            Enabled = true,
            PublicKeyBase64 = tokens.PublicKeyBase64Url,
            Issuer = PlayforwardTokenFactory.Issuer,
            BaseUrl = "http://playforward.test",
        }, TestTitle.Id, PlayforwardTokenFactory.Audience, TestTitle.Quazal.Settings.Playforward.LoginNameMaxLength, store)!;
        PlayforwardTitles.Register(PlayforwardTitle.ForTests(TestTitle.Quazal, PlayforwardTokenFactory.Audience, gateway, store));
        uint pid = gateway.PreBind(tokens.Mint("account-deck"), IPAddress.Parse("10.0.0.7"))!.Pid;

        Assert.Empty(BlacklistServices.PlayerStats.BuildWriteStatsResult(Request(1, DeckRound1), pid, TestTitle.Id)!);
        Assert.Empty(BlacklistServices.PlayerStats.BuildWriteStatsResult(Request(1, DeckMatchEnd), pid, TestTitle.Id)!);
        string read = Read(pid, 10, 0, 100, 122, 126, 229);
        byte[] before = BlacklistServices.PlayerStats.BuildReadStatsByPlayersResult(Request(2, read), _ => "deck", TestTitle.Id)!;

        BlacklistStatistics.Clear();   // a restart
        byte[] after = BlacklistServices.PlayerStats.BuildReadStatsByPlayersResult(Request(2, read), _ => "deck", TestTitle.Id)!;
        Assert.Equal(before, after);

        StatSet deck = Assert.Single(Assert.Single(Results(after)).Sets);
        Assert.Equal(2L, deck.Stats[100]);
        Assert.Equal(1L, deck.Stats[122]);
        Assert.Contains(store.LoadPlayer(pid)!, blob => blob.Namespace == "scbl-stats");

        Assert.Empty(BlacklistServices.PlayerStats.BuildWriteStatsResult(Request(1, DeckRound2), pid, TestTitle.Id)!);
        BlacklistStatistics.Clear();
        deck = Assert.Single(Assert.Single(Results(BlacklistServices.PlayerStats.BuildReadStatsByPlayersResult(Request(2, read), _ => "deck", TestTitle.Id)!)).Sets);
        Assert.Equal(3L, deck.Stats[100]);
    }

    private const string DeckLeaderboardByPlayers =
        "0100000081841E000100000005000000E300000001000000070000007F0000006400000065000000660000007A0000007C0000007D000000";

    private sealed record RankEntry(uint Rank, long Score, uint Pid, string Name, Dictionary<uint, object> Stats);
    private sealed record Leaderboard(uint Board, uint Context, uint Total, List<RankEntry> Entries);

    /// <summary>Decodes a QList&lt;LeaderboardResult&gt;.</summary>
    private static List<Leaderboard> Leaderboards(byte[] body)
    {
        int offset = 0;
        uint U32() { uint v = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset)); offset += 4; return v; }
        long I64Variant()
        {
            Assert.Equal(1, body[offset++]);
            long v = BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(offset)); offset += 8; return v;
        }
        var boards = new List<Leaderboard>();
        uint resultCount = U32();
        for (uint r = 0; r < resultCount; r++)
        {
            uint board = U32(), context = U32();
            U32();   // reset frequency
            uint total = U32();
            var entries = new List<RankEntry>();
            uint entryCount = U32();
            for (uint e = 0; e < entryCount; e++)
            {
                uint pid = U32();
                ushort nameLength = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(offset));
                string name = Encoding.UTF8.GetString(body, offset + 2, nameLength - 1);
                offset += 2 + nameLength + 8;
                var stats = new Dictionary<uint, object>();
                uint statCount = U32();
                for (uint n = 0; n < statCount; n++)
                {
                    uint id = U32();
                    stats[id] = I64Variant();
                }
                Assert.Equal(0u, U32());   // rank status
                uint rank = U32();
                entries.Add(new RankEntry(rank, I64Variant(), pid, name, stats));
            }
            boards.Add(new Leaderboard(board, context, total, entries));
        }
        Assert.Equal(body.Length, offset);
        return boards;
    }

    /// <summary>A leaderboard query for board 5 in context 227 -- what the screen sends -- after the given prefix.</summary>
    private static string LeaderboardQuery(string prefix) =>
        prefix + "01000000" + "05000000" + "E3000000" + "01000000" + "02000000" + "7F000000" + "64000000";

    /// <summary>Three Playforward players who each played in mode 227: their XP there, and kills, from writes.</summary>
    private static (InMemoryProgressStore Store, uint[] Pids) ThreeRankedPlayers(PlayforwardGateway gateway, PlayforwardTokenFactory tokens)
    {
        var store = (InMemoryProgressStore)PlayforwardTitles.ForTitle(TestTitle.Id)!.Store;
        uint[] pids = new uint[3];
        long[] xp = [30, 2760, 900];
        long[] kills = [1, 5, 3];
        for (int i = 0; i < 3; i++)
        {
            pids[i] = gateway.PreBind(tokens.Mint($"account-{i}"), IPAddress.Parse($"10.0.0.{i + 1}"))!.Pid;
            string write = "01000000" + "0A000000" + "02000000" + "00000000" + "E3000000" + "02000000" +
                           "7F000000" + "01" + Convert.ToHexString(BitConverter.GetBytes(xp[i])) +
                           "64000000" + "01" + Convert.ToHexString(BitConverter.GetBytes(kills[i]));
            Assert.Empty(BlacklistServices.PlayerStats.BuildWriteStatsResult(Request(1, write), pids[i], TestTitle.Id)!);
        }
        return (store, pids);
    }

    private static (PlayforwardGateway Gateway, PlayforwardTokenFactory Tokens) StartPlayforwardTitle()
    {
        var tokens = new PlayforwardTokenFactory();
        var store = new InMemoryProgressStore();
        PlayforwardGateway gateway = PlayforwardGateway.Start(new PlayforwardSettings
        {
            Enabled = true,
            PublicKeyBase64 = tokens.PublicKeyBase64Url,
            Issuer = PlayforwardTokenFactory.Issuer,
            BaseUrl = "http://playforward.test",
        }, TestTitle.Id, PlayforwardTokenFactory.Audience, TestTitle.Quazal.Settings.Playforward.LoginNameMaxLength, store)!;
        PlayforwardTitles.Register(PlayforwardTitle.ForTests(TestTitle.Quazal, PlayforwardTokenFactory.Audience, gateway, store));
        return (gateway, tokens);
    }

    [Fact]
    public void AWriteOfXpRecordsTheRowsScoreAndTheBoardRanksByIt()
    {
        (PlayforwardGateway gateway, PlayforwardTokenFactory tokens) = StartPlayforwardTitle();
        using (gateway)
        {
            (InMemoryProgressStore store, uint[] pids) = ThreeRankedPlayers(gateway, tokens);
            Assert.Equal([pids[1], pids[2], pids[0]], store.TopScores(BlacklistStatistics.ScoreBoard(10, 227), 10)!.Select(s => (uint)s.Pid));
            Assert.Equal(2760, store.ScoresFor(BlacklistStatistics.ScoreBoard(10, 0), [pids[1]])!.Single().Score);

            List<Leaderboard> top = Leaderboards(BlacklistServices.PlayerStats.BuildReadLeaderboardsByRankResult(
                Request(4, LeaderboardQuery("01000000" + "0A000000")), pid => $"p{pid}", TestTitle.Id)!);
            Leaderboard board = Assert.Single(top);
            Assert.Equal((5u, 227u, 3u), (board.Board, board.Context, board.Total));
            Assert.Equal([1u, 2u, 3u], board.Entries.Select(e => e.Rank));
            Assert.Equal([pids[1], pids[2], pids[0]], board.Entries.Select(e => e.Pid));
            Assert.Equal([2760L, 900L, 30L], board.Entries.Select(e => e.Score));
            Assert.Equal(5L, board.Entries[0].Stats[100]);      // kills, from the row
            Assert.Equal(2760L, board.Entries[0].Stats[127]);   // the score again, as a stat

            board = Assert.Single(Leaderboards(BlacklistServices.PlayerStats.BuildReadLeaderboardsByRankResult(
                Request(4, LeaderboardQuery("02000000" + "01000000")), pid => $"p{pid}", TestTitle.Id)!));
            Assert.Equal((2u, pids[2]), (Assert.Single(board.Entries).Rank, board.Entries[0].Pid));

            board = Assert.Single(Leaderboards(BlacklistServices.PlayerStats.BuildReadLeaderboardsNearPlayerResult(
                Request(3, LeaderboardQuery(Hex(pids[0]) + "02000000")), pid => $"p{pid}", TestTitle.Id)!));
            Assert.Equal([2u, 3u], board.Entries.Select(e => e.Rank));

            string mine = "01000000" + Hex(pids[2]) + DeckLeaderboardByPlayers[16..];
            board = Assert.Single(Leaderboards(BlacklistServices.PlayerStats.BuildReadLeaderboardsByPlayersResult(
                Request(5, mine), pid => $"p{pid}", TestTitle.Id)!));
            RankEntry entry = Assert.Single(board.Entries);
            Assert.Equal((2u, 900L, 3L, 0L), (entry.Rank, entry.Score, entry.Stats[100], entry.Stats[122]));
            Assert.Equal(7, entry.Stats.Count);
        }
    }

    [Fact]
    public void APlayerWithoutARowInThatContextHasNoEntryAndTheCountIsStillTheBoards()
    {
        (PlayforwardGateway gateway, PlayforwardTokenFactory tokens) = StartPlayforwardTitle();
        using (gateway)
        {
            (_, uint[] pids) = ThreeRankedPlayers(gateway, tokens);
            uint stranger = gateway.PreBind(tokens.Mint("account-stranger"), IPAddress.Parse("10.0.0.9"))!.Pid;

            Leaderboard board = Assert.Single(Leaderboards(BlacklistServices.PlayerStats.BuildReadLeaderboardsByPlayersResult(
                Request(5, "01000000" + Hex(stranger) + DeckLeaderboardByPlayers[16..]), pid => $"p{pid}", TestTitle.Id)!));
            Assert.Empty(board.Entries);
            Assert.Equal(3u, board.Total);

            board = Assert.Single(Leaderboards(BlacklistServices.PlayerStats.BuildReadLeaderboardsNearPlayerResult(
                Request(3, LeaderboardQuery(Hex(stranger) + "0A000000")), pid => $"p{pid}", TestTitle.Id)!));
            Assert.Empty(board.Entries);
            Assert.Contains(pids[0], Leaderboards(BlacklistServices.PlayerStats.BuildReadLeaderboardsByRankResult(
                Request(4, LeaderboardQuery("01000000" + "0A000000")), pid => $"p{pid}", TestTitle.Id)!)[0].Entries.Select(e => e.Pid));
        }
    }

    [Fact]
    public void WithoutPlayforwardTheCapturedLeaderboardReadIsAnsweredEmptyButWellFormed()
    {
        Write(Deck, DeckMatchEnd);
        Leaderboard board = Assert.Single(Leaderboards(BlacklistServices.PlayerStats.BuildReadLeaderboardsByPlayersResult(
            Request(5, DeckLeaderboardByPlayers), pid => $"p{pid}", Title)!));
        Assert.Equal((5u, 227u, 0u), (board.Board, board.Context, board.Total));
        Assert.Empty(board.Entries);
        Assert.Null(BlacklistServices.PlayerStats.BuildReadLeaderboardsByRankResult(Request(4, "0100000005000000"), _ => "", Title));
    }

    private static string Hex(uint value) => Convert.ToHexString(BitConverter.GetBytes(value));
}
