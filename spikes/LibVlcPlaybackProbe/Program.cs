using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LibVLCSharp.Shared;

const int operationTimeoutMilliseconds = 8_000;
var options = ProbeOptions.Parse(args);
Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath)!);

Core.Initialize();
using var libVlc = new LibVLC(
    "--no-video-title-show",
    "--no-snapshot-preview",
    "--file-caching=150",
    "--avcodec-hw=none");
var diagnosticCounts = new ConcurrentDictionary<string, DiagnosticCount>(StringComparer.Ordinal);
string? currentFile = null;
libVlc.Log += OnLibVlcLog;

var mediaFiles = Directory
    .EnumerateFiles(options.AssetDirectory)
    .Where(path => path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith(".wmv", StringComparison.OrdinalIgnoreCase))
    .Where(path => Path.GetFileName(path).StartsWith("phase2-", StringComparison.Ordinal))
    .OrderBy(path => path, StringComparer.Ordinal)
    .ToArray();

var process = Process.GetCurrentProcess();
var report = new ProbeReport
{
    StartedAt = DateTimeOffset.UtcNow,
    Machine = new MachineInfo
    {
        OperatingSystem = RuntimeInformation.OSDescription,
        Framework = RuntimeInformation.FrameworkDescription,
        Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
        ProcessorCount = Environment.ProcessorCount,
    },
    LibVlcVersion = libVlc.Version,
};

foreach (var path in mediaFiles)
{
    currentFile = Path.GetFileName(path);
    Console.WriteLine($"Probing {currentFile}");
    report.Files.Add(await ProbeFileAsync(libVlc, process, path));
    currentFile = null;
}

report.FinishedAt = DateTimeOffset.UtcNow;
report.Passed = mediaFiles.Length == options.ExpectedFileCount &&
                report.Files.All(result => result.Passed);
report.Diagnostics.AddRange(
    diagnosticCounts.Values.OrderByDescending(item => item.Level).ThenBy(item => item.Module));
if (mediaFiles.Length != options.ExpectedFileCount)
{
    report.Errors.Add(
        $"Expected {options.ExpectedFileCount} media files, but found {mediaFiles.Length}.");
}

var serializerOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};
await File.WriteAllTextAsync(options.OutputPath, JsonSerializer.Serialize(report, serializerOptions));

Console.WriteLine($"Passed: {report.Files.Count(result => result.Passed)}/{report.Files.Count}");
Console.WriteLine($"Report: {options.OutputPath}");
return report.Passed ? 0 : 1;

void OnLibVlcLog(object? sender, LogEventArgs eventArgs)
{
    if (eventArgs.Level < LogLevel.Warning)
    {
        return;
    }

    var diagnosticFile = Volatile.Read(ref currentFile) ?? "(outside file probe)";
    var key = $"{diagnosticFile}\n{eventArgs.Level}\n{eventArgs.Module}\n{eventArgs.Message}";
    diagnosticCounts.AddOrUpdate(
        key,
        _ => new DiagnosticCount
        {
            File = diagnosticFile,
            Level = eventArgs.Level.ToString(),
            Module = eventArgs.Module ?? "",
            Message = eventArgs.Message,
            Count = 1,
        },
        (_, existing) =>
        {
            lock (existing)
            {
                existing.Count++;
            }
            return existing;
        });
}

