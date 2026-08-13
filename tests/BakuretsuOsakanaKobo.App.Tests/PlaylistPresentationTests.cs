using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class PlaylistPresentationTests
{
    [Fact]
    public void From_PreservesDuplicateOrderAndMarksMissingEntries()
    {
        var first = Path.GetFullPath("first.mp4");
        var missing = Path.GetFullPath("missing.wmv");
        var paths = new[] { first, missing, first };

        var entries = PlaylistPresentation.From(
            paths,
            path => string.Equals(path, first, StringComparison.OrdinalIgnoreCase));

        Assert.Equal([0, 1, 2], entries.Select(entry => entry.Index));
        Assert.Equal([1, 2, 3], entries.Select(entry => entry.Order));
        Assert.Equal(paths, entries.Select(entry => entry.Path));
        Assert.Equal(["first.mp4", "missing.wmv", "first.mp4"], entries.Select(entry => entry.FileName));
        Assert.Equal([false, true, false], entries.Select(entry => entry.IsMissing));
        Assert.All(entries, entry => Assert.False(entry.HasLoadError));
        Assert.All(entries, entry => Assert.False(entry.IsCurrent));
    }

    [Fact]
    public void From_MarksCurrentAndLoadErrorRegistrationsByIndex()
    {
        var paths = new[] { Path.GetFullPath("same.mp4"), Path.GetFullPath("same.mp4") };

        var entries = PlaylistPresentation.From(
            paths,
            _ => true,
            currentIndex: 1,
            loadErrorIndices: new HashSet<int> { 0 });

        Assert.True(entries[0].HasLoadError);
        Assert.Equal("読み込み不能", entries[0].StatusText);
        Assert.True(entries[1].IsCurrent);
    }
}
