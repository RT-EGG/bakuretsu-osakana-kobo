using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class RecentFileMenuPresentationTests
{
    [Fact]
    public void From_PreservesOrderAndSeparatesMissingFiles()
    {
        var paths = new[]
        {
            Path.GetFullPath("first.mp4"),
            Path.GetFullPath("missing-one.wmv"),
            Path.GetFullPath("missing-two.mp4"),
        };

        var presentation = RecentFileMenuPresentation.From(
            paths,
            path => string.Equals(path, paths[0], StringComparison.OrdinalIgnoreCase));

        Assert.Equal(paths, presentation.Files.Select(file => file.Path));
        Assert.Equal(
            ["first.mp4", "missing-one.wmv", "missing-two.mp4"],
            presentation.Files.Select(file => file.DisplayName));
        Assert.False(presentation.Files[0].IsMissing);
        Assert.All(presentation.Files.Skip(1), file => Assert.True(file.IsMissing));
        Assert.Equal(paths.Skip(1), presentation.MissingFiles.Select(file => file.Path));
        Assert.True(presentation.ShowRemoveAllMissing);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void ShowRemoveAllMissing_RequiresAtLeastTwoMissingFiles(
        int missingCount,
        bool expected)
    {
        var paths = Enumerable.Range(0, missingCount)
            .Select(index => Path.GetFullPath($"missing-{index}.mp4"));

        var presentation = RecentFileMenuPresentation.From(paths, _ => false);

        Assert.Equal(expected, presentation.ShowRemoveAllMissing);
    }
}
