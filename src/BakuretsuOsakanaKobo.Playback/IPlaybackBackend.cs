namespace BakuretsuOsakanaKobo.Playback;

public interface IPlaybackBackend : IDisposable
{
    event EventHandler<PlaybackErrorEventArgs>? ErrorOccurred;

    event EventHandler? StateChanged;

    bool IsPlaying { get; }

    long LengthMilliseconds { get; }

    long TimeMilliseconds { get; }

    string? CurrentPath { get; }

    Task<bool> OpenAndPlayAsync(string path, CancellationToken cancellationToken = default);

    void Play();

    void Pause();

    void Stop();

    void Seek(double normalizedPosition);
}
