namespace Plugin.UplayServer.Quazal;

/// <summary>
/// Far Cry 3 generated online-data service (protocol 106 / 0x6a)
/// </summary>
internal static class Fc3OnlineDataService
{
    public const byte ProtocolId = 106;
    public const uint MethodFetchOnlineData = 1;
    public const uint MethodStoreOnlineData = 2;
    /// <summary>(dataId, where-columns, u32): remove the rows matching every column. Seen for 0x13 MpNotifications (2026-09-17).</summary>
    public const uint MethodDeleteOnlineData = 3;
    public const uint MethodFetchPackages = 4;

    /// <summary>The message wall / social feed.</summary>
    private const uint WallDataId = 0x09;

    /// <summary>`EWallMessageType_Generic`, the first of the eighteen wall types.</summary>
    private const uint WallTypeGeneric = 0;

    public static byte[] BuildFetchOnlineDataResult(RmcRequest request, uint pid)
    {
        if (!OnlineDataRowSet.TryParseQuery(request.Parameters, out uint dataId,
                out var selectColumns, out var filters, out uint rowLimit, out string sortColumn))
        {
            Serilog.Log.Warning(
                "[quazal] FC3 FetchOnlineData: unparsable query ({Length} bytes) raw={Raw}",
                request.Parameters.Length, Convert.ToHexString(request.Parameters));
            return OnlineDataStore.EmptyRowSet;
        }

        var learned = OnlineDataStore.Learn(dataId,
            filters.Select(filter => filter.Name).Concat(selectColumns).Append(sortColumn));
        if (learned.Count > 0)
            Serilog.Log.Information(
                "[quazal] online-data dataId=0x{DataId:X2} learned column(s) from query: {Learned}",
                dataId, string.Join(", ", learned));

        OnlineDataRowSet? stored = dataId == Fc3StatisticDefinitions.StatisticPlayerDataId
            ? SynthesisedStatistics(dataId, pid, filters)
            : OnlineDataStore.Fetch(pid, dataId)
                ?? OnlineDataSeed.TryGet(dataId)
                ?? SynthesisedStatistics(dataId, pid, filters);
        OnlineDataRowSet matching = Ordered(Matching(stored, filters, sortColumn), sortColumn, rowLimit);
        byte[] body = matching.ToBytes();
        int rows = matching.Rows.Count;

        Serilog.Log.Information(
            "[quazal] FC3 FetchOnlineData pid={Pid} dataId=0x{DataId:X2} select=[{Select}] filters=[{Filters}] orderBy={Sort} limit={Limit} " +
            "-> {Rows} row(s) (stored: {Stored}; vocabulary: {Vocabulary})",
            pid, dataId, string.Join(", ", selectColumns),
            string.Join(", ", filters.Select(f => $"{f.Name}={f.Describe()}")),
            sortColumn.Length > 0 ? sortColumn : "(none)", rowLimit,
            $"{rows} of {stored?.Rows.Count ?? 0}",
            OnlineDataStore.DescribeStored(pid),
            string.Join("/", OnlineDataStore.KnownColumns(dataId)));
        return body;
    }

    /// <summary>The data ids the host uploads a match's statistics under, after every match.</summary>
    private const uint StatisticMatchDataId = 0x05;
    private const uint StatisticTimeSliceDataId = 0x07;
    private const uint StatisticWeaponDataId = 0x08;
    /// <summary>Store mode 4: insert and return the assigned <c>id</c> per row (the match store).</summary>
    private const uint StoreModeInsertReturningId = 4;
    /// <summary>How many of a player's per-match TimeSlice rows are kept.</summary>
    private const int TimeSliceRowsKept = 50;

