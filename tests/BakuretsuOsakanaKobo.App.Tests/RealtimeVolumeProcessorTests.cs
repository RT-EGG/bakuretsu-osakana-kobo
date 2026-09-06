using BakuretsuOsakanaKobo.Playback;
using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class RealtimeVolumeProcessorTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(50, 0.125)]
    [InlineData(100, 0.25)]
    public void Process_AtOrBelowNativeMaximum_AppliesLinearVolume(int volumePercent, double expected)
    {
        var processor = new RealtimeVolumeProcessor();
        processor.SetVolumePercent(volumePercent);

        var output = ProcessConstant(processor, amplitude: 0.25f);

        Assert.All(output, sample => Assert.InRange(Math.Abs(sample), expected - 0.0001, expected + 0.0001));
    }

    [Fact]
    public void Process_AboveNativeMaximum_BoostsSmallSignalAndLimitsPeak()
    {
        var processor = new RealtimeVolumeProcessor();
        processor.SetVolumePercent(500);

        var smallSignal = ProcessConstant(processor, amplitude: 0.02f);
        Assert.All(smallSignal, sample => Assert.InRange(Math.Abs(sample), 0.0998, 0.1002));

        processor.Reset();
        var largeSignal = ProcessConstant(processor, amplitude: 0.9f);
        var ceiling = Math.Pow(10, RealtimeVolumeProcessor.BoostedCeilingDecibels / 20);
        Assert.All(largeSignal, sample => Assert.InRange(Math.Abs(sample), 0, ceiling + 0.000001));
    }

    [Fact]
    public void SetMuted_PreservesVolumeAndSilencesPendingAudioImmediately()
    {
        var processor = new RealtimeVolumeProcessor();
        processor.SetVolumePercent(350);
        Assert.Empty(processor.Process(CreateConstantPcm(frameCount: 120, amplitude: 0.1f)));

        processor.SetMuted(true);
        var muted = processor.Flush();

        Assert.Equal(350, processor.VolumePercent);
        Assert.True(processor.IsMuted);
        Assert.All(muted, sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void SetVolumePercent_ClampsToProductMaximum()
    {
        var processor = new RealtimeVolumeProcessor();

        processor.SetVolumePercent(501);

        Assert.Equal(PlaybackVolume.MaximumPercent, processor.VolumePercent);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(384, 1)]
    [InlineData(384_000, 1000)]
    [InlineData(-1, 0)]
    public void BufferedMilliseconds_ConvertsStereoFloatQueueDepth(
        int bufferedBytes,
        double expectedMilliseconds)
    {
        Assert.Equal(
            expectedMilliseconds,
            RealtimeAudioOutput.BufferedMilliseconds(bufferedBytes),
            precision: 6);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(7_672, true)]
    [InlineData(7_680, false)]
    [InlineData(15_360, false)]
    public void IsBelowRebufferThreshold_UsesTwentyMillisecondLowWatermark(
        int bufferedBytes,
        bool expected)
    {
        Assert.Equal(expected, RealtimeAudioOutput.IsBelowRebufferThreshold(bufferedBytes));
    }

    [Fact]
    public void RealtimeAudioOutput_RecordsLevelChangesWithoutStartingWasapi()
    {
        using var output = new RealtimeAudioOutput(_ => { });
        var initial = output.Diagnostics;

        output.SetVolumePercent(250);
        output.SetMuted(true);

        var diagnostics = output.Diagnostics;
        Assert.Equal(initial.LevelChangeCount + 2, diagnostics.LevelChangeCount);
        Assert.Equal(0, diagnostics.LastLevelChangeBufferedBytes);
        Assert.Equal(0, diagnostics.LastLevelChangeBufferedMilliseconds);
        Assert.False(diagnostics.OutputStarted);
        Assert.Equal(250, output.VolumePercent);
        Assert.True(output.IsMuted);
    }

    [Fact]
    public void RealtimeAudioPipeline_AppliesCurrentVolumeToAlreadyQueuedRawPcm()
    {
        var pipeline = new RealtimeAudioPipeline();
        pipeline.AddPcm16(CreateConstantPcm(frameCount: 1_500, amplitude: 0.1f));

        pipeline.SetVolumePercent(25);
        var output = ReadFrames(pipeline, frameCount: 480, isDraining: false);

        Assert.Equal(480 * RealtimeVolumeProcessor.Channels, output.Length);
        Assert.All(output, sample => Assert.InRange(Math.Abs(sample), 0.0248f, 0.0252f));
        Assert.True(pipeline.RawBufferedBytes > 0);
    }

    [Fact]
    public void RealtimeAudioPipeline_MutesQueuedRawPcmAndPendingLookaheadOnNextRead()
    {
        var pipeline = new RealtimeAudioPipeline();
        pipeline.AddPcm16(CreateConstantPcm(frameCount: 2_000, amplitude: 0.1f));
        Assert.Equal(480 * RealtimeVolumeProcessor.Channels, ReadFrames(pipeline, 480, false).Length);

        pipeline.SetMuted(true);
        var muted = ReadFrames(pipeline, frameCount: 480, isDraining: false);

        Assert.All(muted, sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void RealtimeAudioPipeline_QueuedRawPcmAtFiveHundredPercentKeepsBoostedCeiling()
    {
        var pipeline = new RealtimeAudioPipeline();
        pipeline.AddPcm16(CreateConstantPcm(frameCount: 1_500, amplitude: 0.9f));

        pipeline.SetVolumePercent(500);
        var output = ReadFrames(pipeline, frameCount: 480, isDraining: false);
        var ceiling = Math.Pow(10, RealtimeVolumeProcessor.BoostedCeilingDecibels / 20);

        Assert.All(output, sample => Assert.InRange(Math.Abs(sample), 0, ceiling + 0.000001));
        Assert.Equal(0, pipeline.OverRangeSamples);
        Assert.Equal(0, pipeline.NonFiniteOutputSamples);
    }

    [Fact]
    public void RealtimeAudioPipeline_DrainEmitsEveryQueuedAndLookaheadFrameInOrder()
    {
        const int frameCount = 1_000;
        var pipeline = new RealtimeAudioPipeline();
        pipeline.SetVolumePercent(50);
        pipeline.AddPcm16(CreateConstantPcm(frameCount, amplitude: 0.1f));
        pipeline.BeginDrain();

        var output = new List<float>();
        for (var attempt = 0; attempt < 10 && !pipeline.IsDrainComplete; attempt++)
        {
            output.AddRange(ReadFrames(pipeline, frameCount: 128, isDraining: true));
        }

        Assert.True(pipeline.IsDrainComplete);
        Assert.Equal(frameCount * RealtimeVolumeProcessor.Channels, output.Count);
        Assert.All(output, sample => Assert.InRange(Math.Abs(sample), 0.0498f, 0.0502f));
        Assert.Equal(0, pipeline.PendingFrames);
        Assert.Equal(0, pipeline.DrainBufferedBytes);
    }

    [Fact]
    public void RealtimeAudioPipeline_EmptyDrainCompletesWithoutRenderRead()
    {
        var pipeline = new RealtimeAudioPipeline();

        pipeline.BeginDrain();

        Assert.True(pipeline.IsDrainComplete);
        Assert.Equal(0, pipeline.DrainBufferedBytes);
    }

    [Fact]
    public void RealtimeAudioPipeline_ResetAccountsForRawStagedAndLimiterOwnership()
    {
        var pipeline = new RealtimeAudioPipeline();
        pipeline.AddPcm16(CreateConstantPcm(frameCount: 500, amplitude: 0.1f));
        _ = ReadFrames(pipeline, frameCount: 100, isDraining: false);

        var reset = pipeline.Reset();

        Assert.Equal(0, reset.DiscardedProcessedBytes);
        Assert.Equal(160, reset.DiscardedRawFrames);
        Assert.Equal(240, reset.DiscardedLimiterFrames);
        Assert.Equal(0, pipeline.RawBufferedBytes);
        Assert.Equal(0, pipeline.PendingFrames);
    }

    private static float[] ProcessConstant(RealtimeVolumeProcessor processor, float amplitude)
    {
        var output = processor.Process(CreateConstantPcm(frameCount: 480, amplitude));
        return output.Concat(processor.Flush()).ToArray();
    }

    private static short[] CreateConstantPcm(int frameCount, float amplitude)
    {
        var value = (short)Math.Round(amplitude * short.MaxValue);
        return Enumerable.Repeat(value, frameCount * RealtimeVolumeProcessor.Channels).ToArray();
    }

    private static float[] ReadFrames(
        RealtimeAudioPipeline pipeline,
        int frameCount,
        bool isDraining)
    {
        var bytes = new byte[frameCount * pipeline.WaveFormat.BlockAlign];
        var read = pipeline.Read(bytes, 0, bytes.Length, isDraining, out _);
        var samples = new float[read / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, samples, 0, read);
        return samples;
    }
}
