using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class SemanticVersionTests
{
    [Theory]
    [InlineData("0.0.0")]
    [InlineData("1.2.3")]
    [InlineData("1.2.3-alpha.1")]
    [InlineData("1.2.3-alpha.1+build.45")]
    public void TryParse_AcceptsSemanticVersions(string value)
    {
        Assert.True(SemanticVersion.TryParse(value, out var version));
        Assert.NotNull(version);
        Assert.Equal(value, version.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("v1.2.3")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("01.2.3")]
    [InlineData("1.02.3")]
    [InlineData("1.2.03")]
    [InlineData("1.2.3-01")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3+")]
    [InlineData("1.2.3 alpha")]
    public void TryParse_RejectsInvalidVersions(string value) =>
        Assert.False(SemanticVersion.TryParse(value, out _));

    [Theory]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-alpha.beta", "1.0.0-beta")]
    [InlineData("1.0.0-beta", "1.0.0-beta.2")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]
    [InlineData("1.0.0-beta.11", "1.0.0-rc.1")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    [InlineData("1.0.0", "1.0.1")]
    [InlineData("1.9.9", "2.0.0")]
    public void CompareTo_UsesSemanticVersionPrecedence(string lower, string higher)
    {
        Assert.True(SemanticVersion.TryParse(lower, out var lowerVersion));
        Assert.True(SemanticVersion.TryParse(higher, out var higherVersion));

        Assert.True(lowerVersion!.CompareTo(higherVersion) < 0);
        Assert.True(higherVersion!.CompareTo(lowerVersion) > 0);
    }

    [Fact]
    public void CompareTo_IgnoresBuildMetadata()
    {
        Assert.True(SemanticVersion.TryParse("1.2.3+first", out var first));
        Assert.True(SemanticVersion.TryParse("1.2.3+second", out var second));

        Assert.Equal(0, first!.CompareTo(second));
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second!.GetHashCode());
    }
}
