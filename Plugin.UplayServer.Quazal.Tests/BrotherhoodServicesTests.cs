using System.Buffers.Binary;

namespace Plugin.UplayServer.Quazal.Tests;

/// <summary>Assassin's Creed Brotherhood's Hermes services, from the calls ACBMP.exe made.</summary>
public sealed class BrotherhoodServicesTests : IDisposable
{
    /// <summary>The parameters of the first GameInfo.GetFile ACBMP sent after logging in.</summary>
    private static readonly byte[] GameSettingsRequest = Convert.FromHexString(
        "0000000032000000220067616D6573657474696E67735F63313338305F643837335F73363238352E63786200");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "acb-storage-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private QuazalTitle Title(string resourceDir)
    {
        QuazalTitle title = QuazalTitle.ForTests("assassins-creed-brotherhood", 6576, 6577, QuazalProtocolFamily.Brotherhood);
        title.Descriptor.ResourceDir = resourceDir;
        return title;
    }

    private static RmcRequest GetFileList(byte[] parameters) =>
        new(BrotherhoodServices.GameInfo.ProtocolId, 32, BrotherhoodServices.GameInfo.MethodGetFileList, parameters);

    [Fact]
    public void ReadsTheFileNameAfterTheTwoLeadingWords()
    {
        Assert.Equal("gamesettings_c1380_d873_s6285.cxb", BrotherhoodServices.GameInfo.ReadFileName(GameSettingsRequest));
        Assert.Null(BrotherhoodServices.GameInfo.ReadFileName(GameSettingsRequest.AsSpan(0, 9)));
    }

    [Fact]
    public void AFileNobodyProvidedIsAnEmptyList()
    {
        byte[] result = BrotherhoodServices.GameInfo.BuildGetFileListResult(GetFileList(GameSettingsRequest), Title(_root));
        Assert.Equal(new byte[4], result);
    }

    [Fact]
    public void AProvidedFileIsListedByNameAndSize()
    {
        string storage = Path.Combine(_root, BrotherhoodServices.GameInfo.StorageFolder);
        Directory.CreateDirectory(storage);
        File.WriteAllBytes(Path.Combine(storage, "gamesettings_c1380_d873_s6285.cxb"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(storage, "other.cxb"), [4]);

        byte[] result = BrotherhoodServices.GameInfo.BuildGetFileListResult(GetFileList(GameSettingsRequest), Title(_root));

        // u32 count, then String (u16 length with NUL, bytes, NUL) and u32 size.
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(result));
        Assert.Equal(34, BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(4)));
        Assert.Equal("gamesettings_c1380_d873_s6285.cxb\0"u8.ToArray(), result[6..40]);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(40)));
        Assert.Equal(44, result.Length);
    }

    [Fact]
    public void ANameCannotLeaveTheStorageFolder()
    {
        Directory.CreateDirectory(Path.Combine(_root, BrotherhoodServices.GameInfo.StorageFolder));
        File.WriteAllBytes(Path.Combine(_root, "secret"), [9]);
        byte[] name = "../secret\0"u8.ToArray();
        byte[] parameters = new byte[10 + name.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(parameters.AsSpan(4), 50);
        BinaryPrimitives.WriteUInt16LittleEndian(parameters.AsSpan(8), (ushort)name.Length);
        name.CopyTo(parameters, 10);

        Assert.Equal(new byte[4], BrotherhoodServices.GameInfo.BuildGetFileListResult(GetFileList(parameters), Title(_root)));
    }
}

/// <summary>PersistentStore.GetItem, which fetches the bytes GameInfo listed.</summary>
public sealed class BrotherhoodPersistentStoreTests : IDisposable
{
    /// <summary>The GetItem ACBMP sent right after the file list: group 0, the file name as tag.</summary>
    private static readonly byte[] GetItemRequest = Convert.FromHexString(
        "00000000220067616D6573657474696E67735F63313338305F643837335F73363238352E63786200");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "acb-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private QuazalTitle Title()
    {
        QuazalTitle title = QuazalTitle.ForTests("assassins-creed-brotherhood", 6576, 6577, QuazalProtocolFamily.Brotherhood);
        title.Descriptor.ResourceDir = _root;
        return title;
    }

    private static RmcRequest GetItem() =>
        new(BrotherhoodServices.PersistentStore.ProtocolId, 31, BrotherhoodServices.PersistentStore.MethodGetItem, GetItemRequest);

