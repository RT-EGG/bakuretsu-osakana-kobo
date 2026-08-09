namespace BakuretsuOsakanaKobo.Infrastructure.Diagnostics;

public enum DiagnosticSeverity
{
    Information,
    Warning,
    Error,
    Critical,
}

public sealed record DiagnosticEvent(
    DiagnosticSeverity Severity,
    string EventName,
    string Message,
    Exception? Exception = null,
    string? TargetPath = null);

public sealed record DiagnosticWriteResult(bool Success, Exception? Exception)
{
    public static DiagnosticWriteResult Succeeded() => new(true, null);

    public static DiagnosticWriteResult Failed(Exception exception) => new(false, exception);
}
