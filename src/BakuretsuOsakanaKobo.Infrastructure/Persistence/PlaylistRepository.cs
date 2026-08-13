namespace BakuretsuOsakanaKobo.Infrastructure.Persistence;

public sealed class PlaylistRepository : IDisposable
{
    private readonly object _sync = new();
    private readonly PortableJsonStore<PlaylistDocument> _store;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly List<string> _entries = [];
    private bool _loop;
    private bool _loaded;
    private bool _disposed;

    public PlaylistRepository(string filePath)
    {
        _store = new PortableJsonStore<PlaylistDocument>(
            filePath,
            () => new PlaylistDocument(),
            PlaylistDocument.IsValid);
    }

    public string FilePath => _store.FilePath;

    public async Task<JsonLoadResult<PlaylistDocument>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var result = await _store.LoadOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            ThrowIfDisposed();
            _entries.Clear();
            _entries.AddRange(result.Value.Entries);
            _loop = result.Value.Loop;
            _loaded = true;
        }

        return result;
    }

    public PlaylistSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            ThrowIfNotReady();
            return new PlaylistSnapshot([.. _entries], _loop);
        }
    }

    public Task<JsonSaveResult> ReplaceEntriesAsync(
        IEnumerable<string> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var normalizedEntries = entries.Select(VideoProfilePath.Normalize).ToArray();
        return MutateAndSaveAsync(
            () =>
            {
                _entries.Clear();
                _entries.AddRange(normalizedEntries);
            },
            cancellationToken);
    }

    public Task<JsonSaveResult> SetLoopAsync(
        bool loop,
        CancellationToken cancellationToken = default) =>
        MutateAndSaveAsync(() => _loop = loop, cancellationToken);

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
            PlaylistDocument snapshot;
            lock (_sync)
            {
                ThrowIfNotReady();
                mutation();
                snapshot = new PlaylistDocument
                {
                    Entries = [.. _entries],
                    Loop = _loop,
                };
            }

            return await _store.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private void ThrowIfNotReady()
    {
        ThrowIfDisposed();
        if (!_loaded)
        {
            throw new InvalidOperationException("The playlist must be loaded before use.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

public sealed record PlaylistSnapshot(IReadOnlyList<string> Entries, bool Loop);
