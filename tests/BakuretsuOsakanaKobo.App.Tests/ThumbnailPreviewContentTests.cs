using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class ThumbnailPreviewContentTests
{
    [Theory]
    [InlineData(false, 10, 1, 1)]
    [InlineData(true, -1, 1, 1)]
    [InlineData(true, 10, 1, 2)]
    public void Resolve_IgnoresInactiveOrStaleRequests(
        bool popupIsOpen,
        long pendingTargetMilliseconds,
        long pendingGenerationId,
        long activeGenerationId)
    {
        Assert.Equal(
            ThumbnailPreviewContentState.Ignore,
            ThumbnailPreviewContent.Resolve(
                popupIsOpen,
                pendingTargetMilliseconds,
                pendingGenerationId,
                activeGenerationId,
                hasFrame: false,
                generationCompleted: false));
    }

    [Fact]
    public void Resolve_KeepsLoadingWhileGenerationCanStillProduceFrame()
    {
        Assert.Equal(
            ThumbnailPreviewContentState.Loading,
            ThumbnailPreviewContent.Resolve(true, 10, 1, 1, false, false));
    }

    [Fact]
    public void Resolve_ShowsFrameEvenAfterGenerationCompleted()
    {
        Assert.Equal(
            ThumbnailPreviewContentState.Frame,
            ThumbnailPreviewContent.Resolve(true, 10, 1, 1, true, true));
    }

    [Fact]
    public void Resolve_KeepsPopupOpenAsUnavailableAfterGenerationFailure()
    {
        Assert.Equal(
            ThumbnailPreviewContentState.Unavailable,
            ThumbnailPreviewContent.Resolve(true, 10, 1, 1, false, true));
    }
}
