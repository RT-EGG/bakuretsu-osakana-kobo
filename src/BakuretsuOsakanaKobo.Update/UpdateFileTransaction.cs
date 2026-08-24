using System.Security.Cryptography;

namespace BakuretsuOsakanaKobo.Update;

internal sealed record UpdateFileDescriptor(string Path, long Bytes, string Sha256);

internal enum UpdateFileTransactionStatus
{
    Succeeded,
    Cancelled,
    ValidationFailed,
    FailedRolledBack,
    FailedRollbackIncomplete,
}

internal sealed record UpdateFileTransactionResult(
    UpdateFileTransactionStatus Status,
    string? TechnicalMessage = null,
    IReadOnlyList<string>? RollbackFailures = null);

internal enum UpdateTransactionCheckpoint
{
    AfterBackup,
    AfterInstall,
    AfterDeleteObsolete,
}

internal interface IUpdateTransactionFaultInjector
{
    void ThrowIfRequested(UpdateTransactionCheckpoint checkpoint, string relativePath);
}

internal sealed class UpdateFileTransaction
{
    internal const string ManifestName = "release-manifest.json";
    private const int BufferSize = 64 * 1024;
    private const int MaximumFileCount = 10_000;
    private readonly IUpdateTransactionFaultInjector? _faultInjector;

    internal UpdateFileTransaction(IUpdateTransactionFaultInjector? faultInjector = null)
    {
        _faultInjector = faultInjector;
    }

    internal async Task<UpdateFileTransactionResult> ApplyAsync(
        string targetDirectory,
        string stagedDirectory,
        string backupDirectory,
        IReadOnlyCollection<string> currentOwnedFiles,
        IReadOnlyCollection<UpdateFileDescriptor> nextFiles,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        ArgumentNullException.ThrowIfNull(currentOwnedFiles);
        ArgumentNullException.ThrowIfNull(nextFiles);

        var targetRoot = Path.GetFullPath(targetDirectory);
        var stagedRoot = Path.GetFullPath(stagedDirectory);
        var backupRoot = Path.GetFullPath(backupDirectory);
        var validation = ValidateInputs(
            targetRoot,
            stagedRoot,
            backupRoot,
            currentOwnedFiles,
            nextFiles,
            out var currentPaths,
            out var nextByPath);
        if (validation is not null)
        {
            return new UpdateFileTransactionResult(UpdateFileTransactionStatus.ValidationFailed, validation);
        }

        var mutationStarted = false;
        var backedUpPaths = new List<string>();
        try
        {
            Directory.CreateDirectory(backupRoot);
            foreach (var file in nextByPath!.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stagedPath = ResolveOwnedPath(stagedRoot, file.Path);
                var stagedValidation = await ValidateStagedFileAsync(
                    stagedPath,
                    file,
                    cancellationToken).ConfigureAwait(false);
                if (stagedValidation is not null)
                {
                    CleanupDirectoryBestEffort(backupRoot);
                    return new UpdateFileTransactionResult(
                        UpdateFileTransactionStatus.ValidationFailed,
                        stagedValidation);
                }
            }

            var stagedManifestPath = ResolveOwnedPath(stagedRoot, ManifestName);
            if (!File.Exists(stagedManifestPath) || PathContainsReparsePoint(stagedManifestPath))
            {
                CleanupDirectoryBestEffort(backupRoot);
                return new UpdateFileTransactionResult(
                    UpdateFileTransactionStatus.ValidationFailed,
                    "The staged update did not contain release-manifest.json.");
            }

            foreach (var relativePath in nextByPath.Keys)
            {
                var targetPath = ResolveOwnedPath(targetRoot, relativePath);
                if ((!currentPaths!.Contains(relativePath) && File.Exists(targetPath)) ||
                    Directory.Exists(targetPath) ||
                    HasFileInParentPath(targetRoot, relativePath))
                {
                    CleanupDirectoryBestEffort(backupRoot);
                    return new UpdateFileTransactionResult(
                        UpdateFileTransactionStatus.ValidationFailed,
                        $"The next release file collided with a manifest-external target path: {relativePath}");
                }
            }

            var backupCandidates = new HashSet<string>(currentPaths!, StringComparer.OrdinalIgnoreCase)
            {
                ManifestName,
            };
            foreach (var relativePath in backupCandidates.Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePath = ResolveOwnedPath(targetRoot, relativePath);
                if (!File.Exists(sourcePath))
                {
                    continue;
                }

                if (PathContainsReparsePoint(sourcePath))
                {
                    CleanupDirectoryBestEffort(backupRoot);
                    return new UpdateFileTransactionResult(
                        UpdateFileTransactionStatus.ValidationFailed,
                        $"The current installation file traversed a reparse point: {relativePath}");
                }

                var backupPath = ResolveOwnedPath(backupRoot, relativePath);
                await CopyDurableAsync(sourcePath, backupPath, overwrite: false, cancellationToken)
                    .ConfigureAwait(false);
                if (!await FilesMatchAsync(sourcePath, backupPath, cancellationToken).ConfigureAwait(false))
                {
                    CleanupDirectoryBestEffort(backupRoot);
                    return new UpdateFileTransactionResult(
                        UpdateFileTransactionStatus.ValidationFailed,
                        $"The durable update backup did not match its source: {relativePath}");
                }

                backedUpPaths.Add(relativePath);
                _faultInjector?.ThrowIfRequested(UpdateTransactionCheckpoint.AfterBackup, relativePath);
            }

            cancellationToken.ThrowIfCancellationRequested();
            mutationStarted = true;
            var installPaths = nextByPath.Keys.Append(ManifestName).Order(StringComparer.Ordinal).ToArray();
            foreach (var relativePath in installPaths)
            {
                var sourcePath = ResolveOwnedPath(stagedRoot, relativePath);
                var targetPath = ResolveOwnedPath(targetRoot, relativePath);
                await InstallFileAtomicallyAsync(sourcePath, targetPath).ConfigureAwait(false);
                _faultInjector?.ThrowIfRequested(UpdateTransactionCheckpoint.AfterInstall, relativePath);
            }

            var obsoletePaths = currentPaths!
                .Where(path => !nextByPath.ContainsKey(path))
                .Order(StringComparer.Ordinal)
                .ToArray();
            foreach (var relativePath in obsoletePaths)
            {
                var targetPath = ResolveOwnedPath(targetRoot, relativePath);
                if (File.Exists(targetPath))
                {
                    if (PathContainsReparsePoint(targetPath))
                    {
                        throw new IOException($"The obsolete update file traversed a reparse point: {relativePath}");
                    }

                    File.Delete(targetPath);
                }

                _faultInjector?.ThrowIfRequested(UpdateTransactionCheckpoint.AfterDeleteObsolete, relativePath);
            }

            CleanupDirectoryBestEffort(backupRoot);
            return new UpdateFileTransactionResult(UpdateFileTransactionStatus.Succeeded);
        }
        catch (OperationCanceledException) when (!mutationStarted && cancellationToken.IsCancellationRequested)
        {
            CleanupDirectoryBestEffort(backupRoot);
            return new UpdateFileTransactionResult(UpdateFileTransactionStatus.Cancelled);
        }
        catch (Exception exception) when (
            mutationStarted ||
            exception is IOException or UnauthorizedAccessException or CryptographicException or InvalidOperationException)
        {
            if (!mutationStarted)
            {
                CleanupDirectoryBestEffort(backupRoot);
                return new UpdateFileTransactionResult(
                    UpdateFileTransactionStatus.ValidationFailed,
                    exception.Message);
            }

            var rollbackFailures = await RollbackAsync(
                targetRoot,
                backupRoot,
                nextByPath!.Keys,
                backedUpPaths).ConfigureAwait(false);
            return rollbackFailures.Count == 0
                ? new UpdateFileTransactionResult(
                    UpdateFileTransactionStatus.FailedRolledBack,
                    exception.Message)
                : new UpdateFileTransactionResult(
                    UpdateFileTransactionStatus.FailedRollbackIncomplete,
                    exception.Message,
                    rollbackFailures);
        }
    }

