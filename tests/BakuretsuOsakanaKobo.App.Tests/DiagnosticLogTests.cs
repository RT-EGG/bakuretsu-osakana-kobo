using System.Collections.Concurrent;
using System.Text.Json;
using BakuretsuOsakanaKobo.Infrastructure.Diagnostics;
using BakuretsuOsakanaKobo.Infrastructure.Errors;
using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class DiagnosticLogTests
{
    [Fact]
    public void Write_RecordsDiagnosticDetailsAsJsonLines()
    {
        using var directory = new TestDirectory();
        var opened = FileDiagnosticLog.TryOpen(directory.Path);
        using var log = opened.Log;
        var exception = new InvalidOperationException("diagnostic detail");

        var result = log.Write(new DiagnosticEvent(
            DiagnosticSeverity.Error,
            "open-failed",
            "The file could not be opened.",
            exception,
            @"C:\videos\sample.mp4"));
        log.Dispose();

        Assert.True(result.Success, result.Exception?.Message);
        var line = Assert.Single(File.ReadAllLines(opened.FilePath!));
        using var json = JsonDocument.Parse(line);
        Assert.Equal("Error", json.RootElement.GetProperty("severity").GetString());
        Assert.Equal("open-failed", json.RootElement.GetProperty("EventName").GetString());
        Assert.Equal(@"C:\videos\sample.mp4", json.RootElement.GetProperty("TargetPath").GetString());
        Assert.Equal("diagnostic detail", json.RootElement.GetProperty("exceptionMessage").GetString());
    }

    [Fact]
    public void TryOpen_WhenCurrentLogReachedLimit_RotatesOnePreviousFile()
    {
        using var directory = new TestDirectory();
        var currentPath = System.IO.Path.Combine(directory.Path, "app.log");
        File.WriteAllText(currentPath, new string('x', 128));

        var opened = FileDiagnosticLog.TryOpen(directory.Path, maximumBytes: 64);
        opened.Log.Dispose();

        Assert.Null(opened.Exception);
        Assert.True(File.Exists(System.IO.Path.Combine(directory.Path, "app.previous.log")));
        Assert.Equal(0, new FileInfo(currentPath).Length);
    }

    [Fact]
    public void TryOpen_WhenDirectoryCannotBeCreated_ReturnsNonThrowingFallback()
    {
        using var directory = new TestDirectory();
        var blockingFile = System.IO.Path.Combine(directory.Path, "blocked");
        File.WriteAllText(blockingFile, "file blocks directory creation");

        var opened = FileDiagnosticLog.TryOpen(System.IO.Path.Combine(blockingFile, "logs"));
        var write = opened.Log.Write(new DiagnosticEvent(
            DiagnosticSeverity.Warning,
            "test",
            "fallback"));

        Assert.NotNull(opened.Exception);
        Assert.False(write.Success);
    }

    [Fact]
    public void Write_IsThreadSafe()
    {
        using var directory = new TestDirectory();
        var opened = FileDiagnosticLog.TryOpen(directory.Path);
        using var log = opened.Log;
        var failures = new ConcurrentBag<Exception?>();

        Parallel.For(0, 100, index =>
        {
            var result = log.Write(new DiagnosticEvent(
                DiagnosticSeverity.Information,
                "parallel-write",
                index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            if (!result.Success)
            {
                failures.Add(result.Exception);
            }
        });
        log.Dispose();

        Assert.Empty(failures);
        Assert.Equal(100, File.ReadLines(opened.FilePath!).Count());
        foreach (var line in File.ReadLines(opened.FilePath!))
        {
            using var _ = JsonDocument.Parse(line);
        }
    }

    [Fact]
    public void ErrorReporter_SeparatesUserMessageFromDiagnosticDetails()
    {
        var log = new RecordingDiagnosticLog();
        var sink = new RecordingNotificationSink();
        var reporter = new ErrorReporter(log, sink);
        var notification = new UserNotification(
            UserNotificationSeverity.Error,
            "動画を開けませんでした。",
            "別のファイルを選択してください。");
        var exception = new InvalidOperationException("internal failure");

        reporter.Report(
            notification,
            "media-open-failed",
            exception.Message,
            exception,
            @"C:\private\video.mp4");

        Assert.Equal(notification, Assert.Single(sink.Notifications));
        var diagnosticEvent = Assert.Single(log.Events);
        Assert.Equal(exception, diagnosticEvent.Exception);
        Assert.Equal(@"C:\private\video.mp4", diagnosticEvent.TargetPath);
        Assert.DoesNotContain("internal failure", notification.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\private", notification.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorReporter_MapsInformationNotificationToInformationDiagnostic()
    {
        var log = new RecordingDiagnosticLog();
        var reporter = new ErrorReporter(log, new RecordingNotificationSink());

        reporter.Report(
            new UserNotification(UserNotificationSeverity.Information, "保存しました。", "次回から適用します。"),
            "profile-saved",
            "The profile was saved.");

        Assert.Equal(DiagnosticSeverity.Information, Assert.Single(log.Events).Severity);
    }

    [Fact]
    public void ErrorReporter_WhenNotificationSinkThrows_ContainsFailureAndRecordsIt()
    {
        var log = new RecordingDiagnosticLog();
        var reporter = new ErrorReporter(log, new ThrowingNotificationSink());

        var exception = Record.Exception(() => reporter.Report(
            new UserNotification(
                UserNotificationSeverity.Warning,
                "設定を保存できません。",
                "権限を確認してください。"),
            "settings-save-failed",
            "Access denied."));

        Assert.Null(exception);
        Assert.Collection(
            log.Events,
            item => Assert.Equal("settings-save-failed", item.EventName),
            item => Assert.Equal("user-notification-failed", item.EventName));
    }

    private sealed class RecordingDiagnosticLog : IDiagnosticLog
    {
        internal List<DiagnosticEvent> Events { get; } = [];

        public DiagnosticWriteResult Write(DiagnosticEvent diagnosticEvent)
        {
            Events.Add(diagnosticEvent);
            return DiagnosticWriteResult.Succeeded();
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingNotificationSink : IUserNotificationSink
    {
        internal List<UserNotification> Notifications { get; } = [];

        public void Show(UserNotification notification) => Notifications.Add(notification);
    }

    private sealed class ThrowingNotificationSink : IUserNotificationSink
    {
        public void Show(UserNotification notification) =>
            throw new InvalidOperationException("Notification host is unavailable.");
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "BakuretsuOsakanaKobo.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
