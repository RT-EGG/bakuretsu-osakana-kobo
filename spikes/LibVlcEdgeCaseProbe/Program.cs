using System.Runtime.InteropServices;
using System.Text.Json;
using LibVLCSharp.Shared;

if (args.Length != 2)
{
    throw new ArgumentException("Usage: LibVlcEdgeCaseProbe <asset-directory> <report-json>");
}

var assetDirectory = Path.GetFullPath(args[0]);
var reportPath = Path.GetFullPath(args[1]);
Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);

Core.Initialize();
using var libVlc = new LibVLC("--no-video-title-show", "--avcodec-hw=none", "--file-caching=150");
var report = new ProbeReport { LibVlcVersion = libVlc.Version };

foreach (var path in Directory.EnumerateFiles(assetDirectory, "edge-*.mp4").OrderBy(Path.GetFileName))
{
    Console.WriteLine($"Probing {Path.GetFileName(path)}");
    report.Files.Add(await ProbeAsync(libVlc, path));
}

report.StateReplacement = await ProbeStateReplacementAsync(
    libVlc,
    Path.Combine(assetDirectory, "edge-h264-vfr-aac.mp4"),
    Path.Combine(assetDirectory, "edge-not-media.mp4"));
report.StagingValidation = await ProbeStagingValidationAsync(libVlc, assetDirectory);

report.FinishedAt = DateTimeOffset.UtcNow;
await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Report: {reportPath}");

static async Task<FileResult> ProbeAsync(LibVLC libVlc, string path)
{
    var result = new FileResult { File = Path.GetFileName(path) };
    using var media = new Media(libVlc, new Uri(path));
    try
    {
        result.ParseStatus = (await media.Parse(MediaParseOptions.ParseLocal, 5_000, CancellationToken.None)).ToString();
        result.DurationMilliseconds = media.Duration;
        var videoTracks = media.Tracks.Where(track => track.TrackType == TrackType.Video).ToArray();
        var audioTracks = media.Tracks.Where(track => track.TrackType == TrackType.Audio).ToArray();
        if (videoTracks.Length > 0)
        {
            var video = videoTracks[0];
            result.VideoCodec = FourCc(video.Codec);
            result.Width = video.Data.Video.Width;
            result.Height = video.Data.Video.Height;
            result.FrameRate = $"{video.Data.Video.FrameRateNum}/{video.Data.Video.FrameRateDen}";
        }
        if (audioTracks.Length > 0) result.AudioCodec = FourCc(audioTracks[0].Codec);
    }
    catch (Exception exception)
    {
        result.ParseException = exception.GetType().Name + ": " + exception.Message;
    }

    using var player = new MediaPlayer(libVlc);
    using var sink = new VideoSink(Math.Max(result.Width, 320), Math.Max(result.Height, 180));
    sink.Attach(player);
    var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var error = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    player.Playing += (_, _) => playing.TrySetResult();
    player.EndReached += (_, _) => ended.TrySetResult();
    player.EncounteredError += (_, _) => error.TrySetResult();

    result.PlayAccepted = player.Play(media);
    if (!result.PlayAccepted) return result;

    var deadline = DateTime.UtcNow.AddSeconds(8);
    while (DateTime.UtcNow < deadline && !ended.Task.IsCompleted && !error.Task.IsCompleted)
    {
        result.MaximumPlaybackTimeMilliseconds = Math.Max(result.MaximumPlaybackTimeMilliseconds, player.Time);
        if (sink.Frames > 0 && playing.Task.IsCompleted && result.File.Contains("not-media", StringComparison.Ordinal)) break;
        await Task.Delay(20);
    }

    result.PlayingEvent = playing.Task.IsCompleted;
    result.EndReached = ended.Task.IsCompleted;
    result.EncounteredError = error.Task.IsCompleted;
    result.VideoFrames = sink.Frames;
    result.MaximumPlaybackTimeMilliseconds = Math.Max(result.MaximumPlaybackTimeMilliseconds, player.Time);
    player.Stop();
    return result;
}

