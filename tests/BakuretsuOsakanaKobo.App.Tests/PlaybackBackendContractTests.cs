using BakuretsuOsakanaKobo.Playback;
using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class PlaybackBackendContractTests
{
    [Fact]
    public async Task ConsumerCanUseReplaceableBackendWithoutLibVlcTypes()
    {
        using IPlaybackBackend backend = new FakePlaybackBackend();

        Assert.True(await backend.OpenAndPlayAsync("sample.mp4"));
        Assert.True(backend.IsPlaying);
        Assert.Equal("sample.mp4", backend.CurrentPath);

        backend.Pause();

        Assert.False(backend.IsPlaying);
    }

    [Fact]
    public void LibVlcBackendInitializesAndDisposesIdempotently()
    {
        var backend = new LibVlcPlaybackBackend();

        backend.Dispose();
        backend.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _ = backend.IsPlaying);
    }

    [Fact]
    public async Task MissingFileReportsStructuredErrorWithoutLeakingSubscriberException()
    {
        var callbackExceptions = new List<Exception>();
        using var backend = new LibVlcPlaybackBackend(callbackExceptions.Add);
        PlaybackErrorEventArgs? observedError = null;
        backend.ErrorOccurred += (_, eventArgs) => observedError = eventArgs;
        backend.ErrorOccurred += (_, _) => throw new InvalidOperationException("subscriber failed");

        var opened = await backend.OpenAndPlayAsync(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.mp4"));

        Assert.False(opened);
        Assert.NotNull(observedError);
        Assert.Equal("playback-file-missing", observedError.EventCode);
        Assert.Single(callbackExceptions);
    }

    [Fact]
    public async Task UnsupportedExtensionIsRejectedBeforeItCanChangePlaybackState()
    {
        using var backend = new LibVlcPlaybackBackend();
        PlaybackErrorEventArgs? observedError = null;
        backend.ErrorOccurred += (_, eventArgs) => observedError = eventArgs;

        var opened = await backend.OpenAndPlayAsync(Path.Combine(Path.GetTempPath(), "sample.mkv"));

        Assert.False(opened);
        Assert.Null(backend.CurrentPath);
        Assert.Equal("playback-extension-unsupported", observedError?.EventCode);
    }

    [Fact]
    public async Task CanceledOpenStopsBeforeChangingPlaybackStateOrReportingAnError()
    {
        using var backend = new LibVlcPlaybackBackend();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var errorCount = 0;
        backend.ErrorOccurred += (_, _) => errorCount++;

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            backend.OpenAndPlayAsync("sample.mp4", cancellation.Token));

        Assert.Null(backend.CurrentPath);
        Assert.Equal(0, errorCount);
    }

    private sealed class FakePlaybackBackend : IPlaybackBackend
    {
        public event EventHandler<PlaybackErrorEventArgs>? ErrorOccurred
        {
            add { }
            remove { }
        }

        public event EventHandler? StateChanged;

        public bool IsPlaying { get; private set; }

        public long LengthMilliseconds => 0;

        public long TimeMilliseconds => 0;

        public string? CurrentPath { get; private set; }

        public Task<bool> OpenAndPlayAsync(string path, CancellationToken cancellationToken = default)
        {
            CurrentPath = path;
            IsPlaying = true;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(true);
        }

        public void Play() => IsPlaying = true;

        public void Pause() => IsPlaying = false;

        public void Stop() => IsPlaying = false;

        public void Seek(double normalizedPosition)
        {
        }

        public void Dispose()
        {
        }
    }
}
