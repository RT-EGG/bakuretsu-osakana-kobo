namespace BakuretsuOsakanaKobo.Playback;

public interface IPlaybackBackend : IDisposable
{
    event EventHandler<PlaybackErrorEventArgs>? ErrorOccurred;

    event EventHandler? StateChanged;

    bool IsPlaying { get; }

    long LengthMilliseconds { get; }

    long TimeMilliseconds { get; }

    string? CurrentPath { get; }

    bool OpenAndPlay(string path);

    void Play();

    void Pause();

    void Stop();

    void Seek(double normalizedPosition);
}
