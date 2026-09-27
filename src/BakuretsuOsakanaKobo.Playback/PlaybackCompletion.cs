namespace BakuretsuOsakanaKobo.Playback;

internal static class PlaybackCompletion
{
    internal const long MinimumInspectableLengthMilliseconds = 2_000;
    internal const long MinimumCompletionToleranceMilliseconds = 1_000;
    internal const long MaximumCompletionToleranceMilliseconds = 5_000;
    private const double CompletionToleranceRatio = 0.02;

    internal static bool IsPrematureEnd(
        long playbackTimeMilliseconds,
        long lengthMilliseconds)
    {
        if (lengthMilliseconds < MinimumInspectableLengthMilliseconds)
        {
            return false;
        }

        var normalizedTime = Math.Clamp(playbackTimeMilliseconds, 0, lengthMilliseconds);
        var proportionalTolerance = (long)Math.Ceiling(
            lengthMilliseconds * CompletionToleranceRatio);
        var tolerance = Math.Clamp(
            proportionalTolerance,
            MinimumCompletionToleranceMilliseconds,
            MaximumCompletionToleranceMilliseconds);
        return normalizedTime < lengthMilliseconds - tolerance;
    }

    internal static long TerminalTime(
        long playbackTimeMilliseconds,
        long lengthMilliseconds) =>
        lengthMilliseconds > 0
            ? lengthMilliseconds
            : Math.Max(0, playbackTimeMilliseconds);

    internal static double ReplayPosition(
        bool completedPositionChanged,
        double completedSeekPosition) =>
        completedPositionChanged
            ? PlaybackPosition.Normalize(completedSeekPosition)
            : 0d;

    internal static long TimeAtPosition(double normalizedPosition, long lengthMilliseconds) =>
        lengthMilliseconds <= 0
            ? 0
            : (long)Math.Round(
                PlaybackPosition.Normalize(normalizedPosition) * lengthMilliseconds);
}
