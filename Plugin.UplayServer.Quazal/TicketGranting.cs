using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Plugin.UplayServer.Quazal;

/// <summary>
/// A stateful RC4 keystream. PRUDP keeps one stream per direction for the
/// lifetime of a connection, so the cipher state has to persist across packets
/// rather than being rebuilt per payload.
/// </summary>
internal sealed class Rc4Stream
{
    private readonly byte[] _s = new byte[256];
    private int _i;
    private int _j;

    public Rc4Stream(ReadOnlySpan<byte> key)
    {
        for (int n = 0; n < 256; n++)
            _s[n] = (byte)n;
        for (int n = 0, k = 0; n < 256; n++)
        {
            k = (k + _s[n] + key[n % key.Length]) & 0xFF;
            (_s[n], _s[k]) = (_s[k], _s[n]);
        }
    }

    public byte[] Process(ReadOnlySpan<byte> data)
    {
        byte[] output = new byte[data.Length];
        for (int n = 0; n < data.Length; n++)
        {
            _i = (_i + 1) & 0xFF;
            _j = (_j + _s[_i]) & 0xFF;
            (_s[_i], _s[_j]) = (_s[_j], _s[_i]);
            output[n] = (byte)(data[n] ^ _s[(_s[_i] + _s[_j]) & 0xFF]);
        }
        return output;
    }
}

/// <summary>
/// Ticket Granting (protocol 10) the only protocol served on the
/// authentication connection. Far Cry 3 opens by logging in the built-in
/// "Tracking" account; player principals are supplied by server configuration.
/// </summary>
internal static class TicketGranting
{
    public const byte ProtocolId = 0x0A;
    public const uint MethodLogin = 1;
    public const uint MethodLoginEx = 2;
    public const uint MethodRequestTicket = 3;

    /// <summary>Quazal's fixed RC4 key for the authentication connection.</summary>
    public static readonly byte[] DefaultAuthKey = "CD&ML"u8.ToArray();

    /// <summary>Success. Quazal result codes set the high bit on error.</summary>
    private const uint ResultSuccess = 0x00010001;

    /// <summary>
    /// Telemetry account credentials, read out of FC3_d3d11.dll: the client
    /// calls its tracking login helpers with ("Tracking", "JaDe!").
    /// </summary>
    public const string TrackingUser = "Tracking";
    public const string TrackingPassword = "JaDe!";

    /// <summary>
    /// FC3's LoginEx ticket-manager job supplies this fixed client credential,
    /// rather than the password configured in upc.json or the LoginEx custom
    /// authentication holder. Its source key is the regular Quazal MD5 KDF of
    /// this value and the PID returned by the server.
    /// </summary>
    public const string PlayerTicketPassword = "UbiDummyPwd";

    /// <summary>Principal id representing the secure server itself.</summary>
    public const uint TrackingSecureServerPid = 2;

    /// <summary>
    /// FC3 retains the Tracking endpoint in its RendezVous registry. The
    /// player LoginEx reply therefore needs a distinct secure-server identity.
    /// Both identities may still share the same local UDP listener.
    /// </summary>
    public const uint PlayerSecureServerPid = 3;
    public const uint TrackingSecureServerCid = 1;
    public const uint PlayerSecureServerCid = 2;

    /// <summary>
    /// Principal id handed back for the tracking account. The client derives
    /// its ticket key from whatever pid we return, so this only has to stay
    /// consistent with the key used to seal the ticket.
    /// </summary>
    public const uint TrackingPid = 100;

    /// <summary>
    /// Quazal key derivation: MD5 applied 65000 + (pid % 1024) times over the
    /// account password.
    /// </summary>
    public static byte[] DeriveKey(uint pid, string password)
    {
        // Some 65,000 MD5s, and a Tracking login (which anybody may send)
        // asks for one: remembered, so a stream of logins costs a lookup each.
        if (_derivedKeys.TryGetValue((pid, password), out byte[]? known))
            return known.ToArray();

        byte[] buffer = Encoding.ASCII.GetBytes(password);
        uint rounds = 65000 + (pid % 1024);
        for (uint n = 0; n < rounds; n++)
            buffer = MD5.HashData(buffer);

        if (_derivedKeys.Count >= DerivedKeyCapacity)
            _derivedKeys.Clear();
        _derivedKeys[(pid, password)] = buffer.ToArray();
        return buffer;
    }

