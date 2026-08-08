using System.Windows.Threading;

namespace BakuretsuOsakanaKobo.Spikes.WpfWindowing;

internal interface IPlaybackBackend : IDisposable
{
    event EventHandler? StateChanged;

    bool IsPlaying { get; }
    TimeSpan Position { get; }
    TimeSpan Duration { get; }
    void Play();
    void Pause();
    void Seek(double normalizedPosition);
}

internal sealed class MockPlaybackBackend : IPlaybackBackend
{
    private readonly DispatcherTimer _timer;

    public MockPlaybackBackend()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _timer.Tick += OnTick;
    }

    public event EventHandler? StateChanged;

    public bool IsPlaying => _timer.IsEnabled;
    public TimeSpan Position { get; private set; }
    public TimeSpan Duration { get; } = TimeSpan.FromMinutes(2);

    public void Play()
    {
        _timer.Start();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Pause()
    {
        _timer.Stop();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Seek(double normalizedPosition)
    {
        Position = Duration * Math.Clamp(normalizedPosition, 0, 1);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        Position += _timer.Interval;
        if (Position >= Duration)
        {
            Position = Duration;
            _timer.Stop();
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
