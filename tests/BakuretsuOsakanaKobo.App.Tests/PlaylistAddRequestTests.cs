using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class PlaylistAddRequestTests
{
    [Fact]
    public void From_PreservesSupportedOrderDuplicatesJapaneseAndWhitespace()
    {
        var first = @"C:\動画\  日本語 動画  .MP4";
        var second = @"D:\movie.wmv";

        var request = PlaylistAddRequest.From([first, second, first]);

        Assert.Equal([first, second, first], request.Paths);
        Assert.Equal(0, request.RejectedCount);
    }

    [Fact]
    public void From_RejectsUnsupportedAndBlankItemsWhileKeepingSupportedItems()
    {
        var request = PlaylistAddRequest.From(
            [@"C:\movie.mp4", @"C:\notes.txt", null, " ", "relative.mp4", @"C:\movie.WMV"]);

        Assert.Equal([@"C:\movie.mp4", @"C:\movie.WMV"], request.Paths);
        Assert.Equal(4, request.RejectedCount);
    }
}
