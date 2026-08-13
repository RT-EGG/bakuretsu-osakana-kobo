using LibVLCSharp.Shared;

namespace BakuretsuOsakanaKobo.Playback;

public sealed class LibVlcPlaybackBackend : IPlaybackBackend
{
    private readonly LibVLC _libVlc;
    private readonly Action<Exception>? _callbackExceptionHandler;
    private readonly RealtimeAudioOutput _audioOutput;
    private Media? _currentMedia;
    private int _volumePercent = PlaybackVolume.DefaultPercent;
    private bool _isMuted;
    private float _rate = PlaybackRate.Default;
    private bool _disposed;

    public LibVlcPlaybackBackend(Action<Exception>? callbackExceptionHandler = null)
    {
        _callbackExceptionHandler = callbackExceptionHandler;
        Core.Initialize();
        _libVlc = new LibVLC(enableDebugLogs: false);
        MediaPlayer? mediaPlayer = null;
        RealtimeAudioOutput? audioOutput = null;

        try
        {
            mediaPlayer = new MediaPlayer(_libVlc);
            MediaPlayer = mediaPlayer;
            audioOutput = new RealtimeAudioOutput(OnAudioOutputFailure);
            audioOutput.AttachTo(MediaPlayer);
            _audioOutput = audioOutput;
            MediaPlayer.Volume = PlaybackVolume.DefaultPercent;
            MediaPlayer.Mute = false;
            SubscribePlayerEvents();
        }
        catch
        {
            mediaPlayer?.Dispose();
            audioOutput?.Dispose();
            _libVlc.Dispose();
            throw;
        }
    }

    public event EventHandler<PlaybackErrorEventArgs>? ErrorOccurred;

    public event EventHandler? StateChanged;

    public event EventHandler? PlaybackEnded;

    public MediaPlayer MediaPlayer { get; }

    public bool IsPlaying
    {
        get
        {
            ThrowIfDisposed();
            return MediaPlayer.IsPlaying;
        }
    }

