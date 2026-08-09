using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class PlaybackButtonPresentationTests
{
    [Fact]
    public void From_WithoutMedia_DisablesPlayAndExplainsWhy()
    {
        var presentation = PlaybackButtonPresentation.From(hasMedia: false, isPlaying: false);

        Assert.False(presentation.IsEnabled);
        Assert.Equal("▶", presentation.Glyph);
        Assert.Equal("再生", presentation.AccessibleName);
        Assert.Contains("動画を開く", presentation.ToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public void From_WhilePlaying_OffersPause()
    {
        var presentation = PlaybackButtonPresentation.From(hasMedia: true, isPlaying: true);

        Assert.True(presentation.IsEnabled);
        Assert.Equal("Ⅱ", presentation.Glyph);
        Assert.Equal("一時停止", presentation.AccessibleName);
    }

    [Fact]
    public void From_WhilePaused_OffersPlay()
    {
        var presentation = PlaybackButtonPresentation.From(hasMedia: true, isPlaying: false);

        Assert.True(presentation.IsEnabled);
        Assert.Equal("▶", presentation.Glyph);
        Assert.Equal("再生", presentation.AccessibleName);
    }
}
