using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class PlaybackVolumePresentationTests
{
    [Theory]
    [InlineData(false, false, false, "動画を開くと")]
    [InlineData(true, true, false, "読み込み中")]
    [InlineData(true, false, true, "再生エラー")]
    public void From_WhenVolumeCannotBeControlled_DisablesBothControls(
        bool hasMedia,
        bool isLoading,
        bool hasPlaybackError,
        string expectedToolTipText)
    {
        var presentation = PlaybackVolumePresentation.From(
            hasMedia,
            isLoading,
            hasPlaybackError,
            volumePercent: 65,
            isMuted: false);

        Assert.False(presentation.IsEnabled);
        Assert.Contains(expectedToolTipText, presentation.VolumeToolTip, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(65, 65)]
    [InlineData(101, 100)]
    public void From_WithMedia_EnablesAndClampsDisplayedVolume(int volumePercent, int expected)
    {
        var presentation = PlaybackVolumePresentation.From(
            hasMedia: true,
            isLoading: false,
            hasPlaybackError: false,
            volumePercent,
            isMuted: false);

        Assert.True(presentation.IsEnabled);
        Assert.Equal(expected, presentation.VolumePercent);
        Assert.Equal("🔊", presentation.MuteGlyph);
        Assert.Equal("ミュート", presentation.MuteAccessibleName);
    }

    [Fact]
    public void From_WhileMuted_OffersUnmuteWithoutChangingDisplayedVolume()
    {
        var presentation = PlaybackVolumePresentation.From(
            hasMedia: true,
            isLoading: false,
            hasPlaybackError: false,
            volumePercent: 40,
            isMuted: true);

        Assert.Equal(40, presentation.VolumePercent);
        Assert.Equal("🔇", presentation.MuteGlyph);
        Assert.Equal("ミュート解除", presentation.MuteAccessibleName);
    }
}