    private static string? ValidateInputs(
        string targetRoot,
        string stagedRoot,
        string backupRoot,
        IReadOnlyCollection<string> currentOwnedFiles,
        IReadOnlyCollection<UpdateFileDescriptor> nextFiles,
        out HashSet<string>? currentPaths,
        out Dictionary<string, UpdateFileDescriptor>? nextByPath)
    {
        currentPaths = null;
        nextByPath = null;
        if (!Directory.Exists(targetRoot) ||
            !Directory.Exists(stagedRoot) ||
            PathContainsReparsePoint(targetRoot) ||
            PathContainsReparsePoint(stagedRoot) ||
            PathContainsReparsePoint(Path.GetDirectoryName(backupRoot)!) ||
            PathsOverlap(targetRoot, stagedRoot) ||
            PathsOverlap(targetRoot, backupRoot) ||
            PathsOverlap(stagedRoot, backupRoot) ||
            (Directory.Exists(backupRoot) && Directory.EnumerateFileSystemEntries(backupRoot).Any()))
        {
            return "The update target, staging, and backup directories were missing, overlapping, or not empty.";
        }

        if (currentOwnedFiles.Count > MaximumFileCount || nextFiles.Count is <= 0 or > MaximumFileCount)
        {
            return "The update file count was outside the approved limit.";
        }

        currentPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in currentOwnedFiles)
        {
            if (!IsSafeOwnedPath(path) ||
                string.Equals(path, ManifestName, StringComparison.OrdinalIgnoreCase) ||
                !currentPaths.Add(path))
            {
                return "The current release manifest contained an unsafe or duplicate path.";
            }
        }

