using System.Collections.Concurrent;
using BakuretsuOsakanaKobo.Playback;
using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class ThumbnailGenerationServiceTests
{
    [Fact]
    public async Task CompletedRunStoresFramesAndFindsNearestFrame()
    {
        var extractor = new RecordingExtractor(async (run, frameReady, cancellationToken) =>
        {
            frameReady(CreateFrame(0, 5));
            frameReady(CreateFrame(1_000, 995));
            await Task.CompletedTask;
        });
        await using var service = new ThumbnailGenerationService(extractor);

        var run = service.StartSession("video.mp4", 10_000, 1);
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, run.Count);
        Assert.True(run.TryGetNearest(900, out var nearest));
        Assert.Equal(1_000, nearest!.TargetMilliseconds);
        Assert.Equal([0L, 1_000L], run.GetSnapshot().Select(frame => frame.TargetMilliseconds));
    }

    [Fact]
    public async Task NewSessionCancelsActiveRunBeforeStartingReplacement()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var extractor = new RecordingExtractor(async (run, frameReady, cancellationToken) =>
        {
            if (run.VideoPath.EndsWith("first.mp4", StringComparison.OrdinalIgnoreCase))
            {
                firstStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            frameReady(CreateFrame(0, 0));
        });
        await using var service = new ThumbnailGenerationService(extractor);
        var first = service.StartSession("first.mp4", 10_000, 1);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var second = service.StartSession("second.mp4", 10_000, 1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await first.Completion);
        await second.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, second.Count);
        Assert.Equal(2, extractor.StartedRuns.Count);
    }

    [Fact]
    public async Task PendingQueueKeepsOnlyNewestSession()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFirstCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var extractor = new RecordingExtractor(async (run, frameReady, cancellationToken) =>
        {
            if (run.VideoPath.EndsWith("first.mp4", StringComparison.OrdinalIgnoreCase))
            {
                firstStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                finally
                {
                    await allowFirstCleanup.Task;
                }
            }
        });
        await using var service = new ThumbnailGenerationService(extractor);
        var first = service.StartSession("first.mp4", 10_000, 1);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var replaced = service.StartSession("replaced.mp4", 10_000, 1);
        var newest = service.StartSession("newest.mp4", 10_000, 1);
        allowFirstCleanup.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await first.Completion);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await replaced.Completion);
        await newest.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.DoesNotContain(
            extractor.StartedRuns,
            run => run.VideoPath.EndsWith("replaced.mp4", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StopCancelsActiveRunAndIsIdempotent()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var extractor = new RecordingExtractor(async (run, frameReady, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        await using var service = new ThumbnailGenerationService(extractor);
        var run = service.StartSession("video.mp4", 10_000, 1);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await run.Completion);
        Assert.Throws<ObjectDisposedException>(() =>
            service.StartSession("after-stop.mp4", 10_000, 1));
    }

    private static ThumbnailFrame CreateFrame(long targetMilliseconds, long observedMilliseconds) =>
        new(targetMilliseconds, observedMilliseconds, 1, 1, 4, [0, 0, 0, 255]);

    private sealed class RecordingExtractor(
        Func<ThumbnailGenerationRun, Action<ThumbnailFrame>, CancellationToken, Task> extract)
        : IThumbnailFrameExtractor
    {
        public ConcurrentQueue<ThumbnailGenerationRun> StartedRuns { get; } = new();

        public Task ExtractAsync(
            ThumbnailGenerationRun run,
            Action<ThumbnailFrame> frameReady,
            CancellationToken cancellationToken)
        {
            StartedRuns.Enqueue(run);
            return extract(run, frameReady, cancellationToken);
        }
    }
}
