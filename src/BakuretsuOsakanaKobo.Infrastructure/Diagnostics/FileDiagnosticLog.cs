using System.IO;
using System.Text.Json;

namespace BakuretsuOsakanaKobo.Infrastructure.Diagnostics;

public sealed class FileDiagnosticLog : IDiagnosticLog
{
    public const long DefaultMaximumBytes = 5 * 1024 * 1024;
    private const string CurrentLogFileName = "app.log";
    private const string PreviousLogFileName = "app.previous.log";

    private readonly object _sync = new();
    private StreamWriter? _writer;

    private FileDiagnosticLog(StreamWriter writer, string filePath)
    {
        _writer = writer;
        FilePath = filePath;
    }

    public string FilePath { get; }

    public static DiagnosticLogOpenResult TryOpen(
        string logsDirectory,
        long maximumBytes = DefaultMaximumBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);

        try
        {
            var fullDirectory = Path.GetFullPath(logsDirectory);
            Directory.CreateDirectory(fullDirectory);
            var currentPath = Path.Combine(fullDirectory, CurrentLogFileName);
            RotateIfNeeded(currentPath, Path.Combine(fullDirectory, PreviousLogFileName), maximumBytes);
            var stream = new FileStream(
                currentPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.WriteThrough);
            var writer = new StreamWriter(stream) { AutoFlush = true };
            return DiagnosticLogOpenResult.Opened(new FileDiagnosticLog(writer, currentPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return DiagnosticLogOpenResult.Failed(exception);
        }
    }

    public DiagnosticWriteResult Write(DiagnosticEvent diagnosticEvent)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);

        lock (_sync)
        {
            if (_writer is null)
            {
                return DiagnosticWriteResult.Failed(
                    new ObjectDisposedException(nameof(FileDiagnosticLog)));
            }

            try
            {
                var line = JsonSerializer.Serialize(new
                {
                    timestampUtc = DateTimeOffset.UtcNow,
                    severity = diagnosticEvent.Severity.ToString(),
                    diagnosticEvent.EventName,
                    diagnosticEvent.Message,
                    diagnosticEvent.TargetPath,
                    exceptionType = diagnosticEvent.Exception?.GetType().FullName,
                    exceptionMessage = diagnosticEvent.Exception?.Message,
                    stackTrace = diagnosticEvent.Exception?.StackTrace,
                });
                _writer.WriteLine(line);
                return DiagnosticWriteResult.Succeeded();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return DiagnosticWriteResult.Failed(exception);
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            try
            {
                _writer?.Dispose();
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            finally
            {
                _writer = null;
            }
        }
    }

    private static void RotateIfNeeded(string currentPath, string previousPath, long maximumBytes)
    {
        if (!File.Exists(currentPath) || new FileInfo(currentPath).Length < maximumBytes)
        {
            return;
        }

        File.Move(currentPath, previousPath, overwrite: true);
    }
}

public sealed record DiagnosticLogOpenResult(
    IDiagnosticLog Log,
    string? FilePath,
    Exception? Exception)
{
    internal static DiagnosticLogOpenResult Opened(FileDiagnosticLog log) =>
        new(log, log.FilePath, null);

    internal static DiagnosticLogOpenResult Failed(Exception exception) =>
        new(NullDiagnosticLog.Instance, null, exception);
}

internal sealed class NullDiagnosticLog : IDiagnosticLog
{
    internal static NullDiagnosticLog Instance { get; } = new();

    public DiagnosticWriteResult Write(DiagnosticEvent diagnosticEvent)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);
        return DiagnosticWriteResult.Failed(new IOException("Diagnostic logging is unavailable."));
    }

    public void Dispose()
    {
    }
}
