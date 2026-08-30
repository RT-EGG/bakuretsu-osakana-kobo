namespace BakuretsuOsakanaKobo;

internal static class UpdateCheckSchedule
{
    internal static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromHours(24);

    internal static bool IsAutomaticCheckDue(
        DateTimeOffset nowUtc,
        DateTimeOffset? lastAttemptUtc)
    {
        if (nowUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The current timestamp must use UTC.", nameof(nowUtc));
        }

        if (lastAttemptUtc is null)
        {
            return true;
        }

        if (lastAttemptUtc.Value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The previous attempt timestamp must use UTC.", nameof(lastAttemptUtc));
        }

        var elapsed = nowUtc - lastAttemptUtc.Value;
        return elapsed < TimeSpan.Zero || elapsed >= AutomaticCheckInterval;
    }
}
