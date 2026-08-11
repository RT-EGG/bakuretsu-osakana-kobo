using BakuretsuOsakanaKobo.Playback;
using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class StreamingLookaheadLimiterTests
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private static readonly double Ceiling = Math.Pow(10, -1.0 / 20);

    [Fact]
    public void ProcessAndFlush_BoostSmallSignalAndCapLargePeak()
    {
        var limiter = CreateLimiter(boost: 5);
        var input = CreateConstantFrames(frameCount: 480, left: 0.02f, right: -0.02f)
            .Concat(CreateConstantFrames(frameCount: 480, left: 0.9f, right: -0.9f))
            .ToArray();

        var output = limiter.Process(input).Concat(limiter.Flush()).ToArray();

        Assert.Equal(input.Length, output.Length);
        Assert.Contains(output.Take(480 * Channels), sample => Math.Abs(sample - 0.1f) < 0.0001f);
        Assert.All(output, sample => Assert.InRange(Math.Abs(sample), 0, (float)Ceiling));
        Assert.Equal(960, limiter.OutputFrames);
        Assert.Equal(0, limiter.PendingFrames);
        Assert.Equal(0, limiter.OverRangeSamples);
        Assert.True(limiter.MinimumAppliedGain < 1);
    }

    [Fact]
    public void Process_ReplacesNonFiniteInputWithoutProducingNonFiniteOutput()
    {
        var limiter = CreateLimiter(boost: 5);
        var input = CreateConstantFrames(frameCount: 300, left: 0.01f, right: -0.01f);
        input[20] = float.NaN;
        input[21] = float.PositiveInfinity;

        var output = limiter.Process(input).Concat(limiter.Flush()).ToArray();

        Assert.Equal(2, limiter.NonFiniteInputSamples);
        Assert.Equal(0, limiter.NonFiniteOutputSamples);
        Assert.All(output, sample => Assert.True(float.IsFinite(sample)));
    }

    [Fact]
    public void Reset_DiscardsPendingLookaheadAndStartsClean()
    {
        var limiter = CreateLimiter(boost: 2);
        var pendingOnly = CreateConstantFrames(frameCount: 120, left: 0.1f, right: 0.1f);

        Assert.Empty(limiter.Process(pendingOnly));
        Assert.Equal(120, limiter.PendingFrames);

        limiter.Reset();

        Assert.Equal(0, limiter.PendingFrames);
        Assert.Empty(limiter.Flush());
    }

    [Fact]
    public void Process_RejectsIncompletePcmFrame()
    {
        var limiter = CreateLimiter(boost: 5);

        Assert.Throws<ArgumentException>(() => limiter.Process([0.1f]));
    }

    [Fact]
    public void Constructor_RejectsNonFiniteDspParameters()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateLimiter(double.NaN));
    }

    [Fact]
    public void UpdateParameters_AppliesNewLevelToPendingLookahead()
    {
        var limiter = CreateLimiter(boost: 1);
        var pendingOnly = CreateConstantFrames(frameCount: 120, left: 0.1f, right: -0.1f);
        Assert.Empty(limiter.Process(pendingOnly));

        limiter.UpdateParameters(boost: 2, ceiling: 1);
        var output = limiter.Flush();

        Assert.All(output, sample => Assert.InRange(Math.Abs(sample), 0.1999f, 0.2001f));
    }

    private static StreamingLookaheadLimiter CreateLimiter(double boost) =>
        new(
            Channels,
            SampleRate,
            boost,
            Ceiling,
            lookaheadMilliseconds: 5,
            releaseMilliseconds: 80);

    private static float[] CreateConstantFrames(int frameCount, float left, float right)
    {
        var samples = new float[frameCount * Channels];
        for (var frame = 0; frame < frameCount; frame++)
        {
            samples[frame * Channels] = left;
            samples[(frame * Channels) + 1] = right;
        }

        return samples;
    }
}
