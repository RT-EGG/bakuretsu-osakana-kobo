namespace BakuretsuOsakanaKobo.Spikes.LibVlcWpf;

internal sealed record PerformanceValidationOptions(
    string VideoPath,
    string ReportPath,
    TimeSpan Timeout,
    TimeSpan IdleSamplingDelay,
    TimeSpan SamplingDelay)
{
    public static PerformanceValidationOptions? TryParse(string[] args)
    {
        string? videoPath = null;
        string? reportPath = null;

        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--video", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                videoPath = args[++index];
            }
            else if (string.Equals(args[index], "--performance-report", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                reportPath = args[++index];
            }
        }

        if (string.IsNullOrWhiteSpace(videoPath) || string.IsNullOrWhiteSpace(reportPath))
        {
            return null;
        }

        return new PerformanceValidationOptions(
            videoPath,
            reportPath,
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(3));
    }
}

internal sealed class PerformanceValidationReport
{
    public bool Success { get; set; }
    public string VideoPath { get; set; } = string.Empty;
    public string? Error { get; set; }
    public string? HardwareDecoder { get; set; }
    public double ProcessStartToWindowLoadedMs { get; set; }
    public double ProcessStartToDispatcherReadyMs { get; set; }
    public double OpenRequestedProcessElapsedMs { get; set; }
    public double PlaybackReadyProcessElapsedMs { get; set; }
    public double IdleSamplingDelayMs { get; set; }
    public double PlaybackSamplingDelayMs { get; set; }
    public double OpenToPlayingMs { get; set; }
    public double OpenToFirstPlaybackClockMs { get; set; }
    public double OpenToVideoOutputMs { get; set; }
    public double OpenToAudioOutputMs { get; set; }
    public double Seek50PercentMs { get; set; }
    public double Seek90PercentMs { get; set; }
    public bool RateAccepted { get; set; }
    public double RateChangeReflectMs { get; set; }
    public float ObservedRate { get; set; }
    public double VolumeChangeReflectMs { get; set; }
    public int ObservedVolumePercent { get; set; }
    public long WorkingSetBytes { get; set; }
    public long PeakWorkingSetBytes { get; set; }
}
