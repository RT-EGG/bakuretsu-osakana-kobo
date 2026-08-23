namespace BakuretsuOsakanaKobo.Infrastructure.Persistence;

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public double? ThumbnailIntervalPercent { get; init; }

    public double? ThumbnailPreviewWidthPercent { get; init; }

    public DateTimeOffset? LastAutomaticUpdateCheckAttemptUtc { get; init; }

    public static bool IsValid(AppSettings settings) =>
        settings.SchemaVersion == CurrentSchemaVersion &&
        (settings.ThumbnailIntervalPercent is null ||
         ThumbnailGenerationInterval.IsValid(settings.ThumbnailIntervalPercent.Value)) &&
        (settings.ThumbnailPreviewWidthPercent is null ||
         ThumbnailPreviewSize.IsValid(settings.ThumbnailPreviewWidthPercent.Value)) &&
        (settings.LastAutomaticUpdateCheckAttemptUtc is null ||
         settings.LastAutomaticUpdateCheckAttemptUtc.Value.Offset == TimeSpan.Zero);
}

public static class ThumbnailPreviewSize
{
    public const double MinimumPercent = 5;
    public const double MaximumPercent = 30;
    public const double StepPercent = 1;
    public const double DefaultPercent = 15;
    public const double MinimumWidth = 60;
    public const double MaximumWidth = 320;

    public static bool IsValid(double percent) =>
        double.IsFinite(percent) &&
        percent >= MinimumPercent &&
        percent <= MaximumPercent &&
        Math.Abs(percent - Math.Round(percent)) < 0.000_001;

    public static void EnsureValid(double percent, string? parameterName = null)
    {
        if (!IsValid(percent))
        {
            throw new ArgumentOutOfRangeException(
                parameterName ?? nameof(percent),
                percent,
                $"Thumbnail preview width must be {MinimumPercent:0} to {MaximumPercent:0} percent in {StepPercent:0}-percent steps.");
        }
    }

    public static double ResolveWidth(double windowWidth, double percent)
    {
        EnsureValid(percent, nameof(percent));
        if (!double.IsFinite(windowWidth) || windowWidth <= 0)
        {
            return MinimumWidth;
        }

        return Math.Clamp(windowWidth * percent / 100, MinimumWidth, MaximumWidth);
    }
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
