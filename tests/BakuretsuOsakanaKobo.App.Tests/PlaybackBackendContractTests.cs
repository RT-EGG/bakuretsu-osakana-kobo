using BakuretsuOsakanaKobo.Playback;
using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class PlaybackBackendContractTests
{
    [Fact]
    public void ConsumerCanUseReplaceableBackendWithoutLibVlcTypes()
    {
        using IPlaybackBackend backend = new FakePlaybackBackend();

        Assert.True(backend.OpenAndPlay("sample.mp4"));
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
    public void MissingFileReportsStructuredErrorWithoutLeakingSubscriberException()
    {
        var callbackExceptions = new List<Exception>();
        using var backend = new LibVlcPlaybackBackend(callbackExceptions.Add);
        PlaybackErrorEventArgs? observedError = null;
        backend.ErrorOccurred += (_, eventArgs) => observedError = eventArgs;
        backend.ErrorOccurred += (_, _) => throw new InvalidOperationException("subscriber failed");

        var opened = backend.OpenAndPlay(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.mp4"));

        Assert.False(opened);
        Assert.NotNull(observedError);
        Assert.Equal("playback-file-missing", observedError.EventCode);
        Assert.Single(callbackExceptions);
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

        public bool OpenAndPlay(string path)
        {
            CurrentPath = path;
            IsPlaying = true;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return true;
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