static async Task<StateReplacementResult> ProbeStateReplacementAsync(
    LibVLC libVlc,
    string baselinePath,
    string invalidPath)
{
    using var player = new MediaPlayer(libVlc);
    using var sink = new VideoSink(1920, 1080);
    using var baseline = new Media(libVlc, new Uri(baselinePath));
    using var invalid = new Media(libVlc, new Uri(invalidPath));
    sink.Attach(player);

    if (!player.Play(baseline)) throw new InvalidOperationException("Baseline playback was rejected.");
    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (player.Time < 300 && DateTime.UtcNow < deadline) await Task.Delay(20);

    var result = new StateReplacementResult
    {
        BaselineTimeBeforeOpenMilliseconds = player.Time,
        BaselineFramesBeforeOpen = sink.Frames,
    };
    var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var error = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    player.EndReached += (_, _) => ended.TrySetResult();
    player.EncounteredError += (_, _) => error.TrySetResult();

    result.InvalidPlayAccepted = player.Play(invalid);
    await Task.Delay(500);
    result.TimeAfterInvalidOpenMilliseconds = player.Time;
    result.FramesAfterInvalidOpen = sink.Frames;
    result.InvalidEndReached = ended.Task.IsCompleted;
    result.InvalidEncounteredError = error.Task.IsCompleted;
    result.BaselineStatePreserved =
        result.TimeAfterInvalidOpenMilliseconds > result.BaselineTimeBeforeOpenMilliseconds &&
        result.FramesAfterInvalidOpen > result.BaselineFramesBeforeOpen;
    player.Stop();
    return result;
}

static async Task<List<StagingResult>> ProbeStagingValidationAsync(LibVLC libVlc, string assetDirectory)
{
    var results = new List<StagingResult>();
    var baselinePath = Path.Combine(assetDirectory, "edge-h264-vfr-aac.mp4");
    foreach (var candidatePath in Directory.EnumerateFiles(assetDirectory, "edge-*.mp4").OrderBy(Path.GetFileName))
    {
        using var currentPlayer = new MediaPlayer(libVlc);
        using var currentSink = new VideoSink(1920, 1080);
        using var baseline = new Media(libVlc, new Uri(baselinePath));
        currentSink.Attach(currentPlayer);
        if (!currentPlayer.Play(baseline)) throw new InvalidOperationException("Baseline playback was rejected.");
        var baselineDeadline = DateTime.UtcNow.AddSeconds(5);
        while (currentPlayer.Time < 300 && DateTime.UtcNow < baselineDeadline) await Task.Delay(20);

        var result = new StagingResult
        {
            File = Path.GetFileName(candidatePath),
            BaselineTimeBeforeValidationMilliseconds = currentPlayer.Time,
            BaselineFramesBeforeValidation = currentSink.Frames,
        };

        using var candidate = new Media(libVlc, new Uri(candidatePath));
        var parseStatus = await candidate.Parse(MediaParseOptions.ParseLocal, 5_000, CancellationToken.None);
        var videoTracks = candidate.Tracks.Where(track => track.TrackType == TrackType.Video).ToArray();
        var audioTracks = candidate.Tracks.Where(track => track.TrackType == TrackType.Audio).ToArray();
        if (parseStatus != MediaParsedStatus.Done || videoTracks.Length == 0 || audioTracks.Length == 0)
        {
            result.RejectionReason = "Missing or unreadable video/audio tracks.";
        }
        else if (!string.Equals(FourCc(videoTracks[0].Codec), "h264", StringComparison.OrdinalIgnoreCase))
        {
            result.RejectionReason = $"Unsupported video codec: {FourCc(videoTracks[0].Codec)}";
        }
        else if (!string.Equals(FourCc(audioTracks[0].Codec), "mp4a", StringComparison.OrdinalIgnoreCase))
        {
            result.RejectionReason = $"Unsupported audio codec: {FourCc(audioTracks[0].Codec)}";
        }
        else
        {
            result.PreflightAccepted = true;
            using var stagingPlayer = new MediaPlayer(libVlc);
            using var stagingSink = new VideoSink(
                Math.Max(videoTracks[0].Data.Video.Width, 320),
                Math.Max(videoTracks[0].Data.Video.Height, 180));
            stagingSink.Attach(stagingPlayer);
            var stagingError = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            stagingPlayer.EncounteredError += (_, _) => stagingError.TrySetResult();
            result.StagingPlayAccepted = stagingPlayer.Play(candidate);
            if (result.StagingPlayAccepted)
            {
                var ready = Task.WhenAll(stagingSink.FirstVideoFrame, stagingSink.FirstAudioBlock);
                await Task.WhenAny(ready, stagingError.Task, Task.Delay(3_000));
                result.FirstVideoFrame = stagingSink.FirstVideoFrame.IsCompleted;
                result.FirstAudioBlock = stagingSink.FirstAudioBlock.IsCompleted;
                result.StagingEncounteredError = stagingError.Task.IsCompleted;
            }
            stagingPlayer.Stop();
        }

        await Task.Delay(150);
        result.BaselineTimeAfterValidationMilliseconds = currentPlayer.Time;
        result.BaselineFramesAfterValidation = currentSink.Frames;
        result.BaselineStillPlaying = currentPlayer.IsPlaying;
        result.BaselineStatePreserved =
            result.BaselineStillPlaying &&
            result.BaselineTimeAfterValidationMilliseconds >= result.BaselineTimeBeforeValidationMilliseconds &&
            result.BaselineFramesAfterValidation > result.BaselineFramesBeforeValidation;
        currentPlayer.Stop();
        results.Add(result);
    }
    return results;
}

