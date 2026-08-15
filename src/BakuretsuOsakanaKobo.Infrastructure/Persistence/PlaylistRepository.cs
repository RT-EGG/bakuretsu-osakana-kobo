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

    public Task<JsonSaveResult> AddEntriesAsync(
        IEnumerable<string> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var normalizedEntries = entries.Select(VideoProfilePath.Normalize).ToArray();
        if (normalizedEntries.Length == 0)
        {
            throw new ArgumentException("At least one playlist entry is required.", nameof(entries));
        }

        return MutateAndSaveAsync(
            () => _entries.AddRange(normalizedEntries),
            cancellationToken);
    }

    public Task<JsonSaveResult> RemoveAtIndicesAsync(
        IEnumerable<int> indices,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(indices);
        var distinctIndices = indices.Distinct().OrderDescending().ToArray();
        if (distinctIndices.Length == 0)
        {
            throw new ArgumentException("At least one playlist index is required.", nameof(indices));
        }

        return MutateAndSaveAsync(
            () =>
            {
                if (distinctIndices.Any(index => index < 0 || index >= _entries.Count))
                {
                    throw new ArgumentOutOfRangeException(nameof(indices));
                }

                foreach (var index in distinctIndices)
                {
                    _entries.RemoveAt(index);
                }
            },
            cancellationToken);
    }

    public Task<JsonSaveResult> MoveToInsertionIndexAsync(
        int sourceIndex,
        int insertionIndex,
        CancellationToken cancellationToken = default) =>
        MutateAndSaveAsync(
            () =>
            {
                if (sourceIndex < 0 || sourceIndex >= _entries.Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(sourceIndex));
                }

                if (insertionIndex < 0 || insertionIndex > _entries.Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(insertionIndex));
                }

                var entry = _entries[sourceIndex];
                _entries.RemoveAt(sourceIndex);
                var adjustedInsertionIndex = sourceIndex < insertionIndex
                    ? insertionIndex - 1
                    : insertionIndex;
                _entries.Insert(adjustedInsertionIndex, entry);
            },
            cancellationToken);

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
