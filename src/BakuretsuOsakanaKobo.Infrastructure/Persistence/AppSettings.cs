namespace BakuretsuOsakanaKobo.Infrastructure.Persistence;

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public double? ThumbnailIntervalPercent { get; init; }

    public static bool IsValid(AppSettings settings) =>
        settings.SchemaVersion == CurrentSchemaVersion &&
        (settings.ThumbnailIntervalPercent is null ||
         ThumbnailGenerationInterval.IsValid(settings.ThumbnailIntervalPercent.Value));
}

public static class ThumbnailGenerationInterval
{
    public const double MinimumPercent = 0.25;
    public const double MaximumPercent = 5.0;
    public const double StepPercent = 0.25;
    public const double DefaultPercent = 1.0;

    public static bool IsValid(double percent)
    {
        if (!double.IsFinite(percent) ||
            percent < MinimumPercent ||
            percent > MaximumPercent)
        {
            return false;
        }

        var steps = (percent - MinimumPercent) / StepPercent;
        return Math.Abs(steps - Math.Round(steps)) < 0.000_001;
    }

    public static void EnsureValid(double percent, string? parameterName = null)
    {
        if (!IsValid(percent))
        {
            throw new ArgumentOutOfRangeException(
                parameterName ?? nameof(percent),
                percent,
                $"Thumbnail interval must be {MinimumPercent:0.00} to {MaximumPercent:0.00} percent in {StepPercent:0.00}-percent steps.");
        }
    }
}
