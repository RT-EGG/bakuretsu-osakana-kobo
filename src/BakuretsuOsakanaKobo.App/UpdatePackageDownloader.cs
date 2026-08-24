using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BakuretsuOsakanaKobo;

internal enum UpdatePackageDownloadStatus
{
    Prepared,
    Unavailable,
    InvalidPackage,
    InsufficientSpace,
    StorageFailure,
}

internal sealed record PreparedUpdatePackage(
    string WorkingDirectory,
    string ArchivePath,
    long UncompressedBytes,
    IReadOnlyList<string> Files);

internal sealed record UpdatePackageDownloadResult(
    UpdatePackageDownloadStatus Status,
    PreparedUpdatePackage? Package = null,
    string? TechnicalMessage = null);

internal interface IUpdateStorageCapacity
{
    long GetAvailableFreeSpace(string directoryPath);
}

internal sealed class DriveUpdateStorageCapacity : IUpdateStorageCapacity
{
    public long GetAvailableFreeSpace(string directoryPath)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(directoryPath));
        if (string.IsNullOrEmpty(root))
        {
            throw new IOException("The update working directory did not have a drive root.");
        }

        return new DriveInfo(root).AvailableFreeSpace;
    }
}

internal sealed class UpdatePackageDownloader
{
    internal const long MaximumArchiveBytes = 512L * 1024 * 1024;
    internal const long MaximumUncompressedBytes = 1024L * 1024 * 1024;
    internal const int MaximumManifestBytes = 1024 * 1024;
    private const int MaximumFileCount = 10_000;
    private const int MaximumRelativePathLength = 240;
    private const int BufferSize = 64 * 1024;
    private const string ManifestName = "release-manifest.json";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly IUpdateStorageCapacity _storageCapacity;

    internal UpdatePackageDownloader(
        HttpClient httpClient,
        IUpdateStorageCapacity? storageCapacity = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _storageCapacity = storageCapacity ?? new DriveUpdateStorageCapacity();
    }

