using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BakuretsuOsakanaKobo.Update;

internal enum UpdateArchiveStageStatus
{
    Prepared,
    InvalidPackage,
    StorageFailure,
}

internal sealed record UpdateArchiveStageResult(
    UpdateArchiveStageStatus Status,
    IReadOnlyList<UpdateFileDescriptor>? Files = null,
    string? TechnicalMessage = null);

internal sealed class UpdateArchiveStager
{
    internal const long MaximumArchiveBytes = 512L * 1024 * 1024;
    internal const long MaximumUncompressedBytes = 1024L * 1024 * 1024;
    internal const int MaximumManifestBytes = 1024 * 1024;
    private const int MaximumFileCount = 10_000;
    private const int BufferSize = 64 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    internal async Task<UpdateArchiveStageResult> StageAsync(
        string archivePath,
        long expectedBytes,
        string expectedSha256,
        string stagedDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedDirectory);

        var fullArchivePath = Path.GetFullPath(archivePath);
        var fullStagedDirectory = Path.GetFullPath(stagedDirectory);
        try
        {
            if (expectedBytes is <= 0 or > MaximumArchiveBytes ||
                !IsSha256(expectedSha256) ||
                !File.Exists(fullArchivePath) ||
                new FileInfo(fullArchivePath).Length != expectedBytes ||
                PathContainsReparsePoint(fullArchivePath) ||
                PathContainsReparsePoint(Path.GetDirectoryName(fullStagedDirectory)!) ||
                Directory.Exists(fullStagedDirectory))
            {
                return Invalid("The update archive or staging directory was outside the approved boundary.");
            }

            await using var archiveStream = new FileStream(
                fullArchivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            var actualHash = await SHA256.HashDataAsync(archiveStream, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(
                actualHash,
                Convert.FromHexString(expectedSha256)))
            {
                return Invalid("The update archive SHA-256 did not match the approved request.");
            }

            archiveStream.Position = 0;
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
            var inspection = await InspectArchiveAsync(archive, cancellationToken).ConfigureAwait(false);
            if (!inspection.Success || inspection.ManifestBytes is null || inspection.Files is null)
            {
                return Invalid(inspection.ErrorMessage ?? "The update ZIP was invalid.");
            }

            Directory.CreateDirectory(fullStagedDirectory);
            foreach (var file in inspection.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destinationPath = ResolveOwnedPath(fullStagedDirectory, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                if (PathContainsReparsePoint(Path.GetDirectoryName(destinationPath)!))
                {
                    CleanupDirectoryBestEffort(fullStagedDirectory);
                    return Invalid($"The update staging path traversed a reparse point: {file.Path}");
                }

                await using var entryStream = inspection.Entries![file.Path].Open();
                var extractedHash = await ExtractExactAsync(
                    entryStream,
                    destinationPath,
                    file.Bytes,
                    cancellationToken).ConfigureAwait(false);
                if (extractedHash is null ||
                    !string.Equals(extractedHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    CleanupDirectoryBestEffort(fullStagedDirectory);
                    return Invalid($"The extracted update file did not match its manifest: {file.Path}");
                }
            }

            var manifestPath = ResolveOwnedPath(fullStagedDirectory, UpdateFileTransaction.ManifestName);
            if (PathContainsReparsePoint(Path.GetDirectoryName(manifestPath)!))
            {
                CleanupDirectoryBestEffort(fullStagedDirectory);
                return Invalid("The update manifest staging path traversed a reparse point.");
            }

            await WriteDurableAsync(manifestPath, inspection.ManifestBytes, cancellationToken).ConfigureAwait(false);
            return new UpdateArchiveStageResult(UpdateArchiveStageStatus.Prepared, inspection.Files);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CleanupDirectoryBestEffort(fullStagedDirectory);
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or JsonException or IOException or UnauthorizedAccessException or
                OverflowException or EndOfStreamException or CryptographicException)
        {
            CleanupDirectoryBestEffort(fullStagedDirectory);
            var status = exception is IOException or UnauthorizedAccessException
                ? UpdateArchiveStageStatus.StorageFailure
                : UpdateArchiveStageStatus.InvalidPackage;
            return new UpdateArchiveStageResult(status, TechnicalMessage: exception.Message);
        }
    }

    internal static async Task<(IReadOnlyList<string>? Files, string? ErrorMessage)> ReadInstalledManifestAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var fullPath = Path.GetFullPath(manifestPath);
            if (!File.Exists(fullPath) ||
                PathContainsReparsePoint(fullPath) ||
                new FileInfo(fullPath).Length is <= 0 or > MaximumManifestBytes)
            {
                return (null, "The installed release manifest was missing or outside the approved boundary.");
            }

            var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
            var manifest = JsonSerializer.Deserialize<ReleaseManifest>(bytes, SerializerOptions);
            var validation = ValidateManifestShape(manifest);
            return validation is null
                ? (manifest!.Files.Select(file => file.Path).ToArray(), null)
                : (null, validation);
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException or OverflowException)
        {
            return (null, exception.Message);
        }
    }

