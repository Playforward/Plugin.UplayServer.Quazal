using System.Buffers.Binary;

namespace Plugin.UplayServer.Quazal;

/// <summary>
/// Far Cry 3's generated Intel service (protocol 107 / 0x6b): the player's
/// multiplayer progression.
/// </summary>
internal static class Fc3IntelService
{
    public const byte ProtocolId = 107;
    public const uint MethodGetIntel = 1;
    public const uint MethodAddXp = 2;

    /// <summary>Where the XP total is kept in the player-data store.</summary>
    private const string Namespace = "intel";
    private const string XpKey = "xp";
    /// <summary>The Playforward board the XP total is ranked on.</summary>
    public const string XpBoard = "xp";

    /// <summary>Where the found data and decodes are kept in the player-data store.</summary>
    private const string BoostersKey = "boosters";

    /// <summary>
    /// The booster catalogue a working server sends
    /// </summary>
    private static readonly uint[] BoosterDurations = [600, 1800, 3600, 10800, 28800];
    private const uint BoosterCooldownSeconds = 7200;
    private const float BoosterMultiplier = 2.0f;
    private const int DecodesKept = 8;

    /// <summary>
    /// Method 3, <c>(uint8 kind, uint32 count) -> (uint32, uint32)</c>: the
    /// client reporting the "data" it found at the end of a match -- the
    /// drop is rolled on the client 
    /// </summary>
    public const uint MethodReportFound = 3;
    /// <summary>
    /// Method 5, <c>(uint8 kind, uint32, uint32 id, bool) -> (uint32 start,
    /// uint32 end, bool)</c>: start decoding a found item
    /// </summary>
    public const uint MethodStartDecoding = 5;
    /// <summary>
    /// <c>(u32 kind, u8 1)</c> -> <c>bool true</c>: the player claims the
    /// decoded data of that kind
    /// </summary>
    public const uint MethodClaimDecoded = 7;

    /// <summary>What the Intel service holds per player besides the XP total.</summary>
    private sealed class IntelState
    {
        public uint[] Found { get; set; } = new uint[5];
        public List<Decode> Decodes { get; set; } = [];
    }

    private sealed record Decode(byte Kind, uint Start, uint End, uint Id);

    public static byte[] BuildGetIntelResult(RmcRequest request, uint pid)
    {
        using var body = new MemoryStream();
        void U32(uint v) => body.Write(BitConverter.GetBytes(v));
        void U8(byte v) => body.WriteByte(v);
        void Bool(bool v) => body.WriteByte(v ? (byte)1 : (byte)0);
        void F32(float v) => body.Write(BitConverter.GetBytes(v));

        uint xp = ReadXp(pid);
        IntelState state = ReadState(pid);
        U32((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        U32(xp);
        U32(0xFFFFFFFF);
        U32(0);
        Bool(false);
        U32((uint)BoosterDurations.Length);
        for (int i = 0; i < BoosterDurations.Length; i++)
        {
            U8((byte)i); Bool(true); U32(BoosterDurations[i]); U32(BoosterCooldownSeconds); F32(BoosterMultiplier);
        }
        U32((uint)BoosterDurations.Length);
        for (int i = 0; i < BoosterDurations.Length; i++)
        {
            U32((uint)i); U32(0); U8((byte)i);
            U32(i < state.Found.Length ? state.Found[i] : 0);
            Bool(state.Decodes.Any(decode => decode.Kind == i));
        }
        U32((uint)(state.Decodes.Count + 1));
        foreach (Decode decode in state.Decodes)
        {
            U8(0); U32(decode.Start); U32(decode.End); U32(0); U32(0); U32(decode.Id); Bool(false); Bool(true);
        }
        U8(1); U32(0); U32(0); U32(0); U32(0xFFFFFFFF); U32(0xFFFFFFFF); Bool(false); Bool(true);
        U32(0);

        Serilog.Log.Information("[quazal] FC3 Intel.GetIntel pid={Pid} -> xp={Xp}, found=[{Found}], {Decodes} decoding",
            pid, xp, string.Join(",", state.Found), state.Decodes.Count);
        return body.ToArray();
    }

    public static byte[] BuildReportFoundResult(RmcRequest request, uint pid)
    {
        byte kind = request.Parameters.Length > 0 ? request.Parameters[0] : (byte)0;
        uint count = request.Parameters.Length >= 5 ? BinaryPrimitives.ReadUInt32LittleEndian(request.Parameters.AsSpan(1)) : 0;
        uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (pid != 0 && kind < BoosterDurations.Length && count > 0)
        {
            IntelState state = ReadState(pid);
            state.Found[kind] += count;
            WriteState(pid, state);
            Serilog.Log.Information("[quazal] FC3 Intel.ReportFound pid={Pid} kind={Kind} +{Count} -> {Total} held", pid, kind, count, state.Found[kind]);
        }
        else
        {
            Serilog.Log.Warning("[quazal] FC3 Intel.ReportFound pid={Pid} (kind={Kind}, count={Count}) not recorded", pid, kind, count);
        }
        var body = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), now);
        return body;
    }

