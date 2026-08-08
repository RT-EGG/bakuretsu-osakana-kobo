namespace BakuretsuOsakanaKobo.Spikes.LibVlcWpf.Playback;

internal interface IPlaybackBackend : IDisposable
{
    event EventHandler<string>? ErrorOccurred;

    bool IsPlaying { get; }

    long LengthMilliseconds { get; }

    long TimeMilliseconds { get; }

    int VolumePercent { get; }

    float Rate { get; }

    string? CurrentPath { get; }

    bool Open(string path);

    void Play();

    void Pause();

    void Seek(double normalizedPosition);

    bool SetRate(float rate);

    int SetVolume(int requestedPercent);
}