    public bool IsSeekable
    {
        get
        {
            ThrowIfDisposed();
            return MediaPlayer.IsSeekable;
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

    public int VolumePercent
    {
        get
        {
            ThrowIfDisposed();
            return _volumePercent;
        }
    }

    public bool IsMuted
    {
        get
        {
            ThrowIfDisposed();
            return _isMuted;
        }
    }

    public float Rate
    {
        get
        {
            ThrowIfDisposed();
            return _rate;
        }
    }

    public string? CurrentPath { get; private set; }

    internal RealtimeAudioDiagnostics AudioDiagnostics => _audioOutput.Diagnostics;

    public async Task<bool> OpenAndPlayAsync(
        string path,
        PlaybackInitialState? initialState = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

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

        if (!SupportedMediaPolicy.SupportsExtension(fullPath))
        {
            RaisePolicyError(SupportedMediaPolicy.Validate(fullPath, null, null), fullPath);
            return false;
        }

        try
        {
            using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
        }
        catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            RaiseError(new PlaybackErrorEventArgs(
                "playback-file-missing",
                "動画ファイルが見つかりません。",
                "ファイルの場所を確認して、もう一度開いてください。",
                exception.Message,
                exception,
                targetPath: fullPath));
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            RaiseError(new PlaybackErrorEventArgs(
                "playback-file-access-denied",
                "動画ファイルを読み取る権限がありません。",
                "ファイルのアクセス権限を確認してください。",
                exception.Message,
                exception,
                fullPath));
            return false;
        }
        catch (IOException exception)
        {
            RaiseError(new PlaybackErrorEventArgs(
                "playback-file-unreadable",
                "動画ファイルを読み取れませんでした。",
                "ファイルが使用中でないか、状態を確認してください。",
                exception.Message,
                exception,
                fullPath));
            return false;
        }

        Media? nextMedia = null;
        var previousAudioState = new PlaybackAudioState(_volumePercent, _isMuted);
        var nextAudioState = initialState?.AudioState ?? previousAudioState;
        var nextAudioStateApplied = false;
        var nextAudioStateCommitted = false;
        try
        {
            nextMedia = new Media(_libVlc, new Uri(fullPath));
            var parseStatus = await nextMedia.Parse(
                MediaParseOptions.ParseLocal,
                5_000,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (parseStatus != MediaParsedStatus.Done)
            {
                RaiseError(new PlaybackErrorEventArgs(
                    "playback-parse-failed",
                    "動画ファイルを解析できませんでした。",
                    "ファイルが破損していないか確認してください。",
                    $"LibVLC media parse status was {parseStatus}.",
                    targetPath: fullPath));
                return false;
            }

            var videoCodec = nextMedia.Tracks
                .FirstOrDefault(track => track.TrackType == TrackType.Video)
                .Codec;
            var audioCodec = nextMedia.Tracks
                .FirstOrDefault(track => track.TrackType == TrackType.Audio)
                .Codec;
            var mediaPolicy = SupportedMediaPolicy.Validate(
                fullPath,
                FourCc(videoCodec),
                FourCc(audioCodec));
            if (!mediaPolicy.IsAccepted)
            {
                RaisePolicyError(mediaPolicy, fullPath);
                return false;
            }

            // A staging player is only needed when a failed candidate must not disturb an
            // existing playback session. Avoid decoding the first video twice on startup.
            if (CurrentPath is not null &&
                !await CanStartInStagingPlayerAsync(fullPath, cancellationToken).ConfigureAwait(false))
            {
                RaiseError(new PlaybackErrorEventArgs(
                    "playback-staging-failed",
                    "動画の再生準備を完了できませんでした。",
                    "ファイルが破損していないか確認してください。",
                    "The muted staging MediaPlayer did not enter the playing state.",
                    targetPath: fullPath));
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            _volumePercent = nextAudioState.VolumePercent;
            _isMuted = nextAudioState.IsMuted;
            nextAudioStateApplied = true;
            ApplyVolumeState();
            _audioOutput.PrepareForPlayback();
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

            if (MediaPlayer.SetRate(PlaybackRate.Default) != 0)
            {
                RaiseError(new PlaybackErrorEventArgs(
                    "playback-default-rate-rejected",
                    "新しい動画を標準速度へ戻せませんでした。",
                    "右クリックメニューから1.0倍を選び直してください。",
                    "LibVLC rejected the default 1.0 playback rate after opening media.",
                    targetPath: fullPath));
            }
            else
            {
                _rate = PlaybackRate.Default;
            }

            var initialPosition = PlaybackPosition.ResolveInitialPosition(
                initialState?.StartPositionMilliseconds,
                Math.Max(0, nextMedia.Duration));
            if (initialPosition is not null)
            {
                MediaPlayer.Position = (float)initialPosition.Value;
            }

            var previousMedia = _currentMedia;
            _currentMedia = nextMedia;
            nextMedia = null;
            CurrentPath = fullPath;
            nextAudioStateCommitted = true;
            DisposeResource(previousMedia);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
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
            if (nextAudioStateApplied && !nextAudioStateCommitted)
            {
                _volumePercent = previousAudioState.VolumePercent;
                _isMuted = previousAudioState.IsMuted;
                ApplyVolumeState();
            }

            if (nextMedia is not null)
            {
                try
                {
                    nextMedia.ParseStop();
                }
                catch (Exception exception)
                {
                    ReportCallbackException(exception);
                }

                // Parse() completes from a native notification. Keep rejected media alive until
                // the notification has fully unwound before releasing its native handle.
                await Task.Delay(150, CancellationToken.None).ConfigureAwait(false);
                DisposeResource(nextMedia);
            }
        }
    }

    public void Play()
    {
        ThrowIfDisposed();
        _audioOutput.PrepareForPlayback();
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
        MediaPlayer.Position = (float)PlaybackPosition.Normalize(normalizedPosition);
    }

    public void SetVolumePercent(int volumePercent)
    {
        ThrowIfDisposed();
        _volumePercent = PlaybackVolume.Clamp(volumePercent);
        _audioOutput.SetVolumePercent(_volumePercent);
    }

    public void SetMuted(bool isMuted)
    {
        ThrowIfDisposed();
        _isMuted = isMuted;
        _audioOutput.SetMuted(isMuted);
    }

    public bool TrySetRate(float rate)
    {
        ThrowIfDisposed();
        if (CurrentPath is null || !PlaybackRate.IsSupported(rate))
        {
            return false;
        }

        if (PlaybackRate.AreEqual(_rate, rate))
        {
            return true;
        }

        _audioOutput.PrepareForRateChange();
        if (MediaPlayer.SetRate(rate) != 0)
        {
            return false;
        }

        _rate = rate;
        RaiseSafely(StateChanged, EventArgs.Empty);
        return true;
    }

    private void ApplyVolumeState()
    {
        _audioOutput.SetVolumePercent(_volumePercent);
        _audioOutput.SetMuted(_isMuted);
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
        _audioOutput.Dispose();
        DisposeResource(_libVlc);
    }

    private void SubscribePlayerEvents()
    {
        MediaPlayer.Playing += OnStateChanged;
        MediaPlayer.Paused += OnStateChanged;
        MediaPlayer.Stopped += OnStateChanged;
        MediaPlayer.EndReached += OnPlaybackEnded;
        MediaPlayer.EncounteredError += OnEncounteredError;
    }

    private void UnsubscribePlayerEvents()
    {
        MediaPlayer.Playing -= OnStateChanged;
        MediaPlayer.Paused -= OnStateChanged;
        MediaPlayer.Stopped -= OnStateChanged;
        MediaPlayer.EndReached -= OnPlaybackEnded;
        MediaPlayer.EncounteredError -= OnEncounteredError;
    }

    private void OnStateChanged(object? sender, EventArgs eventArgs) => RaiseSafely(StateChanged, EventArgs.Empty);

    private void OnPlaybackEnded(object? sender, EventArgs eventArgs)
    {
        RaiseSafely(StateChanged, EventArgs.Empty);
        RaiseSafely(PlaybackEnded, EventArgs.Empty);
    }

    private void OnEncounteredError(object? sender, EventArgs eventArgs) =>
        RaiseError(new PlaybackErrorEventArgs(
            "playback-native-error",
            "動画の再生中に問題が発生しました。",
            "別の動画を開くか、ファイルの状態を確認してください。",
            "LibVLC raised the MediaPlayer.EncounteredError event.",
            targetPath: CurrentPath));

    private void OnAudioOutputFailure(Exception exception) =>
        RaiseError(new PlaybackErrorEventArgs(
            "playback-audio-output-error",
            "音声の再生中に問題が発生しました。",
            "Windowsの音声出力を確認して、アプリを再起動してください。",
            exception.Message,
            exception,
            CurrentPath));

    private void RaiseError(PlaybackErrorEventArgs eventArgs) => RaiseSafely(ErrorOccurred, eventArgs);

    private async Task<bool> CanStartInStagingPlayerAsync(string path, CancellationToken cancellationToken)
    {
        Media? stagingMedia = null;
        MediaPlayer? stagingPlayer = null;

        try
        {
            stagingMedia = new Media(_libVlc, new Uri(path));
            stagingMedia.AddOption(":aout=dummy");
            stagingMedia.AddOption(":vout=dummy");
            stagingPlayer = new MediaPlayer(_libVlc)
            {
                Mute = true,
            };

            if (!stagingPlayer.Play(stagingMedia))
            {
                return false;
            }

            var deadline = DateTime.UtcNow.AddMilliseconds(1_500);
            while (!stagingPlayer.IsPlaying && DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stagingPlayer.State is VLCState.Error or VLCState.Ended or VLCState.Stopped)
                {
                    return false;
                }

                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }

            return stagingPlayer.IsPlaying;
        }
        finally
        {
            if (stagingPlayer is not null)
            {
                try
                {
                    stagingPlayer.Stop();
                }
                catch (Exception exception)
                {
                    ReportCallbackException(exception);
                }

                // Stop() can return while LibVLC is still unwinding decoder work on another thread.
                // Keep the staging objects alive briefly before releasing their native handles.
                await Task.Delay(150, CancellationToken.None).ConfigureAwait(false);
            }

            DisposeResource(stagingMedia);
            DisposeResource(stagingPlayer);
        }
    }

    private void RaisePolicyError(MediaPolicyResult result, string targetPath) =>
        RaiseError(new PlaybackErrorEventArgs(
            result.EventCode!,
            result.UserMessage!,
            result.SuggestedAction!,
            result.TechnicalMessage!,
            targetPath: targetPath));

    private static string? FourCc(uint value)
    {
        if (value == 0)
        {
            return null;
        }

        Span<char> characters = stackalloc char[4];
        for (var index = 0; index < characters.Length; index++)
        {
            characters[index] = (char)((value >> (index * 8)) & 0xff);
        }

        return new string(characters).TrimEnd('\0');
    }

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
