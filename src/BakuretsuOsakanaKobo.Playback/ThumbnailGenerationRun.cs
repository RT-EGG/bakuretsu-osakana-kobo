using System.Collections.Concurrent;

namespace BakuretsuOsakanaKobo.Playback;

public sealed class ThumbnailGenerationRun
{
    private readonly ConcurrentDictionary<long, ThumbnailFrame> _frames = new();
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _priorityTargetMilliseconds = -1;

    internal ThumbnailGenerationRun(
        long generationId,
        string videoPath,
        long durationMilliseconds,
        double intervalPercent)
    {
        GenerationId = generationId;
        VideoPath = videoPath;
        DurationMilliseconds = durationMilliseconds;
        IntervalPercent = intervalPercent;
    }

    public long GenerationId { get; }

    public string VideoPath { get; }

    public long DurationMilliseconds { get; }

    public double IntervalPercent { get; }

    public int Count => _frames.Count;

    public Task Completion => _completion.Task;

    public IReadOnlyCollection<ThumbnailFrame> GetSnapshot() =>
        _frames.Values.OrderBy(frame => frame.TargetMilliseconds).ToArray();

    public bool TryGetNearest(long targetMilliseconds, out ThumbnailFrame? frame)
    {
        frame = _frames.Values.MinBy(candidate =>
            Math.Abs(candidate.TargetMilliseconds - targetMilliseconds));
        return frame is not null;
    }

    public bool TryGet(long targetMilliseconds, out ThumbnailFrame? frame) =>
        _frames.TryGetValue(targetMilliseconds, out frame);

    public void RequestPriority(long targetMilliseconds)
    {
        if (targetMilliseconds < 0 || targetMilliseconds >= DurationMilliseconds)
        {
            throw new ArgumentOutOfRangeException(nameof(targetMilliseconds));
        }

        if (!_frames.ContainsKey(targetMilliseconds))
        {
            Interlocked.Exchange(ref _priorityTargetMilliseconds, targetMilliseconds);
        }
    }

    internal void Add(ThumbnailFrame frame) => _frames[frame.TargetMilliseconds] = frame;

    internal bool TryTakePriority(out long targetMilliseconds)
    {
        targetMilliseconds = Interlocked.Exchange(ref _priorityTargetMilliseconds, -1);
        return targetMilliseconds >= 0;
    }

    internal void Complete() => _completion.TrySetResult();

    internal void Cancel(CancellationToken cancellationToken) =>
        _completion.TrySetCanceled(cancellationToken);

    internal void Fail(Exception exception) => _completion.TrySetException(exception);
}