    [Fact]
    public void AStoredFileComesBackAsBufferThenTrue()
    {
        string storage = Path.Combine(_root, BrotherhoodServices.GameInfo.StorageFolder);
        Directory.CreateDirectory(storage);
        File.WriteAllBytes(Path.Combine(storage, "gamesettings_c1380_d873_s6285.cxb"), [7, 8]);

        Assert.Equal(new byte[] { 2, 0, 0, 0, 7, 8, 1 }, BrotherhoodServices.PersistentStore.BuildGetItemResult(GetItem(), Title()));
    }

    [Fact]
    public void AMissingFileIsAnEmptyBufferAndFalse()
    {
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0 }, BrotherhoodServices.PersistentStore.BuildGetItemResult(GetItem(), Title()));
    }
}

/// <summary>ACB keeps searching after it hosts; its own session must not come back to it.</summary>
public sealed class BrotherhoodSearchTests
{
    /// <summary>The SearchSessions ACBMP sent while matchmaking: type 1, query 3, seven criteria including playlist 0x64.</summary>
    private static readonly byte[] SearchRequest = Convert.FromHexString(
        "0100000003000000070000006C000000040000006B000000010000006E0000000100000064000000EB51752969000000000000006A000000000000007000000004000000");

    private static uint Count(byte[] result) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(result);

    [Fact]
    public void ASearcherDoesNotFindTheSessionItHosts()
    {
        const string title = "acb-search-own";
        GameSessionStore.CreateSession(title, 1, sessionId: 1, hostPid: 1_000_000);
        GameSessionStore.AddParticipants(title, 1, 1, [1_000_000u]);
        var search = new RmcRequest(GameSession.ProtocolId, 60, GameSession.MethodSearchSessions, SearchRequest);

        Assert.Equal(0u, Count(GameSession.BuildSearchSessionsResult(search, 1_000_000, title, excludeOwn: true)));
        Assert.Equal(1u, Count(GameSession.BuildSearchSessionsResult(search, 1_000_001, title, excludeOwn: true)));

        // Far Cry 3's search is unchanged.
        Assert.Equal(1u, Count(GameSession.BuildSearchSessionsResult(search, 1_000_000, title)));
        GameSessionStore.DeleteSession(title, 1, 1);
    }
}