    /// <summary>
    /// A store: <c>uint32 dataId, RowSet rows, string returnColumn, uint32 mode</c>.
    /// </summary>
    /// <param name="titleId">
    /// The title the storing player is in, for finding the session whose
    /// participants are told about the new rows.
    /// </param>
    public static byte[] BuildStoreOnlineDataResult(RmcRequest request, uint pid, string titleId,
        GameSession.Notifier? notify = null)
    {
        ReadOnlySpan<byte> parameters = request.Parameters;
        int offset = 4;
        if (parameters.Length < 8 ||
            !OnlineDataRowSet.TryParse(parameters, ref offset, out OnlineDataRowSet uploaded))
        {
            Serilog.Log.Warning(
                "[quazal] FC3 StoreOnlineData pid={Pid}: upload did not parse, not stored ({Length} bytes)",
                pid, request.Parameters.Length);
            return OnlineDataStore.EmptyRowSet;
        }

        uint dataId = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(parameters);
        (string returnColumn, uint mode) = ReadStoreTail(parameters, offset);
        var learned = OnlineDataStore.Learn(dataId, uploaded.ColumnNames);
        if (learned.Count > 0)
            Serilog.Log.Information(
                "[quazal] online-data dataId=0x{DataId:X2} learned column(s) from upload: {Learned}",
                dataId, string.Join(", ", learned));

        if (uploaded.Rows.Count == 0)
        {
            Serilog.Log.Information("[quazal] FC3 StoreOnlineData pid={Pid} dataId=0x{DataId:X2} with no rows (mode {Mode}): nothing to store",
                pid, dataId, mode);
            return OnlineDataStore.EmptyRowSet;
        }

        bool isTombstone = uploaded.Rows.All(row => row.Count == 0);
        if (isTombstone)
        {
            OnlineDataStore.Clear(pid, dataId);
            Serilog.Log.Information(
                "[quazal] FC3 StoreOnlineData pid={Pid} dataId=0x{DataId:X2} cleared (column-less write)",
                pid, dataId);
            return OnlineDataStore.EmptyRowSet;
        }

        switch (dataId)
        {
            case StatisticMatchDataId:
                return StoreMatch(pid, uploaded, mode, notify, titleId);
            case Fc3StatisticDefinitions.StatisticPlayerDataId:
                return AccumulatePlayerStatistics(pid, uploaded, notify, titleId);
            case StatisticTimeSliceDataId:
                return StoreTimeSlices(pid, uploaded);
            case StatisticWeaponDataId:
                return StoreWeapons(pid, uploaded);
        }

        OnlineDataRowSet toStore = dataId == WallDataId ? Completed(uploaded, dataId, pid) : uploaded;
        OnlineDataStore.Store(pid, dataId, toStore);
        byte[] result = dataId == WallDataId || mode == StoreModeInsertReturningId
            ? AssignedIds(toStore)
            : OnlineDataStore.EmptyRowSet;
        Serilog.Log.Information(
            "[quazal] FC3 StoreOnlineData pid={Pid} dataId=0x{DataId:X2} stored {Rows} row(s), columns {Columns} " +
            "(return {Return}, mode {Mode}) -> {Reply}",
            pid, dataId, toStore.Rows.Count, string.Join("/", toStore.ColumnNames), returnColumn, mode,
            result.Length > 4 ? $"{toStore.Rows.Count} assigned id(s)" : "empty");
        return result;
    }

    /// <summary>
    /// DeleteOnlineData: drops the player's stored rows of the data id that
    /// match every where-column, answered with an empty RowSet like every
    /// other write. 
    /// </summary>
    public static byte[] BuildDeleteOnlineDataResult(RmcRequest request, uint pid)
    {
        if (!OnlineDataRowSet.TryParseDelete(request.Parameters, out uint dataId, out List<OnlineDataColumn> filters))
        {
            Serilog.Log.Warning("[quazal] FC3 DeleteOnlineData pid={Pid}: request did not parse ({Length} bytes)", pid, request.Parameters.Length);
            return OnlineDataStore.EmptyRowSet;
        }
        OnlineDataRowSet? stored = OnlineDataStore.Fetch(pid, dataId);
        int before = stored?.Rows.Count ?? 0;
        if (stored is not null && filters.Count > 0)
        {
            var kept = new OnlineDataRowSet();
            foreach (var row in stored.Rows)
            {
                bool matches = filters.All(filter =>
                    row.Any(column => column.Name == filter.Name && column.NumericValue == filter.NumericValue));
                if (!matches) kept.Rows.Add(row);
            }
            if (kept.Rows.Count == 0) OnlineDataStore.Clear(pid, dataId);
            else if (kept.Rows.Count != before) OnlineDataStore.Store(pid, dataId, kept);
            stored = kept;
        }
        Serilog.Log.Information("[quazal] FC3 DeleteOnlineData pid={Pid} dataId=0x{DataId:X2} where [{Where}] -> {Removed} of {Before} row(s) removed",
            pid, dataId, string.Join(", ", filters.Select(f => $"{f.Name}={f.Describe()}")), before - (stored?.Rows.Count ?? 0), before);
        return OnlineDataStore.EmptyRowSet;
    }

