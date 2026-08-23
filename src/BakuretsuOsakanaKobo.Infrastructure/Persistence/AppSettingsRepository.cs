namespace BakuretsuOsakanaKobo.Infrastructure.Persistence;

public sealed class AppSettingsRepository : IDisposable
{
    private readonly object _sync = new();
    private readonly PortableJsonStore<AppSettings> _store;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private double _thumbnailIntervalPercent = ThumbnailGenerationInterval.DefaultPercent;
    private double _thumbnailPreviewWidthPercent = ThumbnailPreviewSize.DefaultPercent;
    private DateTimeOffset? _lastAutomaticUpdateCheckAttemptUtc;
    private bool _loaded;
    private bool _disposed;

    public AppSettingsRepository(string filePath)
    {
        _store = new PortableJsonStore<AppSettings>(
            filePath,
            () => new AppSettings(),
            AppSettings.IsValid);
    }

    public string FilePath => _store.FilePath;

    public async Task<JsonLoadResult<AppSettings>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var result = await _store.LoadOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            ThrowIfDisposed();
            _thumbnailIntervalPercent = result.Value.ThumbnailIntervalPercent ??
                                        ThumbnailGenerationInterval.DefaultPercent;
            _thumbnailPreviewWidthPercent = result.Value.ThumbnailPreviewWidthPercent ??
                                            ThumbnailPreviewSize.DefaultPercent;
            _lastAutomaticUpdateCheckAttemptUtc = result.Value.LastAutomaticUpdateCheckAttemptUtc;
            _loaded = true;
        }

        return result;
    }

    public AppSettingsSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            ThrowIfNotReady();
            return CreateSnapshot();
        }
    }

    public Task<JsonSaveResult> SaveAsync(CancellationToken cancellationToken = default) =>
        MutateAndSaveAsync(static () => { }, cancellationToken);

    public Task<JsonSaveResult> SetThumbnailIntervalPercentAsync(
        double percent,
        CancellationToken cancellationToken = default)
    {
        ThumbnailGenerationInterval.EnsureValid(percent, nameof(percent));
        return MutateAndSaveAsync(() => _thumbnailIntervalPercent = percent, cancellationToken);
    }

    public Task<JsonSaveResult> SetThumbnailSettingsAsync(
        double intervalPercent,
        double previewWidthPercent,
        CancellationToken cancellationToken = default)
    {
        ThumbnailGenerationInterval.EnsureValid(intervalPercent, nameof(intervalPercent));
        ThumbnailPreviewSize.EnsureValid(previewWidthPercent, nameof(previewWidthPercent));
        return MutateAndSaveAsync(
            () =>
            {
                _thumbnailIntervalPercent = intervalPercent;
                _thumbnailPreviewWidthPercent = previewWidthPercent;
            },
            cancellationToken);
    }

    public Task<JsonSaveResult> RecordAutomaticUpdateCheckAttemptAsync(
        DateTimeOffset attemptedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (attemptedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The update check attempt timestamp must use UTC.", nameof(attemptedAtUtc));
        }

        return MutateAndSaveAsync(
            () => _lastAutomaticUpdateCheckAttemptUtc = attemptedAtUtc,
            cancellationToken);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _saveGate.Dispose();
    }

    private async Task<JsonSaveResult> MutateAndSaveAsync(
        Action mutation,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AppSettings snapshot;
            lock (_sync)
            {
                ThrowIfNotReady();
                mutation();
                snapshot = CreateDocument();
            }

            return await _store.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private AppSettings CreateDocument() => new()
    {
        ThumbnailIntervalPercent = _thumbnailIntervalPercent,
        ThumbnailPreviewWidthPercent = _thumbnailPreviewWidthPercent,
        LastAutomaticUpdateCheckAttemptUtc = _lastAutomaticUpdateCheckAttemptUtc,
    };

    private AppSettingsSnapshot CreateSnapshot() => new(
        _thumbnailIntervalPercent,
        _thumbnailPreviewWidthPercent,
        _lastAutomaticUpdateCheckAttemptUtc);

    private void ThrowIfNotReady()
    {
        ThrowIfDisposed();
        if (!_loaded)
        {
            throw new InvalidOperationException("The application settings must be loaded before use.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

public sealed record AppSettingsSnapshot(
    double ThumbnailIntervalPercent,
    double ThumbnailPreviewWidthPercent,
    DateTimeOffset? LastAutomaticUpdateCheckAttemptUtc);
