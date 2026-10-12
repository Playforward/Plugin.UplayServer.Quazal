namespace Plugin.UplayServer.Quazal.Tests;

/// <summary>
/// The rule that a player is in one match at a time.
/// </summary>
[Collection("OnlineDataStore")]
public class GameSessionStoreTests
{
    /// <summary>
    /// Whether the store still holds a session at all, as distinct from holding
    /// an empty one. Both answer nothing from GetParticipants, and the whole
    /// point of the teardown is the difference between them.
    /// </summary>
    private static bool SessionExists(uint typeId, uint sessionId) =>
        GameSessionStore.Snapshot(TestTitle.Id).Any(s => s.TypeId == typeId && s.SessionId == sessionId);

    [Fact]
    public void JoiningAMatchLeavesTheOneBefore()
    {
        const uint type = 900;
        GameSessionStore.CreateSession(TestTitle.Id, type,sessionId: 1, hostPid: 2000001);
        GameSessionStore.AddParticipants(TestTitle.Id, type,1, [2000001]);

        GameSessionStore.CreateSession(TestTitle.Id, type,sessionId: 2, hostPid: 2000002);
        GameSessionStore.AddParticipants(TestTitle.Id, type,2, [2000002]);

        // The player abandons their own match and joins the other. The game
        // never says so that is the case this exists for.
        GameSessionStore.AddParticipants(TestTitle.Id, type,2, [2000001]);

        Assert.Equal([2000001u, 2000002u], GameSessionStore.GetParticipants(TestTitle.Id, type, 2).Order());
        Assert.Empty(GameSessionStore.GetParticipants(TestTitle.Id, type, 1));

        // And the match they walked out of is gone, not merely empty: an empty
        // session is still reported, still browsable, still offered to the next
        // player who searches.
        Assert.False(SessionExists(type, 1));
    }

    [Fact]
    public void TheAbandonedMatchStopsClaimingThemEvenWhileItLivesOn()
    {
        const uint type = 901;
        GameSessionStore.CreateSession(TestTitle.Id, type,sessionId: 1, hostPid: 2000001);
        GameSessionStore.AddParticipants(TestTitle.Id, type,1, [2000001, 2000003]);

        GameSessionStore.CreateSession(TestTitle.Id, type,sessionId: 2, hostPid: 2000002);
        GameSessionStore.AddParticipants(TestTitle.Id, type,2, [2000002]);
        GameSessionStore.AddParticipants(TestTitle.Id, type,2, [2000003]);

        // The old match survives with whoever is still in it, which is the
        // point: leaving is personal, and the rest of that lobby carries on.
        Assert.Equal([2000001u], GameSessionStore.GetParticipants(TestTitle.Id, type, 1));
        Assert.Equal(2, GameSessionStore.GetParticipantCount(TestTitle.Id, type, 2));
    }

    [Fact]
    public void ReAddingSomebodyAlreadyThereDisturbsNothing()
    {
        const uint type = 902;
        GameSessionStore.CreateSession(TestTitle.Id, type,sessionId: 1, hostPid: 2000001);
        GameSessionStore.AddParticipants(TestTitle.Id, type,1, [2000001, 2000002]);

        // The reporter re-lists a live roster every few seconds, and a game
        // re-announces its own participants. Neither is anybody moving.
        GameSessionStore.AddParticipants(TestTitle.Id, type,1, [2000001, 2000002]);
        GameSessionStore.AddParticipants(TestTitle.Id, type,1, [2000002]);

        Assert.Equal(2, GameSessionStore.GetParticipantCount(TestTitle.Id, type, 1));
    }

    [Fact]
    public void TheLastPlayerLeavingEndsTheMatch()
    {
        const uint type = 903;
        GameSessionStore.CreateSession(TestTitle.Id, type,sessionId: 1, hostPid: 2000001);
        GameSessionStore.AddParticipants(TestTitle.Id, type,1, [2000001, 2000002]);

        // One of two leaving is a match carrying on a player down.
        GameSessionStore.RemoveParticipants(TestTitle.Id, type, 1, [2000002]);
        Assert.True(SessionExists(type, 1));

        GameSessionStore.RemoveParticipants(TestTitle.Id, type, 1, [2000001]);
        Assert.False(SessionExists(type, 1));
    }

    [Fact]
    public void ANewlyCreatedMatchSurvivesUntilItsHostArrives()
    {
        const uint type = 904;

        // A session is empty for the moment between being created and its host
        // being seated. Tearing down on emptiness alone would delete every
        // match at birth, so only a departure can end one.
        GameSessionStore.CreateSession(TestTitle.Id, type,sessionId: 1, hostPid: 2000001);
        Assert.True(SessionExists(type, 1));

        GameSessionStore.AddParticipants(TestTitle.Id, type,1, [2000001]);
        Assert.True(SessionExists(type, 1));
    }

    [Fact]
    public void KickingTheLastPlayerEndsTheMatchToo()
    {
        const uint type = 905;
        GameSessionStore.CreateSession(TestTitle.Id, type,sessionId: 1, hostPid: 2000001);
        GameSessionStore.AddParticipants(TestTitle.Id, type,1, [2000001]);

        // A moderator's kick empties a one-player lobby, and what it leaves
        // behind should not be an empty lobby advertising itself.
        Assert.Equal(1, GameSessionStore.RemovePlayerEverywhere(TestTitle.Id, 2000001));
        Assert.False(SessionExists(type, 1));
    }

