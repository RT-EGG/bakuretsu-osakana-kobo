namespace BakuretsuOsakanaKobo;

internal enum ThumbnailPreviewContentState
{
    Ignore,
    Loading,
    Frame,
    Unavailable,
}

internal static class ThumbnailPreviewContent
{
    public static ThumbnailPreviewContentState Resolve(
        bool popupIsOpen,
        long pendingTargetMilliseconds,
        long pendingGenerationId,
        long activeGenerationId,
        bool hasFrame,
        bool generationCompleted)
    {
        if (!popupIsOpen ||
            pendingTargetMilliseconds < 0 ||
            pendingGenerationId != activeGenerationId)
        {
            return ThumbnailPreviewContentState.Ignore;
        }

        if (hasFrame)
        {
            return ThumbnailPreviewContentState.Frame;
        }

        return generationCompleted
            ? ThumbnailPreviewContentState.Unavailable
            : ThumbnailPreviewContentState.Loading;
    }
}
