using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace BakuretsuOsakanaKobo;

internal sealed class StartupPerformanceTrace
{
    internal const string ReportPathEnvironmentVariable = "BOK_STARTUP_TRACE_PATH";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly object _gate = new();
    private readonly string _reportPath;
    private readonly DateTimeOffset _processStartUtc;
    private readonly Func<double> _elapsedMilliseconds;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly bool _hasFileArgument;
    private readonly Dictionary<string, double> _milestones = new(StringComparer.Ordinal);
    private bool _reportWritten;

    internal StartupPerformanceTrace(
        string reportPath,
        DateTimeOffset processStartUtc,
        bool hasFileArgument,
        Func<double>? elapsedMilliseconds = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportPath);
        if (processStartUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The process start time must be UTC.", nameof(processStartUtc));
        }

        _reportPath = Path.GetFullPath(reportPath);
        _processStartUtc = processStartUtc;
        _hasFileArgument = hasFileArgument;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        if (elapsedMilliseconds is null)
        {
            var traceStartedUtc = DateTimeOffset.UtcNow;
            var processStartToTrace = Math.Max(0, (traceStartedUtc - processStartUtc).TotalMilliseconds);
            var traceClock = Stopwatch.StartNew();
            _elapsedMilliseconds = () => processStartToTrace + traceClock.Elapsed.TotalMilliseconds;
        }
        else
        {
            _elapsedMilliseconds = elapsedMilliseconds;
        }
    }

    internal static StartupPerformanceTrace? TryCreate(IReadOnlyList<string> launchArguments)
    {
        var reportPath = Environment.GetEnvironmentVariable(ReportPathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(reportPath))
        {
            return null;
        }

        try
        {
            using var process = Process.GetCurrentProcess();
            var processStartUtc = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            return new StartupPerformanceTrace(
                reportPath,
                processStartUtc,
                launchArguments.Count == 1);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or InvalidOperationException or
                NotSupportedException or UnauthorizedAccessException or
                System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    internal void Record(string milestone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(milestone);
        lock (_gate)
        {
            if (_reportWritten || _milestones.ContainsKey(milestone))
            {
                return;
            }

            _milestones.Add(
                milestone,
                Math.Max(0, _elapsedMilliseconds()));
            TryWriteReport(complete: HasCompletedStartup());
        }
    }

    internal void WriteIncompleteOnExit()
    {
        lock (_gate)
        {
            if (_reportWritten)
            {
                return;
            }

            if (!_milestones.ContainsKey("applicationExitStarted"))
            {
                _milestones.Add(
                    "applicationExitStarted",
                    Math.Max(0, _elapsedMilliseconds()));
            }

            TryWriteReport(complete: false);
        }
    }

    private bool HasCompletedStartup() =>
        _milestones.ContainsKey("contentRendered") &&
        _milestones.ContainsKey("dispatcherIdle") &&
        _milestones.ContainsKey("initialLaunchHandled") &&
        _milestones.ContainsKey("automaticUpdateCheckStarted");

    private void TryWriteReport(bool complete)
    {
        if (!complete && !_milestones.ContainsKey("applicationExitStarted"))
        {
            return;
        }

        if (complete && !_milestones.ContainsKey("interactiveReady"))
        {
            _milestones.Add(
                "interactiveReady",
                new[]
                {
                    Value("contentRendered"),
                    Value("dispatcherIdle"),
                    Value("initialLaunchHandled"),
                    Value("automaticUpdateCheckStarted"),
                }.Max());
        }

        var report = new
        {
            schemaVersion = 1,
            complete,
            processId = Environment.ProcessId,
            processStartUtc = _processStartUtc,
            capturedAtUtc = _utcNow(),
            hasFileArgument = _hasFileArgument,
            milestonesMs = _milestones,
            durationsMs = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["windowConstruction"] = Duration("windowConstructionStarted", "windowConstructionCompleted"),
                ["updateResultRead"] = Duration("updateResultReadStarted", "updateResultReadCompleted"),
                ["appSettingsInitialization"] = Duration("appSettingsInitializationStarted", "appSettingsInitializationCompleted"),
                ["videoProfilesInitialization"] = Duration("videoProfilesInitializationStarted", "videoProfilesInitializationCompleted"),
                ["recentFilesInitialization"] = Duration("recentFilesInitializationStarted", "recentFilesInitializationCompleted"),
                ["playlistInitialization"] = Duration("playlistInitializationStarted", "playlistInitializationCompleted"),
                ["playbackInitialization"] = Duration("playbackInitializationStarted", "playbackInitializationCompleted"),
                ["thumbnailInitialization"] = Duration("thumbnailInitializationStarted", "thumbnailInitializationCompleted"),
                ["serviceConfiguration"] = Duration("serviceConfigurationStarted", "serviceConfigurationCompleted"),
                ["showCall"] = Duration("showStarted", "showReturned"),
                ["showToContentRendered"] = Duration("showReturned", "contentRendered"),
                ["initialLaunchHandling"] = Duration("initialLaunchHandlingStarted", "initialLaunchHandled"),
                ["processStartToShowReturned"] = Value("showReturned"),
                ["processStartToShellInteractive"] = Value("shellInteractive"),
                ["processStartToInteractiveReady"] = Value("interactiveReady"),
            },
        };

        var temporaryPath = $"{_reportPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var parent = Path.GetDirectoryName(_reportPath);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            {
                return;
            }

            var bytes = JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions);
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _reportPath, overwrite: true);
            _reportWritten = true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private double Duration(string start, string end) =>
        _milestones.TryGetValue(start, out var startValue) &&
        _milestones.TryGetValue(end, out var endValue)
            ? Math.Max(0, endValue - startValue)
            : 0;

    private double Value(string milestone) =>
        _milestones.TryGetValue(milestone, out var value) ? value : 0;
}
