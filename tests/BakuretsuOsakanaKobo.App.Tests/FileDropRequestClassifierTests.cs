using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class FileDropRequestClassifierTests
{
    [Fact]
    public void Classify_EmptyDropIsIgnored()
    {
        Assert.Equal(FileDropRequestKind.None, FileDropRequestClassifier.Classify([]).Kind);
    }

    [Theory]
    [InlineData(@"C:\動画\sample.MP4")]
    [InlineData(@"C:\videos\sample.wmv")]
    public void Classify_SingleSupportedFilePreservesPath(string path)
    {
        var request = FileDropRequestClassifier.Classify([path]);

        Assert.Equal(FileDropRequestKind.SingleSupportedFile, request.Kind);
        Assert.Equal(path, request.Path);
    }

    [Fact]
    public void Classify_SingleUnsupportedFileIsRejected()
    {
        var request = FileDropRequestClassifier.Classify([@"C:\videos\sample.avi"]);

        Assert.Equal(FileDropRequestKind.UnsupportedFile, request.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Classify_BlankPathIsRejectedWithoutThrowing(string path)
    {
        Assert.Equal(
            FileDropRequestKind.UnsupportedFile,
            FileDropRequestClassifier.Classify([path]).Kind);
    }

    [Fact]
    public void Classify_MultipleFilesAreRejectedBeforeInspectingExtensions()
    {
        var request = FileDropRequestClassifier.Classify(
            [@"C:\videos\one.mp4", @"C:\videos\two.wmv"]);

        Assert.Equal(FileDropRequestKind.MultipleFiles, request.Kind);
        Assert.Null(request.Path);
    }
}
