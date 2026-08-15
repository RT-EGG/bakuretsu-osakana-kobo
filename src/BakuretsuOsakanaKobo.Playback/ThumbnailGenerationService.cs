namespace BakuretsuOsakanaKobo.Playback;

public sealed class ThumbnailGenerationService : IThumbnailGenerationService
{
    private readonly object _sync = new();
    private readonly IThumbnailFrameExtractor _extractor;
    private readonly Action<Exception>? _callbackExceptionHandler;
    private readonly SemaphoreSlim _pendingSignal = new(0, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private ThumbnailGenerationRun? _pending;
    private CancellationTokenSource? _activeCancellation;
    private long _nextGenerationId;
    private bool _stopping;

    public ThumbnailGenerationService(Action<Exception>? callbackExceptionHandler = null)
        : this(new LibVlcThumbnailFrameExtractor(callbackExceptionHandler), callbackExceptionHandler)
    {
    }

    internal ThumbnailGenerationService(
        IThumbnailFrameExtractor extractor,
        Action<Exception>? callbackExceptionHandler = null)
    {
        _extractor = extractor;
        _callbackExceptionHandler = callbackExceptionHandler;
        _worker = Task.Run(ProcessQueueAsync);
    }

    public event EventHandler<ThumbnailGenerationErrorEventArgs>? GenerationFailed;

    public ThumbnailGenerationRun StartSession(
        string videoPath,
        long durationMilliseconds,
        double intervalPercent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(durationMilliseconds);
        if (!double.IsFinite(intervalPercent) || intervalPercent is < 0.25 or > 5 ||
            Math.Abs((intervalPercent * 4) - Math.Round(intervalPercent * 4)) > 0.000_001)
        {
            throw new ArgumentOutOfRangeException(nameof(intervalPercent));
        }

        var run = new ThumbnailGenerationRun(
            Interlocked.Increment(ref _nextGenerationId),
            Path.GetFullPath(videoPath),
            durationMilliseconds,
            intervalPercent);

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            _activeCancellation?.Cancel();
            if (_pending is { } replaced)
            {
                replaced.Cancel(new CancellationToken(canceled: true));
            }

            var queueWasEmpty = _pending is null;
            _pending = run;
            if (queueWasEmpty)
            {
                _pendingSignal.Release();
            }
        }

        return run;
    }

    public async Task StopAsync()
    {
        lock (_sync)
        {
            if (!_stopping)
            {
                _stopping = true;
                _shutdown.Cancel();
                _activeCancellation?.Cancel();
                if (_pending is { } pending)
                {
                    pending.Cancel(_shutdown.Token);
                    _pending = null;
                }
            }
        }

        await _worker.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _activeCancellation?.Dispose();
        _shutdown.Dispose();
        _pendingSignal.Dispose();
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            while (true)
            {
                await _pendingSignal.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                ThumbnailGenerationRun? run;
                CancellationTokenSource activeCancellation;
                lock (_sync)
                {
                    run = _pending;
                    _pending = null;
                    activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                    _activeCancellation?.Dispose();
                    _activeCancellation = activeCancellation;
                }

                if (run is null)
                {
                    continue;
                }

                try
                {
                    await _extractor.ExtractAsync(run, run.Add, activeCancellation.Token)
                        .ConfigureAwait(false);
                    run.Complete();
                }
                catch (OperationCanceledException) when (activeCancellation.IsCancellationRequested)
                {
                    run.Cancel(activeCancellation.Token);
                }
                catch (Exception exception)
                {
                    run.Fail(exception);
                    _ = run.Completion.Exception;
                    RaiseGenerationFailed(run.VideoPath, exception);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private void RaiseGenerationFailed(string videoPath, Exception exception)
    {
        var handlers = GenerationFailed;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<ThumbnailGenerationErrorEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, new ThumbnailGenerationErrorEventArgs(videoPath, exception));
            }
            catch (Exception callbackException)
            {
                try
                {
                    _callbackExceptionHandler?.Invoke(callbackException);
                }
                catch
                {
                    // Exceptions must not escape the background worker.
                }
            }
        }
    }
}
