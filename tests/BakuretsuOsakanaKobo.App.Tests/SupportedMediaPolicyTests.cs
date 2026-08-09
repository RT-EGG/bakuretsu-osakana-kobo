using BakuretsuOsakanaKobo.Playback;
using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class SupportedMediaPolicyTests
{
    [Theory]
    [InlineData("sample.mp4", "h264", "mp4a")]
    [InlineData("sample.MP4", "H264", "MP4A")]
    [InlineData("sample.wmv", "VC-1", "WMAP")]
    public void Validate_AcceptsGuaranteedContainerAndCodecPairs(
        string path,
        string videoCodec,
        string audioCodec)
    {
        var result = SupportedMediaPolicy.Validate(path, videoCodec, audioCodec);

        Assert.True(result.IsAccepted);
    }

    [Theory]
    [InlineData("sample.mkv", "h264", "mp4a", "playback-extension-unsupported")]
    [InlineData("sample.mp4", "hevc", "mp4a", "playback-codec-unsupported")]
    [InlineData("sample.mp4", "VP90", "mp4a", "playback-codec-unsupported")]
    [InlineData("sample.mp4", null, null, "playback-tracks-missing")]
    public void Validate_RejectsUnsupportedOrUnreadableMedia(
        string path,
        string? videoCodec,
        string? audioCodec,
        string expectedEventCode)
    {
        var result = SupportedMediaPolicy.Validate(path, videoCodec, audioCodec);

        Assert.False(result.IsAccepted);
        Assert.Equal(expectedEventCode, result.EventCode);
    }
}
