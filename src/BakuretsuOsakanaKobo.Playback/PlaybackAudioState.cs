namespace BakuretsuOsakanaKobo.Playback;

public readonly record struct PlaybackAudioState
{
    public PlaybackAudioState(int volumePercent, bool isMuted)
    {
        VolumePercent = PlaybackVolume.Clamp(volumePercent);
        IsMuted = isMuted;
    }

    public int VolumePercent { get; }

    public bool IsMuted { get; }

    public static PlaybackAudioState Default =>
        new(PlaybackVolume.DefaultPercent, isMuted: false);
}

public readonly record struct PlaybackInitialState
{
    public PlaybackInitialState(PlaybackAudioState audioState, long? startPositionMilliseconds)
    {
        if (startPositionMilliseconds is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startPositionMilliseconds));
        }

        AudioState = audioState;
        StartPositionMilliseconds = startPositionMilliseconds;
    }

    public PlaybackAudioState AudioState { get; }

    public long? StartPositionMilliseconds { get; }
}