static string FourCc(uint value)
{
    Span<char> chars = stackalloc char[4];
    for (var index = 0; index < 4; index++) chars[index] = (char)((value >> (index * 8)) & 0xff);
    return new string(chars).TrimEnd('\0');
}

internal sealed class VideoSink : IDisposable
{
    private readonly IntPtr _buffer;
    private readonly MediaPlayer.LibVLCVideoLockCb _lock;
    private readonly MediaPlayer.LibVLCVideoUnlockCb _unlock;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _display;
    private readonly MediaPlayer.LibVLCAudioPlayCb _audioPlay;
    private readonly MediaPlayer.LibVLCAudioPauseCb _audioPause;
    private readonly MediaPlayer.LibVLCAudioResumeCb _audioResume;
    private readonly MediaPlayer.LibVLCAudioFlushCb _audioFlush;
    private readonly MediaPlayer.LibVLCAudioDrainCb _audioDrain;
    private readonly TaskCompletionSource _firstVideoFrame =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _firstAudioBlock =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _frames;

    public VideoSink(uint width, uint height)
    {
        Width = width;
        Height = height;
        _buffer = Marshal.AllocHGlobal(checked((int)(width * height * 4)));
        _lock = (_, planes) => { Marshal.WriteIntPtr(planes, _buffer); return IntPtr.Zero; };
        _unlock = (_, _, _) => { };
        _display = (_, _) => { Interlocked.Increment(ref _frames); _firstVideoFrame.TrySetResult(); };
        _audioPlay = (_, _, _, _) => _firstAudioBlock.TrySetResult();
        _audioPause = (_, _) => { };
        _audioResume = (_, _) => { };
        _audioFlush = (_, _) => { };
        _audioDrain = _ => { };
    }

    public uint Width { get; }
    public uint Height { get; }
    public int Frames => Volatile.Read(ref _frames);
    public Task FirstVideoFrame => _firstVideoFrame.Task;
    public Task FirstAudioBlock => _firstAudioBlock.Task;
    public void Attach(MediaPlayer player)
    {
        player.SetVideoCallbacks(_lock, _unlock, _display);
        player.SetVideoFormat("RV32", Width, Height, checked(Width * 4));
        player.SetAudioCallbacks(_audioPlay, _audioPause, _audioResume, _audioFlush, _audioDrain);
        player.SetAudioFormat("S16N", 48_000, 2);
    }
    public void Dispose() => Marshal.FreeHGlobal(_buffer);
}

internal sealed class ProbeReport
{
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset FinishedAt { get; set; }
    public string? LibVlcVersion { get; set; }
    public List<FileResult> Files { get; } = [];
    public StateReplacementResult? StateReplacement { get; set; }
    public List<StagingResult> StagingValidation { get; set; } = [];
}

internal sealed class StagingResult
{
    public string File { get; set; } = "";
    public bool PreflightAccepted { get; set; }
    public string? RejectionReason { get; set; }
    public bool StagingPlayAccepted { get; set; }
    public bool FirstVideoFrame { get; set; }
    public bool FirstAudioBlock { get; set; }
    public bool StagingEncounteredError { get; set; }
    public long BaselineTimeBeforeValidationMilliseconds { get; set; }
    public long BaselineTimeAfterValidationMilliseconds { get; set; }
    public int BaselineFramesBeforeValidation { get; set; }
    public int BaselineFramesAfterValidation { get; set; }
    public bool BaselineStillPlaying { get; set; }
    public bool BaselineStatePreserved { get; set; }
}

internal sealed class StateReplacementResult
{
    public long BaselineTimeBeforeOpenMilliseconds { get; set; }
    public int BaselineFramesBeforeOpen { get; set; }
    public bool InvalidPlayAccepted { get; set; }
    public long TimeAfterInvalidOpenMilliseconds { get; set; }
    public int FramesAfterInvalidOpen { get; set; }
    public bool InvalidEndReached { get; set; }
    public bool InvalidEncounteredError { get; set; }
    public bool BaselineStatePreserved { get; set; }
}

internal sealed class FileResult
{
    public string File { get; set; } = "";
    public string? ParseStatus { get; set; }
    public string? ParseException { get; set; }
    public long DurationMilliseconds { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public uint Width { get; set; }
    public uint Height { get; set; }
    public string? FrameRate { get; set; }
    public bool PlayAccepted { get; set; }
    public bool PlayingEvent { get; set; }
    public bool EndReached { get; set; }
    public bool EncounteredError { get; set; }
    public long MaximumPlaybackTimeMilliseconds { get; set; }
    public int VideoFrames { get; set; }
}
