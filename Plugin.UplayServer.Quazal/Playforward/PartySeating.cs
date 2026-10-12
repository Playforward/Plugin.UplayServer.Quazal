namespace Plugin.UplayServer.Quazal.Playforward;

/// <summary>
/// Seats the members of one Playforward party into the same Far Cry 3 match.
/// </summary>
internal static class PartySeating
{
    /// <summary>
    /// Whether party steering is switched on for this server. Set once when the
    /// plugin starts; the GameSession handlers are static and have no
    /// configuration of their own to read.
    /// </summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>
    /// The search handlers' entry point: reads the process-wide switch, takes
    /// the searching title's gateway (null when that title has no Playforward
    /// half), and defers to the testable overload.
    /// </summary>
    public static IReadOnlyList<(uint SessionId, uint HostPid, IReadOnlyList<string> HostUrls,
        IReadOnlyDictionary<uint, int> Properties)> Steer(
        PlayforwardGateway? gateway,
        uint searchingPid,
        IReadOnlyList<(uint SessionId, uint HostPid, IReadOnlyList<string> HostUrls,
            IReadOnlyDictionary<uint, int> Properties)> candidates) =>
        Steer(gateway, Enabled, searchingPid, candidates);

    /// <summary>
    /// Narrows a search result to the party's own session, when the searcher is
    /// in a party and one of its members is already hosting.
    /// </summary>
    public static IReadOnlyList<(uint SessionId, uint HostPid, IReadOnlyList<string> HostUrls,
        IReadOnlyDictionary<uint, int> Properties)> Steer(
        PlayforwardGateway? gateway,
        bool enabled,
        uint searchingPid,
        IReadOnlyList<(uint SessionId, uint HostPid, IReadOnlyList<string> HostUrls,
            IReadOnlyDictionary<uint, int> Properties)> candidates)
    {
        if (!enabled || gateway is null || candidates.Count == 0 || searchingPid == 0)
            return candidates;

        string partyRef = gateway.ForPid(searchingPid)?.Identity.PartyRef ?? string.Empty;
        if (string.IsNullOrEmpty(partyRef))
            return candidates;

        foreach (var candidate in candidates)
        {
            if (candidate.HostPid == searchingPid)
                continue;

            string hostParty = gateway.ForPid(candidate.HostPid)?.Identity.PartyRef ?? string.Empty;
            if (!string.Equals(hostParty, partyRef, StringComparison.Ordinal))
                continue;

            Log.Information("[playforward] seating pid={Pid} into their party's session {SessionId} (party {PartyRef})",
                searchingPid, candidate.SessionId, partyRef);
            return [candidate];
        }

        Log.Debug("[playforward] pid={Pid} is in party {PartyRef} but no member is hosting yet; leaving the search alone",
            searchingPid, partyRef);
        return candidates;
    }
}