    [Fact]
    public void ATitleNeverSeesAnotherTitlesMatches()
    {
        // Playforward allocates pids per title from the same starting number,
        // so the same pid is two different players in two games. Neither
        // game's search, snapshot or kick may reach the other's session.
        const uint type = 906;
        const string other = "splinter-cell-blacklist";
        GameSessionStore.CreateSession(TestTitle.Id, type, sessionId: 1, hostPid: 2000001);
        GameSessionStore.AddParticipants(TestTitle.Id, type, 1, [2000001]);
        GameSessionStore.CreateSession(other, type, sessionId: 2, hostPid: 2000001);
        GameSessionStore.AddParticipants(other, type, 2, [2000001]);

        // Joining in one title did not evict the "same" pid from the other.
        Assert.Equal([2000001u], GameSessionStore.GetParticipants(TestTitle.Id, type, 1));
        Assert.Equal([2000001u], GameSessionStore.GetParticipants(other, type, 2));

        Assert.Single(GameSessionStore.FindByType(TestTitle.Id, type));
        Assert.Single(GameSessionStore.Snapshot(other), s => s.SessionId == 2);
        Assert.DoesNotContain(GameSessionStore.Snapshot(TestTitle.Id), s => s.TypeId == type && s.SessionId == 2);

        // A kick in the other title leaves this title's player where they are.
        Assert.Equal(1, GameSessionStore.RemovePlayerEverywhere(other, 2000001));
        Assert.True(SessionExists(type, 1));
        GameSessionStore.DeleteSession(TestTitle.Id, type, 1);
    }

    [Fact]
    public void TheSameSessionIdInTwoTitlesIsTwoSessions()
    {
        // Session ids are sequential from 1, so two titles meet on the same
        // (type, id). Keyed without the title, the second overwrote the first,
        // and a client of one game changed the other's match.
        const uint type = 907;
        const string other = "splinter-cell-blacklist";
        GameSessionStore.CreateSession(TestTitle.Id, type, sessionId: 1, hostPid: 2000001);
        GameSessionStore.AddParticipants(TestTitle.Id, type, 1, [2000001]);
        GameSessionStore.CreateSession(other, type, sessionId: 1, hostPid: 2000009);

        Assert.True(GameSessionStore.IsHost(TestTitle.Id, type, 1, 2000001));
        Assert.True(GameSessionStore.IsHost(other, type, 1, 2000009));
        Assert.Equal([2000001u], GameSessionStore.GetParticipants(TestTitle.Id, type, 1));

        GameSessionStore.SetProperties(other, type, 1, new Dictionary<uint, int> { [0x64] = 7 });
        Assert.DoesNotContain(0x64u, GameSessionStore.Snapshot(TestTitle.Id).Single(s => s.TypeId == type).Properties.Keys);

        GameSessionStore.DeleteSession(TestTitle.Id, type, 1);
        GameSessionStore.DeleteSession(other, type, 1);
    }

    [Fact]
    public void OnlyTheHostChangesAnotherPlayersSeat()
    {
        const uint type = 908;
        GameSessionStore.CreateSession(TestTitle.Id, type, sessionId: 1, hostPid: 2000001);
        GameSessionStore.AddParticipants(TestTitle.Id, type, 1, [2000001, 2000002, 2000003]);

        Assert.True(GameSessionStore.MayChangeRoster(TestTitle.Id, type, 1, 2000001, [2000002, 2000003]));
        Assert.True(GameSessionStore.MayChangeRoster(TestTitle.Id, type, 1, 2000002, [2000002]));
        Assert.False(GameSessionStore.MayChangeRoster(TestTitle.Id, type, 1, 2000002, [2000003]));
        Assert.False(GameSessionStore.MayChangeRoster(TestTitle.Id, type, 1, 2000002, [2000002, 2000001]));

        // Nothing can be done to a session that does not exist -- not even
        // seating yourself, which would conjure a hostless lobby.
        Assert.False(GameSessionStore.MayChangeRoster(TestTitle.Id, type, 99, 2000002, [2000002]));
        GameSessionStore.DeleteSession(TestTitle.Id, type, 1);
    }

    [Fact]
    public void OnlySomebodyInTheSessionCanMigrateIt()
    {
        const uint type = 909;
        GameSessionStore.CreateSession(TestTitle.Id, type, sessionId: 1, hostPid: 2000001);
        GameSessionStore.AddParticipants(TestTitle.Id, type, 1, [2000001, 2000002]);

        // An outsider copying the roster into a match they host.
        Assert.False(GameSessionStore.MigrateSession(TestTitle.Id, type, 1, newSessionId: 2, newHost: 2000009));
        Assert.False(SessionExists(type, 2));
        Assert.False(GameSessionStore.MigrateSession(TestTitle.Id, type, 98, newSessionId: 3, newHost: 2000002));

        // The player left behind when the host goes.
        Assert.True(GameSessionStore.MigrateSession(TestTitle.Id, type, 1, newSessionId: 4, newHost: 2000002));
        Assert.True(GameSessionStore.IsHost(TestTitle.Id, type, 4, 2000002));

        GameSessionStore.DeleteSession(TestTitle.Id, type, 1);
        GameSessionStore.DeleteSession(TestTitle.Id, type, 4);
    }
}
