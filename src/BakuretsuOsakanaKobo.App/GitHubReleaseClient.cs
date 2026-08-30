using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BakuretsuOsakanaKobo;

internal enum UpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    RateLimited,
    Unavailable,
    InvalidResponse,
}

internal sealed record UpdateAsset(
    string Name,
    long Size,
    string Sha256Digest,
    Uri DownloadUri);

internal sealed record UpdateRelease(
    SemanticVersion Version,
    string TagName,
    string Name,
    string Body,
    Uri ReleasePageUri,
    UpdateAsset Asset);

internal sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    UpdateRelease? Release = null,
    string? TechnicalMessage = null);

internal interface IUpdateCheckService
{
    Task<UpdateCheckResult> CheckAsync(
        SemanticVersion currentVersion,
        CancellationToken cancellationToken = default);
}

internal sealed class GitHubReleaseClient : IUpdateCheckService
{
    internal const string AssetName = "BakuretsuOsakanaKobo-win-x64.zip";
    internal static readonly Uri LatestReleaseApiUri =
        new("https://api.github.com/repos/RT-EGG/bakuretsu-osakana-kobo/releases/latest");
    private const int MaximumResponseBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;

    internal GitHubReleaseClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<UpdateCheckResult> CheckAsync(
        SemanticVersion currentVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);

        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        request.Headers.UserAgent.ParseAdd(
            $"BakuretsuOsakanaKobo/{currentVersion.Major}.{currentVersion.Minor}.{currentVersion.Patch}");

        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                return new UpdateCheckResult(UpdateCheckStatus.RateLimited, TechnicalMessage: response.ReasonPhrase);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.Unavailable,
                    TechnicalMessage: $"GitHub returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
            }

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength > MaximumResponseBytes)
            {
                return new UpdateCheckResult(UpdateCheckStatus.InvalidResponse, TechnicalMessage: "The release response was too large.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var bytes = await ReadBoundedAsync(stream, cancellationToken).ConfigureAwait(false);
            if (bytes is null)
            {
                return new UpdateCheckResult(UpdateCheckStatus.InvalidResponse, TechnicalMessage: "The release response exceeded the size limit.");
            }

            var document = JsonSerializer.Deserialize<GitHubReleaseDocument>(bytes, SerializerOptions);
            return Validate(document, currentVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Unavailable, TechnicalMessage: exception.Message);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or JsonException or NotSupportedException)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Unavailable, TechnicalMessage: exception.Message);
        }
    }

    private static UpdateCheckResult Validate(
        GitHubReleaseDocument? document,
        SemanticVersion currentVersion)
    {
        if (document is null || document.Draft || document.Prerelease || string.IsNullOrWhiteSpace(document.TagName))
        {
            return Invalid("The latest release document was missing required stable-release fields.");
        }

        var versionText = document.TagName.StartsWith('v') ? document.TagName[1..] : document.TagName;
        if (!SemanticVersion.TryParse(versionText, out var version) || version is null)
        {
            return Invalid("The latest release tag was not a valid semantic version.");
        }

        var matchingAssets = document.Assets?.Where(candidate =>
            string.Equals(candidate.Name, AssetName, StringComparison.Ordinal)).ToArray() ?? [];
        if (matchingAssets.Length != 1)
        {
            return Invalid("The latest release did not contain exactly one update asset.");
        }

        var asset = matchingAssets[0];
        if (
            !string.Equals(asset.State, "uploaded", StringComparison.Ordinal) ||
            asset.Size <= 0 ||
            !TryNormalizeSha256(asset.Digest, out var digest) ||
            !TryCreateGitHubUri(asset.BrowserDownloadUrl, out var downloadUri) ||
            !TryCreateGitHubUri(document.HtmlUrl, out var releasePageUri))
        {
            return Invalid("The latest release did not contain one valid update asset and release page.");
        }

        if (version.CompareTo(currentVersion) <= 0)
        {
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate);
        }

        return new UpdateCheckResult(
            UpdateCheckStatus.UpdateAvailable,
            new UpdateRelease(
                version,
                document.TagName,
                string.IsNullOrWhiteSpace(document.Name) ? document.TagName : document.Name,
                document.Body ?? string.Empty,
                releasePageUri!,
                new UpdateAsset(AssetName, asset.Size, digest!, downloadUri!)));
    }

    private static UpdateCheckResult Invalid(string message) =>
        new(UpdateCheckStatus.InvalidResponse, TechnicalMessage: message);

    private static bool TryNormalizeSha256(string? value, out string? digest)
    {
        digest = null;
        const string prefix = "sha256:";
        if (value is null || !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var hex = value[prefix.Length..];
        if (hex.Length != 64 || !hex.All(Uri.IsHexDigit))
        {
            return false;
        }

        digest = hex.ToUpperInvariant();
        return true;
    }

    private static bool TryCreateGitHubUri(string? value, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate) ||
            candidate.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(candidate.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        uri = candidate;
        return true;
    }

    private static async Task<byte[]?> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > MaximumResponseBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }
    }

    private sealed class GitHubReleaseDocument
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("body")]
        public string? Body { get; init; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; init; }

        [JsonPropertyName("draft")]
        public bool Draft { get; init; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; init; }

        [JsonPropertyName("assets")]
        public GitHubReleaseAssetDocument[]? Assets { get; init; }
    }

    private sealed class GitHubReleaseAssetDocument
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("state")]
        public string? State { get; init; }

        [JsonPropertyName("size")]
        public long Size { get; init; }

        [JsonPropertyName("digest")]
        public string? Digest { get; init; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; init; }
    }
}
