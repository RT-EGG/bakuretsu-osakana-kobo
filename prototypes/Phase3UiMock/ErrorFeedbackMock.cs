namespace BakuretsuOsakanaKobo.Phase3UiMock;

internal enum NotificationSeverity
{
    Info,
    Warning,
    Error,
}

internal enum ReviewFailureScenario
{
    MissingFileWithoutMedia,
    AccessDeniedWithoutMedia,
    UnsupportedFormatWithoutMedia,
    UnsupportedCodecBeforeSwitch,
    CorruptBeforeSwitch,
    CorruptDuringPlayback,
    PersistenceUnavailable,
    CorruptSavedDataRecovered,
}

internal enum FailureOutcome
{
    Empty,
    PreservePlayback,
    PlaybackError,
    Continue,
}

internal sealed record FailureFeedback(
    string Message,
    NotificationSeverity Severity,
    FailureOutcome Outcome);

internal static class ErrorFeedbackCatalog
{
    public static FailureFeedback Get(ReviewFailureScenario scenario) => scenario switch
    {
        ReviewFailureScenario.MissingFileWithoutMedia => new(
            "ファイルが見つかりません。移動先を確認するか、別の動画を選択してください。",
            NotificationSeverity.Error,
            FailureOutcome.Empty),
        ReviewFailureScenario.AccessDeniedWithoutMedia => new(
            "ファイルを読み取れません。アクセス権を確認するか、別の動画を選択してください。",
            NotificationSeverity.Error,
            FailureOutcome.Empty),
        ReviewFailureScenario.UnsupportedFormatWithoutMedia => new(
            "このファイル形式には対応していません。MP4またはWMVを選択してください。",
            NotificationSeverity.Error,
            FailureOutcome.Empty),
        ReviewFailureScenario.UnsupportedCodecBeforeSwitch => new(
            "この動画のコーデックは再生できません。現在の動画を継続します。対応形式の動画を選択してください。",
            NotificationSeverity.Error,
            FailureOutcome.PreservePlayback),
        ReviewFailureScenario.CorruptBeforeSwitch => new(
            "動画が破損しているため開けません。現在の動画を継続します。別の動画を選択してください。",
            NotificationSeverity.Error,
            FailureOutcome.PreservePlayback),
        ReviewFailureScenario.CorruptDuringPlayback => new(
            "動画の破損により再生を続けられません。別の動画を選択してください。",
            NotificationSeverity.Error,
            FailureOutcome.PlaybackError),
        ReviewFailureScenario.PersistenceUnavailable => new(
            "設定を保存できません。再生は継続できます。dataフォルダーの書き込み権限を確認してください。",
            NotificationSeverity.Warning,
            FailureOutcome.Continue),
        ReviewFailureScenario.CorruptSavedDataRecovered => new(
            "保存データに問題があったため、安全な既定値で起動しました。詳細はログに記録しました。",
            NotificationSeverity.Warning,
            FailureOutcome.Empty),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
    };
}
