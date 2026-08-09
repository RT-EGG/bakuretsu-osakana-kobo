namespace BakuretsuOsakanaKobo.Playback;

public static class SupportedMediaPolicy
{
    public static bool SupportsExtension(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return TryGetExpectedCodecs(path, out _);
    }

    public static MediaPolicyResult Validate(string path, string? videoCodec, string? audioCodec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var extension = Path.GetExtension(path);
        if (!TryGetExpectedCodecs(path, out var expectedCodecs))
        {
            return MediaPolicyResult.Rejected(
                "playback-extension-unsupported",
                "選択されたファイル形式には対応していません。",
                "mp4またはwmvファイルを選択してください。",
                $"Unsupported file extension: {extension}");
        }

        if (string.IsNullOrWhiteSpace(videoCodec) || string.IsNullOrWhiteSpace(audioCodec))
        {
            return MediaPolicyResult.Rejected(
                "playback-tracks-missing",
                "動画または音声を確認できませんでした。",
                "破損していない映像・音声付きの動画を選択してください。",
                $"Required tracks were missing. Video={videoCodec ?? "<null>"}, Audio={audioCodec ?? "<null>"}.");
        }

        if (!string.Equals(videoCodec, expectedCodecs.Video, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(audioCodec, expectedCodecs.Audio, StringComparison.OrdinalIgnoreCase))
        {
            return MediaPolicyResult.Rejected(
                "playback-codec-unsupported",
                "この動画のコーデックは保証対象外です。",
                "対応形式一覧を確認し、別の動画を選択してください。",
                $"Unsupported codecs for {extension}: video={videoCodec}, audio={audioCodec}; " +
                $"expected video={expectedCodecs.Video}, audio={expectedCodecs.Audio}.");
        }

        return MediaPolicyResult.Accepted;
    }

    private static bool TryGetExpectedCodecs(
        string path,
        out (string Video, string Audio) expectedCodecs)
    {
        expectedCodecs = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mp4" => ("h264", "mp4a"),
            ".wmv" => ("VC-1", "WMAP"),
            _ => default,
        };
        return expectedCodecs != default;
    }
}

public sealed record MediaPolicyResult(
    bool IsAccepted,
    string? EventCode,
    string? UserMessage,
    string? SuggestedAction,
    string? TechnicalMessage)
{
    public static MediaPolicyResult Accepted { get; } = new(true, null, null, null, null);

    public static MediaPolicyResult Rejected(
        string eventCode,
        string userMessage,
        string suggestedAction,
        string technicalMessage) =>
        new(false, eventCode, userMessage, suggestedAction, technicalMessage);
}
