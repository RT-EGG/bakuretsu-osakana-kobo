using System.IO;
using LibVLCSharp.Shared;

namespace BakuretsuOsakanaKobo.Spikes.LibVlcWpf.Playback;

internal sealed class LibVlcPlaybackBackend : IPlaybackBackend
{
    private readonly LibVLC _libVlc;
    private readonly object _logSync = new();
    private readonly string? _validationLogPath;
    private Media? _currentMedia;
    private bool _disposed;

    public LibVlcPlaybackBackend()
    {
        _libVlc = new LibVLC(enableDebugLogs: true);
        _validationLogPath = Environment.GetEnvironmentVariable("BOK_WPF_VLC_LOG");
        _libVlc.Log += OnLog;

        MediaPlayer = new MediaPlayer(_libVlc);
        MediaPlayer.EncounteredError += OnEncounteredError;
    }

    public event EventHandler<string>? ErrorOccurred;

    public event EventHandler<string>? LogObserved;

    public MediaPlayer MediaPlayer { get; }

    public bool IsPlaying => MediaPlayer.IsPlaying;

    public long LengthMilliseconds => Math.Max(0, MediaPlayer.Length);

    public long TimeMilliseconds => Math.Max(0, MediaPlayer.Time);

    public int VolumePercent => MediaPlayer.Volume;

    public float Rate => MediaPlayer.Rate;

    public string? CurrentPath { get; private set; }

    public bool Open(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!File.Exists(path))
        {
            ErrorOccurred?.Invoke(this, $"ファイルが見つかりません: {path}");
            return false;
        }

        Media? media = null;
        try
        {
            media = new Media(_libVlc, new Uri(Path.GetFullPath(path)));
            if (!MediaPlayer.Play(media))
            {
                ErrorOccurred?.Invoke(this, "LibVLCが再生開始要求を受け付けませんでした。");
                media.Dispose();
                return false;
            }

            _currentMedia?.Dispose();
            _currentMedia = media;
            CurrentPath = Path.GetFullPath(path);
            return true;
        }
        catch (Exception exception)
        {
            media?.Dispose();
            ErrorOccurred?.Invoke(this, $"動画を開けませんでした: {exception.Message}");
            return false;
        }
    }

    public void Play()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        MediaPlayer.Play();
    }

    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        MediaPlayer.Pause();
    }

    public void Seek(double normalizedPosition)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        MediaPlayer.Position = (float)Math.Clamp(normalizedPosition, 0d, 1d);
    }

    public bool SetRate(float rate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return MediaPlayer.SetRate(rate) == 0;
    }

    public int SetVolume(int requestedPercent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        MediaPlayer.Volume = Math.Clamp(requestedPercent, 0, 500);
        return MediaPlayer.Volume;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _libVlc.Log -= OnLog;
        MediaPlayer.EncounteredError -= OnEncounteredError;
        MediaPlayer.Stop();
        _currentMedia?.Dispose();
        MediaPlayer.Dispose();
        _libVlc.Dispose();
    }

    private void OnEncounteredError(object? sender, EventArgs eventArgs)
    {
        ErrorOccurred?.Invoke(this, "LibVLCがメディアエラーを通知しました。詳細はデバッグログを確認してください。");
    }

    private void OnLog(object? sender, LogEventArgs eventArgs)
    {
        LogObserved?.Invoke(this, eventArgs.FormattedLog);

        if (string.IsNullOrWhiteSpace(_validationLogPath))
        {
            return;
        }

        lock (_logSync)
        {
            File.AppendAllText(
                _validationLogPath,
                $"{DateTimeOffset.Now:O} {eventArgs.FormattedLog}{Environment.NewLine}");
        }
    }
}
