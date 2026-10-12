using System.Buffers.Binary;
using System.Text;

namespace Plugin.UplayServer.Quazal;

/// <summary>
/// One column of a protocol 106 RowSet, kept with its exact wire bytes so it can
/// be re-emitted without re-deriving a single field.
///
/// Grammar (Ubisoft's generated skeleton, encoder 0x1079acd8, Column codec
/// 0x1079ab0b / 0x1079abad — see docs/60):
///   string ColId (uint16 length including NUL)
///   5 x uint8 flags (one of which is the DataItem type tag; the internal order
///                    is NOT proven, which is why flags are copied, never built)
///   uint32, uint64, uint32   value union, selected by the tag
///   string
///   uint16-counted blob
/// </summary>
internal sealed record OnlineDataColumn(string Name, byte[] Raw)
{
    /// <summary>Offset of the 5 flag bytes: past the uint16 length and the name.</summary>
    private int FlagsOffset => 2 + Encoding.UTF8.GetByteCount(Name) + 1;

    public ReadOnlySpan<byte> Flags => Raw.AsSpan(FlagsOffset, 5);

    /// <summary>The uint32 immediately after the flags — where integer columns carry their value.</summary>
    public uint FirstValue => BinaryPrimitives.ReadUInt32LittleEndian(Raw.AsSpan(FlagsOffset + 5));

    /// <summary>
    /// The column's integer value regardless of which slot the type tag put it
    /// in: the uint32 slot for tag 1, or the trailing blob for tag 7, which is
    /// how FC3 sends `pid` and cursor values in a query.
    /// </summary>
    public uint NumericValue
    {
        get
        {
            uint first = FirstValue;
            if (first != 0) return first;
            return Raw.Length >= 4
                ? BinaryPrimitives.ReadUInt32LittleEndian(Raw.AsSpan(Raw.Length - 4))
                : 0;
        }
    }

    /// <summary>
    /// The value as text. Integer columns carry it in the uint32 slot, but a
    /// tag-7 column (TupleU32, which is how FC3 sends `pid` in a wall query)
    /// carries it in the trailing blob instead, so reporting only the uint32
    /// would print a misleading zero.
    /// </summary>
    public string Describe()
    {
        uint first = FirstValue;
        if (first != 0) return first.ToString();
        // Fall back to the last four bytes, which is where a tag-7 blob value sits.
        return Raw.Length >= 4
            ? $"blob:0x{BinaryPrimitives.ReadUInt32LittleEndian(Raw.AsSpan(Raw.Length - 4)):X}"
            : "0";
    }

    /// <summary>
    /// Builds a column with a different name but this column's flag bytes and a
    /// uint32 value, leaving the remaining value slots and the trailing string
    /// and blob empty. Used to fill in a column FC3 requires but did not
    /// upload: the flags are copied from a real column of the intended kind
    /// rather than invented, since their layout is unproven.
    /// </summary>
    public static OnlineDataColumn LikeTemplate(OnlineDataColumn template, string name, uint value)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        var raw = new List<byte>(nameBytes.Length + 32);
        raw.AddRange(BitConverter.GetBytes((ushort)(nameBytes.Length + 1)));
        raw.AddRange(nameBytes);
        raw.Add(0x00);
        raw.AddRange(template.Flags);
        raw.AddRange(BitConverter.GetBytes(value));   // uint32 value slot
        raw.AddRange(new byte[8]);                    // uint64
        raw.AddRange(new byte[4]);                    // uint32
        raw.AddRange(BitConverter.GetBytes((ushort)1));
        raw.Add(0x00);                                // empty string ("" + NUL)
        raw.AddRange(BitConverter.GetBytes((ushort)0));// empty blob
        return new OnlineDataColumn(name, [.. raw]);
    }

    /// <summary>
    /// A column as a working server serves one: flags <c>01 00 00 00 00</c>,
    /// the value in the uint32 slot, an empty string (its NUL only) and an
    /// empty blob. Every column of every statistics row the community server
    /// returned (<c>0x06</c>, <c>0x07</c>, <c>0x08</c> fetches, the match
    /// store's <c>id</c>; captures of 2026-09-17) has exactly these flags,
    /// whatever flags the client had uploaded the row with.
    /// </summary>
    public static OnlineDataColumn Served(string name, uint value)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        var raw = new List<byte>(nameBytes.Length + 29);
        raw.AddRange(BitConverter.GetBytes((ushort)(nameBytes.Length + 1)));
        raw.AddRange(nameBytes);
        raw.Add(0x00);
        raw.AddRange([0x01, 0x00, 0x00, 0x00, 0x00]);
        raw.AddRange(BitConverter.GetBytes(value));
        raw.AddRange(new byte[8]);
        raw.AddRange(new byte[4]);
        raw.AddRange(BitConverter.GetBytes((ushort)1));   // string: "" with its NUL
        raw.Add(0x00);
        raw.AddRange(BitConverter.GetBytes((ushort)0));   // blob: empty
        return new OnlineDataColumn(name, [.. raw]);
    }

    /// <summary>
    /// The <c>id</c> column a store's result carries per uploaded row, in the
    /// exact shape a working server sends it
    /// </summary>
    public static OnlineDataColumn AssignedId(uint id) => Served("id", id);
}

/// <summary>A parsed RowSet: rows of columns, each column retaining its wire bytes.</summary>
internal sealed class OnlineDataRowSet
{
    public List<List<OnlineDataColumn>> Rows { get; } = [];

    public IEnumerable<string> ColumnNames =>
        Rows.SelectMany(row => row.Select(column => column.Name)).Distinct();

