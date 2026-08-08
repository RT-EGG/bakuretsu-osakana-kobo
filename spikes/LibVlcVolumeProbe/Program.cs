using System.Text.Json;
using LibVLCSharp.Shared;

const int sampleRate = 48_000;
const int channels = 2;
const double durationSeconds = 3.2;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: LibVlcVolumeProbe <output-directory> [scenario-name-contains]");
    return 2;
}

var outputDirectory = Path.GetFullPath(args[0]);
Directory.CreateDirectory(outputDirectory);
var inputPath = Path.Combine(outputDirectory, "volume-probe-input.wav");
WriteInputWave(inputPath);

Core.Initialize();
var scenarios = new[]
{
    new Scenario("volume-100", 100, null, null, null),
    new Scenario("volume-200", 200, null, null, null),
    new Scenario("volume-500", 500, null, null, null),
    new Scenario("core-gain-200", 100, null, null, 2),
    new Scenario("core-gain-500", 100, null, null, 5),
    new Scenario("equalizer-preamp-200", 100, null, null, null, 6.0206f),
    new Scenario("equalizer-preamp-500", 100, null, null, null, 13.9794f),
    new Scenario("custom-limiter-200", 200, null, null, null, null, true),
    new Scenario("custom-limiter-500", 500, null, null, null, null, true),
    new Scenario("gain-200", 100, "gain", 2, null),
    new Scenario("gain-500", 100, "gain", 5, null),
    new Scenario("gain-compressor-500", 100, "gain:compressor", 5, null),
    new Scenario("compressor-gain-500", 100, "compressor:gain", 5, null),
};
if (args.Length == 2)
{
    scenarios = scenarios
        .Where(scenario => scenario.Name.Contains(args[1], StringComparison.OrdinalIgnoreCase))
        .ToArray();
}

var results = new List<ScenarioResult>();
foreach (var scenario in scenarios)
{
    Console.WriteLine($"Running {scenario.Name} (speaker-free file output)");
    var outputPath = Path.Combine(outputDirectory, scenario.Name + ".wav");
    if (File.Exists(outputPath))
    {
        File.Delete(outputPath);
    }

    var errors = await RenderAsync(scenario, inputPath, outputPath);
    WaveMetrics? metrics = null;
    if (File.Exists(outputPath))
    {
        try
        {
            metrics = AnalyzeWave(outputPath);
            if (scenario.CustomLimiter)
            {
                ValidateCustomLimiter(scenario, metrics, errors);
            }
        }
        catch (Exception exception)
        {
            errors.Add($"Wave analysis failed: {exception.Message}");
        }
    }
    else
    {
        errors.Add("LibVLC did not create the output WAV file.");
    }

    results.Add(new ScenarioResult(scenario, outputPath, metrics, errors));
}