/// <summary>The Hermes statistics, presence and UbiNews answers, in the shapes ACBMP's decoders read.</summary>
[Collection("OnlineDataStore")]
public sealed class BrotherhoodHermesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "acb-hermes-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Standalone.LocalTitles.Clear();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void TheNewsChannelIsAGatheringThenItsOwnFields()
    {
        byte[] channel = BrotherhoodServices.UbiNews.BuildGetChannelResult(
            new RmcRequest(BrotherhoodServices.UbiNews.ProtocolId, 35, BrotherhoodServices.UbiNews.MethodGetChannel, []));

        byte[] expected = Convert.FromHexString(
            "01000000" + "00000000" + "00000000" + "0000" + "0000" + "00000000" + "00000000" + "00000000" + "00000000" +
            "010000" +                       // description ""
            "0800" + "5562694E65777300" +   // name "UbiNews"
            "010000" + "010000" +           // channelType, locale
            "00");                           // subscribable
        Assert.Equal(expected, channel);
    }

    [Theory]
    [InlineData(7u, 4)]
    [InlineData(8u, 4)]
    [InlineData(4u, 8)]
    [InlineData(5u, 8)]
    [InlineData(11u, 8)]
    [InlineData(12u, 8)]
    public void StatisticsReadsAreEmptyListsInTheirShape(uint method, int length)
    {
        bool withCount = BrotherhoodServices.PlayerStatistics.ListAndCountMethods.Contains(method);
        byte[] result = BrotherhoodServices.PlayerStatistics.BuildEmptyResult(
            new RmcRequest(BrotherhoodServices.PlayerStatistics.ProtocolId, 1, method, []), withCount);
        Assert.Equal(new byte[length], result);
    }

    /// <summary>The ReadPlayerStats ACBMP sent at login: twelve boards of stat ids, then the one name it asks about.</summary>
    private static readonly byte[] ReadPlayerStatsRequest = Convert.FromHexString(
        "0C0000000A0000001B000000010000000200000003000000040000000500000006000000070000000B00000016000000090000000A0000000C000000" +
        "0D0000000E0000000F0000001000000011000000120000001300000014000000150000001700000018000000190000001A0000001B0000001C000000" +
        "0B0000001B000000010000000200000003000000040000000500000006000000070000000B00000016000000090000000A0000000C0000000D000000" +
        "0E0000000F0000001000000011000000120000001300000014000000150000001700000018000000190000001A0000001B0000001C00000004000000" +
        "0A0000000100000002000000030000000400000005000000060000000700000008000000090000000A00000002000000010000000100000003000000" +
        "0800000001000000020000000300000004000000050000000600000007000000080000000C00000001000000010000000D0000000800000001000000" +
        "020000000300000004000000050000000600000007000000080000001600000001000000010000001700000008000000010000000200000003000000" +
        "040000000500000006000000070000000800000020000000010000000100000021000000080000000100000002000000030000000400000005000000" +
        "060000000700000008000000010000000100000001000000010000000500457A696F00");

    [Fact]
    public void ReadPlayerStatsAnswersEveryNameBoardAndStatAsked()
    {
        var request = new RmcRequest(BrotherhoodServices.PlayerStatistics.ProtocolId, 39,
            BrotherhoodServices.PlayerStatistics.MethodReadPlayerStats, ReadPlayerStatsRequest);
        Assert.Equal(["Ezio"], BrotherhoodServices.PlayerStatistics.ReadNames(ReadPlayerStatsRequest, -1));
        var boards = BrotherhoodServices.PlayerStatistics.ReadBoards(ReadPlayerStatsRequest, -1);
        Assert.Equal(12, boards.Count);
        Assert.Equal(10u, boards[0].Board);
        Assert.Equal(27, boards[0].Stats.Count);

        byte[] result = BrotherhoodServices.PlayerStatistics.BuildPlayersResult(request, "t", name => name == "Ezio" ? 1_000_000u : null);

        // One player, its twelve boards, the first board 10 whose first stat is 1 holding int32 0.
        const int variant = 4 + 8 + 8 + 3 + 1;
        Assert.Equal(Convert.FromHexString(
            "01000000" + "40420F00" + "0500457A696F00" + "0C000000" +
            "0A000000" + "00000000" + "00000000" + "1B000000" +
            "01" + "00000000" + "0000000000000000" + "0000000000000000" + "010000" + "01"),
            result[..(4 + 4 + 7 + 4 + 16 + 1 + variant)]);
        int stats = boards.Sum(b => b.Stats.Count);
        Assert.Equal(4 + (4 + 7 + 4) + boards.Count * (16 + 8) + stats * (2 + 3 * variant), result.Length);
    }

    [Fact]
    public void ReadPlayerStatsByBoardAnswersPerNameWithARowPerBoard()
    {
        byte[] parameters = Convert.FromHexString("010000000A00000002000000010000000500457A696F00");
        var request = new RmcRequest(BrotherhoodServices.PlayerStatistics.ProtocolId, 40,
            BrotherhoodServices.PlayerStatistics.MethodReadPlayerStatsByBoard, parameters);

        byte[] result = BrotherhoodServices.PlayerStatistics.BuildPlayersResult(request, "t", _ => null);
        Assert.Equal(Convert.FromHexString(
            "01000000" + "00000000" + "0500457A696F00" + "01000000" +
            "0A000000" + "00000000" + "00000000" + "00000000" + "0000000000000000" +
            "01000000"), result);
    }

    [Fact]
    public void UploadedStatisticsAreKeptForAStandalonePlayer()
    {
        QuazalTitle title = QuazalTitle.ForTests("acb-hermes", 6620, 6621, QuazalProtocolFamily.Brotherhood);
        Standalone.LocalTitle local = Standalone.LocalTitle.Start(
            new Standalone.StandaloneSettings { DataDirectory = _directory }, title)!;
        Standalone.LocalTitles.Register(local);
        uint pid = local.Login("Ezio", new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 3074))!.Pid;

        byte[] upload = [0x0A, 0, 0, 0, 1, 2, 3];
        Assert.Empty(BrotherhoodServices.PlayerStatistics.BuildWriteResult(
            new RmcRequest(BrotherhoodServices.PlayerStatistics.ProtocolId, 1, 1, upload), pid, title.TitleId));

        Assert.Equal(upload, local.Store.LoadPlayer(pid)!
            .Single(b => b.Namespace == BrotherhoodServices.PlayerStatistics.BlobNamespace).Payload);
    }

    [Fact]
    public void PresenceOfOthersIsAnEmptyList()
    {
        Assert.Equal(new byte[4], BrotherhoodServices.RichPresence.BuildGetPresenceResult(
            new RmcRequest(109, 1, BrotherhoodServices.RichPresence.MethodGetPresence, [0, 0, 0, 0])));
    }
}
