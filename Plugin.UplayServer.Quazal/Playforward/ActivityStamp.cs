namespace Plugin.UplayServer.Quazal.Playforward;

/// <summary>
/// The last time something showed its owner was still around, for state that
/// ends when they go quiet: a launch, a login binding.
/// </summary>
internal sealed class ActivityStamp(DateTimeOffset now)
{
    private long _ticks = now.UtcTicks;

    /// <summary>When it was last touched.</summary>
    public DateTimeOffset Value => new(Volatile.Read(ref _ticks), TimeSpan.Zero);

    /// <summary>Records a sign of life.</summary>
    public void Touch(DateTimeOffset now) => Volatile.Write(ref _ticks, now.UtcTicks);

    /// <summary>Whether nothing has touched it for longer than <paramref name="timeout"/>.</summary>
    public bool IdleLongerThan(TimeSpan timeout, DateTimeOffset now) => now - Value > timeout;
}