static async Task<FileProbeResult> ProbeFileAsync(LibVLC libVlc, Process process, string path)
{
    var result = FileProbeResult.FromPath(path);
    var cpuAtStart = process.TotalProcessorTime;
    var stopwatch = Stopwatch.StartNew();

    try
    {
        using var media = new Media(libVlc, new Uri(path));
        var parseStopwatch = Stopwatch.StartNew();
        result.ParseStatus = (await media.Parse(
            MediaParseOptions.ParseLocal,
            operationTimeoutMilliseconds,
            CancellationToken.None)).ToString();
        result.ParseMilliseconds = parseStopwatch.Elapsed.TotalMilliseconds;
        result.DurationMilliseconds = media.Duration;

        var videoTracks = media.Tracks.Where(track => track.TrackType == TrackType.Video).ToArray();
        var audioTracks = media.Tracks.Where(track => track.TrackType == TrackType.Audio).ToArray();
        if (videoTracks.Length == 0)
        {
            result.Errors.Add("LibVLC did not report a video track.");
        }
        else
        {
            var videoTrack = videoTracks[0];
            result.Video = new TrackInfo
            {
                Codec = FourCc(videoTrack.Codec),
                Width = videoTrack.Data.Video.Width,
                Height = videoTrack.Data.Video.Height,
                FrameRateNumerator = videoTrack.Data.Video.FrameRateNum,
                FrameRateDenominator = videoTrack.Data.Video.FrameRateDen,
            };
        }

        if (audioTracks.Length == 0)
        {
            result.Errors.Add("LibVLC did not report an audio track.");
        }
        else
        {
            var audioTrack = audioTracks[0];
            result.Audio = new TrackInfo
            {
                Codec = FourCc(audioTrack.Codec),
                Channels = audioTrack.Data.Audio.Channels,
                SampleRate = audioTrack.Data.Audio.Rate,
            };
        }

        ValidateParsedMedia(result);

        using var sink = new DecodeSink((uint)result.ExpectedWidth, (uint)result.ExpectedHeight);
        using var player = new MediaPlayer(libVlc);
        sink.Attach(player);

        var playing = NewSignal();
        var encounteredError = NewSignal();
        player.Playing += OnPlaying;
        player.EncounteredError += OnError;

        try
        {
            var playStopwatch = Stopwatch.StartNew();
            if (!player.Play(media))
            {
                result.Errors.Add("MediaPlayer.Play returned false.");
                return Complete(result, process, cpuAtStart, stopwatch, sink);
            }

            await WaitForAsync(playing.Task, encounteredError.Task, operationTimeoutMilliseconds, "Playing event");
            result.PlayingEventMilliseconds = playStopwatch.Elapsed.TotalMilliseconds;

            await WaitForAsync(sink.FirstVideoFrame, encounteredError.Task, operationTimeoutMilliseconds, "first video frame");
            result.FirstVideoFrameMilliseconds = playStopwatch.Elapsed.TotalMilliseconds;
            await WaitForAsync(sink.FirstAudioBlock, encounteredError.Task, operationTimeoutMilliseconds, "first audio block");
            result.FirstAudioBlockMilliseconds = playStopwatch.Elapsed.TotalMilliseconds;

            await WaitUntilAsync(
                () => player.Time >= 150,
                encounteredError.Task,
                operationTimeoutMilliseconds,
                "playback clock advancement");

            result.ReportedLengthMilliseconds = player.Length;
            result.CanPause = player.CanPause;
            if (!player.CanPause)
            {
                result.Errors.Add("MediaPlayer reported that the file cannot be paused.");
            }
            else
            {
                var pauseStopwatch = Stopwatch.StartNew();
                player.SetPause(true);
                await WaitUntilAsync(
                    () => !player.IsPlaying,
                    encounteredError.Task,
                    operationTimeoutMilliseconds,
                    "pause");
                var pausedTime = player.Time;
                await Task.Delay(150);
                result.PauseClockDriftMilliseconds = Math.Abs(player.Time - pausedTime);
                if (result.PauseClockDriftMilliseconds > 100)
                {
                    result.Errors.Add(
                        $"Playback clock moved {result.PauseClockDriftMilliseconds} ms while paused.");
                }

                player.SetPause(false);
                await WaitUntilAsync(
                    () => player.IsPlaying && player.Time >= pausedTime + 50,
                    encounteredError.Task,
                    operationTimeoutMilliseconds,
                    "resume");
                result.PauseResumeMilliseconds = pauseStopwatch.Elapsed.TotalMilliseconds;
            }

            result.IsSeekable = player.IsSeekable;
            if (!player.IsSeekable)
            {
                result.Errors.Add("MediaPlayer reported that the file is not seekable.");
            }
            else
            {
                result.MiddleSeekMilliseconds = await SeekAsync(
                    player,
                    sink,
                    player.Length / 2,
                    encounteredError.Task);

                if (result.ExpectedDurationSeconds >= 60)
                {
                    foreach (var rate in new[] { 0.25f, 0.5f, 1.0f, 1.5f, 2.0f })
                    {
                        var returnCode = player.SetRate(rate);
                        result.Rates.Add(new RateResult
                        {
                            Requested = rate,
                            ReturnCode = returnCode,
                            Reported = player.Rate,
                        });
                        if (returnCode != 0 || Math.Abs(player.Rate - rate) > 0.01f)
                        {
                            result.Errors.Add(
                                $"Rate {rate:0.##} was not accepted (code {returnCode}, reported {player.Rate:0.##}).");
                        }
                    }

                    player.SetRate(1.0f);
                }

                result.NearEndSeekMilliseconds = await SeekAsync(
                    player,
                    sink,
                    Math.Max(0, player.Length - 1_000),
                    encounteredError.Task);
            }

            foreach (var volume in new[] { 100, 200, 300, 500 })
            {
                player.Volume = volume;
                var reported = player.Volume;
                result.Volumes.Add(new VolumeResult { Requested = volume, Reported = reported });
            }
            result.VolumeValidation =
                "Informational only: callback audio output bypasses LibVLC's normal audio mixer.";

            if (sink.VideoFrames < 2)
            {
                result.Errors.Add($"Only {sink.VideoFrames} decoded video frame(s) reached the callback.");
            }

            if (sink.AudioBlocks < 1 || sink.AudioSamples < 1)
            {
                result.Errors.Add("No decoded audio samples reached the callback.");
            }

            result.DecodedVideoFrames = sink.VideoFrames;
            result.DecodedAudioBlocks = sink.AudioBlocks;
            result.DecodedAudioSamples = sink.AudioSamples;
        }
        finally
        {
            player.Stop();
            player.Playing -= OnPlaying;
            player.EncounteredError -= OnError;
        }

        void OnPlaying(object? _, EventArgs __) => playing.TrySetResult();
        void OnError(object? _, EventArgs __) => encounteredError.TrySetResult();
    }
    catch (Exception exception)
    {
        result.Errors.Add($"{exception.GetType().Name}: {exception.Message}");
    }

    return Complete(result, process, cpuAtStart, stopwatch, null);
}

