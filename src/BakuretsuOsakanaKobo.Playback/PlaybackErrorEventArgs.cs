namespace BakuretsuOsakanaKobo.Playback;

public sealed class PlaybackErrorEventArgs(
    string eventCode,
    string userMessage,
    string suggestedAction,
    string technicalMessage,
    Exception? exception = null,
    string? targetPath = null) : EventArgs
{
    public string EventCode { get; } = eventCode;

    public string UserMessage { get; } = userMessage;

    public string SuggestedAction { get; } = suggestedAction;

    public string TechnicalMessage { get; } = technicalMessage;

    public Exception? Exception { get; } = exception;

    public string? TargetPath { get; } = targetPath;
}
