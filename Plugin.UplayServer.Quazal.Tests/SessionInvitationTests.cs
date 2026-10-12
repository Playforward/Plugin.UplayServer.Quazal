namespace Plugin.UplayServer.Quazal.Tests;

/// <summary>
/// Game invitations as the community server handled them on 2026-09-17
/// (PC pid 2643 inviting the Deck, pid 2591): the name lookup, the
/// invitation, the notification the invitee receives, the session it then
/// asks for, and host migration.
/// </summary>
[Collection("OnlineDataStore")]
public sealed class SessionInvitationTests
{
    private const uint Pc = 2643;
    private const uint Deck = 2591;

    [Fact]
    public void TheNotificationIsEncodedAsTheWorkingServerSendsIt()
    {
        // 14/1 as the Deck received it: pidSource 2643, type 7005, param1 0,
        // param2 session 0x0108d9, empty string, param3 1.
        Assert.Equal("530A00005D1B000000000000D908010001000001000000",
            Convert.ToHexString(GameSession.NotificationEvent(Pc, GameSession.NotificationInvited, 0, 0x0108d9, 1)));
        // ... and 7003 as the PC received it when the Deck's host added it.
        Assert.Equal("1F0A00005B1B0000530A0000D208010001000001000000",
            Convert.ToHexString(GameSession.NotificationEvent(Deck, GameSession.NotificationAddedToSession, Pc, 0x0108d2, 1)));
    }

    [Fact]
    public void AnInvitationIsDeliveredToEachInviteeAndNotToEveryone()
    {
        var sent = new List<(uint Pid, byte Protocol, uint Method, byte[] Parameters)>();
        bool Notify(uint pid, byte protocol, uint method, byte[] parameters) { sent.Add((pid, protocol, method, parameters)); return true; }

        // 42/12 as the PC sent it: type 1, session 0x0108d9, [2591], 1, "".
        byte[] reply = GameSession.BuildSendInvitationResult(
            new RmcRequest(GameSession.ProtocolId, 1, GameSession.MethodSendInvitation, Convert.FromHexString("01000000d9080100010000001f0a0000010000")), Pc, Notify);
        Assert.Empty(reply);
        (uint pid, byte protocol, uint method, byte[] parameters) = Assert.Single(sent);
        Assert.Equal(Deck, pid);
        Assert.Equal(14, protocol);
        Assert.Equal(1u, method);
        Assert.Equal(GameSession.NotificationEvent(Pc, 7005, 0, 0x0108d9, 1), parameters);

        // The Deck's "invite everyone" (pid 0xFFFFFFFF) reaches nobody, as there.
        sent.Clear();
        GameSession.BuildSendInvitationResult(
            new RmcRequest(GameSession.ProtocolId, 1, GameSession.MethodSendInvitation, Convert.FromHexString("01000000da08010001000000ffffffff010000")), Deck, Notify);
        Assert.Empty(sent);
    }

    [Fact]
    public void TheInviteeGetsTheSessionAndAJoinerIsToldTheyWereAdded()
    {
        var sent = new List<(uint Pid, byte[] Parameters)>();
        bool Notify(uint pid, byte protocol, uint method, byte[] parameters) { sent.Add((pid, parameters)); return true; }

        byte[] created = GameSession.BuildCreateSessionResult(
            new RmcRequest(GameSession.ProtocolId, 1, GameSession.MethodCreateSession, Convert.FromHexString("0100000001000000640000000800000000")), Pc, TestTitle.Id)!;
        uint sessionId = BitConverter.ToUInt32(created, 4);
        GameSession.BuildAddParticipantsResult(
            new RmcRequest(GameSession.ProtocolId, 1, GameSession.MethodAddParticipants, Convert.FromHexString(Key(sessionId) + "01000000" + Hex(Pc) + "00000000")), Pc, Notify, TestTitle.Id);
        Assert.Empty(sent);    // the host adding itself is nobody to tell

        // GetSession: the one record, as a search would list it but bare.
        byte[] record = GameSession.BuildGetSessionResult(new RmcRequest(GameSession.ProtocolId, 1, GameSession.MethodGetSession, Convert.FromHexString(Key(sessionId))), TestTitle.Id)!;
        Assert.Equal(1u, BitConverter.ToUInt32(record, 0));
        Assert.Equal(sessionId, BitConverter.ToUInt32(record, 4));
        Assert.Equal(Pc, BitConverter.ToUInt32(record, 8));
        Assert.Null(GameSession.BuildGetSessionResult(new RmcRequest(GameSession.ProtocolId, 1, GameSession.MethodGetSession, Convert.FromHexString(Key(sessionId + 1000))), TestTitle.Id));

        // The host adds the invitee: the invitee is told, from the host.
        GameSession.BuildAddParticipantsResult(
            new RmcRequest(GameSession.ProtocolId, 1, GameSession.MethodAddParticipants, Convert.FromHexString(Key(sessionId) + "01000000" + Hex(Deck) + "00000000")), Pc, Notify, TestTitle.Id);
        (uint pid, byte[] parameters) = Assert.Single(sent);
        Assert.Equal(Deck, pid);
        Assert.Equal(GameSession.NotificationEvent(Pc, 7003, Deck, sessionId, 1), parameters);

        // The host leaves; the Deck migrates and gets a new key with the
        // same players, as 42/4 answered (typeId, new id).
        byte[] migrated = GameSession.BuildMigrateSessionResult(new RmcRequest(GameSession.ProtocolId, 1, GameSession.MethodMigrateSession, Convert.FromHexString(Key(sessionId))), Deck, TestTitle.Id)!;
        uint newId = BitConverter.ToUInt32(migrated, 4);
        Assert.NotEqual(sessionId, newId);
        Assert.Equal(2, GameSessionStore.GetParticipantCount(TestTitle.Id, 1, newId));
        GameSessionStore.DeleteSession(TestTitle.Id, 1, sessionId);
        GameSessionStore.DeleteSession(TestTitle.Id, 1, newId);
    }

