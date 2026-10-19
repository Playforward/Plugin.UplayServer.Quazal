using System.Buffers.Binary;
using System.Text;

namespace Plugin.UplayServer.Quazal;

/// <summary>
/// Assassin's Creed Brotherhood's "Hermes" services: Ubisoft's layer over
/// RendezVous in ACBMP.exe (GameInfo, PlayerStatistics, RichPresence, Clans,
/// MetaSession, Achievements, ...). Each one here was seen on the wire first.
/// </summary>
internal static class BrotherhoodServices
{
    // Hermes registers its protocols in RDVInterface::registerFriendProtocols:
    // PlayerStatistics 108, RichPresence 109, Clans 110, MetaSession 112,
    // GameInfo 113, ContactsExtensions 114, HermesAchievements 116,
    // SocialNetworks 117.
    public const int RichPresenceProtocolId = 109;
    public const uint MethodSetPresence = 1;

    /// <summary>GameSession (42) method 14 in ACBMP's DDL: GetInvitationsReceived(ResultRange) -> list.</summary>
    public const uint MethodGetInvitationsReceived = 14;

    /// <summary>Messaging (23) method 3 in ACBMP's DDL: GetMessagesHeaders(MessageRecipient, ResultRange) -> list.</summary>
    /// <summary>News (31) method 4 in ACBMP's DDL: GetChannelsByIDs(list u32) -> list NewsChannel.</summary>
    public const uint MethodGetChannelsByIds = 4;

    public const int MessagingProtocolId = 23;
    public const uint MethodGetMessagesHeaders = 3;

    /// <summary>
    /// GameInfo (protocol 113): the title's server-side files.
    /// <para>
    /// Method 5 is GetFileList, reached from
    /// Hermes::GameInfoServiceRDV::getFileListOnGameStorage (TaskRDV_GetFileList):
    /// (u32 offset, u32 max, String name) -> List of (String name, u32 size).
    /// The task copies each name into a 50-character field and keeps the size;
    /// the entry type is a class with a vtable, a String at +4 and a u32 at +8,
    /// which is why nothing precedes the name on the wire. The first thing the
    /// client asks after logging in is the list for
    /// <c>gamesettings_c1380_d873_s6285.cxb</c>; the content comes from a
    /// separate call (TaskRDV_GetFile) once it knows the file exists.
    /// </para>
    /// A 4-byte Buffer reply here was read as a list of 58,484 entries whose
    /// first name overran the 50-character field, and ACBMP died in the CRT's
    /// strcpy_s check (0xc0000417).
    /// </summary>
    internal static class GameInfo
    {
        public const int ProtocolId = 113;
        public const uint MethodGetFileList = 5;

        /// <summary>The longest name the client's 50-character field holds, with its NUL.</summary>
        public const int MaxNameLength = 49;

        /// <summary>Where a title's served files live, under its resource directory.</summary>
        public const string StorageFolder = "storage";

        public static byte[] BuildGetFileListResult(RmcRequest rmc, QuazalTitle title)
        {
            uint max = rmc.Parameters.Length >= 8 ? BinaryPrimitives.ReadUInt32LittleEndian(rmc.Parameters.AsSpan(4)) : 0;
            string? name = ReadFileName(rmc.Parameters);
            List<(string Name, int Size)> files = List(title, name, (int)Math.Min(max, 256u));
            Log.Information("[quazal] {Title} GameInfo.GetFileList({Name}, max {Max}) -> {Count} file(s){Note}",
                title, name ?? "<unreadable>", max, files.Count,
                files.Count == 0 ? $" (put it in {title.Descriptor.ResourceDir}/{StorageFolder}/)" : "");

            using var body = new MemoryStream();
            TicketGranting.WriteU32(body, (uint)files.Count);
            foreach ((string fileName, int size) in files)
            {
                TicketGranting.WriteString(body, fileName);
                TicketGranting.WriteU32(body, (uint)size);
            }
            return body.ToArray();
        }

        /// <summary>The name after the two leading u32s, or null when the parameters are not that shape.</summary>
        internal static string? ReadFileName(ReadOnlySpan<byte> parameters)
        {
            if (parameters.Length < 10)
                return null;
            ReadOnlySpan<byte> rest = parameters[8..];
            int length = BinaryPrimitives.ReadUInt16LittleEndian(rest);
            if (length < 1 || length > rest.Length - 2)
                return null;
            return Encoding.UTF8.GetString(rest.Slice(2, length - 1));
        }