        nextByPath = new Dictionary<string, UpdateFileDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in nextFiles)
        {
            if (!IsSafeOwnedPath(file.Path) ||
                string.Equals(file.Path, ManifestName, StringComparison.OrdinalIgnoreCase) ||
                file.Bytes < 0 ||
                !IsSha256(file.Sha256) ||
                !nextByPath.TryAdd(file.Path, file))
            {
                return "The next release manifest contained an unsafe or duplicate file entry.";
            }
        }

        return null;
    }

    private static async Task<string?> ValidateStagedFileAsync(
        string path,
        UpdateFileDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != descriptor.Bytes)
        {
            return $"The staged update file size did not match its manifest: {descriptor.Path}";
        }

        if (PathContainsReparsePoint(path))
        {
            return $"The staged update file traversed a reparse point: {descriptor.Path}";
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return string.Equals(Convert.ToHexString(hash), descriptor.Sha256, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"The staged update file SHA-256 did not match its manifest: {descriptor.Path}";
    }

    private static async Task InstallFileAtomicallyAsync(string sourcePath, string targetPath)
    {
        if (PathContainsReparsePoint(sourcePath) ||
            PathContainsReparsePoint(File.Exists(targetPath) ? targetPath : Path.GetDirectoryName(targetPath)!))
        {
            throw new IOException("The update transaction encountered a reparse point.");
        }

        var targetDirectory = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(targetDirectory);
        var temporaryPath = Path.Combine(
            targetDirectory,
            $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.update.tmp");
        try
        {
            await CopyDurableAsync(sourcePath, temporaryPath, overwrite: false, CancellationToken.None)
                .ConfigureAwait(false);
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            DeleteFileBestEffort(temporaryPath);
        }
    }

    private static async Task<List<string>> RollbackAsync(
        string targetRoot,
        string backupRoot,
        IEnumerable<string> nextPaths,
        IReadOnlyCollection<string> backedUpPaths)
    {
        var failures = new List<string>();
        var pathsToRemove = new HashSet<string>(nextPaths, StringComparer.OrdinalIgnoreCase)
        {
            ManifestName,
        };
        foreach (var relativePath in pathsToRemove.Order(StringComparer.Ordinal))
        {
            try
            {
                DeleteFileBestEffort(ResolveOwnedPath(targetRoot, relativePath), throwOnFailure: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failures.Add($"remove:{relativePath}:{exception.Message}");
            }
        }

        foreach (var relativePath in backedUpPaths.Order(StringComparer.Ordinal))
        {
            try
            {
                var backupPath = ResolveOwnedPath(backupRoot, relativePath);
                var targetPath = ResolveOwnedPath(targetRoot, relativePath);
                await InstallFileAtomicallyAsync(backupPath, targetPath).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failures.Add($"restore:{relativePath}:{exception.Message}");
            }
        }

        if (failures.Count == 0)
        {
            CleanupDirectoryBestEffort(backupRoot);
        }

        return failures;
    }

    private static async Task CopyDurableAsync(
        string sourcePath,
        string destinationPath,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            overwrite ? FileMode.Create : FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, BufferSize, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
    }

    private static async Task<bool> FilesMatchAsync(
        string leftPath,
        string rightPath,
        CancellationToken cancellationToken)
    {
        if (new FileInfo(leftPath).Length != new FileInfo(rightPath).Length)
        {
            return false;
        }

        await using var left = new FileStream(
            leftPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var right = new FileStream(
            rightPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var leftHash = await SHA256.HashDataAsync(left, cancellationToken).ConfigureAwait(false);
        var rightHash = await SHA256.HashDataAsync(right, cancellationToken).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }

    private static string ResolveOwnedPath(string root, string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(EnsureTrailingSeparator(root), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The update path escaped its approved root.");
        }

        return fullPath;
    }

    private static bool IsSafeOwnedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path.Length > 240 ||
            path.Contains('\\') ||
            path.StartsWith('/') ||
            Path.IsPathRooted(path))
        {
            return false;
        }

        var segments = path.Split('/');
        return !string.Equals(segments[0], "data", StringComparison.OrdinalIgnoreCase) &&
               segments.All(segment =>
                   !string.IsNullOrEmpty(segment) &&
                   segment is not "." and not ".." &&
                   !segment.EndsWith('.') &&
                   !segment.EndsWith(' ') &&
                   !segment.Any(character => character < ' ' || Path.GetInvalidFileNameChars().Contains(character)) &&
                   !IsReservedWindowsName(segment));
    }

    private static bool PathsOverlap(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase) ||
        left.StartsWith(EnsureTrailingSeparator(right), StringComparison.OrdinalIgnoreCase) ||
        right.StartsWith(EnsureTrailingSeparator(left), StringComparison.OrdinalIgnoreCase);

    private static bool HasFileInParentPath(string root, string relativePath)
    {
        var segments = relativePath.Split('/');
        var current = root;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            current = Path.Combine(current, segments[index]);
            if (File.Exists(current))
            {
                return true;
            }
        }

        return false;
    }

    private static string EnsureTrailingSeparator(string path) =>
        Path.EndsInDirectorySeparator(path) ? path : $"{path}{Path.DirectorySeparatorChar}";

    private static bool PathContainsReparsePoint(string path)
    {
        var current = Path.GetFullPath(path);
        var root = Path.GetPathRoot(current);
        while (!string.IsNullOrEmpty(current) &&
               !string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
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

    private static void DeleteFileBestEffort(string path, bool throwOnFailure = false)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch when (!throwOnFailure)
        {
        }
    }

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
}