    [Fact]
    public void APlayerCannotReachIntoSomebodyElsesMatch()
    {
        // Session ids are sequential, so any client can name any match. The
        // host is the Pc; the Deck is a player; the stranger is in neither.
        const uint stranger = 2700;
        bool Notify(uint pid, byte protocol, uint method, byte[] parameters) => true;
        uint sessionId = BitConverter.ToUInt32(GameSession.BuildCreateSessionResult(
            new RmcRequest(GameSession.ProtocolId, 1, GameSession.MethodCreateSession, Convert.FromHexString("0100000001000000640000000800000000")), Pc, TestTitle.Id)!, 4);
        GameSession.BuildAddParticipantsResult(Participants(GameSession.MethodAddParticipants, sessionId, Pc, Deck), Pc, Notify, TestTitle.Id);

        // Removing other people, changing settings, and copying the roster
        // into a match the stranger hosts are all refused...
        GameSession.BuildRemoveParticipantsResult(Participants(GameSession.MethodRemoveParticipants, sessionId, Pc, Deck), stranger, TestTitle.Id);
        GameSession.BuildRemoveParticipantsResult(Participants(GameSession.MethodRemoveParticipants, sessionId, Pc), Deck, TestTitle.Id);
        Assert.Equal(2, GameSessionStore.GetParticipantCount(TestTitle.Id, 1, sessionId));

        GameSession.BuildUpdateSessionResult(
            new RmcRequest(GameSession.ProtocolId, 1, GameSession.MethodUpdateSession, Convert.FromHexString(Key(sessionId) + "01000000" + "64000000" + "63000000")), Deck, TestTitle.Id);
        Assert.Equal(8, GameSessionStore.FindByType(TestTitle.Id, 1).Single(s => s.SessionId == sessionId).Properties[0x64]);

        Assert.Null(GameSession.BuildMigrateSessionResult(
            new RmcRequest(GameSession.ProtocolId, 1, GameSession.MethodMigrateSession, Convert.FromHexString(Key(sessionId))), stranger, TestTitle.Id));

        // ...and seating somebody else is the host's call too.
        GameSession.BuildAddParticipantsResult(Participants(GameSession.MethodAddParticipants, sessionId, stranger), Deck, Notify, TestTitle.Id);
        Assert.Equal(2, GameSessionStore.GetParticipantCount(TestTitle.Id, 1, sessionId));

        // What stays allowed: the host changes settings, a player leaves.
        GameSession.BuildUpdateSessionResult(
            new RmcRequest(GameSession.ProtocolId, 1, GameSession.MethodUpdateSession, Convert.FromHexString(Key(sessionId) + "01000000" + "64000000" + "63000000")), Pc, TestTitle.Id);
        Assert.Equal(0x63, GameSessionStore.FindByType(TestTitle.Id, 1).Single(s => s.SessionId == sessionId).Properties[0x64]);
        GameSession.BuildRemoveParticipantsResult(Participants(GameSession.MethodRemoveParticipants, sessionId, Deck), Deck, TestTitle.Id);
        Assert.Equal(1, GameSessionStore.GetParticipantCount(TestTitle.Id, 1, sessionId));

        GameSessionStore.DeleteSession(TestTitle.Id, 1, sessionId);
    }

    /// <summary>An Add/RemoveParticipants request as captured: key, count, (pid, unknown) each, trailing byte.</summary>
    private static RmcRequest Participants(uint method, uint sessionId, params uint[] pids) =>
        new(GameSession.ProtocolId, 1, method, Convert.FromHexString(
            Key(sessionId) + Hex((uint)pids.Length) + string.Concat(pids.Select(pid => Hex(pid) + "00000000")) + "00"));

    [Fact]
    public void AFriendIsLookedUpByName()
    {
        byte[] body = FriendsCustom.BuildLookupByNameResult(
            new RmcRequest(FriendsCustom.ProtocolId, 1, FriendsCustom.MethodLookupByName, Convert.FromHexString("0a0064796c6c616e35303000")),
            name => name == "dyllan500" ? Deck : null);
        Assert.Equal(Deck, BitConverter.ToUInt32(body, 0));
        body = FriendsCustom.BuildLookupByNameResult(
            new RmcRequest(FriendsCustom.ProtocolId, 1, FriendsCustom.MethodLookupByName, Convert.FromHexString("0700737472616e6765727300")), _ => null);
        Assert.Equal(0u, BitConverter.ToUInt32(body, 0));
    }

    private static string Key(uint sessionId) => "01000000" + Hex(sessionId);
    private static string Hex(uint value) => Convert.ToHexString(BitConverter.GetBytes(value));
}