        /// <summary>
        /// The served files the request names: the one called <paramref name="name"/>,
        /// or every file when the name is empty. Only plain names are looked up,
        /// so a name from the wire cannot climb out of the folder.
        /// </summary>
        internal static List<(string Name, int Size)> List(QuazalTitle title, string? name, int max)
        {
            if (max <= 0 || name is null || StorageDirectory(title) is not { } directory || !Directory.Exists(directory))
                return [];
            if (name.Length > 0)
            {
                if (!IsPlainName(name) || !File.Exists(Path.Combine(directory, name)))
                    return [];
                return [(name, (int)new FileInfo(Path.Combine(directory, name)).Length)];
            }
            return Directory.EnumerateFiles(directory)
                .Select(path => (Name: Path.GetFileName(path), Size: (int)new FileInfo(path).Length))
                .Where(file => IsPlainName(file.Name))
                .OrderBy(file => file.Name, StringComparer.Ordinal)
                .Take(max)
                .ToList();
        }

        /// <summary>A served file's bytes, or null when there is no such file.</summary>
        internal static byte[]? Load(QuazalTitle title, string name) =>
            IsPlainName(name) && StorageDirectory(title) is { } directory && File.Exists(Path.Combine(directory, name))
                ? File.ReadAllBytes(Path.Combine(directory, name))
                : null;

        private static string? StorageDirectory(QuazalTitle title) =>
            string.IsNullOrWhiteSpace(title.Descriptor.ResourceDir)
                ? null
                : Path.Combine(Directory.GetCurrentDirectory(), title.Descriptor.ResourceDir, StorageFolder);

        private static bool IsPlainName(string name) =>
            name.Length is > 0 and <= MaxNameLength && !name.StartsWith('.') &&
            name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
    }

    /// <summary>
    /// PersistentStore (protocol 24), the standard Quazal service, as the
    /// DDL embedded in ACBMP.exe declares it. After GameInfo lists a file the
    /// client fetches its bytes with GetItem, the file name as the tag.
    /// </summary>
    internal static class PersistentStore
    {
        public const int ProtocolId = 24;

        /// <summary>GetItem(uint32 uiGroup, string strTag) -> (buffer bufData, bool bResult).</summary>
        public const uint MethodGetItem = 4;

        public static byte[] BuildGetItemResult(RmcRequest rmc, QuazalTitle title)
        {
            uint group = rmc.Parameters.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(rmc.Parameters) : 0;
            string? tag = ReadString(rmc.Parameters, 4);
            byte[]? data = tag is null ? null : GameInfo.Load(title, tag);
            Log.Information("[quazal] {Title} PersistentStore.GetItem(group {Group}, {Tag}) -> {Result}",
                title, group, tag ?? "<unreadable>", data is null ? "not found" : $"{data.Length} bytes");

            using var body = new MemoryStream();
            TicketGranting.WriteU32(body, (uint)(data?.Length ?? 0));
            if (data is not null)
                body.Write(data);
            body.WriteByte(data is null ? (byte)0 : (byte)1);
            return body.ToArray();
        }

        private static string? ReadString(ReadOnlySpan<byte> parameters, int offset)
        {
            if (parameters.Length < offset + 2)
                return null;
            int length = BinaryPrimitives.ReadUInt16LittleEndian(parameters[offset..]);
            if (length < 1 || length > parameters.Length - offset - 2)
                return null;
            return Encoding.UTF8.GetString(parameters.Slice(offset + 2, length - 1));
        }
    }

    /// <summary>
    /// Hermes PlayerStatistics (protocol 108), read from ACBMP.exe's protocol
    /// client (request stubs from 0x1e046e0, response decoders dispatched at
    /// 0x1e056e0). Every result is a counted list, so "nothing recorded" has
    /// one exact encoding; that is what is served until the row semantics are
    /// known well enough to build real rows. Writes are accepted and kept.
    /// <para>
    /// Reply shapes per method: 1, 2, 9, 10 void; 3, 7, 8 one list; 4, 5, 6,
    /// 11, 12 a list then a u32. The list ReadPlayerStats (3) returns holds
    /// PlayerStats = (u32 pid, String name, list BoardRow), BoardRow = (u32,
    /// u32, u32, list StatEntry, u64 time), StatEntry = (u8, Variant, u8,
    /// Variant, Variant), Variant = (u32 type, i64, f64, String, u8).
    /// </para>
    /// Requests seen: 3 = (list of (u32 board, list u32 stat ids), list String
    /// names); 4 = six u32; 5 = (u32 board, u32, String name, u32, u32, u32);
    /// 6 = (list of (u32 board, u32), list String names).
    /// </summary>
    internal static class PlayerStatistics
    {
        public const int ProtocolId = 108;

        /// <summary>The namespace a player's written statistics are kept under.</summary>
        public const string BlobNamespace = "acb-stats";

        internal static readonly uint[] VoidMethods = [1, 2, 9, 10];
        internal static readonly uint[] ListMethods = [7, 8];
        internal static readonly uint[] ListAndCountMethods = [4, 5, 11, 12];

        /// <summary>ReadPlayerStats: (list of (u32 board, list u32 stats), list String names) -> list PlayerStats.</summary>
        public const uint MethodReadPlayerStats = 3;

