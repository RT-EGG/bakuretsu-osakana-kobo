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
}