    private static (string ReturnColumn, uint Mode) ReadStoreTail(ReadOnlySpan<byte> parameters, int offset)
    {
        string returnColumn = string.Empty;
        if (offset + 2 <= parameters.Length)
        {
            ushort length = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(parameters[offset..]);
            offset += 2;
            if (length > 0 && offset + length <= parameters.Length)
            {
                returnColumn = System.Text.Encoding.UTF8.GetString(parameters.Slice(offset, length - 1));
                offset += length;
            }
        }
        uint mode = offset + 4 <= parameters.Length
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(parameters[offset..])
            : 0;
        return (returnColumn, mode);
    }

    /// <summary>
    /// The match row, filed under the host with a server-assigned <c>id</c>
    /// and a timestamp, the id going back in the result
    /// </summary>
    private static byte[] StoreMatch(uint pid, OnlineDataRowSet uploaded, uint mode, GameSession.Notifier? notify, string titleId)
    {
        var ids = new List<OnlineDataColumn>();
        var stored = new OnlineDataRowSet();
        uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var row in uploaded.Rows)
        {
            uint id = OnlineDataStore.NextRowId();
            stored.Rows.Add(WithColumns(row, ("id", id), ("unixtimestamp", now)));
            ids.Add(OnlineDataColumn.AssignedId(id));
        }
        OnlineDataStore.Store(pid, StatisticMatchDataId, stored);
        Serilog.Log.Information("[quazal] FC3 StoreOnlineData pid={Pid} dataId=0x05 match stored (mode {Mode}) -> match id {Ids}",
            pid, mode, string.Join(", ", stored.Rows.Select(row => row.First(c => c.Name == "id").NumericValue)));
        foreach (OnlineDataColumn id in ids)
            Fc3MatchLedger.MatchStored(titleId, pid, id.NumericValue, notify);
        return OnlineDataRowSet.RowBytes(ids);
    }

    /// <summary>
    /// <c>0x06</c> delta rows, added to each subject's counters
    /// </summary>
    private static byte[] AccumulatePlayerStatistics(uint pid, OnlineDataRowSet uploaded, GameSession.Notifier? notify, string titleId)
    {
        var players = new List<uint>();
        bool matchRows = false;
        foreach (var row in uploaded.Rows)
        {
            uint? client = ClientIdOf(row);
            if (client is null)
            {
                Serilog.Log.Warning("[quazal] FC3 StoreOnlineData pid={Pid} dataId=0x06 delta row without client_id ignored", pid);
                continue;
            }
            bool isMatch = row.Any(column => column.Name == "match_played" && column.NumericValue > 0);
            matchRows |= isMatch;
            PlayerStatistics.Accumulate(client.Value, row.Select(column => (column.Name, column.NumericValue)),
                isMatch ? $"the host (pid={pid})" : "the player's own delta row");
            players.Add(client.Value);
        }
        if (matchRows)
            Fc3MatchLedger.HostReported(titleId, pid, players, notify);
        else if (notify is not null)
            Fc3MatchLedger.TellSessionOf(titleId, pid, notify);
        return OnlineDataStore.EmptyRowSet;
    }

    /// <summary>One TimeSlice row per player per match, kept under that player, newest last.</summary>
    private static byte[] StoreTimeSlices(uint pid, OnlineDataRowSet uploaded)
    {
        foreach (var row in uploaded.Rows)
        {
            uint? client = ClientIdOf(row);
            if (client is null) continue;
            FileTimeSlice(client.Value, row, $"host pid={pid}");
        }
        return OnlineDataStore.EmptyRowSet;
    }

    /// <summary>
    /// Files one match's TimeSlice row under its player, with a row id and a
    /// timestamp, every column re-encoded the way a working server serves it
    /// (<see cref="OnlineDataColumn.Served"/>) whatever the source uploaded.
    /// </summary>
    internal static void FileTimeSlice(uint client, IReadOnlyList<OnlineDataColumn> row, string source)
    {
        uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var rows = new OnlineDataRowSet();
        foreach (var existing in OnlineDataStore.Fetch(client, StatisticTimeSliceDataId)?.Rows ?? [])
            rows.Rows.Add(existing);
        rows.Rows.Add(Served(WithColumns(row, ("id", OnlineDataStore.NextRowId()), ("unixtimestamp", now))));
        while (rows.Rows.Count > TimeSliceRowsKept) rows.Rows.RemoveAt(0);
        OnlineDataStore.Store(client, StatisticTimeSliceDataId, rows);
        Serilog.Log.Information("[quazal] FC3 StoreOnlineData: TimeSlice row for pid={Client} (match {Match}) from {Source}; {Count} kept",
            client, row.FirstOrDefault(c => c.Name == "match_id")?.NumericValue ?? 0, source, rows.Rows.Count);
    }

    /// <summary>
    /// One Weapon row per player and weapon, the match's numbers added to the
    /// player's existing row for that <c>weapon_id</c>.
    /// </summary>
    private static byte[] StoreWeapons(uint pid, OnlineDataRowSet uploaded)
    {
        foreach (var row in uploaded.Rows)
        {
            uint? client = ClientIdOf(row);
            if (client is null || !row.Any(c => c.Name == "weapon_id")) continue;
            FileWeapon(client.Value, row, $"host pid={pid}");
        }
        return OnlineDataStore.EmptyRowSet;
    }

    /// <summary>
    /// Adds one match's numbers for one weapon to the player's row for it,
    /// or starts that row. Served-shape columns, as for the TimeSlice.
    /// </summary>
    internal static void FileWeapon(uint client, IReadOnlyList<OnlineDataColumn> row, string source)
    {
        OnlineDataColumn weapon = row.First(c => c.Name == "weapon_id");
        var rows = new OnlineDataRowSet();
        foreach (var existing in OnlineDataStore.Fetch(client, StatisticWeaponDataId)?.Rows ?? [])
            rows.Rows.Add(existing);
        int index = rows.Rows.FindIndex(existing =>
            existing.FirstOrDefault(c => c.Name == "weapon_id")?.NumericValue == weapon.NumericValue);
        if (index < 0)
        {
            rows.Rows.Add(Served(WithColumns(row, ("id", OnlineDataStore.NextRowId()),
                ("unixtimestamp", (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()))));
        }
        else
        {
            var merged = new List<OnlineDataColumn>();
            foreach (OnlineDataColumn existing in rows.Rows[index])
            {
                OnlineDataColumn? delta = row.FirstOrDefault(c => c.Name == existing.Name);
                bool key = existing.Name is "client_id" or "weapon_id" or "id" or "unixtimestamp";
                merged.Add(OnlineDataColumn.Served(existing.Name,
                    delta is null || key ? existing.NumericValue : existing.NumericValue + delta.NumericValue));
            }
            rows.Rows[index] = merged;
        }
        OnlineDataStore.Store(client, StatisticWeaponDataId, rows);
        Serilog.Log.Information("[quazal] FC3 StoreOnlineData: Weapon row for pid={Client} weapon_id={Weapon} from {Source} ({Action}); {Count} weapon(s)",
            client, weapon.NumericValue, source, index < 0 ? "new" : "added", rows.Rows.Count);
    }

    /// <summary>The row with every column in served shape (flags <c>01 00 00 00 00</c>, the value in the uint32 slot).</summary>
    private static List<OnlineDataColumn> Served(IEnumerable<OnlineDataColumn> row) =>
        row.Select(column => OnlineDataColumn.Served(column.Name, column.NumericValue)).ToList();

    private static uint? ClientIdOf(List<OnlineDataColumn> row)
    {
        OnlineDataColumn? column = row.FirstOrDefault(c => c.Name == Fc3StatisticDefinitions.ClientIdColumn);
        return column is null || column.NumericValue == 0 ? null : column.NumericValue;
    }

    /// <summary>
    /// The row with the named columns put in front of it (from the row's own
    /// column bytes) where it lacks them: a working server's rows lead with
    /// <c>id</c> and <c>unixtimestamp</c>, then the client's columns.
    /// </summary>
    private static List<OnlineDataColumn> WithColumns(IReadOnlyList<OnlineDataColumn> row, params (string Name, uint Value)[] columns)
    {
        var result = new List<OnlineDataColumn>(row);
        OnlineDataColumn? template = row.FirstOrDefault();
        if (template is null) return result;
        int at = 0;
        foreach ((string name, uint value) in columns)
            if (!result.Any(c => c.Name == name))
                result.Insert(at++, OnlineDataColumn.LikeTemplate(template, name, value));
        return result;
    }

    /// <summary>
    /// The player's <c>0x06 StatisticPlayer</c> row, the one the profile
    /// screen reads its XP and level from at login and again after every
    /// match (on the statistics-updated notification). Every column is what
    /// was accumulated for it (<see cref="PlayerStatistics"/>)
    /// </summary>
    private static OnlineDataRowSet? SynthesisedStatistics(uint dataId, uint pid, List<OnlineDataColumn> filters)
    {
        if (dataId != Fc3StatisticDefinitions.StatisticPlayerDataId)
            return null;
        OnlineDataColumn? key = filters.FirstOrDefault(filter =>
            string.Equals(filter.Name, Fc3StatisticDefinitions.ClientIdColumn, StringComparison.Ordinal));
        if (key is null || key.NumericValue != pid)
            return null;
        IReadOnlyDictionary<string, uint> counters = PlayerStatistics.For(pid);
        if (counters.Count == 0)
            return null;
        uint xp = counters.GetValueOrDefault("xp", 0u);

        var row = new List<OnlineDataColumn>(3 + Fc3StatisticDefinitions.PlayerColumns.Length)
        {
            OnlineDataColumn.Served("id", pid),
            OnlineDataColumn.Served("unixtimestamp", (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            OnlineDataColumn.Served(Fc3StatisticDefinitions.ClientIdColumn, pid),
        };
        foreach (string column in Fc3StatisticDefinitions.PlayerColumns)
            row.Add(OnlineDataColumn.Served(column, counters.GetValueOrDefault(column, 0u)));
        var rowSet = new OnlineDataRowSet();
        rowSet.Rows.Add(row);
        Serilog.Log.Information("[quazal] FC3 StatisticPlayer row for pid={Pid}: xp={Xp} (level {Level}), {Columns} columns", pid, xp, Fc3RankTable.RankFor(xp), row.Count);
        return rowSet;
    }

    /// <summary>
    /// Applies a query's filters to the stored rows, which is what makes the
    /// wall converge instead of looping
    /// </summary>
    private static OnlineDataRowSet Matching(OnlineDataRowSet? stored,
        List<OnlineDataColumn> filters, string sortColumn)
    {
        var result = new OnlineDataRowSet();
        if (stored is null) return result;
        foreach (var row in stored.Rows)
        {
            bool keep = true;
            foreach (var filter in filters)
            {
                var column = row.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, filter.Name, StringComparison.Ordinal));
                if (column is null) continue;
                bool isCursor = string.Equals(filter.Name, sortColumn, StringComparison.Ordinal);
                if (isCursor ? column.NumericValue <= filter.NumericValue
                             : column.NumericValue != filter.NumericValue)
                {
                    keep = false;
                    break;
                }
            }
            if (keep) result.Rows.Add(row);
        }
        return result;
    }

    /// <summary>
    /// Serves a query's rows the way a working server does
    /// </summary>
    private static OnlineDataRowSet Ordered(OnlineDataRowSet matching, string sortColumn, uint rowLimit)
    {
        IEnumerable<List<OnlineDataColumn>> rows = matching.Rows;
        if (sortColumn.Length > 0)
        {
            rows = rows
                .Select((row, index) => (row, index))
                .OrderByDescending(entry => entry.row.FirstOrDefault(c =>
                    string.Equals(c.Name, sortColumn, StringComparison.Ordinal))?.NumericValue ?? 0)
                .ThenBy(entry => entry.index)
                .Select(entry => entry.row);
        }
        if (rowLimit > 0)
            rows = rows.Take((int)Math.Min(rowLimit, int.MaxValue));
        var result = new OnlineDataRowSet();
        foreach (var row in rows) result.Rows.Add(row);
        return result;
    }

    /// <summary>
    /// Builds the result for a store: one Column per uploaded row, each
    /// carrying that row's server-assigned identifier.
    /// </summary>
    private static byte[] AssignedIds(OnlineDataRowSet stored)
    {
        var columns = new List<OnlineDataColumn>();
        foreach (var row in stored.Rows)
        {
            if (row.Count == 0) continue;
            var id = row.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, "id", StringComparison.Ordinal));
            columns.Add(OnlineDataColumn.AssignedId(id?.NumericValue ?? 0));
        }
        if (columns.Count == 0) return OnlineDataStore.EmptyRowSet;
        return OnlineDataRowSet.RowBytes(columns);
    }

    /// <summary>
    /// FC3's wall consumer requires every column it looks up by name and drops
    /// the row silently if one is missing, so an uploaded wall row is completed
    /// with any column this data id is known to use but the row lacks. 
    /// </summary>
    private static OnlineDataRowSet Completed(OnlineDataRowSet uploaded, uint dataId, uint pid)
    {
        var known = new HashSet<string>(OnlineDataStore.InternedColumns);
        known.UnionWith(OnlineDataStore.KnownColumns(dataId));
        var completed = new OnlineDataRowSet();
        foreach (var row in uploaded.Rows)
        {
            var columns = new List<OnlineDataColumn>(row);
            OnlineDataColumn? template = columns.FirstOrDefault();
            if (template is null)
            {
                completed.Rows.Add(columns);
                continue;
            }
            var present = columns.Select(column => column.Name).ToHashSet();
            var added = new List<string>();
            uint messageId = OnlineDataStore.NextWallMessageId();
            uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (string name in known.Where(name => !present.Contains(name)))
            {
                uint value = name switch
                {
                    "type" => WallTypeGeneric,
                    "id" or "msg_id" => messageId,
                    "unixtimestamp" => now,
                    "pid" or "client_id" => pid,
                    _ => 0,
                };
                columns.Add(OnlineDataColumn.LikeTemplate(template, name, value));
                added.Add($"{name}={value}");
            }
            if (added.Count > 0)
                Serilog.Log.Information(
                    "[quazal] wall row completed with harvested column(s): {Added}",
                    string.Join(", ", added));
            completed.Rows.Add(columns);
        }
        return completed;
    }

    /// <summary>
    /// Method 4 returns `qlist&lt;{ uint32 id; qlist&lt;qlist&lt;Column&gt;&gt; rowSet }&gt;`
    /// the shape the client's own encoder `FUN_1079ad1b` produces.
    /// </summary>
    public static byte[] BuildFetchPackagesResult(RmcRequest request, List<int> dataIds, uint pid)
    {
        Serilog.Log.Information("[quazal] FC3 online-data 106/4 accepted: {Parameters}",
            Convert.ToHexString(request.Parameters));

        if (dataIds.Count == 0) return OnlineDataStore.EmptyRowSet;

        var body = new List<byte>(4 + dataIds.Count * 8);
        void WriteU32(uint value)
        {
            Span<byte> word = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(word, value);
            body.AddRange(word);
        }

        var described = new List<string>();
        WriteU32((uint)dataIds.Count);
        foreach (int id in dataIds)
        {
            WriteU32((uint)id);
            OnlineDataRowSet? stored = OnlineDataStore.Fetch(pid, (uint)id);
            body.AddRange(stored?.ToBytes() ?? OnlineDataStore.EmptyRowSet);
            described.Add($"0x{id:X2}({stored?.Rows.Count ?? 0})");
        }

        Serilog.Log.Information("[quazal] FC3 106/4 pid={Pid} -> {Count} package(s): {Ids} ({Length} bytes)",
            pid, dataIds.Count, string.Join(", ", described), body.Count);
        return body.ToArray();
    }
}
