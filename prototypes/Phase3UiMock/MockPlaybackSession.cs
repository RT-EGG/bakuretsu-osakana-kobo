namespace BakuretsuOsakanaKobo.Phase3UiMock;

public sealed class MockPlaybackSession
{
    private static readonly TimeSpan MockDuration = new(hours: 1, minutes: 2, seconds: 3);
    private static readonly double[] SupportedPlaybackRates = [0.25, 0.5, 1.0, 1.5, 2.0];

    public event EventHandler? Changed;

    public MediaUiState State { get; private set; } = MediaUiState.Empty;

    public TimeSpan Position { get; private set; }

    public TimeSpan Duration { get; private set; }

    public bool HasKnownDuration => Duration > TimeSpan.Zero;

    public bool CanControlPlayback => State is MediaUiState.Playing or MediaUiState.Paused;

    public int VolumePercent { get; private set; } = 100;

    public bool IsMuted { get; private set; }

    public double PlaybackRate { get; private set; } = 1.0;

    public void Reset()
    {
        State = MediaUiState.Empty;
        Position = TimeSpan.Zero;
        Duration = TimeSpan.Zero;
        RaiseChanged();
    }

    public void BeginLoading()
    {
        State = MediaUiState.Loading;
        Position = TimeSpan.Zero;
        Duration = TimeSpan.Zero;
        PlaybackRate = 1.0;
        RaiseChanged();
    }

    public void CompleteLoading(TimeSpan? startPosition = null)
    {
        State = MediaUiState.Playing;
        Duration = MockDuration;
        Position = startPosition is { } requestedPosition
                   && requestedPosition >= TimeSpan.Zero
                   && requestedPosition <= Duration
            ? requestedPosition
            : TimeSpan.Zero;
        RaiseChanged();
    }

    public void TogglePlayPause()
    {
        if (State == MediaUiState.Playing)
        {
            State = MediaUiState.Paused;
        }
        else if (State == MediaUiState.Paused)
        {
            State = MediaUiState.Playing;
        }
        else
        {
            return;
        }

        RaiseChanged();
    }

    public void SeekTo(TimeSpan position)
    {
        if (!CanControlPlayback || !HasKnownDuration)
        {
            return;
        }

        Position = position < TimeSpan.Zero
            ? TimeSpan.Zero
            : position > Duration
                ? Duration
                : position;
        RaiseChanged();
    }

    public void Advance(TimeSpan elapsed)
    {
        if (State != MediaUiState.Playing || elapsed <= TimeSpan.Zero)
        {
            return;
        }

        Position += elapsed;
        if (Position >= Duration)
        {
            Position = Duration;
            State = MediaUiState.Paused;
        }

        RaiseChanged();
    }

    public void ShowError()
    {
        State = MediaUiState.Error;
        Position = TimeSpan.Zero;
        Duration = TimeSpan.Zero;
        RaiseChanged();
    }

    public void SetVolume(int volumePercent)
    {
        var clampedVolume = Math.Clamp(volumePercent, 0, 500);
        if (VolumePercent == clampedVolume)
        {
            return;
        }

        VolumePercent = clampedVolume;
        RaiseChanged();
    }

    public void ToggleMute()
    {
        IsMuted = !IsMuted;
        RaiseChanged();
    }

    public bool SetPlaybackRate(double playbackRate)
    {
        if (!SupportedPlaybackRates.Contains(playbackRate))
        {
            return false;
        }

        if (Math.Abs(PlaybackRate - playbackRate) < double.Epsilon)
        {
            return true;
        }

        PlaybackRate = playbackRate;
        RaiseChanged();
        return true;
    }

    public void StepPlaybackRate(int direction)
    {
        if (direction == 0)
        {
            return;
        }

        var currentIndex = Array.IndexOf(SupportedPlaybackRates, PlaybackRate);
        var nextIndex = Math.Clamp(currentIndex + Math.Sign(direction), 0, SupportedPlaybackRates.Length - 1);
        SetPlaybackRate(SupportedPlaybackRates[nextIndex]);
    }

    public static string FormatTime(TimeSpan value)
    {
        var totalHours = (int)Math.Floor(value.TotalHours);
        return $"{totalHours:00}:{value.Minutes:00}:{value.Seconds:00}";
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
