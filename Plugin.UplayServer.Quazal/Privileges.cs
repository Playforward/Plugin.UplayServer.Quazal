namespace Plugin.UplayServer.Quazal;

/// <summary>Privileges (protocol 35).</summary>
internal static class Privileges
{
    public const byte ProtocolId = 35;
    public const uint MethodGetPrivileges = 1;
    public const uint MethodGetExpiredPrivileges = 5;
    public const uint MethodGetPrivilegesEx = 6;

    /// <summary>
    /// The 23 privileges a working server grants every account 
    /// </summary>
    private static readonly (uint Id, string Name)[] Granted =
    [
        (1001, "Hurk_020"), (1002, "Hurk_030"), (1003, "Hurk_040"), (1004, "Hurk_050"),
        (1005, "ULC_010"), (1006, "ULC_020"), (1007, "ULC_030-Uplay"),
        (1008, "Brutal_End"), (1009, "Humiliate_End"),
        (2001, "Exclusive_SP_Tattoos"), (2002, "Customizable_MP_Tattoos"), (2003, "Collector_Knife"), (2004, "MP_Tattoos_Editor"),
        (3001, "Rare_Animal_1"), (3002, "Rare_Animal_2"), (3003, "Rare_Animal_3"), (3004, "Rare_Animal_4"),
        (3005, "Hunting_Skins_1"), (3006, "Hunting_Skins_2"), (3007, "Hunting_Skins_3"), (3008, "Predator_Gun"),
        (4001, "Exclusive_Uplay_Theme"), (4002, "Customizable_MP_Tattoos_Uplay"),
    ];

    /// <summary>
    /// GetPrivileges takes a locale code and returns a
    /// Map&lt;Uint32, Privilege&gt;
    /// </summary>
    public static byte[] BuildGetPrivilegesResult(RmcRequest request)
    {
        using var body = new MemoryStream();
        TicketGranting.WriteU32(body, (uint)Granted.Length);
        foreach ((uint id, string name) in Granted)
        {
            TicketGranting.WriteU32(body, id);
            TicketGranting.WriteU32(body, id);
            TicketGranting.WriteString(body, name);
        }
        Serilog.Log.Information(
            "[quazal] Privileges.GetPrivileges -> {Count} privileges, as the working server grants ({Parameters})",
            Granted.Length, Convert.ToHexString(request.Parameters));
        return body.ToArray();
    }

    /// <summary>
    /// GetExpiredPrivileges and GetPrivilegesEx both return
    /// List&lt;PrivilegeEx&gt;; an account with no privileges has none of either.
    /// </summary>
    public static byte[] BuildEmptyPrivilegeListResult(RmcRequest request, string method)
    {
        Serilog.Log.Information("[quazal] Privileges.{Method} -> 0 privileges", method);
        return Quazal.EmptyList;
    }
}
