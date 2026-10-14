using Org.BouncyCastle.Crypto.Parameters;

namespace Plugin.UplayServer.Quazal.Playforward;

/// <summary>What a moderation command tells this server to do.</summary>
internal static class CommandActions
{
    /// <summary>Remove them from the match, with nothing stopping them coming back.</summary>
    public const string Kick = "kick";

    /// <summary>
    /// Remove them and refuse the re-join until it lapses. The distinction
    /// matters more here than it looks
    /// </summary>
    public const string Ban = "ban";

    public const string Mute = "mute";
    public const string Unmute = "unmute";

    /// <summary>
    /// Wipe the saved progress this server holds for them
    /// </summary>
    public const string ResetProgress = "reset_progress";

    /// <summary>
    /// Forget the account entirely
    /// </summary>
    public const string DeletePlayer = "delete_player";
}

/// <summary>
/// A moderation command, verified.
/// </summary>
/// <param name="ActionId">Playforward's id for it, and what an acknowledgement names.</param>
/// <param name="NativeSessionId">This server's own id for the match, echoed back.</param>
/// <param name="TargetAccountId">Who it lands on, in the identity both sides share.</param>
/// <param name="Action">Kick, ban, mute, unmute, reset_progress or delete_player.</param>
/// <param name="Reason">Why, in the moderator's own words.</param>
/// <param name="EnforceUntil"> When a ban or mute lapses. </param>
internal sealed record ServerCommand(
    Guid ActionId,
    string NativeSessionId,
    string TargetAccountId,
    string Action,
    string Reason,
    DateTimeOffset? EnforceUntil);

/// <summary>
/// Verifies the moderation commands Playforward issues to this server.
/// </summary>
internal sealed class ServerCommandVerifier
{
    private readonly SigningKeys _keys;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly TimeSpan _clockSkew;

    public ServerCommandVerifier(SigningKeys keys, string issuer, string audience, TimeSpan clockSkew)
    {
        _keys = keys;
        _issuer = issuer;
        _audience = audience;
        _clockSkew = clockSkew;
    }

    /// <summary>
    /// Verifies one command, or returns null with a logged reason.
    /// </summary>
    public ServerCommand? Verify(string token)
    {
        SignedJwt? jwt = SignedJwt.Verify(token, _keys, "command");
        if (jwt is null || !jwt.HasValidEnvelope(_issuer, _audience, _clockSkew, "command"))
            return null;

        string? rawActionId = jwt.String("action_id");
        string? target = jwt.String("target_account_id");
        string? action = jwt.String("action");
        if (!Guid.TryParse(rawActionId, out Guid actionId) ||
            string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(action))
        {
            Log.Warning("[playforward] rejected a command with no action id, target or action");
            return null;
        }

        if (action is not (CommandActions.Kick or CommandActions.Ban or CommandActions.Mute or CommandActions.Unmute
            or CommandActions.ResetProgress or CommandActions.DeletePlayer))
        {
            Log.Warning("[playforward] rejected a command with an unrecognised action {Action}", action);
            return null;
        }

        return new ServerCommand(
            ActionId: actionId,
            NativeSessionId: jwt.String("native_session_id") ?? string.Empty,
            TargetAccountId: target,
            Action: action,
            Reason: jwt.String("reason") ?? string.Empty,
            EnforceUntil: jwt.ReadUnixTime("enforce_until"));
    }
}
