using System.Net;
using System.Text;
using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class GitHubReleaseClientTests
{
    [Fact]
    public async Task CheckAsync_WithHigherStableReleaseAndValidAsset_ReturnsUpdate()
    {
        using var handler = new StubHandler(HttpStatusCode.OK, ReleaseJson("v1.2.0"));
        using var httpClient = new HttpClient(handler);
        var client = new GitHubReleaseClient(httpClient);

        var result = await client.CheckAsync(Version("1.1.0"));

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("1.2.0", result.Release!.Version.ToString());
        Assert.Equal(GitHubReleaseClient.AssetName, result.Release.Asset.Name);
        Assert.Equal(12345, result.Release.Asset.Size);
        Assert.Equal(new string('A', 64), result.Release.Asset.Sha256Digest);
        Assert.Equal(GitHubReleaseClient.LatestReleaseApiUri, handler.RequestUri);
        Assert.Equal("application/vnd.github+json", handler.Accept);
        Assert.Equal("2026-03-10", handler.ApiVersion);
        Assert.StartsWith("BakuretsuOsakanaKobo/", handler.UserAgent);
    }

    [Theory]
    [InlineData("v1.1.0")]
    [InlineData("v1.0.9")]
    public async Task CheckAsync_WithCurrentOrOlderRelease_ReturnsUpToDate(string tag)
    {
        using var handler = new StubHandler(HttpStatusCode.OK, ReleaseJson(tag));
        using var httpClient = new HttpClient(handler);

        var result = await new GitHubReleaseClient(httpClient).CheckAsync(Version("1.1.0"));

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Null(result.Release);
    }

    [Theory]
    [InlineData("\"draft\":false", "\"draft\":true")]
    [InlineData("\"prerelease\":false", "\"prerelease\":true")]
    [InlineData("\"name\":\"BakuretsuOsakanaKobo-win-x64.zip\"", "\"name\":\"wrong.zip\"")]
    [InlineData("sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "sha256:invalid")]
    [InlineData("\"state\":\"uploaded\"", "\"state\":\"new\"")]
    public async Task CheckAsync_WithInvalidReleaseOrAsset_ReturnsInvalidResponse(
        string original,
        string replacement)
    {
        var json = ReleaseJson("v1.2.0").Replace(original, replacement, StringComparison.Ordinal);
        using var handler = new StubHandler(HttpStatusCode.OK, json);
        using var httpClient = new HttpClient(handler);

        var result = await new GitHubReleaseClient(httpClient).CheckAsync(Version("1.1.0"));

        Assert.Equal(UpdateCheckStatus.InvalidResponse, result.Status);
        Assert.Null(result.Release);
    }

    [Fact]
    public async Task CheckAsync_WithMalformedVersion_ReturnsInvalidResponse()
    {
        using var handler = new StubHandler(HttpStatusCode.OK, ReleaseJson("not-a-version"));
        using var httpClient = new HttpClient(handler);

        var result = await new GitHubReleaseClient(httpClient).CheckAsync(Version("1.1.0"));

        Assert.Equal(UpdateCheckStatus.InvalidResponse, result.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "RateLimited")]
    [InlineData(HttpStatusCode.TooManyRequests, "RateLimited")]
    [InlineData(HttpStatusCode.InternalServerError, "Unavailable")]
    public async Task CheckAsync_WithHttpFailure_DegradesSafely(
        HttpStatusCode statusCode,
        string expected)
    {
        using var handler = new StubHandler(statusCode, "{}");
        using var httpClient = new HttpClient(handler);

        var result = await new GitHubReleaseClient(httpClient).CheckAsync(Version("1.1.0"));

        Assert.Equal(Enum.Parse<UpdateCheckStatus>(expected), result.Status);
        Assert.Null(result.Release);
    }

    [Fact]
    public async Task CheckAsync_WithOversizedBody_ReturnsInvalidResponse()
    {
        using var handler = new StubHandler(HttpStatusCode.OK, new string(' ', 1024 * 1024 + 1));
        using var httpClient = new HttpClient(handler);

        var result = await new GitHubReleaseClient(httpClient).CheckAsync(Version("1.1.0"));

        Assert.Equal(UpdateCheckStatus.InvalidResponse, result.Status);
    }

    [Fact]
    public async Task CheckAsync_WhenHttpClientTimesOut_DegradesSafely()
    {
        using var handler = new ThrowingHandler(new TaskCanceledException("The request timed out."));
        using var httpClient = new HttpClient(handler);

        var result = await new GitHubReleaseClient(httpClient).CheckAsync(Version("1.1.0"));

        Assert.Equal(UpdateCheckStatus.Unavailable, result.Status);
        Assert.Null(result.Release);
    }

    [Fact]
    public async Task CheckAsync_WhenCallerCancels_PropagatesCancellation()
    {
        using var handler = new ThrowingHandler(new OperationCanceledException());
        using var httpClient = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new GitHubReleaseClient(httpClient).CheckAsync(Version("1.1.0"), cancellation.Token));
    }

    private static SemanticVersion Version(string value)
    {
        Assert.True(SemanticVersion.TryParse(value, out var version));
        return version!;
    }

    private static string ReleaseJson(string tag) => $$"""
        {
          "tag_name":"{{tag}}",
          "name":"Release {{tag}}",
          "body":"Changes",
          "html_url":"https://github.com/RT-EGG/bakuretsu-osakana-kobo/releases/tag/{{tag}}",
          "draft":false,
          "prerelease":false,
          "assets":[{
            "name":"BakuretsuOsakanaKobo-win-x64.zip",
            "state":"uploaded",
            "size":12345,
            "digest":"sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            "browser_download_url":"https://github.com/RT-EGG/bakuretsu-osakana-kobo/releases/download/{{tag}}/BakuretsuOsakanaKobo-win-x64.zip"
          }]
        }
        """;

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _body;

        internal StubHandler(HttpStatusCode statusCode, string body)
        {
            _statusCode = statusCode;
            _body = body;
        }

        internal Uri? RequestUri { get; private set; }

        internal string? Accept { get; private set; }

        internal string? ApiVersion { get; private set; }

        internal string? UserAgent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Accept = request.Headers.Accept.Single().MediaType;
            ApiVersion = request.Headers.GetValues("X-GitHub-Api-Version").Single();
            UserAgent = request.Headers.UserAgent.ToString();
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromException<HttpResponseMessage>(exception);
    }
}
