using System.Diagnostics;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace BakuretsuOsakanaKobo.Playback;

internal sealed class LibVlcThumbnailFrameExtractor(Action<Exception>? callbackExceptionHandler = null)
    : IThumbnailFrameExtractor
{
    private const int OutputWidth = 320;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DecoderShutdownDelay = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan CooperativeDelay = TimeSpan.FromMilliseconds(25);

    public async Task ExtractAsync(
        ThumbnailGenerationRun run,
        Action<ThumbnailFrame> frameReady,
        CancellationToken cancellationToken)
    {
        Core.Initialize();
        using var libVlc = new LibVLC(
            "--no-audio",
            "--no-video-title-show",
            "--avcodec-hw=none",
            "--file-caching=100");
        using var media = new Media(libVlc, new Uri(run.VideoPath));
        await media.Parse(MediaParseOptions.ParseLocal, 8_000, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var videoTrack = media.Tracks.FirstOrDefault(track => track.TrackType == TrackType.Video);
        if (videoTrack.TrackType != TrackType.Video)
        {
            throw new InvalidOperationException("The media does not contain a video track.");
        }

        var sourceWidth = checked((int)videoTrack.Data.Video.Width);
        var sourceHeight = checked((int)videoTrack.Data.Video.Height);
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            throw new InvalidOperationException("The video dimensions could not be determined.");
        }

        var outputHeight = Math.Max(
            2,
            checked((int)Math.Round(sourceHeight * (OutputWidth / (double)sourceWidth) / 2) * 2));
        var player = new MediaPlayer(libVlc) { Mute = true };
        var sink = new ThumbnailFrameSink(OutputWidth, outputHeight, callbackExceptionHandler);
        var playbackError = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<EventArgs> errorHandler = (_, _) => playbackError.TrySetResult();
        player.EncounteredError += errorHandler;
        sink.Attach(player);

        try
        {
            var duration = media.Duration > 0 ? media.Duration : run.DurationMilliseconds;
            var thumbnailCount = checked((int)Math.Ceiling(100 / run.IntervalPercent));
            for (var index = 0; index < thumbnailCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fraction = Math.Min(0.999_999, index * run.IntervalPercent / 100);
                var target = Math.Min(duration - 1, Math.Max(0, (long)Math.Round(duration * fraction)));
                byte[] pixels;

                if (index == 0)
                {
                    var capture = sink.RequestFrame(cancellationToken);
                    if (!player.Play(media))
                    {
                        throw new InvalidOperationException("LibVLC could not start thumbnail extraction.");
                    }

                    pixels = await WaitForFrameAsync(capture, playbackError.Task, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    player.SetPause(true);
                    player.Time = target;
                    player.SetPause(false);
                    await WaitForPlaybackClockAsync(player, target, playbackError.Task, cancellationToken)
                        .ConfigureAwait(false);
                    pixels = await WaitForFrameAsync(
                            sink.RequestFrame(cancellationToken),
                            playbackError.Task,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                player.SetPause(true);
                frameReady(new ThumbnailFrame(
                    target,
                    Math.Max(0, player.Time),
                    OutputWidth,
                    outputHeight,
                    checked(OutputWidth * 4),
                    pixels));

                await Task.Delay(CooperativeDelay, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            player.EncounteredError -= errorHandler;
            sink.CancelPending();
            try
            {
                player.Stop();
            }
            catch (Exception exception)
            {
                ReportCallbackException(exception);
            }

            // Stop can return while a decoder callback is still unwinding on a native thread.
            await Task.Delay(DecoderShutdownDelay, CancellationToken.None).ConfigureAwait(false);
            // LibVLCSharp does not expose nullable video callbacks. Releasing MediaPlayer first
            // unregisters native callbacks while the delegates and unmanaged buffer remain alive.
            try
            {
                player.Dispose();
            }
            finally
            {
                sink.Dispose();
            }
        }
    }

    private static async Task<byte[]> WaitForFrameAsync(
        Task<byte[]> capture,
        Task playbackError,
        CancellationToken cancellationToken)
    {
        var timeout = Task.Delay(OperationTimeout, cancellationToken);
        var completed = await Task.WhenAny(capture, playbackError, timeout).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (completed == playbackError)
        {
            throw new InvalidOperationException("LibVLC reported a thumbnail decoding error.");
        }

        if (completed != capture)
        {
            throw new TimeoutException("Thumbnail frame capture did not complete in time.");
        }

        return await capture.ConfigureAwait(false);
    }

    private static async Task WaitForPlaybackClockAsync(
        MediaPlayer player,
        long targetMilliseconds,
        Task playbackError,
        CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        while (player.Time < targetMilliseconds - 100 && deadline.Elapsed < OperationTimeout)
        {
            if (playbackError.IsCompleted)
            {
                throw new InvalidOperationException("LibVLC reported a thumbnail decoding error.");
            }

            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        }

        if (player.Time < targetMilliseconds - 100)
        {
            throw new TimeoutException($"Thumbnail seek did not reach {targetMilliseconds} ms in time.");
        }
    }

    private void ReportCallbackException(Exception exception)
    {
        try
        {
            callbackExceptionHandler?.Invoke(exception);
        }
        catch
        {
            // Exceptions must not escape cleanup paths reached from native work.
        }
    }

    private sealed class ThumbnailFrameSink : IDisposable
    {
        private readonly object _sync = new();
        private readonly IntPtr _buffer;
        private readonly int _byteCount;
        private readonly Action<Exception>? _callbackExceptionHandler;
        private readonly MediaPlayer.LibVLCVideoLockCb _lockCallback;
        private readonly MediaPlayer.LibVLCVideoUnlockCb _unlockCallback;
        private readonly MediaPlayer.LibVLCVideoDisplayCb _displayCallback;
        private TaskCompletionSource<byte[]>? _requestedFrame;
        private bool _disposed;

        public ThumbnailFrameSink(int width, int height, Action<Exception>? callbackExceptionHandler)
        {
            Width = width;
            Height = height;
            _byteCount = checked(width * height * 4);
            _buffer = Marshal.AllocHGlobal(_byteCount);
            _callbackExceptionHandler = callbackExceptionHandler;
            _lockCallback = Lock;
            _unlockCallback = static (_, _, _) => { };
            _displayCallback = Display;
        }

        public int Width { get; }

        public int Height { get; }

        public void Attach(MediaPlayer player)
        {
            player.SetVideoCallbacks(_lockCallback, _unlockCallback, _displayCallback);
            player.SetVideoFormat("RV32", (uint)Width, (uint)Height, checked((uint)Width * 4));
        }

        public Task<byte[]> RequestFrame(CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _requestedFrame?.TrySetCanceled(cancellationToken);
                _requestedFrame = new TaskCompletionSource<byte[]>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                return _requestedFrame.Task;
            }
        }

        public void CancelPending()
        {
            lock (_sync)
            {
                _requestedFrame?.TrySetCanceled();
                _requestedFrame = null;
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _requestedFrame?.TrySetCanceled();
                _requestedFrame = null;
            }

            Marshal.FreeHGlobal(_buffer);
        }

        private IntPtr Lock(IntPtr opaque, IntPtr planes)
        {
            try
            {
                Marshal.WriteIntPtr(planes, _buffer);
            }
            catch (Exception exception)
            {
                ReportCallbackException(exception);
            }

            return IntPtr.Zero;
        }

        private void Display(IntPtr opaque, IntPtr picture)
        {
            try
            {
                TaskCompletionSource<byte[]>? request;
                lock (_sync)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    request = _requestedFrame;
                    _requestedFrame = null;
                }

                if (request is null)
                {
                    return;
                }

                var pixels = new byte[_byteCount];
                Marshal.Copy(_buffer, pixels, 0, _byteCount);
                request.TrySetResult(pixels);
            }
            catch (Exception exception)
            {
                ReportCallbackException(exception);
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
                // Managed exceptions must never escape a native LibVLC callback.
            }
        }
    }
}