    private static async Task<ArchiveInspection> InspectArchiveAsync(
        ZipArchive archive,
        CancellationToken cancellationToken)
    {
        if (archive.Entries.Count == 0 || archive.Entries.Count > MaximumFileCount * 2)
        {
            return ArchiveInspection.Invalid("The update ZIP entry count was outside the approved limit.");
        }

        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        var allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryValidateRelativePath(entry.FullName, out var normalizedPath))
            {
                return ArchiveInspection.Invalid($"The update ZIP contained an unsafe path: {entry.FullName}");
            }

            if (!allPaths.Add(normalizedPath!.TrimEnd('/')) || IsLinkOrReparsePoint(entry))
            {
                return ArchiveInspection.Invalid($"The update ZIP contained a duplicate or linked path: {entry.FullName}");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                if (entry.Length != 0 || entry.CompressedLength != 0)
                {
                    return ArchiveInspection.Invalid($"The update ZIP contained data in a directory entry: {entry.FullName}");
                }

                continue;
            }

            entries.Add(normalizedPath, entry);
            totalBytes = checked(totalBytes + entry.Length);
            if (totalBytes > MaximumUncompressedBytes)
            {
                return ArchiveInspection.Invalid("The update ZIP exceeded the uncompressed size limit.");
            }
        }

        if (!entries.TryGetValue(UpdateFileTransaction.ManifestName, out var manifestEntry) ||
            !string.Equals(manifestEntry.FullName, UpdateFileTransaction.ManifestName, StringComparison.Ordinal) ||
            manifestEntry.Length is <= 0 or > MaximumManifestBytes)
        {
            return ArchiveInspection.Invalid("The update ZIP did not contain one valid root release manifest.");
        }

        var manifestBytes = new byte[checked((int)manifestEntry.Length)];
        await using (var manifestStream = manifestEntry.Open())
        {
            await manifestStream.ReadExactlyAsync(manifestBytes, cancellationToken).ConfigureAwait(false);
            if (await manifestStream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
            {
                return ArchiveInspection.Invalid("The release manifest expanded beyond its declared size.");
            }
        }

        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(manifestBytes, SerializerOptions);
        var manifestError = ValidateManifestShape(manifest);
        if (manifestError is not null)
        {
            return ArchiveInspection.Invalid(manifestError);
        }

        var files = new Dictionary<string, UpdateFileDescriptor>(StringComparer.OrdinalIgnoreCase);
        long manifestBytesTotal = 0;
        foreach (var file in manifest!.Files)
        {
            if (!TryValidateRelativePath(file.Path, out var normalizedPath) ||
                !string.Equals(file.Path, normalizedPath, StringComparison.Ordinal) ||
                string.Equals(file.Path, UpdateFileTransaction.ManifestName, StringComparison.OrdinalIgnoreCase) ||
                file.Bytes < 0 ||
                !IsSha256(file.Sha256) ||
                !entries.TryGetValue(file.Path, out var entry) ||
                !string.Equals(entry.FullName, file.Path, StringComparison.Ordinal) ||
                entry.Length != file.Bytes ||
                !files.TryAdd(file.Path, new UpdateFileDescriptor(file.Path, file.Bytes, file.Sha256)))
            {
                return ArchiveInspection.Invalid("The release manifest contained an invalid, duplicate, or missing file entry.");
            }

            manifestBytesTotal = checked(manifestBytesTotal + file.Bytes);
        }

        if (manifestBytesTotal != manifest.TotalBytes ||
            totalBytes - manifestEntry.Length != manifest.TotalBytes ||
            entries.Count != files.Count + 1 ||
            !files.ContainsKey("BakuretsuOsakanaKobo.exe"))
        {
            return ArchiveInspection.Invalid("The update ZIP file set did not exactly match the release manifest.");
        }

        return ArchiveInspection.Valid(manifestBytes, files.Values.ToArray(), entries);
    }

    private static string? ValidateManifestShape(ReleaseManifest? manifest) =>
        manifest is null ||
        manifest.ValidationOnly ||
        !string.Equals(manifest.DistributionMode, "FrameworkDependent", StringComparison.Ordinal) ||
        !string.Equals(manifest.RuntimeIdentifier, "win-x64", StringComparison.Ordinal) ||
        manifest.PublishSingleFile ||
        manifest.TotalFiles is <= 0 or > MaximumFileCount ||
        manifest.TotalBytesValue is <= 0 or > MaximumUncompressedBytes ||
        manifest.TotalBytesValue != decimal.Truncate(manifest.TotalBytesValue) ||
        manifest.Files is null ||
        manifest.Files.Count != manifest.TotalFiles
            ? "The release manifest did not match the approved distribution shape."
            : null;

    private static async Task<string?> ExtractExactAsync(
        Stream source,
        string destinationPath,
        long expectedBytes,
        CancellationToken cancellationToken)
    {
        await using var destination = new FileStream(
            destinationPath,
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
            if (total > expectedBytes)
            {
                return null;
            }

            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
        return total == expectedBytes ? Convert.ToHexString(hash.GetHashAndReset()) : null;
    }

    private static async Task WriteDurableAsync(
        string path,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static string ResolveOwnedPath(string root, string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(EnsureTrailingSeparator(root), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The update path escaped its staging directory.");
        }

        return path;
    }

    private static bool TryValidateRelativePath(string? value, out string? normalizedPath)
    {
        normalizedPath = null;
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 240 ||
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

    private static bool PathContainsReparsePoint(string path)
    {
        var current = Path.GetFullPath(path);
        var root = Path.GetPathRoot(current);
        while (!string.IsNullOrEmpty(current) && !string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            current = Path.GetDirectoryName(current) ?? string.Empty;
        }

        return false;
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string EnsureTrailingSeparator(string path) =>
        Path.EndsInDirectorySeparator(path) ? path : $"{path}{Path.DirectorySeparatorChar}";

    private static void CleanupDirectoryBestEffort(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static UpdateArchiveStageResult Invalid(string message) =>
        new(UpdateArchiveStageStatus.InvalidPackage, TechnicalMessage: message);

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

    private sealed record ArchiveInspection(
        bool Success,
        byte[]? ManifestBytes = null,
        IReadOnlyList<UpdateFileDescriptor>? Files = null,
        IReadOnlyDictionary<string, ZipArchiveEntry>? Entries = null,
        string? ErrorMessage = null)
    {
        internal static ArchiveInspection Valid(
            byte[] manifestBytes,
            IReadOnlyList<UpdateFileDescriptor> files,
            IReadOnlyDictionary<string, ZipArchiveEntry> entries) =>
            new(true, manifestBytes, files, entries);

        internal static ArchiveInspection Invalid(string message) => new(false, ErrorMessage: message);
    }
}
