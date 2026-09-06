using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class ApplicationInfoTests
{
    [Fact]
    public void DisplayName_UsesApprovedProductName() =>
        Assert.Equal("爆裂おさかな工房", ApplicationInfo.DisplayName);

    [Fact]
    public void TryGetCurrentSemanticVersion_UsesBuiltAssemblyInformationalVersion()
    {
        Assert.True(ApplicationInfo.TryGetCurrentSemanticVersion(out var version));
        Assert.NotNull(version);
        Assert.Equal((ulong)1, version.Major);
        Assert.Equal((ulong)1, version.Minor);
        Assert.Equal((ulong)1, version.Patch);
    }
}