    /// <summary>Keys <see cref="DeriveKey"/> has already worked out. Accounts are the server's, so few.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(uint Pid, string Password), byte[]> _derivedKeys = new();

    /// <summary>The most derived keys remembered; past it the cache starts over.</summary>
    private const int DerivedKeyCapacity = 4096;

    internal static void WriteString(Stream stream, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(length, (ushort)(bytes.Length + 1));
        stream.Write(length);
        stream.Write(bytes);
        stream.WriteByte(0);
    }

    internal static void WriteU32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    internal static void WriteU16(Stream stream, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        stream.Write(buffer);
    }
    
    /// <summary>A Kerberos session key's length.</summary>
    private const int SessionKeyBytes = 16;

    /// <summary>AES-GCM's standard nonce, and its full-length tag.</summary>
    private const int InnerTicketNonceBytes = 12;
    private const int InnerTicketTagBytes = 16;

    /// <summary>What an inner ticket carries: the session key, then the pid it was issued to.</summary>
    private const int InnerTicketPlainBytes = SessionKeyBytes + sizeof(uint);

    /// <summary>A sealed inner ticket: nonce, ciphertext, tag.</summary>
    internal const int InnerTicketBytes = InnerTicketNonceBytes + InnerTicketPlainBytes + InnerTicketTagBytes;

    /// <summary>
    /// The key inner tickets are sealed with: AES-256, random per process.
    /// </summary>
    private static readonly byte[] InnerTicketKey = RandomNumberGenerator.GetBytes(32);

    /// <summary>
    /// Seals the inner ticket: opaque to the client, which forwards it
    /// untouched on the secure CONNECT, and unforgeable by it.
    /// </summary>
    internal static byte[] SealInnerTicket(ReadOnlySpan<byte> sessionKey, uint userPid)
    {
        Span<byte> plain = stackalloc byte[InnerTicketPlainBytes];
        sessionKey.CopyTo(plain);
        BinaryPrimitives.WriteUInt32LittleEndian(plain[SessionKeyBytes..], userPid);

        byte[] sealedTicket = new byte[InnerTicketBytes];
        Span<byte> nonce = sealedTicket.AsSpan(0, InnerTicketNonceBytes);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(InnerTicketKey, InnerTicketTagBytes);
        aes.Encrypt(nonce, plain,
            sealedTicket.AsSpan(InnerTicketNonceBytes, InnerTicketPlainBytes),
            sealedTicket.AsSpan(InnerTicketNonceBytes + InnerTicketPlainBytes));
        return sealedTicket;
    }

