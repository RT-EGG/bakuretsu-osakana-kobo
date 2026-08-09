using LibVLCSharp.Shared;

namespace BakuretsuOsakanaKobo.Playback;

public sealed class LibVlcPlaybackBackend : IPlaybackBackend
{
    private readonly LibVLC _libVlc;
    private readonly Action<Exception>? _callbackExceptionHandler;
    private Media? _currentMedia;
    private bool _disposed;

    public LibVlcPlaybackBackend(Action<Exception>? callbackExceptionHandler = null)
    {
        _callbackExceptionHandler = callbackExceptionHandler;
        Core.Initialize();
        _libVlc = new LibVLC(enableDebugLogs: false);
        MediaPlayer? mediaPlayer = null;

        try
        {
            mediaPlayer = new MediaPlayer(_libVlc);
            MediaPlayer = mediaPlayer;
            SubscribePlayerEvents();
        }
        catch
        {
            mediaPlayer?.Dispose();
            _libVlc.Dispose();
            throw;
        }
    }

    public event EventHandler<PlaybackErrorEventArgs>? ErrorOccurred;

    public event EventHandler? StateChanged;

    public MediaPlayer MediaPlayer { get; }

    public bool IsPlaying
    {
        get
        {
            ThrowIfDisposed();
            return MediaPlayer.IsPlaying;
        }
    }

    public long LengthMilliseconds
    {
        get
        {
            ThrowIfDisposed();
            return Math.Max(0, MediaPlayer.Length);
        }
    }

    public long TimeMilliseconds
    {
        get
        {
            ThrowIfDisposed();
            return Math.Max(0, MediaPlayer.Time);
        }
    }

    public string? CurrentPath { get; private set; }

    public bool OpenAndPlay(string path)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            RaiseError(new PlaybackErrorEventArgs(
                "playback-path-invalid",
                "指定された動画の場所を確認できませんでした。",
                "別の動画ファイルを選択してください。",
                exception.Message,
                exception,
                path));
            return false;
        }

        if (!File.Exists(fullPath))
        {
            RaiseError(new PlaybackErrorEventArgs(
                "playback-file-missing",
                "動画ファイルが見つかりません。",
                "ファイルの場所を確認して、もう一度開いてください。",
                "The requested media file does not exist.",
                targetPath: fullPath));
            return false;
        }

        Media? nextMedia = null;
        try
        {
            nextMedia = new Media(_libVlc, new Uri(fullPath));
            if (!MediaPlayer.Play(nextMedia))
            {
                RaiseError(new PlaybackErrorEventArgs(
                    "playback-start-rejected",
                    "動画の再生を開始できませんでした。",
                    "対応形式とファイルの状態を確認してください。",
                    "LibVLC rejected the play request.",
                    targetPath: fullPath));
                return false;
            }

            var previousMedia = _currentMedia;
            _currentMedia = nextMedia;
            nextMedia = null;
            CurrentPath = fullPath;
            DisposeResource(previousMedia);
            return true;
        }
        catch (Exception exception)
        {
            RaiseError(new PlaybackErrorEventArgs(
                "playback-open-failed",
                "動画を開けませんでした。",
                "対応形式とファイルの状態を確認してください。",
                exception.Message,
                exception,
                fullPath));
            return false;
        }
        finally
        {
            nextMedia?.Dispose();
        }
    }

    public void Play()
    {
        ThrowIfDisposed();
        MediaPlayer.Play();
    }

    public void Pause()
    {
        ThrowIfDisposed();
        MediaPlayer.Pause();
    }

    public void Stop()
    {
        ThrowIfDisposed();
        MediaPlayer.Stop();
    }

    public void Seek(double normalizedPosition)
    {
        ThrowIfDisposed();
        MediaPlayer.Position = (float)Math.Clamp(normalizedPosition, 0d, 1d);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UnsubscribePlayerEvents();

        try
        {
            MediaPlayer.Stop();
        }
        catch (Exception exception)
        {
            ReportCallbackException(exception);
        }

        DisposeResource(_currentMedia);
        _currentMedia = null;
        DisposeResource(MediaPlayer);
        DisposeResource(_libVlc);
    }

    private void SubscribePlayerEvents()
    {
        MediaPlayer.Playing += OnStateChanged;
        MediaPlayer.Paused += OnStateChanged;
        MediaPlayer.Stopped += OnStateChanged;
        MediaPlayer.EndReached += OnStateChanged;
        MediaPlayer.LengthChanged += OnStateChanged;
        MediaPlayer.TimeChanged += OnStateChanged;
        MediaPlayer.EncounteredError += OnEncounteredError;
    }

    private void UnsubscribePlayerEvents()
    {
        MediaPlayer.Playing -= OnStateChanged;
        MediaPlayer.Paused -= OnStateChanged;
        MediaPlayer.Stopped -= OnStateChanged;
        MediaPlayer.EndReached -= OnStateChanged;
        MediaPlayer.LengthChanged -= OnStateChanged;
        MediaPlayer.TimeChanged -= OnStateChanged;
        MediaPlayer.EncounteredError -= OnEncounteredError;
    }

    private void OnStateChanged(object? sender, EventArgs eventArgs) => RaiseSafely(StateChanged, EventArgs.Empty);

    private void OnEncounteredError(object? sender, EventArgs eventArgs) =>
        RaiseError(new PlaybackErrorEventArgs(
            "playback-native-error",
            "動画の再生中に問題が発生しました。",
            "別の動画を開くか、ファイルの状態を確認してください。",
            "LibVLC raised the MediaPlayer.EncounteredError event.",
            targetPath: CurrentPath));

    private void RaiseError(PlaybackErrorEventArgs eventArgs) => RaiseSafely(ErrorOccurred, eventArgs);

    private void RaiseSafely(EventHandler? handlers, EventArgs eventArgs)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, eventArgs);
            }
            catch (Exception exception)
            {
                ReportCallbackException(exception);
            }
        }
    }

    private void RaiseSafely<TEventArgs>(EventHandler<TEventArgs>? handlers, TEventArgs eventArgs)
        where TEventArgs : EventArgs
    {
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<TEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, eventArgs);
            }
            catch (Exception exception)
            {
                ReportCallbackException(exception);
            }
        }
    }

    private void ReportCallbackException(Exception exception)
    {
        try
        {
            _callbackExceptionHandler?.Invoke(exception);
        }
        catch
        {
            // A managed exception must never escape a native LibVLC callback.
        }
    }

    private void DisposeResource(IDisposable? resource)
    {
        try
        {
            resource?.Dispose();
        }
        catch (Exception exception)
        {
            ReportCallbackException(exception);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
