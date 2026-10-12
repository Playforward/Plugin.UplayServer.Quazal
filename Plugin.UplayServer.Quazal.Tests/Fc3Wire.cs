using System.Buffers.Binary;
using System.Text;

namespace Plugin.UplayServer.Quazal.Tests;

/// <summary>Builders for protocol 106 wire shapes, shared by the tests that speak it.</summary>
internal static class Fc3Wire
{
    /// <summary>
    /// A column in the wire layout OnlineDataColumn expects: the same bytes
    /// LikeTemplate produces, with the tag byte set so the uint32 slot is the
    /// value. Real FC3 columns are only ever copied, never built, but a test
    /// has to start somewhere.
    /// </summary>
    public static OnlineDataColumn Column(string name, uint value)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        var raw = new List<byte>();
        raw.AddRange(BitConverter.GetBytes((ushort)(nameBytes.Length + 1)));
        raw.AddRange(nameBytes);
        raw.Add(0);
        raw.AddRange([1, 0, 0, 0, 0]);                 // flags
        raw.AddRange(BitConverter.GetBytes(value));    // uint32
        raw.AddRange(new byte[8]);                     // uint64
        raw.AddRange(new byte[4]);                     // uint32
        raw.AddRange(BitConverter.GetBytes((ushort)1));
        raw.Add(0);                                    // empty string
        raw.AddRange(BitConverter.GetBytes((ushort)0));// empty blob
        return new OnlineDataColumn(name, [.. raw]);
    }

    public static OnlineDataRowSet RowSet(params (string Name, uint Value)[][] rows)
    {
        var rowSet = new OnlineDataRowSet();
        foreach (var row in rows)
            rowSet.Rows.Add(row.Select(column => Column(column.Name, column.Value)).ToList());
        return rowSet;
    }

    public static uint Value(OnlineDataRowSet rowSet, int row, string column) =>
        rowSet.Rows[row].Single(candidate => candidate.Name == column).NumericValue;

    /// <summary>A StoreOnlineData (106/2) request: uint32 dataId, then the RowSet.</summary>
    public static RmcRequest StoreRequest(uint dataId, byte[] rowSet)
    {
        byte[] parameters = new byte[4 + rowSet.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(parameters, dataId);
        rowSet.CopyTo(parameters, 4);
        return new RmcRequest(Fc3OnlineDataService.ProtocolId, CallId: 1, Fc3OnlineDataService.MethodStoreOnlineData, parameters);
    }

    /// <summary>
    /// A FetchOnlineData (106/1) request with no select list and no filters:
    /// uint32 dataId, qlist&lt;string&gt; select (0), qlist&lt;Column&gt; filters (0),
    /// uint32 limit, string order-by (empty).
    /// </summary>
    public static RmcRequest FetchRequest(uint dataId, uint limit = 150)
    {
        var parameters = new List<byte>();
        parameters.AddRange(BitConverter.GetBytes(dataId));
        parameters.AddRange(BitConverter.GetBytes(0u));
        parameters.AddRange(BitConverter.GetBytes(0u));
        parameters.AddRange(BitConverter.GetBytes(limit));
        parameters.AddRange(BitConverter.GetBytes((ushort)0));
        return new RmcRequest(Fc3OnlineDataService.ProtocolId, CallId: 2, Fc3OnlineDataService.MethodFetchOnlineData, [.. parameters]);
    }
}