    internal async Task<UpdatePackageDownloadResult> DownloadAsync(
        UpdateAsset asset,
        string workingRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingRoot);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(asset.Name, GitHubReleaseClient.AssetName, StringComparison.Ordinal) ||
            asset.Size <= 0 ||
            asset.Size > MaximumArchiveBytes ||
            !IsSha256(asset.Sha256Digest) ||
            !IsApprovedDownloadUri(asset.DownloadUri, asset.Name))
        {
            return Invalid("The update asset metadata was outside the approved download boundary.");
        }

        var fullWorkingRoot = Path.GetFullPath(workingRoot);
        string? workingDirectory = null;
        try
        {
            if (_storageCapacity.GetAvailableFreeSpace(fullWorkingRoot) < asset.Size)
            {
                return Insufficient("The update working drive did not have enough space for the archive.");
            }

            Directory.CreateDirectory(fullWorkingRoot);
            workingDirectory = Path.Combine(fullWorkingRoot, $"update-{Guid.NewGuid():N}");
            Directory.CreateDirectory(workingDirectory);
            var partialPath = Path.Combine(workingDirectory, $"{asset.Name}.partial");
            var archivePath = Path.Combine(workingDirectory, asset.Name);

            using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUri);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return CleanupAndReturn(
                    workingDirectory,
                    new UpdatePackageDownloadResult(
                        UpdatePackageDownloadStatus.Unavailable,
                        TechnicalMessage: $"GitHub returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase})."));
            }

            if (response.Content.Headers.ContentLength is { } contentLength && contentLength != asset.Size)
            {
                return CleanupAndReturn(workingDirectory, Invalid("The update response size did not match the release asset."));
            }

            var download = await DownloadToFileAsync(
                response.Content,
                partialPath,
                asset.Size,
                cancellationToken).ConfigureAwait(false);
            if (download.Size != asset.Size ||
                !string.Equals(download.Sha256, asset.Sha256Digest, StringComparison.OrdinalIgnoreCase))
            {
                return CleanupAndReturn(workingDirectory, Invalid("The downloaded update size or SHA-256 did not match the release asset."));
            }

            File.Move(partialPath, archivePath);
            var packageValidation = await ValidatePackageAsync(archivePath, cancellationToken).ConfigureAwait(false);
            if (!packageValidation.Success || packageValidation.Manifest is null)
            {
                return CleanupAndReturn(workingDirectory, Invalid(packageValidation.ErrorMessage ?? "The update ZIP was invalid."));
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (_storageCapacity.GetAvailableFreeSpace(workingDirectory) < packageValidation.Manifest.TotalBytes)
            {
                return CleanupAndReturn(
                    workingDirectory,
                    Insufficient("The update working drive did not have enough space to stage the package contents."));
            }

            return new UpdatePackageDownloadResult(
                UpdatePackageDownloadStatus.Prepared,
                new PreparedUpdatePackage(
                    workingDirectory,
                    archivePath,
                    packageValidation.Manifest.TotalBytes,
                    packageValidation.Manifest.Files.Select(file => file.Path).ToArray()));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CleanupOwnedDirectory(workingDirectory);
            throw;
        }
        catch (OperationCanceledException exception)
        {
            return CleanupAndReturn(
                workingDirectory,
                new UpdatePackageDownloadResult(UpdatePackageDownloadStatus.Unavailable, TechnicalMessage: exception.Message));
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return CleanupAndReturn(
                workingDirectory,
                new UpdatePackageDownloadResult(UpdatePackageDownloadStatus.StorageFailure, TechnicalMessage: exception.Message));
        }
    }

    internal static void DeletePreparedPackage(PreparedUpdatePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var fullWorkingDirectory = Path.GetFullPath(package.WorkingDirectory);
        var fullArchivePath = Path.GetFullPath(package.ArchivePath);
        var directoryName = Path.GetFileName(fullWorkingDirectory);
        if (directoryName.Length != 39 ||
            !directoryName.StartsWith("update-", StringComparison.Ordinal) ||
            !directoryName[7..].All(Uri.IsHexDigit) ||
            !string.Equals(Path.GetDirectoryName(fullArchivePath), fullWorkingDirectory, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(fullArchivePath), GitHubReleaseClient.AssetName, StringComparison.Ordinal))
        {
            throw new ArgumentException("The prepared update package paths were outside the owned update directory.", nameof(package));
        }

        CleanupOwnedDirectory(package.WorkingDirectory);
    }

    private static async Task<(long Size, string Sha256)> DownloadToFileAsync(
        HttpContent content,
        string path,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > expectedSize)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
        return (total, Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static async Task<PackageValidation> ValidatePackageAsync(
        string archivePath,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count == 0 || archive.Entries.Count > MaximumFileCount * 2)
            {
                return PackageValidation.Invalid("The update ZIP entry count was outside the approved limit.");
            }

            var fileEntries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            var allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long totalBytes = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryValidateRelativePath(entry.FullName, out var normalizedPath))
                {
                    return PackageValidation.Invalid($"The update ZIP contained an unsafe path: {entry.FullName}");
                }

                var collisionPath = normalizedPath!.TrimEnd('/');
                if (!allPaths.Add(collisionPath) || IsLinkOrReparsePoint(entry))
                {
                    return PackageValidation.Invalid($"The update ZIP contained a duplicate or linked path: {entry.FullName}");
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    if (entry.Length != 0 || entry.CompressedLength != 0)
                    {
                        return PackageValidation.Invalid($"The update ZIP contained data in a directory entry: {entry.FullName}");
                    }

                    continue;
                }

                if (!fileEntries.TryAdd(normalizedPath!, entry))
                {
                    return PackageValidation.Invalid($"The update ZIP contained a duplicate path: {normalizedPath}");
                }

                totalBytes = checked(totalBytes + entry.Length);
                if (totalBytes > MaximumUncompressedBytes)
                {
                    return PackageValidation.Invalid("The update ZIP exceeded the uncompressed size limit.");
                }
            }

            if (!fileEntries.TryGetValue(ManifestName, out var manifestEntry) ||
                !string.Equals(manifestEntry.FullName, ManifestName, StringComparison.Ordinal) ||
                manifestEntry.Length <= 0 ||
                manifestEntry.Length > MaximumManifestBytes)
            {
                return PackageValidation.Invalid("The update ZIP did not contain one valid root release manifest.");
            }

            var manifestBytes = new byte[checked((int)manifestEntry.Length)];
            await using (var manifestStream = manifestEntry.Open())
            {
                await manifestStream.ReadExactlyAsync(manifestBytes, cancellationToken).ConfigureAwait(false);
                var extra = new byte[1];
                if (await manifestStream.ReadAsync(extra, cancellationToken).ConfigureAwait(false) != 0)
                {
                    return PackageValidation.Invalid("The release manifest expanded beyond its declared size.");
                }
            }

            var manifest = JsonSerializer.Deserialize<ReleaseManifest>(manifestBytes, SerializerOptions);
            var manifestValidation = ValidateManifest(manifest, fileEntries, totalBytes - manifestEntry.Length);
            if (!manifestValidation.Success || manifest is null)
            {
                return manifestValidation;
            }

            foreach (var file in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = fileEntries[file.Path];
                await using var entryStream = entry.Open();
                var actualHash = await HashExactEntryAsync(
                    entryStream,
                    file.Bytes,
                    cancellationToken).ConfigureAwait(false);
                if (actualHash is null ||
                    !string.Equals(actualHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return PackageValidation.Invalid($"The update ZIP entry SHA-256 did not match its manifest: {file.Path}");
                }
            }

            return PackageValidation.Valid(manifest);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or JsonException or OverflowException or EndOfStreamException)
        {
            return PackageValidation.Invalid(exception.Message);
        }
    }

    private static PackageValidation ValidateManifest(
        ReleaseManifest? manifest,
        IReadOnlyDictionary<string, ZipArchiveEntry> fileEntries,
        long zipFileBytes)
    {
        if (manifest is null ||
            manifest.ValidationOnly ||
            !string.Equals(manifest.DistributionMode, "FrameworkDependent", StringComparison.Ordinal) ||
            !string.Equals(manifest.RuntimeIdentifier, "win-x64", StringComparison.Ordinal) ||
            manifest.PublishSingleFile ||
            manifest.TotalFiles <= 0 ||
            manifest.TotalFiles > MaximumFileCount ||
            manifest.TotalBytesValue <= 0 ||
            manifest.TotalBytesValue > MaximumUncompressedBytes ||
            manifest.TotalBytesValue != decimal.Truncate(manifest.TotalBytesValue) ||
            manifest.Files is null ||
            manifest.Files.Count != manifest.TotalFiles ||
            manifest.TotalBytes != zipFileBytes)
        {
            return PackageValidation.Invalid("The release manifest did not match the approved distribution shape.");
        }

        var manifestFiles = new Dictionary<string, ReleaseManifestFile>(StringComparer.OrdinalIgnoreCase);
        long manifestTotalBytes = 0;
        foreach (var file in manifest.Files)
        {
            if (!TryValidateRelativePath(file.Path, out var normalizedPath) ||
                !string.Equals(file.Path, normalizedPath, StringComparison.Ordinal) ||
                string.Equals(file.Path, ManifestName, StringComparison.OrdinalIgnoreCase) ||
                file.Bytes < 0 ||
                !IsSha256(file.Sha256) ||
                !manifestFiles.TryAdd(file.Path, file))
            {
                return PackageValidation.Invalid("The release manifest contained an invalid or duplicate file entry.");
            }

            manifestTotalBytes = checked(manifestTotalBytes + file.Bytes);
            if (!fileEntries.TryGetValue(file.Path, out var zipEntry) ||
                !string.Equals(zipEntry.FullName, file.Path, StringComparison.Ordinal) ||
                zipEntry.Length != file.Bytes)
            {
                return PackageValidation.Invalid($"The update ZIP did not match the release manifest entry: {file.Path}");
            }
        }

        if (manifestTotalBytes != manifest.TotalBytes ||
            fileEntries.Count != manifest.Files.Count + 1 ||
            !manifestFiles.ContainsKey("BakuretsuOsakanaKobo.exe"))
        {
            return PackageValidation.Invalid("The update ZIP file set did not exactly match the release manifest.");
        }

        return PackageValidation.Valid(manifest);
    }

    private static async Task<string?> HashExactEntryAsync(
        Stream stream,
        long expectedBytes,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return total == expectedBytes ? Convert.ToHexString(hash.GetHashAndReset()) : null;
            }

            total += read;
            if (total > expectedBytes)
            {
                return null;
            }

            hash.AppendData(buffer, 0, read);
        }
    }

    private static bool TryValidateRelativePath(string? value, out string? normalizedPath)
    {
        normalizedPath = null;
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > MaximumRelativePathLength ||
            value.Contains('\\') ||
            value.StartsWith('/') ||
            Path.IsPathRooted(value))
        {
            return false;
        }

        var isDirectory = value.EndsWith('/');
        var candidate = isDirectory ? value[..^1] : value;
        var segments = candidate.Split('/');
        if (segments.Length == 0 ||
            segments.Any(segment =>
                string.IsNullOrEmpty(segment) ||
                segment is "." or ".." ||
                segment.EndsWith('.') ||
                segment.EndsWith(' ') ||
                segment.Any(character => character < ' ' || Path.GetInvalidFileNameChars().Contains(character)) ||
                IsReservedWindowsName(segment)) ||
            string.Equals(segments[0], "data", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        normalizedPath = isDirectory ? $"{string.Join('/', segments)}/" : string.Join('/', segments);
        return string.Equals(value, normalizedPath, StringComparison.Ordinal);
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool IsApprovedDownloadUri(Uri uri, string assetName)
    {
        const string expectedPrefix = "/RT-EGG/bakuretsu-osakana-kobo/releases/download/";
        return uri.IsAbsoluteUri &&
               uri.Scheme == Uri.UriSchemeHttps &&
               string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
               uri.AbsolutePath.StartsWith(expectedPrefix, StringComparison.Ordinal) &&
               uri.AbsolutePath.EndsWith($"/{assetName}", StringComparison.Ordinal) &&
               string.IsNullOrEmpty(uri.Query) &&
               string.IsNullOrEmpty(uri.Fragment);
    }

    private static bool IsLinkOrReparsePoint(ZipArchiveEntry entry)
    {
        const int unixFileTypeMask = 0xF000;
        const int unixSymbolicLink = 0xA000;
        var unixMode = (entry.ExternalAttributes >> 16) & unixFileTypeMask;
        var windowsAttributes = entry.ExternalAttributes & 0xFFFF;
        return unixMode == unixSymbolicLink ||
               (windowsAttributes & (int)FileAttributes.ReparsePoint) != 0;
    }

    private static bool IsReservedWindowsName(string segment)
    {
        var baseName = segment.Split('.')[0];
        return baseName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
               baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
               baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
               baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
               (baseName.Length == 4 &&
                (baseName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                 baseName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                baseName[3] is >= '1' and <= '9');
    }

    private static UpdatePackageDownloadResult Invalid(string message) =>
        new(UpdatePackageDownloadStatus.InvalidPackage, TechnicalMessage: message);

    private static UpdatePackageDownloadResult Insufficient(string message) =>
        new(UpdatePackageDownloadStatus.InsufficientSpace, TechnicalMessage: message);

    private static UpdatePackageDownloadResult CleanupAndReturn(
        string? workingDirectory,
        UpdatePackageDownloadResult result)
    {
        CleanupOwnedDirectory(workingDirectory);
        return result;
    }

    private static void CleanupOwnedDirectory(string? workingDirectory)
    {
        if (string.IsNullOrEmpty(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return;
        }

        try
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort only. The directory is uniquely owned and never contains application data.
        }
    }

    private sealed class ReleaseManifest
    {
        public bool ValidationOnly { get; init; }

        public string? DistributionMode { get; init; }

        public string? RuntimeIdentifier { get; init; }

        public bool PublishSingleFile { get; init; }

        public int TotalFiles { get; init; }

        [JsonPropertyName("totalBytes")]
        public decimal TotalBytesValue { get; init; }

        [JsonIgnore]
        public long TotalBytes => checked((long)TotalBytesValue);

        public List<ReleaseManifestFile> Files { get; init; } = [];
    }

    private sealed class ReleaseManifestFile
    {
        public string Path { get; init; } = string.Empty;

        public long Bytes { get; init; }

        public string Sha256 { get; init; } = string.Empty;
    }

    private sealed record PackageValidation(
        bool Success,
        ReleaseManifest? Manifest = null,
        string? ErrorMessage = null)
    {
        internal static PackageValidation Valid(ReleaseManifest manifest) => new(true, manifest);

        internal static PackageValidation Invalid(string message) => new(false, ErrorMessage: message);
    }
}
