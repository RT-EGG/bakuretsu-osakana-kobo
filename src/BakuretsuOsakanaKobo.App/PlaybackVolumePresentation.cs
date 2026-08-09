using BakuretsuOsakanaKobo.Playback;

namespace BakuretsuOsakanaKobo;

public readonly record struct PlaybackVolumePresentation(
    bool IsEnabled,
    int VolumePercent,
    string MuteGlyph,
    string MuteToolTip,
    string MuteAccessibleName,
    string VolumeToolTip)
{
    public static PlaybackVolumePresentation From(
        bool hasMedia,
        bool isLoading,
        bool hasPlaybackError,
        int volumePercent,
        bool isMuted)
    {
        var isEnabled = hasMedia && !isLoading && !hasPlaybackError;
        var clampedVolume = PlaybackVolume.ClampBasic(volumePercent);
        var disabledMuteToolTip = !hasMedia
            ? "動画を開くとミュートを変更できます"
            : isLoading
                ? "動画の読み込み中はミュートを変更できません"
                : "再生エラーのためミュートを変更できません";
        var disabledVolumeToolTip = !hasMedia
            ? "動画を開くと音量を変更できます"
            : isLoading
                ? "動画の読み込み中は音量を変更できません"
                : "再生エラーのため音量を変更できません";
        return new PlaybackVolumePresentation(
            isEnabled,
            clampedVolume,
            isMuted ? "🔇" : "🔊",
            isEnabled
                ? isMuted ? "ミュート解除" : "ミュート"
                : disabledMuteToolTip,
            isMuted ? "ミュート解除" : "ミュート",
            isEnabled ? "音量 0～100%" : disabledVolumeToolTip);
    }
}