    public static byte[] BuildClaimDecodedResult(RmcRequest request, uint pid)
    {
        ReadOnlySpan<byte> p = request.Parameters;
        uint kind = p.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(p) : 0;
        if (pid != 0)
        {
            IntelState state = ReadState(pid);
            int cleared = state.Decodes.RemoveAll(decode => decode.Kind == kind);
            if (kind < state.Found.Length && state.Found[kind] > 0) state.Found[kind]--;
            WriteState(pid, state);
            Serilog.Log.Information("[quazal] FC3 Intel.ClaimDecoded pid={Pid} kind={Kind} -> {Cleared} decode(s) cleared, {Left} of the kind held",
                pid, kind, cleared, kind < state.Found.Length ? state.Found[kind] : 0);
        }
        return [1];
    }

    public static byte[] BuildStartDecodingResult(RmcRequest request, uint pid)
    {
        ReadOnlySpan<byte> p = request.Parameters;
        byte kind = p.Length > 0 ? p[0] : (byte)0;
        uint id = p.Length >= 9 ? BinaryPrimitives.ReadUInt32LittleEndian(p[5..]) : 0;
        uint start = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        uint end = start + (kind < BoosterDurations.Length ? BoosterDurations[kind] : BoosterDurations[0]);
        var body = new byte[9];
        if (pid != 0)
        {
            IntelState state = ReadState(pid);
            state.Decodes.RemoveAll(decode => decode.Id == id);
            state.Decodes.Add(new Decode(kind, start, end, id));
            while (state.Decodes.Count > DecodesKept) state.Decodes.RemoveAt(0);
            WriteState(pid, state);
            Serilog.Log.Information("[quazal] FC3 Intel.StartDecoding pid={Pid} kind={Kind} id={Id} -> {Start}..{End} ({Seconds} s)",
                pid, kind, id, start, end, end - start);
            BinaryPrimitives.WriteUInt32LittleEndian(body, start);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), end);
            body[8] = 1;
        }
        return body;
    }

    private static IntelState ReadState(uint pid)
    {
        byte[]? stored = OnlineDataStore.GetBlob(pid, Namespace, BoostersKey);
        if (stored is null) return new IntelState();
        try
        {
            IntelState? state = System.Text.Json.JsonSerializer.Deserialize<IntelState>(stored);
            if (state is not null)
            {
                if (state.Found.Length < BoosterDurations.Length)
                    state.Found = [.. state.Found, .. new uint[BoosterDurations.Length - state.Found.Length]];
                return state;
            }
        }
        catch (System.Text.Json.JsonException exception)
        {
            Serilog.Log.Warning("[quazal] FC3 Intel pid={Pid}: stored boosters did not parse ({Error}); starting over", pid, exception.Message);
        }
        return new IntelState();
    }

    private static void WriteState(uint pid, IntelState state) =>
        OnlineDataStore.PutBlob(pid, Namespace, BoostersKey, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(state));

    /// <summary>
    /// Adds a match's XP to the player's total. The result is empty by the
    /// skeleton's own encoder, so an empty array.
    /// </summary>
    public static byte[] BuildAddXpResult(RmcRequest request, uint pid)
    {
        if (request.Parameters.Length < 4)
        {
            Serilog.Log.Warning("[quazal] FC3 Intel.AddXp pid={Pid}: {Length}-byte parameters, expected a uint32; ignoring",
                pid, request.Parameters.Length);
            return [];
        }
        uint awarded = BinaryPrimitives.ReadUInt32LittleEndian(request.Parameters);
        uint total = ReadXp(pid) + awarded;
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, total);
        OnlineDataStore.PutBlob(pid, Namespace, XpKey, bytes);
        Serilog.Log.Information("[quazal] FC3 Intel.AddXp pid={Pid} +{Awarded} -> intel xp={Total}", pid, awarded, total);
        return [];
    }

    /// <summary>The player's Intel XP total on record (the GetIntel header's), 0 for a stranger.</summary>
    public static uint ReadXp(uint pid)
    {
        byte[]? stored = OnlineDataStore.GetBlob(pid, Namespace, XpKey);
        return stored is { Length: >= 4 } ? BinaryPrimitives.ReadUInt32LittleEndian(stored) : 0;
    }
}