    /// <summary>
    /// Recovers the session key and pid from an inner ticket presented on a
    /// secure CONNECT, or returns false for one this process did not seal --
    /// forged, altered, or from before a restart.
    /// </summary>
    public static bool TryOpenInnerTicket(ReadOnlySpan<byte> blob, out byte[] sessionKey, out uint userPid)
    {
        sessionKey = [];
        userPid = 0;
        if (blob.Length != InnerTicketBytes)
            return false;

        byte[] plain = new byte[InnerTicketPlainBytes];
        try
        {
            using var aes = new AesGcm(InnerTicketKey, InnerTicketTagBytes);
            aes.Decrypt(blob[..InnerTicketNonceBytes],
                blob.Slice(InnerTicketNonceBytes, InnerTicketPlainBytes),
                blob[(InnerTicketNonceBytes + InnerTicketPlainBytes)..],
                plain);
        }
        catch (AuthenticationTagMismatchException)
        {
            return false;
        }

        sessionKey = plain[..SessionKeyBytes];
        userPid = BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(SessionKeyBytes));
        return true;
    }

    public static byte[] BuildTicket(
        uint userPid, string password, uint targetPid, out byte[] sessionKey,
        byte[]? sourceKey = null)
    {
        sessionKey = RandomNumberGenerator.GetBytes(SessionKeyBytes);

        byte[] internalTicket = SealInnerTicket(sessionKey, userPid);

        using var plain = new MemoryStream();
        plain.Write(sessionKey);
        WriteU32(plain, targetPid);
        WriteU32(plain, (uint)internalTicket.Length);
        plain.Write(internalTicket);

        byte[] key = sourceKey ?? DeriveKey(userPid, password);
        if (key.Length != 16)
            throw new ArgumentException("Kerberos source keys must be 16 bytes", nameof(sourceKey));
        byte[] sealedTicket = new Rc4Stream(key).Process(plain.ToArray());
        byte[] mac = HMACMD5.HashData(key, sealedTicket);

        byte[] result = new byte[sealedTicket.Length + mac.Length];
        sealedTicket.CopyTo(result, 0);
        mac.CopyTo(result, sealedTicket.Length);
        return result;
    }

    /// <summary>
    /// Ticket Granting Login result: Result, PID, Buffer(ticket), then a flat
    /// RVConnectionData (station url, special-protocol list, special url) and
    /// the server build string. This title predates the versioned-structure
    /// encoding, so RVConnectionData carries no version header.
    /// </summary>
    public static byte[] BuildLoginResult(
        uint pid, byte[] ticket, string host, int port, uint serverPid, uint serverCid)
    {
        using var body = new MemoryStream();
        WriteU32(body, ResultSuccess);
        WriteU32(body, pid);

        WriteU32(body, (uint)ticket.Length);
        body.Write(ticket);

        WriteString(body,
            $"prudps:/address={host};port={port};CID={serverCid};PID={serverPid};sid=1;stream=3;type=2");
        WriteU32(body, 0);   // no special protocols
        WriteString(body, string.Empty);
        WriteString(body, string.Empty);   // server build name

        return body.ToArray();
    }

    /// <summary>
    /// Answers a Kerberos secure CONNECT. The decrypted payload is a
    /// compression byte, Buffer(inner ticket) and Buffer(request data), where
    /// the request data is RC4 under the session key followed by its HMAC-MD5.
    /// The reply echoes the client's check value plus one, which is what proves
    /// the server recovered the session key.
    /// </summary>
    public static byte[]? BuildSecureConnectResponse(ReadOnlySpan<byte> clear, out uint userPid, out uint checkValue)
    {
        userPid = 0;
        checkValue = 0;
        if (clear.Length < 5 || clear[0] != 0)
            return null;

        int offset = 1;
        if (!TryReadBuffer(clear, ref offset, out ReadOnlySpan<byte> innerTicket) ||
            !TryReadBuffer(clear, ref offset, out ReadOnlySpan<byte> sealedRequest))
            return null;

        if (!TryOpenInnerTicket(innerTicket, out byte[] sessionKey, out uint ticketPid))
            return null;

        // Encrypt-then-HMAC: the trailing 16 bytes authenticate the ciphertext.
        if (sealedRequest.Length < 16 + 12)
            return null;
        ReadOnlySpan<byte> ciphertext = sealedRequest[..^16];
        ReadOnlySpan<byte> mac = sealedRequest[^16..];
        if (!HMACMD5.HashData(sessionKey, ciphertext).AsSpan().SequenceEqual(mac))
            return null;

        byte[] request = new Rc4Stream(sessionKey).Process(ciphertext);
        if (request.Length < 12)
            return null;

        uint requestPid = BinaryPrimitives.ReadUInt32LittleEndian(request);
        if (requestPid != ticketPid)
            return null;
        userPid = ticketPid;
        checkValue = BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(8));

        using var response = new MemoryStream();
        response.WriteByte(0);            // compression
        WriteU32(response, sizeof(uint)); // Buffer length
        WriteU32(response, checkValue + 1);
        return response.ToArray();
    }

    private static bool TryReadBuffer(ReadOnlySpan<byte> data, ref int offset, out ReadOnlySpan<byte> value)
    {
        value = default;
        if (offset + 4 > data.Length)
            return false;
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        offset += 4;
        if (length > data.Length - offset)
            return false;
        value = data.Slice(offset, (int)length);
        offset += (int)length;
        return true;
    }

    /// <summary>
    /// RequestTicket result: just the result code and the sealed ticket. The
    /// Switch-only source-key string is not part of this dialect.
    /// </summary>
    public static byte[] BuildRequestTicketResult(byte[] ticket)
    {
        using var body = new MemoryStream();
        WriteU32(body, ResultSuccess);
        WriteU32(body, (uint)ticket.Length);
        body.Write(ticket);
        return body.ToArray();
    }

    /// <summary>
    /// Frames an RMC success response: size, protocol id, success flag, call
    /// id, method id with 0x8000 set, then the result body.
    /// </summary>
    public static byte[] BuildRmcErrorResponse(byte protocolId, uint callId, uint errorCode,
        ushort extendedProtocolId = 0)
    {
        using var message = new MemoryStream();
        message.WriteByte(protocolId);
        if (protocolId == RmcRequest.ExtendedSentinel)
            WriteU16(message, extendedProtocolId);
        message.WriteByte(0);              // failure
        WriteU32(message, errorCode);
        WriteU32(message, callId);

        byte[] payload = message.ToArray();
        using var framed = new MemoryStream();
        WriteU32(framed, (uint)payload.Length);
        framed.Write(payload);
        return framed.ToArray();
    }

    public static byte[] BuildRmcResponse(byte protocolId, uint callId, uint methodId, byte[] result,
        ushort extendedProtocolId = 0)
    {
        using var message = new MemoryStream();
        message.WriteByte(protocolId);
        if (protocolId == RmcRequest.ExtendedSentinel)
            WriteU16(message, extendedProtocolId);
        message.WriteByte(1);              // success
        WriteU32(message, callId);
        WriteU32(message, methodId | 0x8000);
        message.Write(result);

        byte[] payload = message.ToArray();
        using var framed = new MemoryStream();
        WriteU32(framed, (uint)payload.Length);
        framed.Write(payload);
        return framed.ToArray();
    }
}