static FileProbeResult Complete(
    FileProbeResult result,
    Process process,
    TimeSpan cpuAtStart,
    Stopwatch stopwatch,
    DecodeSink? sink)
{
    result.ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
    result.CpuMilliseconds = (process.TotalProcessorTime - cpuAtStart).TotalMilliseconds;
    result.WorkingSetBytes = process.WorkingSet64;
    if (sink is not null)
    {
        result.DecodedVideoFrames = sink.VideoFrames;
        result.DecodedAudioBlocks = sink.AudioBlocks;
        result.DecodedAudioSamples = sink.AudioSamples;
    }

    result.Passed = result.Errors.Count == 0;
    return result;
}

static void ValidateParsedMedia(FileProbeResult result)
{
    if (!string.Equals(result.ParseStatus, nameof(MediaParsedStatus.Done), StringComparison.Ordinal))
    {
        result.Errors.Add($"Media parse status was {result.ParseStatus}.");
    }

    if (result.Video is not null &&
        (result.Video.Width != result.ExpectedWidth || result.Video.Height != result.ExpectedHeight))
    {
        result.Errors.Add(
            $"Video dimensions were {result.Video.Width}x{result.Video.Height}; " +
            $"expected {result.ExpectedWidth}x{result.ExpectedHeight}.");
    }

    if (result.Video is not null)
    {
        var expectedVideoCodec = result.Container == "mp4" ? "h264" : "VC-1";
        if (!string.Equals(result.Video.Codec, expectedVideoCodec, StringComparison.Ordinal))
        {
            result.Errors.Add(
                $"Video codec was {result.Video.Codec}; expected {expectedVideoCodec}.");
        }

        if (result.Video.FrameRateDenominator == 0)
        {
            result.Errors.Add("Video frame-rate denominator was zero.");
        }
        else
        {
            var reportedFps =
                (double)result.Video.FrameRateNumerator / result.Video.FrameRateDenominator;
            if (Math.Abs(reportedFps - result.ExpectedFps) > 0.1)
            {
                result.Errors.Add(
                    $"Video frame rate was {reportedFps:0.###}; expected {result.ExpectedFps} fps.");
            }
        }
    }

    if (result.Audio is not null &&
        (result.Audio.Channels != 2 || result.Audio.SampleRate != 48_000))
    {
        result.Errors.Add(
            $"Audio was {result.Audio.SampleRate} Hz/{result.Audio.Channels} channels; expected 48000 Hz/2 channels.");
    }

    if (result.Audio is not null)
    {
        var expectedAudioCodec = result.Container == "mp4" ? "mp4a" : "WMAP";
        if (!string.Equals(result.Audio.Codec, expectedAudioCodec, StringComparison.Ordinal))
        {
            result.Errors.Add(
                $"Audio codec was {result.Audio.Codec}; expected {expectedAudioCodec}.");
        }
    }

    if (Math.Abs(result.DurationMilliseconds - (result.ExpectedDurationSeconds * 1_000L)) > 150)
    {
        result.Errors.Add(
            $"Duration was {result.DurationMilliseconds} ms; expected {result.ExpectedDurationSeconds * 1_000} +/- 150 ms.");
    }
}