        /// <summary>(list of (u32 board, u32), list String names) -> (list PlayerStats, u32).</summary>
        public const uint MethodReadPlayerStatsByBoard = 6;

        /// <summary>
        /// One PlayerStats per name asked about, in the order asked, each with
        /// a row per board asked about and, for ReadPlayerStats, an entry per
        /// stat id asked about, every value an integer 0 until the uploads
        /// can be read back. Both halves are load-bearing: the client indexes
        /// its array of answers by the position of each name it sent (an
        /// empty list read past the end, null read at ACBMP+0xa3a9f8), then
        /// looks every (board, stat) it asked for up in the rows and reads
        /// through the result without checking it (null read at
        /// ACBMP+0x18ac54a when the rows were empty).
        /// <para>
        /// Wire: PlayerStats = (u32 pid, String name, list BoardRow);
        /// BoardRow = (u32 board, u32, u32, list StatEntry, u64 time);
        /// StatEntry = (u8 stat, Variant value, u8, Variant, Variant);
        /// Variant = (i32, i64, f64, String, u8 type), the type choosing which
        /// field holds the value (1 i32, 2 i64, 3 f64, 4 String).
        /// </para>
        /// </summary>
        public static byte[] BuildPlayersResult(RmcRequest rmc, string titleId, Func<string, uint?> pidOf)
        {
            bool withCount = rmc.MethodId == MethodReadPlayerStatsByBoard;
            int elementBytes = rmc.MethodId == MethodReadPlayerStats ? -1 : 8;
            List<(uint Board, List<uint> Stats)> boards = ReadBoards(rmc.Parameters, elementBytes);
            List<string> names = ReadNames(rmc.Parameters, elementBytes);
            using var body = new MemoryStream();
            TicketGranting.WriteU32(body, (uint)names.Count);
            foreach (string name in names)
            {
                TicketGranting.WriteU32(body, pidOf(name) ?? 0);
                TicketGranting.WriteString(body, name);
                TicketGranting.WriteU32(body, (uint)boards.Count);
                foreach ((uint board, List<uint> stats) in boards)
                {
                    TicketGranting.WriteU32(body, board);
                    TicketGranting.WriteU32(body, 0);
                    TicketGranting.WriteU32(body, 0);
                    TicketGranting.WriteU32(body, (uint)stats.Count);
                    foreach (uint stat in stats)
                    {
                        body.WriteByte((byte)stat);
                        WriteZero(body);
                        body.WriteByte(0);
                        WriteZero(body);
                        WriteZero(body);
                    }
                    body.Write(new byte[8]); // time
                }
            }
            if (withCount)
                TicketGranting.WriteU32(body, (uint)names.Count);
            Log.Information("[quazal] PlayerStatistics.{Method} for [{Names}] -> {Count} player(s), {Boards} board(s) of zeros",
                rmc.MethodId, string.Join(", ", names), names.Count, boards.Count);
            return body.ToArray();
        }

        /// <summary>An integer Variant holding 0.</summary>
        private static void WriteZero(Stream body)
        {
            TicketGranting.WriteU32(body, 0);  // i32
            body.Write(new byte[8]);           // i64
            body.Write(new byte[8]);           // f64
            TicketGranting.WriteString(body, "");
            body.WriteByte(1);                 // type: i32
        }

        /// <summary>The boards a read asks about, with the stat ids it wants from each (none for fixed-size entries).</summary>
        internal static List<(uint Board, List<uint> Stats)> ReadBoards(ReadOnlySpan<byte> parameters, int elementBytes)
        {
            var boards = new List<(uint, List<uint>)>();
            int offset = 0;
            if (!TryU32(parameters, ref offset, out uint count) || count > 1024)
                return boards;
            for (uint i = 0; i < count; i++)
            {
                if (!TryU32(parameters, ref offset, out uint board))
                    break;
                var stats = new List<uint>();
                if (elementBytes >= 0)
                {
                    offset += elementBytes - 4;
                }
                else
                {
                    if (!TryU32(parameters, ref offset, out uint statCount) || statCount > 4096)
                        break;
                    for (uint j = 0; j < statCount && TryU32(parameters, ref offset, out uint stat); j++)
                        stats.Add(stat);
                }
                boards.Add((board, stats));
            }
            return boards;
        }

