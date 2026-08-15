namespace BakuretsuOsakanaKobo.Infrastructure.Persistence;

public sealed class RecentFileRepository : IDisposable
{
    private readonly object _sync = new();
    private readonly PortableJsonStore<RecentFileDocument> _store;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly List<string> _files = [];
    private bool _loaded;
    private bool _disposed;

    public RecentFileRepository(string filePath)
    {
        _store = new PortableJsonStore<RecentFileDocument>(
            filePath,
            () => new RecentFileDocument(),
            RecentFileDocument.IsValid);
    }

    public string FilePath => _store.FilePath;

    public async Task<JsonLoadResult<RecentFileDocument>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var result = await _store.LoadOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        lock (_sync)
        {
            ThrowIfDisposed();
            _files.Clear();
            _files.AddRange(result.Value.Files);
            _loaded = true;
        }

        return result;
    }

    public IReadOnlyList<string> GetFiles()
    {
        lock (_sync)
        {
            ThrowIfNotReady();
            return _files.ToArray();
        }
    }

    public async Task<JsonSaveResult> RecordSuccessfulOpenAsync(
        string videoPath,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = VideoProfilePath.Normalize(videoPath);
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RecentFileDocument snapshot;
            lock (_sync)
            {
                ThrowIfNotReady();
                _files.RemoveAll(path =>
                    string.Equals(path, normalizedPath, StringComparison.OrdinalIgnoreCase));
                _files.Insert(0, normalizedPath);
                if (_files.Count > RecentFileDocument.MaximumFiles)
                {
                    _files.RemoveRange(
                        RecentFileDocument.MaximumFiles,
                        _files.Count - RecentFileDocument.MaximumFiles);
                }

                snapshot = CreateSnapshot();
            }

            return await _store.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public Task<JsonSaveResult> RemoveAsync(
        string videoPath,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = VideoProfilePath.Normalize(videoPath);
        return MutateAndSaveAsync(
            files => files.RemoveAll(path =>
                string.Equals(path, normalizedPath, StringComparison.OrdinalIgnoreCase)),
            cancellationToken);
    }

    public Task<JsonSaveResult> RemoveMissingAsync(CancellationToken cancellationToken = default) =>
        MutateAndSaveAsync(
            files => files.RemoveAll(path => !File.Exists(path)),
            cancellationToken);

    public Task<JsonSaveResult> ClearAsync(CancellationToken cancellationToken = default) =>
        MutateAndSaveAsync(
            files =>
            {
                var removedCount = files.Count;
                files.Clear();
                return removedCount;
            },
            cancellationToken);

    public async Task<JsonSaveResult> SaveAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RecentFileDocument snapshot;
            lock (_sync)
            {
                ThrowIfNotReady();
                snapshot = CreateSnapshot();
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

    private async Task<JsonSaveResult> MutateAndSaveAsync(
        Func<List<string>, int> mutation,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RecentFileDocument snapshot;
            lock (_sync)
            {
                ThrowIfNotReady();
                mutation(_files);
                snapshot = CreateSnapshot();
            }

            return await _store.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private RecentFileDocument CreateSnapshot() => new()
    {
        Files = [.. _files],
    };

    private void ThrowIfNotReady()
    {
        ThrowIfDisposed();
        if (!_loaded)
        {
            throw new InvalidOperationException("Recent files must be loaded before use.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
