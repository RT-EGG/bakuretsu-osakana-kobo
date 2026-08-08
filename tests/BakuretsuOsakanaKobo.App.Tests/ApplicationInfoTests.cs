using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class ApplicationInfoTests
{
    [Fact]
    public void DisplayName_UsesApprovedProductName()
    {
        Assert.Equal("爆裂おさかな工房", ApplicationInfo.DisplayName);
    }
}
