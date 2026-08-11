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
}
