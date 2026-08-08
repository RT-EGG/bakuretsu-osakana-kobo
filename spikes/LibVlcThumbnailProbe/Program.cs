using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using LibVLCSharp.Shared;

if (args.Length is < 3 or > 6)
{
    Console.Error.WriteLine("Usage: LibVlcThumbnailProbe <media-path> <image-directory> <report-json> [count] [output-width] [fractions-csv]");
    return 2;
}

var mediaPath = Path.GetFullPath(args[0]);
var imageDirectory = Path.GetFullPath(args[1]);
var reportPath = Path.GetFullPath(args[2]);
var requestedCount = args.Length >= 4 && int.TryParse(args[3], out var parsedCount) ? parsedCount : 3;
var requestedWidth = args.Length == 5 && int.TryParse(args[4], out var parsedWidth) ? parsedWidth : 0;
if (args.Length >= 5 && int.TryParse(args[4], out var suppliedWidth)) requestedWidth = suppliedWidth;
var suppliedFractions = args.Length == 6
    ? args[5].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(value => double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray()
    : null;
if (suppliedFractions is not null)
{
    if (suppliedFractions.Length == 0 || suppliedFractions.Any(value => value is < 0 or >= 1))
        throw new ArgumentOutOfRangeException(nameof(suppliedFractions));
    requestedCount = suppliedFractions.Length;
}
if (requestedCount is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(requestedCount));
if (!File.Exists(mediaPath)) throw new FileNotFoundException("Media file not found.", mediaPath);
Directory.CreateDirectory(imageDirectory);
Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);

Core.Initialize();
using var libVlc = new LibVLC("--no-audio", "--no-video-title-show", "--avcodec-hw=none", "--file-caching=100");
using var media = new Media(libVlc, new Uri(mediaPath));
await media.Parse(MediaParseOptions.ParseLocal, 8_000, CancellationToken.None);
var videoTrack = media.Tracks.First(track => track.TrackType == TrackType.Video);
var sourceWidth = checked((int)videoTrack.Data.Video.Width);
var sourceHeight = checked((int)videoTrack.Data.Video.Height);
var width = requestedWidth > 0 ? requestedWidth : sourceWidth;
var height = requestedWidth > 0
    ? Math.Max(2, (int)Math.Round(sourceHeight * (width / (double)sourceWidth) / 2) * 2)
    : sourceHeight;
var duration = media.Duration;
var report = new ThumbnailReport
{
    StartedAt = DateTimeOffset.Now,
    MediaPath = mediaPath,
    DurationMilliseconds = duration,
    SourceWidth = sourceWidth,
    SourceHeight = sourceHeight,
    Width = width,
    Height = height,
};

var process = Process.GetCurrentProcess();
var cpuAtStart = process.TotalProcessorTime;
using var player = new MediaPlayer(libVlc);
using var sink = new FrameSink(width, height);
sink.Attach(player);
var playbackError = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
player.EncounteredError += (_, _) => playbackError.TrySetResult();

try
{
    var fractions = suppliedFractions ?? (requestedCount == 3
        ? new[] { 0d, 0.5d, 0.9d }
        : Enumerable.Range(0, requestedCount).Select(index => index / (double)requestedCount).ToArray());
    for (var frameIndex = 0; frameIndex < fractions.Length; frameIndex++)
    {
        var fraction = fractions[frameIndex];
        var target = (long)Math.Round(duration * fraction);
        var stopwatch = Stopwatch.StartNew();
        Task<byte[]> capture;
        if (fraction == 0)
        {
            capture = sink.RequestFrame();
            if (!player.Play(media)) throw new InvalidOperationException("MediaPlayer.Play returned false.");
        }
        else
        {
            if (frameIndex == 0)
            {
                var warmupFrame = sink.RequestFrame();
                if (!player.Play(media)) throw new InvalidOperationException("MediaPlayer.Play returned false.");
                var warmupCompleted = await Task.WhenAny(warmupFrame, playbackError.Task, Task.Delay(5_000));
                if (warmupCompleted != warmupFrame)
                    throw new TimeoutException("Extractor warmup did not complete.");
                await warmupFrame;
            }
            player.SetPause(true);
            player.Time = target;
            player.SetPause(false);
            var seekDeadline = Stopwatch.StartNew();
            while (player.Time < target - 100 && seekDeadline.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(5);
            if (player.Time < target - 100)
                throw new TimeoutException($"Playback clock did not reach {target} ms.");
            capture = sink.RequestFrame();
        }

        var completed = await Task.WhenAny(capture, playbackError.Task, Task.Delay(5_000));
        if (completed != capture)
            throw new TimeoutException($"Frame capture at {fraction:P0} did not complete.");
        var pixels = await capture;
        player.SetPause(true);
        var observedTime = player.Time;
        var outputPath = Path.Combine(imageDirectory, $"thumbnail-{frameIndex:000}.bmp");
        WriteBgraBmp(outputPath, width, height, pixels);
        report.Frames.Add(new FrameResult
        {
            Fraction = fraction,
            TargetMilliseconds = target,
            ObservedMilliseconds = observedTime,
            DifferenceMilliseconds = Math.Abs(observedTime - target),
            ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
            OutputPath = outputPath,
            Sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(outputPath))),
        });
    }
}
catch (Exception exception)
{
    report.Errors.Add($"{exception.GetType().Name}: {exception.Message}");
}
finally
{
    player.Stop();
}

