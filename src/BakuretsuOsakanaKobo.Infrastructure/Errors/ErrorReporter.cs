using BakuretsuOsakanaKobo.Infrastructure.Diagnostics;

namespace BakuretsuOsakanaKobo.Infrastructure.Errors;

public enum UserNotificationSeverity
{
    Information,
    Warning,
    Error,
}

public sealed record UserNotification(
    UserNotificationSeverity Severity,
    string Message,
    string SuggestedAction);

public interface IUserNotificationSink
{
    void Show(UserNotification notification);
}

public sealed class ErrorReporter
{
    private readonly IDiagnosticLog _diagnosticLog;
    private readonly IUserNotificationSink _notificationSink;

    public ErrorReporter(IDiagnosticLog diagnosticLog, IUserNotificationSink notificationSink)
    {
        _diagnosticLog = diagnosticLog ?? throw new ArgumentNullException(nameof(diagnosticLog));
        _notificationSink = notificationSink ?? throw new ArgumentNullException(nameof(notificationSink));
    }

    public void Report(
        UserNotification notification,
        string eventName,
        string diagnosticMessage,
        Exception? exception = null,
        string? targetPath = null)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticMessage);

        var severity = notification.Severity switch
        {
            UserNotificationSeverity.Information => DiagnosticSeverity.Information,
            UserNotificationSeverity.Error => DiagnosticSeverity.Error,
            _ => DiagnosticSeverity.Warning,
        };
        TryWriteDiagnostic(new DiagnosticEvent(
            severity,
            eventName,
            diagnosticMessage,
            exception,
            targetPath));
        try
        {
            _notificationSink.Show(notification);
        }
        catch (Exception notificationException)
        {
            TryWriteDiagnostic(new DiagnosticEvent(
                DiagnosticSeverity.Error,
                "user-notification-failed",
                notificationException.Message,
                notificationException));
        }
    }

    private void TryWriteDiagnostic(DiagnosticEvent diagnosticEvent)
    {
        try
        {
            _diagnosticLog.Write(diagnosticEvent);
        }
        catch
        {
            // Diagnostics must never replace the original recoverable failure.
        }
    }
}