static async Task<double> SeekAsync(
    MediaPlayer player,
    DecodeSink sink,
    long targetMilliseconds,
    Task encounteredError,
    int timeoutMilliseconds = operationTimeoutMilliseconds)
{
    var framesBeforeSeek = sink.VideoFrames;
    var stopwatch = Stopwatch.StartNew();
    player.Time = targetMilliseconds;

    await WaitUntilAsync(
        () => player.Time >= targetMilliseconds + 100 &&
              player.Time <= targetMilliseconds + 1_500 &&
              sink.VideoFrames >= framesBeforeSeek + 3,
        encounteredError,
        timeoutMilliseconds,
        $"seek to {targetMilliseconds} ms");

    return stopwatch.Elapsed.TotalMilliseconds;
}

static async Task WaitForAsync(
    Task expected,
    Task encounteredError,
    int timeoutMilliseconds,
    string operation)
{
    using var timeout = new CancellationTokenSource(timeoutMilliseconds);
    var timeoutTask = Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token);
    var completed = await Task.WhenAny(expected, encounteredError, timeoutTask);
    if (completed == expected)
    {
        timeout.Cancel();
        await expected;
        return;
    }

    throw completed == encounteredError
        ? new InvalidOperationException($"LibVLC reported an error while waiting for {operation}.")
        : new TimeoutException($"Timed out waiting for {operation}.");
}

static async Task WaitUntilAsync(
    Func<bool> condition,
    Task encounteredError,
    int timeoutMilliseconds,
    string operation)
{
    var stopwatch = Stopwatch.StartNew();
    while (!condition())
    {
        if (encounteredError.IsCompleted)
        {
            throw new InvalidOperationException($"LibVLC reported an error while waiting for {operation}.");
        }

        if (stopwatch.ElapsedMilliseconds >= timeoutMilliseconds)
        {
            throw new TimeoutException($"Timed out waiting for {operation}.");
        }

        await Task.Delay(10);
    }
}

static TaskCompletionSource NewSignal() =>
    new(TaskCreationOptions.RunContinuationsAsynchronously);

static string FourCc(uint value)
{
    Span<char> characters = stackalloc char[4];
    characters[0] = (char)(value & 0xff);
    characters[1] = (char)((value >> 8) & 0xff);
    characters[2] = (char)((value >> 16) & 0xff);
    characters[3] = (char)((value >> 24) & 0xff);
    return new string(characters).TrimEnd('\0');
}

