using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Plugin.UplayServer.Quazal.Tests;

/// <summary>
/// A secure CONNECT is who a connection is. The inner ticket that proves it
/// used to be RC4 under a key built from constants in the source, with
/// nothing authenticating it
/// </summary>
public sealed class TicketGrantingTests
{
    private const uint Player = 2_000_001;
    private const uint Victim = 2_000_002;
    private const uint CheckValue = 0xF94C56FA;

    /// <summary>What the client learns from its ticket: the session key and the inner blob it forwards.</summary>
    private static (byte[] SessionKey, byte[] Inner) OpenAsClient(byte[] ticket, byte[] accountKey)
    {
        byte[] sealedTicket = ticket[..^16];
        Assert.Equal(HMACMD5.HashData(accountKey, sealedTicket), ticket[^16..]);
        byte[] plain = new Rc4Stream(accountKey).Process(sealedTicket);
        uint innerLength = BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(20));
        return (plain[..16], plain.AsSpan(24, (int)innerLength).ToArray());
    }

    /// <summary>The decrypted CONNECT payload a client sends: compression, Buffer(inner), Buffer(sealed request).</summary>
    internal static byte[] Connect(byte[] inner, byte[] sessionKey, uint claimedPid)
    {
        byte[] request = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(request, claimedPid);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(8), CheckValue);
        byte[] ciphertext = new Rc4Stream(sessionKey).Process(request);
        byte[] sealedRequest = [.. ciphertext, .. HMACMD5.HashData(sessionKey, ciphertext)];

        using var payload = new MemoryStream();
        payload.WriteByte(0);
        TicketGranting.WriteU32(payload, (uint)inner.Length);
        payload.Write(inner);
        TicketGranting.WriteU32(payload, (uint)sealedRequest.Length);
        payload.Write(sealedRequest);
        return payload.ToArray();
    }

    /// <summary>A ticket for a player, as the client holds it after RequestTicket.</summary>
    internal static (byte[] SessionKey, byte[] Inner) Issue(uint pid)
    {
        byte[] accountKey = TicketGranting.DeriveKey(pid, TicketGranting.PlayerTicketPassword);
        byte[] ticket = TicketGranting.BuildTicket(pid, "unused", TicketGranting.PlayerSecureServerPid, out _, accountKey);
        return OpenAsClient(ticket, accountKey);
    }

    [Fact]
    public void AGenuineTicketOpensTheSecureConnectionAsItsPlayer()
    {
        (byte[] sessionKey, byte[] inner) = Issue(Player);

        byte[]? reply = TicketGranting.BuildSecureConnectResponse(Connect(inner, sessionKey, Player), out uint pid, out uint check);
        Assert.NotNull(reply);
        Assert.Equal(Player, pid);
        Assert.Equal(CheckValue, check);
        Assert.Equal(CheckValue + 1, BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(5)));
    }

    [Fact]
    public void AClientCannotTurnItsOwnTicketIntoSomebodyElses()
    {
        (byte[] sessionKey, byte[] inner) = Issue(Player);

        // The old attack: the client knows the session key and pid its inner
        // ticket carries, so it rewrites the pid bytes in place.
        byte[] forged = (byte[])inner.Clone();
        int pidAt = forged.Length - 16 - sizeof(uint);
        uint delta = Player ^ Victim;
        for (int i = 0; i < sizeof(uint); i++)
            forged[pidAt + i] ^= (byte)(delta >> (8 * i));
        Assert.Null(TicketGranting.BuildSecureConnectResponse(Connect(forged, sessionKey, Victim), out _, out _));

        // Or keeps its genuine ticket and claims another pid in the request.
        Assert.Null(TicketGranting.BuildSecureConnectResponse(Connect(inner, sessionKey, Victim), out _, out _));
    }

    [Fact]
    public void AnInnerTicketThisServerDidNotSealIsRefused()
    {
        byte[] sessionKey = RandomNumberGenerator.GetBytes(16);

        // Invented bytes of the right length.
        byte[] invented = RandomNumberGenerator.GetBytes(TicketGranting.InnerTicketBytes);
        Assert.False(TicketGranting.TryOpenInnerTicket(invented, out _, out _));
        Assert.Null(TicketGranting.BuildSecureConnectResponse(Connect(invented, sessionKey, Victim), out _, out _));

        // The old 20-byte shape, sealed with the old constant-derived key.
        byte[] oldKey = TicketGranting.DeriveKey(TicketGranting.TrackingSecureServerPid, "Quazal Rendez-Vous");
        byte[] oldPlain = [.. sessionKey, .. BitConverter.GetBytes(Victim)];
        byte[] oldStyle = new Rc4Stream(oldKey).Process(oldPlain);
        Assert.Null(TicketGranting.BuildSecureConnectResponse(Connect(oldStyle, sessionKey, Victim), out _, out _));
    }

    [Fact]
    public void EveryTicketIsSealedAfresh()
    {
        // A fresh nonce each time: two tickets for the same player share no
        // bytes an observer could line up.
        (_, byte[] first) = Issue(Player);
        (_, byte[] second) = Issue(Player);
        Assert.NotEqual(first, second);
        Assert.Equal(TicketGranting.InnerTicketBytes, first.Length);
    }
}
