namespace BakuretsuOsakanaKobo.Infrastructure.Persistence;

public sealed class VideoProfileRepository : IDisposable
{
    private readonly object _sync = new();
    private readonly PortableJsonStore<VideoProfileDocument> _store;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly Dictionary<string, VideoProfileEntry> _profiles =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;
    private bool _disposed;

    public VideoProfileRepository(string filePath)
    {
        _store = new PortableJsonStore<VideoProfileDocument>(
            filePath,
            () => new VideoProfileDocument(),
            VideoProfileDocument.IsValid);
    }

    public string FilePath => _store.FilePath;

    public async Task<JsonLoadResult<VideoProfileDocument>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var result = await _store.LoadOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        lock (_sync)
        {
            ThrowIfDisposed();
            _profiles.Clear();
            foreach (var profile in result.Value.Profiles)
            {
                _profiles.Add(profile.VideoPath, profile);
            }

            _loaded = true;
        }

        return result;
    }

    public bool TryGet(string videoPath, out VideoProfileEntry profile)
    {
        var normalizedPath = VideoProfilePath.Normalize(videoPath);
        lock (_sync)
        {
            ThrowIfNotReady();
            return _profiles.TryGetValue(normalizedPath, out profile!);
        }
    }

    public void Set(string videoPath, int volumePercent, bool isMuted)
    {
        var normalizedPath = VideoProfilePath.Normalize(videoPath);
        var profile = new VideoProfileEntry
        {
            VideoPath = normalizedPath,
            VolumePercent = Math.Clamp(
                volumePercent,
                VideoProfileEntry.MinimumVolumePercent,
                VideoProfileEntry.MaximumVolumePercent),
            IsMuted = isMuted,
        };

        lock (_sync)
        {
            ThrowIfNotReady();
            _profiles[normalizedPath] = profile;
        }
    }

    public async Task<JsonSaveResult> SaveAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            VideoProfileDocument snapshot;
            lock (_sync)
            {
                ThrowIfNotReady();
                snapshot = new VideoProfileDocument
                {
                    Profiles = _profiles.Values
                        .OrderBy(profile => profile.VideoPath, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                };
            }

            return await _store.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _saveGate.Release();
        }
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

    private void ThrowIfNotReady()
    {
        ThrowIfDisposed();
        if (!_loaded)
        {
            throw new InvalidOperationException("Video profiles must be loaded before use.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