    /// <summary>
    /// One row as a store's result carries it: a bare <c>qlist&lt;Column&gt;</c>
    /// (count, then columns)
    /// </summary>
    public static byte[] RowBytes(IReadOnlyList<OnlineDataColumn> columns)
    {
        var output = new List<byte>(4 + columns.Count * 32);
        output.AddRange(BitConverter.GetBytes((uint)columns.Count));
        foreach (OnlineDataColumn column in columns)
            output.AddRange(column.Raw);
        return [.. output];
    }

    public byte[] ToBytes()
    {
        var output = new List<byte>(64);
        output.AddRange(BitConverter.GetBytes((uint)Rows.Count));
        foreach (var row in Rows)
        {
            output.AddRange(BitConverter.GetBytes((uint)row.Count));
            foreach (var column in row)
                output.AddRange(column.Raw);
        }
        return [.. output];
    }

    /// <summary>Reads a RowSet, leaving <paramref name="offset"/> just past it.</summary>
    public static bool TryParse(ReadOnlySpan<byte> data, ref int offset, out OnlineDataRowSet rowSet)
    {
        rowSet = new OnlineDataRowSet();
        if (!TryReadU32(data, ref offset, out uint rowCount) || rowCount > 4096)
            return false;
        for (uint row = 0; row < rowCount; row++)
        {
            if (!TryReadU32(data, ref offset, out uint columnCount) || columnCount > 4096)
                return false;
            var columns = new List<OnlineDataColumn>((int)columnCount);
            for (uint column = 0; column < columnCount; column++)
            {
                if (!TryReadColumn(data, ref offset, out OnlineDataColumn? parsed))
                    return false;
                columns.Add(parsed!);
            }
            rowSet.Rows.Add(columns);
        }
        return true;
    }

    /// <summary>
    /// Reads a method 1 query:
    ///
    ///   uint32 dataId
    ///   qlist&lt;string&gt;  select columns   (empty for the wall's own poll)
    ///   qlist&lt;Column&gt;  filters
    ///   uint32         row limit
    ///   string         order-by column
    /// </summary>
    public static bool TryParseQuery(ReadOnlySpan<byte> parameters, out uint dataId,
        out List<string> selectColumns, out List<OnlineDataColumn> filters, out string sortColumn) =>
        TryParseQuery(parameters, out dataId, out selectColumns, out filters, out uint _, out sortColumn);

    /// <summary>
    /// As above, also returning the row limit: 0 for no limit, else the most
    /// rows the client wants (5 for its TimeSlice history, 150 for the wall).
    /// </summary>
    public static bool TryParseQuery(ReadOnlySpan<byte> parameters, out uint dataId,
        out List<string> selectColumns, out List<OnlineDataColumn> filters, out uint rowLimit, out string sortColumn)
    {
        dataId = 0;
        selectColumns = [];
        filters = [];
        rowLimit = 0;
        sortColumn = string.Empty;
        int offset = 0;
        if (!TryReadU32(parameters, ref offset, out dataId)) return false;
        if (!TryReadU32(parameters, ref offset, out uint selectCount) || selectCount > 256) return false;
        for (uint index = 0; index < selectCount; index++)
        {
            if (!TryReadString(parameters, ref offset, out string name)) return false;
            selectColumns.Add(name);
        }
        if (!TryReadU32(parameters, ref offset, out uint filterCount) || filterCount > 256) return false;
        for (uint index = 0; index < filterCount; index++)
        {
            if (!TryReadColumn(parameters, ref offset, out OnlineDataColumn? filter)) return false;
            filters.Add(filter!);
        }
        if (!TryReadU32(parameters, ref offset, out rowLimit)) return false;
        TryReadString(parameters, ref offset, out sortColumn);          // order-by column
        return true;
    }

    /// <summary>
    /// A DeleteOnlineData request (106/3): <c>u32 dataId, qlist&lt;Column&gt;
    /// where, u32</c> 
    /// </summary>
    public static bool TryParseDelete(ReadOnlySpan<byte> parameters, out uint dataId, out List<OnlineDataColumn> filters)
    {
        filters = [];
        int offset = 0;
        if (!TryReadU32(parameters, ref offset, out dataId)) return false;
        if (!TryReadU32(parameters, ref offset, out uint filterCount) || filterCount > 256) return false;
        for (uint i = 0; i < filterCount; i++)
        {
            if (!TryReadColumn(parameters, ref offset, out OnlineDataColumn? filter)) return false;
            filters.Add(filter!);
        }
        return true;
    }

    private static bool TryReadColumn(ReadOnlySpan<byte> data, ref int offset, out OnlineDataColumn? column)
    {
        column = null;
        int start = offset;
        if (!TryReadString(data, ref offset, out string name)) return false;
        offset += 5 + 4 + 8 + 4;
        if (offset > data.Length) return false;
        if (!TryReadString(data, ref offset, out _)) return false;
        if (!TryReadU16(data, ref offset, out ushort blobLength)) return false;
        offset += blobLength;
        if (offset > data.Length) return false;
        column = new OnlineDataColumn(name, data[start..offset].ToArray());
        return true;
    }

    private static bool TryReadString(ReadOnlySpan<byte> data, ref int offset, out string value)
    {
        value = string.Empty;
        if (!TryReadU16(data, ref offset, out ushort length)) return false;
        if (length == 0) return true;
        if (offset + length > data.Length) return false;
        value = Encoding.UTF8.GetString(data.Slice(offset, length - 1));
        offset += length;
        return true;
    }

    private static bool TryReadU16(ReadOnlySpan<byte> data, ref int offset, out ushort value)
    {
        value = 0;
        if (offset + 2 > data.Length) return false;
        value = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
        offset += 2;
        return true;
    }

    private static bool TryReadU32(ReadOnlySpan<byte> data, ref int offset, out uint value)
    {
        value = 0;
        if (offset + 4 > data.Length) return false;
        value = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        offset += 4;
        return true;
    }
}
