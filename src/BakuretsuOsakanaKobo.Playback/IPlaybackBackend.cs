namespace BakuretsuOsakanaKobo.Playback;

public interface IPlaybackBackend : IDisposable
{
    event EventHandler<PlaybackErrorEventArgs>? ErrorOccurred;

    event EventHandler? StateChanged;

    bool IsPlaying { get; }

    bool IsSeekable { get; }

    long LengthMilliseconds { get; }

    long TimeMilliseconds { get; }

    int VolumePercent { get; }

    bool IsMuted { get; }

    string? CurrentPath { get; }

    Task<bool> OpenAndPlayAsync(
        string path,
        PlaybackAudioState? initialAudioState = null,
        CancellationToken cancellationToken = default);

    void Play();

    void Pause();

    void Stop();

    void Seek(double normalizedPosition);

    void SetVolumePercent(int volumePercent);

    void SetMuted(bool isMuted);
}
