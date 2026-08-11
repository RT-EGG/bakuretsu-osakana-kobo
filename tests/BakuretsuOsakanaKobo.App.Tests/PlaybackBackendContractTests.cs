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

        backend.SetVolumePercent(65);
        backend.SetMuted(true);

        Assert.Equal(65, backend.VolumePercent);
        Assert.True(backend.IsMuted);

        backend.Pause();

        Assert.False(backend.IsPlaying);
    }

    [Fact]
    public async Task OpenAppliesInitialAudioStateAsPartOfTheSuccessfulTransition()
    {
        using IPlaybackBackend backend = new FakePlaybackBackend();

        Assert.True(await backend.OpenAndPlayAsync(
            "sample.mp4",
            new PlaybackAudioState(350, isMuted: true)));

        Assert.Equal(350, backend.VolumePercent);
        Assert.True(backend.IsMuted);
    }

    [Fact]
    public void LibVlcBackendInitializesAndDisposesIdempotently()
    {
        var backend = new LibVlcPlaybackBackend();

        backend.Dispose();
        backend.Dispose();

        Assert.False(backend.AudioDiagnostics.RenderThreadAlive);
        Assert.Throws<ObjectDisposedException>(() => _ = backend.IsPlaying);
    }

    [Fact]
    public void LibVlcBackendClampsProductVolumeAndKeepsMuteIndependent()
    {
        using var backend = new LibVlcPlaybackBackend();

        Assert.Equal(PlaybackVolume.DefaultPercent, backend.VolumePercent);
        Assert.False(backend.IsMuted);

        backend.SetVolumePercent(350);
        Assert.Equal(350, backend.VolumePercent);

        backend.SetVolumePercent(501);
        Assert.Equal(PlaybackVolume.MaximumPercent, backend.VolumePercent);

        backend.SetMuted(true);
        backend.SetVolumePercent(-1);

        Assert.Equal(0, backend.VolumePercent);
        Assert.True(backend.IsMuted);
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
            backend.OpenAndPlayAsync("sample.mp4", cancellationToken: cancellation.Token));

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

        public bool IsSeekable => false;

        public long LengthMilliseconds => 0;

        public long TimeMilliseconds => 0;

        public int VolumePercent { get; private set; } = PlaybackVolume.DefaultPercent;

        public bool IsMuted { get; private set; }

        public string? CurrentPath { get; private set; }

        public Task<bool> OpenAndPlayAsync(
            string path,
            PlaybackAudioState? initialAudioState = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CurrentPath = path;
            IsPlaying = true;
            if (initialAudioState is { } audioState)
            {
                VolumePercent = audioState.VolumePercent;
                IsMuted = audioState.IsMuted;
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(true);
        }

        public void Play() => IsPlaying = true;

        public void Pause() => IsPlaying = false;

        public void Stop() => IsPlaying = false;

        public void Seek(double normalizedPosition)
        {
        }

        public void SetVolumePercent(int volumePercent) =>
            VolumePercent = PlaybackVolume.Clamp(volumePercent);

        public void SetMuted(bool isMuted) => IsMuted = isMuted;

        public void Dispose()
        {
        }
    }
}