report.CpuMilliseconds = (process.TotalProcessorTime - cpuAtStart).TotalMilliseconds;
report.WorkingSetBytes = process.WorkingSet64;
foreach (var frame in report.Frames)
{
    if (frame.DifferenceMilliseconds > 1_000)
        report.Errors.Add($"{frame.Fraction:P0} frame differed from target by {frame.DifferenceMilliseconds} ms.");
    if (frame.ElapsedMilliseconds > 2_000)
        report.Errors.Add($"{frame.Fraction:P0} frame took {frame.ElapsedMilliseconds:0} ms.");
}
if (report.Frames.Count != requestedCount) report.Errors.Add($"Captured {report.Frames.Count}/{requestedCount} frames.");
if (report.Frames.Select(frame => frame.Sha256).Distinct().Count() != report.Frames.Count)
    report.Errors.Add("Captured thumbnails were not distinct.");
report.Passed = report.Errors.Count == 0;
report.FinishedAt = DateTimeOffset.Now;
await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

Console.WriteLine($"Passed: {report.Passed}");
foreach (var frame in report.Frames)
    Console.WriteLine($"{frame.Fraction:P0}: {frame.ElapsedMilliseconds:0.0} ms, target={frame.TargetMilliseconds}, observed={frame.ObservedMilliseconds}, delta={frame.DifferenceMilliseconds}");
foreach (var error in report.Errors) Console.WriteLine($"ERROR: {error}");
Console.WriteLine($"CPU: {report.CpuMilliseconds:0.0} ms, working set: {report.WorkingSetBytes / 1024 / 1024.0:0.0} MiB");
Console.WriteLine($"Report: {reportPath}");
return report.Passed ? 0 : 1;

static void WriteBgraBmp(string path, int width, int height, byte[] pixels)
{
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    var pixelBytes = checked(width * height * 4);
    writer.Write((byte)'B'); writer.Write((byte)'M');
    writer.Write(54 + pixelBytes); writer.Write(0); writer.Write(54);
    writer.Write(40); writer.Write(width); writer.Write(-height);
    writer.Write((short)1); writer.Write((short)32); writer.Write(0);
    writer.Write(pixelBytes); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
    writer.Write(pixels);
}

internal sealed class FrameSink : IDisposable
{
    private readonly object _sync = new();
    private readonly IntPtr _buffer;
    private readonly int _byteCount;
    private TaskCompletionSource<byte[]>? _requestedFrame;
    private readonly MediaPlayer.LibVLCVideoLockCb _lockCallback;
    private readonly MediaPlayer.LibVLCVideoUnlockCb _unlockCallback;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _displayCallback;

    public FrameSink(int width, int height)
    {
        _byteCount = checked(width * height * 4);
        _buffer = Marshal.AllocHGlobal(_byteCount);
        _lockCallback = Lock;
        _unlockCallback = (_, _, _) => { };
        _displayCallback = Display;
        Width = width; Height = height;
    }
    public int Width { get; }
    public int Height { get; }
    public void Attach(MediaPlayer player)
    {
        player.SetVideoCallbacks(_lockCallback, _unlockCallback, _displayCallback);
        player.SetVideoFormat("RV32", (uint)Width, (uint)Height, checked((uint)Width * 4));
    }
    public Task<byte[]> RequestFrame()
    {
        lock (_sync)
        {
            _requestedFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return _requestedFrame.Task;
        }
    }
    public void Dispose() => Marshal.FreeHGlobal(_buffer);
    private IntPtr Lock(IntPtr opaque, IntPtr planes) { Marshal.WriteIntPtr(planes, _buffer); return IntPtr.Zero; }
    private void Display(IntPtr opaque, IntPtr picture)
    {
        TaskCompletionSource<byte[]>? request;
        lock (_sync) { request = _requestedFrame; _requestedFrame = null; }
        if (request is null) return;
        var pixels = new byte[_byteCount];
        Marshal.Copy(_buffer, pixels, 0, _byteCount);
        request.TrySetResult(pixels);
    }
}

internal sealed class ThumbnailReport
{
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public bool Passed { get; set; }
    public string MediaPath { get; set; } = "";
    public long DurationMilliseconds { get; set; }
    public int SourceWidth { get; set; }
    public int SourceHeight { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double CpuMilliseconds { get; set; }
    public long WorkingSetBytes { get; set; }
    public List<FrameResult> Frames { get; } = [];
    public List<string> Errors { get; } = [];
}
internal sealed class FrameResult
{
    public double Fraction { get; set; }
    public long TargetMilliseconds { get; set; }
    public long ObservedMilliseconds { get; set; }
    public long DifferenceMilliseconds { get; set; }
    public double ElapsedMilliseconds { get; set; }
    public string OutputPath { get; set; } = "";
    public string Sha256 { get; set; } = "";
}
