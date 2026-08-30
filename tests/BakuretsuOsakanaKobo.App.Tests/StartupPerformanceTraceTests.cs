using System.Text.Json;
using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class StartupPerformanceTraceTests
{
    [Fact]
    public void Record_WhenRequiredMilestonesComplete_WritesDurableReportOnce()
    {
        using var directory = new TemporaryDirectory();
        var reportPath = Path.Combine(directory.Path, "startup.json");
        var processStart = new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);
        var elapsed = new Queue<double>(
        [
            10,
            100,
            150,
            175,
            200,
            250,
        ]);
        var trace = new StartupPerformanceTrace(
            reportPath,
            processStart,
            hasFileArgument: true,
            elapsedMilliseconds: () => elapsed.Dequeue(),
            utcNow: () => processStart.AddSeconds(1));

        trace.Record("showReturned");
        trace.Record("contentRendered");
        trace.Record("dispatcherIdle");
        trace.Record("shellInteractive");
        trace.Record("initialLaunchHandled");
        Assert.False(File.Exists(reportPath));
        trace.Record("automaticUpdateCheckStarted");

        using var report = JsonDocument.Parse(File.ReadAllBytes(reportPath));
        Assert.True(report.RootElement.GetProperty("complete").GetBoolean());
        Assert.True(report.RootElement.GetProperty("hasFileArgument").GetBoolean());
        Assert.Equal(
            175,
            report.RootElement.GetProperty("durationsMs")
                .GetProperty("processStartToShellInteractive")
                .GetDouble());
        Assert.Equal(
            250,
            report.RootElement.GetProperty("durationsMs")
                .GetProperty("processStartToInteractiveReady")
                .GetDouble());

        trace.Record("ignoredAfterCompletion");
        Assert.False(report.RootElement.GetProperty("milestonesMs").TryGetProperty("ignoredAfterCompletion", out _));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void WriteIncompleteOnExit_WritesIncompleteReport()
    {
        using var directory = new TemporaryDirectory();
        var reportPath = Path.Combine(directory.Path, "startup.json");
        var processStart = new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);
        var elapsed = new Queue<double>(
        [
            10,
            20,
        ]);
        var trace = new StartupPerformanceTrace(
            reportPath,
            processStart,
            hasFileArgument: false,
            elapsedMilliseconds: () => elapsed.Dequeue(),
            utcNow: () => processStart.AddSeconds(1));

        trace.Record("onStartupEntered");
        trace.WriteIncompleteOnExit();

        using var report = JsonDocument.Parse(File.ReadAllBytes(reportPath));
        Assert.False(report.RootElement.GetProperty("complete").GetBoolean());
        Assert.False(report.RootElement.GetProperty("hasFileArgument").GetBoolean());
        Assert.True(report.RootElement.GetProperty("milestonesMs").TryGetProperty("applicationExitStarted", out _));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "BakuretsuOsakanaKobo.StartupTrace.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
