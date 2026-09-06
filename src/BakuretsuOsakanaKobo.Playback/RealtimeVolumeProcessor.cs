namespace BakuretsuOsakanaKobo.Playback;

internal sealed class RealtimeVolumeProcessor
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;
    public const double BoostedCeilingDecibels = -1;
    public const double LookaheadMilliseconds = 5;
    public const double ReleaseMilliseconds = 80;

    private static readonly double BoostedCeiling =
        Math.Pow(10, BoostedCeilingDecibels / 20);

    private readonly StreamingLookaheadLimiter _limiter = new(
        Channels,
        SampleRate,
        boost: 1,
        ceiling: 1,
        LookaheadMilliseconds,
        ReleaseMilliseconds);
    private int _volumePercent = PlaybackVolume.DefaultPercent;
    private bool _isMuted;

    public int VolumePercent => _volumePercent;

    public bool IsMuted => _isMuted;

    public int PendingFrames => _limiter.PendingFrames;

    public long NonFiniteInputSamples => _limiter.NonFiniteInputSamples;

    public long NonFiniteOutputSamples => _limiter.NonFiniteOutputSamples;

    public double Peak => _limiter.Peak;

    public long OverRangeSamples => _limiter.OverRangeSamples;

    public void SetVolumePercent(int volumePercent)
    {
        _volumePercent = PlaybackVolume.Clamp(volumePercent);
        ApplyLevel();
    }

    public void SetMuted(bool isMuted)
    {
        _isMuted = isMuted;
        ApplyLevel();
    }

    public float[] Process(short[] pcm16)
    {
        ArgumentNullException.ThrowIfNull(pcm16);
        if (pcm16.Length % Channels != 0)
        {
            throw new ArgumentException("Input must contain complete stereo PCM frames.", nameof(pcm16));
        }

        var samples = new float[pcm16.Length];
        for (var index = 0; index < pcm16.Length; index++)
        {
            samples[index] = pcm16[index] / 32768f;
        }

        return _limiter.Process(samples);
    }

    public float[] Process(float[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Length % Channels != 0)
        {
            throw new ArgumentException("Input must contain complete stereo PCM frames.", nameof(samples));
        }

        return _limiter.Process(samples);
    }

    public float[] Flush() => _limiter.Flush();

    public void Reset() => _limiter.Reset();

    private void ApplyLevel()
    {
        var boost = _isMuted ? 0 : _volumePercent / 100.0;
        var ceiling = _volumePercent > PlaybackVolume.BasicMaximumPercent
            ? BoostedCeiling
            : 1;
        _limiter.UpdateParameters(boost, ceiling);
    }
}