/// <summary>An RMC request decoded from a DATA payload.</summary>
internal readonly record struct RmcRequest(byte ProtocolId, uint CallId, uint MethodId, byte[] Parameters,
    ushort ExtendedProtocolId = 0)
{
    /// <summary>
    /// The protocol id as a service key: the extended u16 when the one-byte id
    /// is the 0x7F "extended" sentinel, otherwise the one-byte id. Dispatch and
    /// logging use this so a 0x7F-extended service (Splinter Cell Blacklist,
    /// Future Soldier) routes by its real id rather than all colliding on 127.
    /// </summary>
    public int ServiceId => ProtocolId == ExtendedSentinel ? ExtendedProtocolId : ProtocolId;

    /// <summary>The one-byte protocol id that means "a u16 extended id follows".</summary>
    public const byte ExtendedSentinel = 0x7F;

    /// <summary>
    /// Parses the RMC request framing: u32 size, protocol id with 0x80 set,
    /// an optional u16 extended protocol id
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out RmcRequest request)
    {
        request = default;
        if (data.Length < 13)
            return false;

        uint size = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (size + 4 > data.Length)
            return false;

        byte protocol = data[4];
        if ((protocol & 0x80) == 0)
            return false;

        byte protocolId = (byte)(protocol & 0x7F);
        int offset = 5;
        ushort extended = 0;
        if (protocolId == ExtendedSentinel)
        {
            if (size + 4 < 15)   // proto(1) + ext(2) + callId(4) + method(4)
                return false;
            extended = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
            offset += 2;
        }
        if (offset + 8 > (int)(size + 4))
            return false;

        request = new RmcRequest(
            protocolId,
            BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 4)..]),
            data[(offset + 8)..(int)(size + 4)].ToArray(),
            extended);
        return true;
    }

    /// <summary>Reads a 32-bit parameter at a byte offset.</summary>
    public bool TryReadU32(int offset, out uint value)
    {
        value = 0;
        if (offset < 0 || offset + 4 > Parameters.Length)
            return false;
        value = BinaryPrimitives.ReadUInt32LittleEndian(Parameters.AsSpan(offset));
        return true;
    }

    /// <summary>Reads a length-prefixed, null-terminated string parameter.</summary>
    public string? ReadString()
    {
        if (Parameters.Length < 2)
            return null;
        int length = BinaryPrimitives.ReadUInt16LittleEndian(Parameters);
        if (length < 1 || 2 + length > Parameters.Length)
            return null;
        return Encoding.UTF8.GetString(Parameters, 2, length - 1);
    }
}