internal sealed class DecodeSink : IDisposable
{
    private readonly IntPtr _videoBuffer;
    private readonly MediaPlayer.LibVLCVideoLockCb _videoLock;
    private readonly MediaPlayer.LibVLCVideoUnlockCb _videoUnlock;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _videoDisplay;
    private readonly MediaPlayer.LibVLCAudioPlayCb _audioPlay;
    private readonly MediaPlayer.LibVLCAudioPauseCb _audioPause;
    private readonly MediaPlayer.LibVLCAudioResumeCb _audioResume;
    private readonly MediaPlayer.LibVLCAudioFlushCb _audioFlush;
    private readonly MediaPlayer.LibVLCAudioDrainCb _audioDrain;
    private readonly TaskCompletionSource _firstVideoFrame =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _firstAudioBlock =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly uint _width;
    private readonly uint _height;
    private int _videoFrames;
    private int _audioBlocks;
    private long _audioSamples;

    public DecodeSink(uint width, uint height)
    {
        _width = width;
        _height = height;
        _videoBuffer = Marshal.AllocHGlobal(checked((int)(width * height * 4)));
        _videoLock = LockVideo;
        _videoUnlock = UnlockVideo;
        _videoDisplay = DisplayVideo;
        _audioPlay = PlayAudio;
        _audioPause = (_, _) => { };
        _audioResume = (_, _) => { };
        _audioFlush = (_, _) => { };
        _audioDrain = _ => { };
    }

    public Task FirstVideoFrame => _firstVideoFrame.Task;

    public Task FirstAudioBlock => _firstAudioBlock.Task;

    public int VideoFrames => Volatile.Read(ref _videoFrames);

    public int AudioBlocks => Volatile.Read(ref _audioBlocks);

    public long AudioSamples => Interlocked.Read(ref _audioSamples);

    public void Attach(MediaPlayer player)
    {
        player.SetVideoCallbacks(_videoLock, _videoUnlock, _videoDisplay);
        player.SetVideoFormat("RV32", _width, _height, checked(_width * 4));
        player.SetAudioCallbacks(_audioPlay, _audioPause, _audioResume, _audioFlush, _audioDrain);
        player.SetAudioFormat("S16N", 48_000, 2);
    }

    public void Dispose()
    {
        Marshal.FreeHGlobal(_videoBuffer);
    }

    private IntPtr LockVideo(IntPtr opaque, IntPtr planes)
    {
        Marshal.WriteIntPtr(planes, _videoBuffer);
        return IntPtr.Zero;
    }

    private void UnlockVideo(IntPtr opaque, IntPtr picture, IntPtr planes)
    {
    }

    private void DisplayVideo(IntPtr opaque, IntPtr picture)
    {
        Interlocked.Increment(ref _videoFrames);
        _firstVideoFrame.TrySetResult();
    }

    private void PlayAudio(IntPtr data, IntPtr samples, uint count, long pts)
    {
        Interlocked.Increment(ref _audioBlocks);
        Interlocked.Add(ref _audioSamples, count);
        _firstAudioBlock.TrySetResult();
    }
}

internal sealed class ProbeOptions
{
    public required string AssetDirectory { get; init; }

    public required string OutputPath { get; init; }

    public int ExpectedFileCount { get; init; } = 16;

    public static ProbeOptions Parse(string[] args)
    {
        if (args.Length is < 2 or > 3)
        {
            throw new ArgumentException(
                "Usage: LibVlcPlaybackProbe <asset-directory> <output-json> [expected-file-count]");
        }

        var assetDirectory = Path.GetFullPath(args[0]);
        if (!Directory.Exists(assetDirectory))
        {
            throw new DirectoryNotFoundException(assetDirectory);
        }

        var expectedFileCount = args.Length == 3 && int.TryParse(args[2], out var parsedCount)
            ? parsedCount
            : 16;
        if (expectedFileCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(args),
                expectedFileCount,
                "Expected file count must be greater than zero.");
        }

        return new ProbeOptions
        {
            AssetDirectory = assetDirectory,
            OutputPath = Path.GetFullPath(args[1]),
            ExpectedFileCount = expectedFileCount,
        };
    }
}

