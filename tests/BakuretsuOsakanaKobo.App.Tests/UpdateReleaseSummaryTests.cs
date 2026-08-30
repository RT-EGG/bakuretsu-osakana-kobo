using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class UpdateReleaseSummaryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithoutReleaseBody_ReturnsFallback(string? releaseBody) =>
        Assert.Contains("GitHub Release", UpdateReleaseSummary.Create(releaseBody), StringComparison.Ordinal);

    [Fact]
    public void Create_NormalizesLineEndingsAndTrimsWhitespace() =>
        Assert.Equal("first\nsecond\nthird", UpdateReleaseSummary.Create("  first\r\nsecond\rthird  "));

    [Fact]
    public void Create_LongBody_IsBoundedAndMarkedAsTruncated()
    {
        var summary = UpdateReleaseSummary.Create(new string('a', 1000));

        Assert.Equal(601, summary.Length);
        Assert.EndsWith("…", summary, StringComparison.Ordinal);
    }
}
