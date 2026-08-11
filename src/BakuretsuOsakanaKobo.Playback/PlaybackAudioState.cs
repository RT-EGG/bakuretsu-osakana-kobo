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