var report = new
{
    StartedAt = DateTimeOffset.Now,
    Safety = "All playback used LibVLC's file audio output. No physical audio device was opened.",
    Input = new
    {
        Path = inputPath,
        SampleRate = sampleRate,
        Channels = channels,
        DurationSeconds = durationSeconds,
        Segments = new[]
        {
            "0.50-1.30 s: 997 Hz at -30 dBFS",
            "1.30-2.10 s: 997 Hz at -12 dBFS",
            "2.10-2.90 s: 997 Hz at -0.4 dBFS",
        },
    },
    Results = results,
};
var reportPath = Path.Combine(outputDirectory, "volume-probe-report.json");
await File.WriteAllTextAsync(
    reportPath,
    JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

foreach (var result in results)
{
    var metrics = result.Metrics;
    Console.WriteLine(metrics is null
        ? $"{result.Scenario.Name}: ERROR ({string.Join("; ", result.Errors)})"
        : $"{result.Scenario.Name}: peak={metrics.Peak:0.0000}, " +
          $"over={metrics.OverRangeSamples}, nearClip={metrics.NearClipSamples}, " +
          $"RMS[-30]={metrics.LowSegmentRms:0.000000}, " +
          $"RMS[-12]={metrics.MidSegmentRms:0.000000}, " +
          $"RMS[-0.4]={metrics.HighSegmentRms:0.000000}");
}
Console.WriteLine($"Report: {reportPath}");
return results.All(result => result.Errors.Count == 0) ? 0 : 1;

static async Task<List<string>> RenderAsync(
    Scenario scenario,
    string inputPath,
    string outputPath)
{
    var errors = new List<string>();
    if (scenario.CustomLimiter)
    {
        var samples = ReadKnownFloatWave(inputPath);
        var processed = ApplyLookaheadLimiter(
            samples,
            channels,
            sampleRate,
            scenario.Volume / 100.0,
            DbToLinear(-1),
            5,
            80);
        WriteFloatWave(outputPath, processed);
        return errors;
    }

    var vlcOptions = new List<string>
    {
        "--no-video",
        "--no-video-title-show",
        "--aout=file",
        $"--audiofile-file={outputPath}",
        "--audiofile-format=float32",
        "--audiofile-channels=2",
        "--audiofile-wav",
        "--audio-replay-gain-mode=none",
    };
    if (scenario.Filter is not null)
    {
        vlcOptions.Add("--audio-filter");
        vlcOptions.Add(scenario.Filter);
        var configPath = Path.Combine(Path.GetDirectoryName(outputPath)!, scenario.Name + ".vlcrc");
        File.WriteAllText(
            configPath,
            $"[core]{Environment.NewLine}" +
            $"audio-filter={scenario.Filter}{Environment.NewLine}" +
            $"[gain]{Environment.NewLine}" +
            $"gain-value={scenario.Gain ?? 1:0.0###}{Environment.NewLine}" +
            $"[compressor]{Environment.NewLine}" +
            "compressor-rms-peak=1.0\n" +
            "compressor-attack=1.5\n" +
            "compressor-release=80.0\n" +
            "compressor-threshold=-1.0\n" +
            "compressor-ratio=20.0\n" +
            "compressor-knee=1.0\n" +
            "compressor-makeup-gain=0.0\n");
        vlcOptions.Add($"--config={configPath}");
        vlcOptions.Add("--no-ignore-config");
    }
    if (scenario.Gain is not null)
    {
        vlcOptions.Add($"--gain-value={scenario.Gain.Value:0.0###}");
    }
    if (scenario.CoreGain is not null)
    {
        vlcOptions.Add($"--gain={scenario.CoreGain.Value:0.0###}");
    }
    if (scenario.Filter?.Contains("compressor", StringComparison.Ordinal) == true)
    {
        vlcOptions.AddRange(new[]
        {
            "--compressor-rms-peak=1.0",
            "--compressor-attack=1.5",
            "--compressor-release=80.0",
            "--compressor-threshold=-1.0",
            "--compressor-ratio=20.0",
            "--compressor-knee=1.0",
            "--compressor-makeup-gain=0.0",
        });
    }

    using var libVlc = new LibVLC(vlcOptions.ToArray());
    using var media = new Media(libVlc, new Uri(inputPath));
    if (scenario.Filter is not null)
    {
        media.AddOption($":audio-filter={scenario.Filter}");
    }
    using var player = new MediaPlayer(libVlc);
    using var equalizer = scenario.PreampDb is null ? null : new Equalizer();
    if (equalizer is not null)
    {
        if (!equalizer.SetPreamp(scenario.PreampDb.GetValueOrDefault()))
        {
            errors.Add($"Equalizer rejected preamp {scenario.PreampDb.GetValueOrDefault():0.####} dB.");
        }
        if (!player.SetEqualizer(equalizer))
        {
            errors.Add("MediaPlayer rejected the equalizer.");
        }
    }
    var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    player.Playing += OnPlaying;
    player.EndReached += OnEndReached;
    player.EncounteredError += OnEncounteredError;
    try
    {
        if (!player.Play(media))
        {
            errors.Add("MediaPlayer.Play returned false.");
            return errors;
        }

        var startTimeout = Task.Delay(TimeSpan.FromSeconds(2));
        var started = await Task.WhenAny(playing.Task, failed.Task, startTimeout);
        if (started != playing.Task)
        {
            errors.Add(started == failed.Task
                ? "LibVLC reported an error before playback started."
                : "Timed out waiting for playback to start.");
            return errors;
        }

        player.Volume = scenario.Volume;
        if (player.Volume != scenario.Volume)
        {
            errors.Add($"Requested volume {scenario.Volume}, reported {player.Volume} after Playing.");
        }

        var timeout = Task.Delay(TimeSpan.FromSeconds(12));
        var completed = await Task.WhenAny(ended.Task, failed.Task, timeout);
        if (completed == failed.Task)
        {
            errors.Add("LibVLC reported a playback error.");
        }
        else if (completed == timeout)
        {
            errors.Add("Timed out waiting for file playback to finish.");
        }
    }
    finally
    {
        player.Stop();
        player.Playing -= OnPlaying;
        player.EndReached -= OnEndReached;
        player.EncounteredError -= OnEncounteredError;
    }

    return errors;

    void OnPlaying(object? sender, EventArgs eventArgs) => playing.TrySetResult();
    void OnEndReached(object? sender, EventArgs eventArgs) => ended.TrySetResult();
    void OnEncounteredError(object? sender, EventArgs eventArgs) => failed.TrySetResult();
}

static void WriteInputWave(string path)
{
    var frameCount = checked((int)(sampleRate * durationSeconds));
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    var dataBytes = checked(frameCount * channels * sizeof(float));
    writer.Write("RIFF"u8);
    writer.Write(36 + dataBytes);
    writer.Write("WAVE"u8);
    writer.Write("fmt "u8);
    writer.Write(16);
    writer.Write((ushort)3);
    writer.Write((ushort)channels);
    writer.Write(sampleRate);
    writer.Write(sampleRate * channels * sizeof(float));
    writer.Write((ushort)(channels * sizeof(float)));
    writer.Write((ushort)32);
    writer.Write("data"u8);
    writer.Write(dataBytes);

    for (var frame = 0; frame < frameCount; frame++)
    {
        var time = (double)frame / sampleRate;
        var amplitude = time switch
        {
            >= 0.50 and < 1.30 => DbToLinear(-30),
            >= 1.30 and < 2.10 => DbToLinear(-12),
            >= 2.10 and < 2.90 => DbToLinear(-0.4),
            _ => 0,
        };
        var sample = (float)(amplitude * Math.Sin(2 * Math.PI * 997 * time));
        writer.Write(sample);
        writer.Write(sample);
    }
}

static float[] ReadKnownFloatWave(string path)
{
    var bytes = File.ReadAllBytes(path);
    if (bytes.Length < 44 ||
        System.Text.Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" ||
        System.Text.Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE" ||
        System.Text.Encoding.ASCII.GetString(bytes, 36, 4) != "data")
    {
        throw new InvalidDataException("The generated input WAV has an unexpected layout.");
    }

    var samples = new float[(bytes.Length - 44) / sizeof(float)];
    Buffer.BlockCopy(bytes, 44, samples, 0, samples.Length * sizeof(float));
    return samples;
}

static void WriteFloatWave(string path, float[] samples)
{
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    var dataBytes = checked(samples.Length * sizeof(float));
    writer.Write("RIFF"u8);
    writer.Write(36 + dataBytes);
    writer.Write("WAVE"u8);
    writer.Write("fmt "u8);
    writer.Write(16);
    writer.Write((ushort)3);
    writer.Write((ushort)channels);
    writer.Write(sampleRate);
    writer.Write(sampleRate * channels * sizeof(float));
    writer.Write((ushort)(channels * sizeof(float)));
    writer.Write((ushort)32);
    writer.Write("data"u8);
    writer.Write(dataBytes);
    foreach (var sample in samples) writer.Write(sample);
}

static float[] ApplyLookaheadLimiter(
    float[] input,
    int channelCount,
    int rate,
    double boost,
    double ceiling,
    double lookaheadMilliseconds,
    double releaseMilliseconds)
{
    var frames = input.Length / channelCount;
    var lookaheadFrames = Math.Max(1, (int)Math.Round(rate * lookaheadMilliseconds / 1000));
    var releaseCoefficient = Math.Exp(-1 / (rate * releaseMilliseconds / 1000));
    var framePeaks = new double[frames];
    for (var frame = 0; frame < frames; frame++)
    {
        double peak = 0;
        for (var channel = 0; channel < channelCount; channel++)
        {
            peak = Math.Max(peak, Math.Abs(input[frame * channelCount + channel] * boost));
        }
        framePeaks[frame] = peak;
    }

    var output = new float[input.Length];
    double smoothedGain = 1;
    for (var frame = 0; frame < frames; frame++)
    {
        double lookaheadPeak = 0;
        var last = Math.Min(frames, frame + lookaheadFrames + 1);
        for (var future = frame; future < last; future++)
        {
            lookaheadPeak = Math.Max(lookaheadPeak, framePeaks[future]);
        }

        var requiredGain = lookaheadPeak > ceiling ? ceiling / lookaheadPeak : 1;
        smoothedGain = requiredGain < smoothedGain
            ? requiredGain
            : requiredGain + releaseCoefficient * (smoothedGain - requiredGain);
        for (var channel = 0; channel < channelCount; channel++)
        {
            var index = frame * channelCount + channel;
            var value = input[index] * boost * smoothedGain;
            output[index] = (float)Math.Clamp(value, -ceiling, ceiling);
        }
    }
    return output;
}

static WaveMetrics AnalyzeWave(string path)
{
    using var stream = File.OpenRead(path);
    using var reader = new BinaryReader(stream);
    if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("Missing RIFF header.");
    _ = reader.ReadUInt32();
    if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("Missing WAVE header.");

    ushort format = 0;
    ushort channelCount = 0;
    int rate = 0;
    ushort bits = 0;
    byte[]? data = null;
    while (stream.Position + 8 <= stream.Length)
    {
        var id = new string(reader.ReadChars(4));
        var size = reader.ReadUInt32();
        if (id == "fmt ")
        {
            format = reader.ReadUInt16();
            channelCount = reader.ReadUInt16();
            rate = reader.ReadInt32();
            _ = reader.ReadInt32();
            _ = reader.ReadUInt16();
            bits = reader.ReadUInt16();
            stream.Position += size - 16;
        }
        else if (id == "data")
        {
            data = reader.ReadBytes(checked((int)size));
        }
        else
        {
            stream.Position += size;
        }
        if ((size & 1) != 0 && stream.Position < stream.Length) stream.Position++;
    }

    if (format != 3 || bits != 32) throw new InvalidDataException($"Expected float32 WAV; got format {format}, {bits} bit.");
    if (channelCount != channels || rate != sampleRate) throw new InvalidDataException($"Unexpected format {rate} Hz/{channelCount} ch.");
    if (data is null) throw new InvalidDataException("Missing data chunk.");

    var samples = new float[data.Length / sizeof(float)];
    Buffer.BlockCopy(data, 0, samples, 0, data.Length);
    double peak = 0;
    long overRange = 0;
    long nearClip = 0;
    foreach (var value in samples)
    {
        var absolute = Math.Abs((double)value);
        peak = Math.Max(peak, absolute);
        if (absolute > 1.0) overRange++;
        if (absolute >= 0.999) nearClip++;
    }

    return new WaveMetrics(
        samples.Length / channelCount,
        peak,
        overRange,
        nearClip,
        SegmentRms(samples, channelCount, rate, 0.60, 1.20),
        SegmentRms(samples, channelCount, rate, 1.40, 2.00),
        SegmentRms(samples, channelCount, rate, 2.20, 2.80));
}

static double SegmentRms(float[] samples, int channelCount, int rate, double start, double end)
{
    var first = Math.Min(samples.Length, checked((int)(start * rate * channelCount)));
    var last = Math.Min(samples.Length, checked((int)(end * rate * channelCount)));
    double squares = 0;
    for (var index = first; index < last; index++) squares += samples[index] * samples[index];
    return last > first ? Math.Sqrt(squares / (last - first)) : double.NaN;
}

static void ValidateCustomLimiter(
    Scenario scenario,
    WaveMetrics metrics,
    List<string> errors)
{
    var ceiling = DbToLinear(-1);
    if (metrics.Peak > ceiling + 0.00001 || metrics.OverRangeSamples != 0)
    {
        errors.Add($"Limiter exceeded its -1 dBFS ceiling: peak {metrics.Peak:0.000000}.");
    }
    if (metrics.Peak < ceiling - 0.001)
    {
        errors.Add($"Limiter stress segment did not reach the expected ceiling: {metrics.Peak:0.000000}.");
    }

    var expectedLowRms = DbToLinear(-30) / Math.Sqrt(2) * scenario.Volume / 100;
    var relativeError = Math.Abs(metrics.LowSegmentRms - expectedLowRms) / expectedLowRms;
    if (relativeError > 0.005)
    {
        errors.Add(
            $"Low-level gain differed from {scenario.Volume}% by {relativeError:P2}: " +
            $"RMS {metrics.LowSegmentRms:0.000000}, expected {expectedLowRms:0.000000}.");
    }
}

static double DbToLinear(double decibels) => Math.Pow(10, decibels / 20);

internal sealed record Scenario(
    string Name,
    int Volume,
    string? Filter,
    double? Gain,
    double? CoreGain,
    float? PreampDb = null,
    bool CustomLimiter = false);
internal sealed record ScenarioResult(
    Scenario Scenario,
    string OutputPath,
    WaveMetrics? Metrics,
    List<string> Errors);
internal sealed record WaveMetrics(
    int Frames,
    double Peak,
    long OverRangeSamples,
    long NearClipSamples,
    double LowSegmentRms,
    double MidSegmentRms,
    double HighSegmentRms);