internal sealed class ProbeReport
{
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public bool Passed { get; set; }
    public string? LibVlcVersion { get; set; }
    public MachineInfo Machine { get; set; } = new();
    public List<string> Errors { get; } = [];
    public List<FileProbeResult> Files { get; } = [];
    public List<DiagnosticCount> Diagnostics { get; } = [];
}

internal sealed class MachineInfo
{
    public string OperatingSystem { get; set; } = "";
    public string Framework { get; set; } = "";
    public string Architecture { get; set; } = "";
    public int ProcessorCount { get; set; }
}

internal sealed class FileProbeResult
{
    private static readonly Regex FilePattern = new(
        @"^phase2-(?:av-sync-)?(?<width>\d+)x(?<height>\d+)-(?<duration>\d+)s-(?<fps>\d+)fps-(?<format>h264-aac|wmv9-wma)\.(?:mp4|wmv)$",
        RegexOptions.CultureInvariant);

    public string File { get; set; } = "";
    public string Container { get; set; } = "";
    public int ExpectedWidth { get; set; }
    public int ExpectedHeight { get; set; }
    public int ExpectedDurationSeconds { get; set; }
    public int ExpectedFps { get; set; }
    public bool Passed { get; set; }
    public string? ParseStatus { get; set; }
    public double ParseMilliseconds { get; set; }
    public long DurationMilliseconds { get; set; }
    public long ReportedLengthMilliseconds { get; set; }
    public TrackInfo? Video { get; set; }
    public TrackInfo? Audio { get; set; }
    public bool IsSeekable { get; set; }
    public bool CanPause { get; set; }
    public long PauseClockDriftMilliseconds { get; set; }
    public double PauseResumeMilliseconds { get; set; }
    public double PlayingEventMilliseconds { get; set; }
    public double FirstVideoFrameMilliseconds { get; set; }
    public double FirstAudioBlockMilliseconds { get; set; }
    public double MiddleSeekMilliseconds { get; set; }
    public double NearEndSeekMilliseconds { get; set; }
    public int DecodedVideoFrames { get; set; }
    public int DecodedAudioBlocks { get; set; }
    public long DecodedAudioSamples { get; set; }
    public double ElapsedMilliseconds { get; set; }
    public double CpuMilliseconds { get; set; }
    public long WorkingSetBytes { get; set; }
    public List<RateResult> Rates { get; } = [];
    public List<VolumeResult> Volumes { get; } = [];
    public string VolumeValidation { get; set; } = "";
    public List<string> Errors { get; } = [];

    public static FileProbeResult FromPath(string path)
    {
        var fileName = Path.GetFileName(path);
        var match = FilePattern.Match(fileName);
        if (!match.Success)
        {
            throw new InvalidDataException($"Unexpected test-video name: {fileName}");
        }

        return new FileProbeResult
        {
            File = fileName,
            Container = Path.GetExtension(path).TrimStart('.').ToLowerInvariant(),
            ExpectedWidth = int.Parse(match.Groups["width"].Value),
            ExpectedHeight = int.Parse(match.Groups["height"].Value),
            ExpectedDurationSeconds = int.Parse(match.Groups["duration"].Value),
            ExpectedFps = int.Parse(match.Groups["fps"].Value),
        };
    }
}

internal sealed class TrackInfo
{
    public string Codec { get; set; } = "";
    public uint Width { get; set; }
    public uint Height { get; set; }
    public uint FrameRateNumerator { get; set; }
    public uint FrameRateDenominator { get; set; }
    public uint Channels { get; set; }
    public uint SampleRate { get; set; }
}

internal sealed class RateResult
{
    public float Requested { get; set; }
    public int ReturnCode { get; set; }
    public float Reported { get; set; }
}

internal sealed class VolumeResult
{
    public int Requested { get; set; }
    public int Reported { get; set; }
}

internal sealed class DiagnosticCount
{
    public string File { get; set; } = "";
    public string Level { get; set; } = "";
    public string Module { get; set; } = "";
    public string Message { get; set; } = "";
    public int Count { get; set; }
}