        /// <summary>
        /// The names list that follows the boards list. The boards list is
        /// (board, list of u32) per entry for method 3 (<paramref name="elementBytes"/> -1)
        /// and fixed-size entries otherwise.
        /// </summary>
        internal static List<string> ReadNames(ReadOnlySpan<byte> parameters, int elementBytes)
        {
            var names = new List<string>();
            int offset = 0;
            if (!TryU32(parameters, ref offset, out uint boards) || boards > 1024)
                return names;
            for (uint i = 0; i < boards; i++)
            {
                if (elementBytes >= 0)
                {
                    offset += elementBytes;
                    continue;
                }
                if (!TryU32(parameters, ref offset, out _) || !TryU32(parameters, ref offset, out uint stats) || stats > 4096)
                    return names;
                offset += (int)stats * 4;
            }
            if (!TryU32(parameters, ref offset, out uint count) || count > 256)
                return names;
            for (uint i = 0; i < count; i++)
            {
                if (offset + 2 > parameters.Length)
                    break;
                int length = BinaryPrimitives.ReadUInt16LittleEndian(parameters[offset..]);
                offset += 2;
                if (length < 1 || offset + length > parameters.Length)
                    break;
                names.Add(Encoding.UTF8.GetString(parameters.Slice(offset, length - 1)));
                offset += length;
            }
            return names;
        }

        private static bool TryU32(ReadOnlySpan<byte> data, ref int offset, out uint value)
        {
            value = 0;
            if (offset < 0 || offset + 4 > data.Length)
                return false;
            value = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
            offset += 4;
            return true;
        }

        /// <summary>Methods 1 and 2 upload statistics; kept as sent, for when reads can return them.</summary>
        public static byte[] BuildWriteResult(RmcRequest rmc, uint pid, string titleId)
        {
            Playforward.IProgressStore? store = Standalone.PlayerStores.ForPlayer(titleId, pid);
            bool kept = store?.SaveBlob(pid, BlobNamespace, $"write-{rmc.MethodId}", rmc.Parameters) ?? false;
            Log.Information("[quazal] PlayerStatistics.{Method} from pid={Pid}: {Length} bytes {Kept}",
                rmc.MethodId, pid, rmc.Parameters.Length, kept ? "kept" : "accepted (no store for this player)");
            return [];
        }

        public static byte[] BuildVoidResult(RmcRequest rmc)
        {
            Log.Information("[quazal] PlayerStatistics.{Method} -> void", rmc.MethodId);
            return [];
        }

        public static byte[] BuildEmptyResult(RmcRequest rmc, bool withCount)
        {
            Log.Information("[quazal] PlayerStatistics.{Method} -> no rows", rmc.MethodId);
            return withCount ? new byte[8] : new byte[4];
        }
    }

    /// <summary>
    /// Hermes RichPresence (protocol 109): 1 = SetPresence(u32, String) -> void;
    /// 2 = (list) -> list, ACBMP's decoder at 0x1e03040. Nobody else's
    /// presence is published yet, so method 2 is an empty list.
    /// </summary>
    internal static class RichPresence
    {
        public const uint MethodGetPresence = 2;

        public static byte[] BuildGetPresenceResult(RmcRequest rmc)
        {
            Log.Information("[quazal] RichPresence.{Method} -> 0 entries", rmc.MethodId);
            return Quazal.EmptyList;
        }
    }

    /// <summary>
    /// UbiNews (protocol 33): 1 = GetChannel() -> NewsChannel, per ACBMP's
    /// DDL. NewsChannel is a Gathering (u32 id, u32 owner, u32 host, u16 min,
    /// u16 max, u32 policy, u32 policy argument, u32 flags, u32 state, String
    /// description) followed by String name, String channelType, String
    /// locale, bool subscribable. This server publishes one empty channel.
    /// </summary>
    internal static class UbiNews
    {
        public const int ProtocolId = 33;
        public const uint MethodGetChannel = 1;

        /// <summary>The channel this server publishes; nothing is ever posted to it.</summary>
        public const uint ChannelId = 1;

        public static byte[] BuildGetChannelResult(RmcRequest rmc)
        {
            using var body = new MemoryStream();
            TicketGranting.WriteU32(body, ChannelId); // m_idMyself
            TicketGranting.WriteU32(body, 0);         // m_pidOwner
            TicketGranting.WriteU32(body, 0);         // m_pidHost
            TicketGranting.WriteU16(body, 0);         // m_uiMinParticipants
            TicketGranting.WriteU16(body, 0);         // m_uiMaxParticipants
            TicketGranting.WriteU32(body, 0);         // m_uiParticipationPolicy
            TicketGranting.WriteU32(body, 0);         // m_uiPolicyArgument
            TicketGranting.WriteU32(body, 0);         // m_uiFlags
            TicketGranting.WriteU32(body, 0);         // m_uiState
            TicketGranting.WriteString(body, "");     // m_strDescription
            TicketGranting.WriteString(body, "UbiNews"); // name
            TicketGranting.WriteString(body, "");     // channelType
            TicketGranting.WriteString(body, "");     // locale
            body.WriteByte(0);                        // subscribable
            Log.Information("[quazal] UbiNews.GetChannel -> channel {Id}, no news", ChannelId);
            return body.ToArray();
        }
    }
}
