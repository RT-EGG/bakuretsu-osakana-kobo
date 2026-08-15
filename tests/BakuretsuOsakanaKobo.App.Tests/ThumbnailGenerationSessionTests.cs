using BakuretsuOsakanaKobo.Infrastructure.Persistence;
using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class ThumbnailGenerationSessionTests
{
    [Fact]
    public void Create_NormalizesPathAndFreezesInterval()
    {
        var relativePath = Path.Combine("media", "video.mp4");

        var session = ThumbnailGenerationSession.Create(relativePath, 1.25);

        Assert.Equal(Path.GetFullPath(relativePath), session.VideoPath);
        Assert.Equal(1.25, session.IntervalPercent);
    }

    [Theory]
    [InlineData(0.3)]
    [InlineData(5.25)]
    public void Create_RejectsInvalidInterval(double intervalPercent)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ThumbnailGenerationSession.Create("video.mp4", intervalPercent));
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(5.0)]
    public void IntervalValidation_AcceptsBoundaries(double intervalPercent)
    {
        Assert.True(ThumbnailGenerationInterval.IsValid(intervalPercent));
    }
}
