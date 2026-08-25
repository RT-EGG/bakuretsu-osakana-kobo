using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using BakuretsuOsakanaKobo.Update;

namespace BakuretsuOsakanaKobo;

internal enum ApplicationUpdateStartStatus
{
    Launched,
    DownloadUnavailable,
    InvalidPackage,
    InsufficientSpace,
    StorageFailure,
    HelperUnavailable,
    HelperLaunchFailed,
}

internal sealed record ApplicationUpdateStartResult(
    ApplicationUpdateStartStatus Status,
    string? TechnicalMessage = null);

internal interface IUpdateHelperLauncher
{
    Task<(bool Ready, string? TechnicalMessage)> LaunchAndWaitForReadyAsync(
        string executablePath,
        string workingDirectory,
        string requestPath,
        string readyPath,
        string launchToken,
        CancellationToken cancellationToken);
}

internal sealed class SystemUpdateHelperLauncher : IUpdateHelperLauncher
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    public async Task<(bool Ready, string? TechnicalMessage)> LaunchAndWaitForReadyAsync(
        string executablePath,
        string workingDirectory,
        string requestPath,
        string readyPath,
        string launchToken,
        CancellationToken cancellationToken)
    {
        Process? process = null;
        var acceptedReady = false;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add(requestPath);
            process = Process.Start(startInfo);
            if (process is null)
            {
                return (false, "The update helper process could not be created.");
            }

            var readyClock = Stopwatch.StartNew();
            while (readyClock.Elapsed < ReadyTimeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    return (false, $"The update helper exited before readiness with code {process.ExitCode}.");
                }

                var ready = await TryReadReadyAsync(readyPath, cancellationToken).ConfigureAwait(false);
                if (ready is not null)
                {
                    acceptedReady = ready.SchemaVersion == 1 &&
                                    ready.LaunchToken is { Length: 32 } &&
                                    CryptographicOperations.FixedTimeEquals(
                                        Convert.FromHexString(ready.LaunchToken),
                                        Convert.FromHexString(launchToken));
                    if (!acceptedReady)
                    {
                        return (false, "The update helper readiness token did not match the request.");
                    }

                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                    if (process.HasExited)
                    {
                        acceptedReady = false;
                        return (false, $"The update helper exited after readiness with code {process.ExitCode}.");
                    }

                    return (true, null);
                }

                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }

            return (false, "The update helper did not report readiness before the timeout.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or
                System.ComponentModel.Win32Exception or JsonException or FormatException)
        {
            return (false, exception.Message);
        }
        finally
        {
            if (process is not null)
            {
                if (!acceptedReady)
                {
                    TryTerminate(process);
                }

                process.Dispose();
            }
        }
    }

    private static async Task<UpdateHelperReady?> TryReadReadyAsync(
        string readyPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(readyPath))
        {
            return null;
        }

        var length = new FileInfo(readyPath).Length;
        if (length is <= 0 or > 64 * 1024)
        {
            throw new InvalidDataException("The update helper readiness file was outside the approved size.");
        }

        var bytes = await File.ReadAllBytesAsync(readyPath, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<UpdateHelperReady>(bytes, SerializerOptions);
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2_000);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }
}

internal sealed class UpdateApplicationCoordinator
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private static readonly string[] RequiredHelperFiles =
    [
        "BakuretsuOsakanaKobo.Updater.exe",
        "BakuretsuOsakanaKobo.Updater.dll",
        "BakuretsuOsakanaKobo.Updater.deps.json",
        "BakuretsuOsakanaKobo.Updater.runtimeconfig.json",
        "BakuretsuOsakanaKobo.Update.dll",
    ];
    private readonly UpdatePackageDownloader _downloader;
    private readonly IUpdateHelperLauncher _helperLauncher;
    private readonly string _helperSourceDirectory;
    private readonly string _workingRoot;

    internal UpdateApplicationCoordinator(
        UpdatePackageDownloader downloader,
        string helperSourceDirectory,
        string workingRoot,
        IUpdateHelperLauncher? helperLauncher = null)
    {
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _helperSourceDirectory = Path.GetFullPath(helperSourceDirectory);
        _workingRoot = Path.GetFullPath(workingRoot);
        _helperLauncher = helperLauncher ?? new SystemUpdateHelperLauncher();
    }

    internal static string GetDefaultWorkingRoot() =>
        Path.Combine(Path.GetTempPath(), "BakuretsuOsakanaKobo", "updates");

    internal async Task<ApplicationUpdateStartResult> DownloadAndLaunchAsync(
        UpdateRelease release,
        string targetDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

        var download = await _downloader.DownloadAsync(
            release.Asset,
            _workingRoot,
            cancellationToken).ConfigureAwait(false);
        if (download.Status != UpdatePackageDownloadStatus.Prepared || download.Package is null)
        {
            return new ApplicationUpdateStartResult(Map(download.Status), download.TechnicalMessage);
        }

        var package = download.Package;
        try
        {
            var helperDirectory = Path.Combine(package.WorkingDirectory, "helper");
            var copyError = await CopyHelperAsync(helperDirectory, cancellationToken).ConfigureAwait(false);
            if (copyError is not null)
            {
                UpdatePackageDownloader.DeletePreparedPackage(package);
                return new ApplicationUpdateStartResult(
                    ApplicationUpdateStartStatus.HelperUnavailable,
                    copyError);
            }

            using var currentProcess = Process.GetCurrentProcess();
            var launchToken = Guid.NewGuid().ToString("N");
            var request = new UpdateHelperRequest(
                SchemaVersion: 1,
                LaunchToken: launchToken,
                ParentProcessId: currentProcess.Id,
                ParentProcessStartTimeUtcTicks: currentProcess.StartTime.ToUniversalTime().Ticks,
                TargetDirectory: Path.GetFullPath(targetDirectory),
                ArchivePath: package.ArchivePath,
                ArchiveBytes: release.Asset.Size,
                ArchiveSha256: release.Asset.Sha256Digest);
            var requestPath = Path.Combine(package.WorkingDirectory, UpdateHelperHost.RequestFileName);
            var readyPath = Path.Combine(package.WorkingDirectory, UpdateHelperHost.ReadyFileName);
            await WriteRequestAsync(requestPath, request, cancellationToken).ConfigureAwait(false);

            var launch = await _helperLauncher.LaunchAndWaitForReadyAsync(
                Path.Combine(helperDirectory, "BakuretsuOsakanaKobo.Updater.exe"),
                helperDirectory,
                requestPath,
                readyPath,
                launchToken,
                cancellationToken).ConfigureAwait(false);
            if (!launch.Ready)
            {
                UpdatePackageDownloader.DeletePreparedPackage(package);
                return new ApplicationUpdateStartResult(
                    ApplicationUpdateStartStatus.HelperLaunchFailed,
                    launch.TechnicalMessage);
            }

            return new ApplicationUpdateStartResult(ApplicationUpdateStartStatus.Launched);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            UpdatePackageDownloader.DeletePreparedPackage(package);
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or
                System.ComponentModel.Win32Exception or CryptographicException)
        {
            UpdatePackageDownloader.DeletePreparedPackage(package);
            return new ApplicationUpdateStartResult(
                ApplicationUpdateStartStatus.StorageFailure,
                exception.Message);
        }
    }

    private async Task<string?> CopyHelperAsync(
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_helperSourceDirectory) ||
            PathContainsReparsePoint(_helperSourceDirectory) ||
            Directory.Exists(destinationDirectory))
        {
            return "The installed update helper directory was missing or unsafe.";
        }

        Directory.CreateDirectory(destinationDirectory);
        foreach (var fileName in RequiredHelperFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePath = Path.Combine(_helperSourceDirectory, fileName);
            var destinationPath = Path.Combine(destinationDirectory, fileName);
            if (!File.Exists(sourcePath) || PathContainsReparsePoint(sourcePath))
            {
                return $"The installed update helper file was missing or unsafe: {fileName}";
            }

            var sourceHash = await CopyDurableAndHashAsync(
                sourcePath,
                destinationPath,
                cancellationToken).ConfigureAwait(false);
            if (!await FileMatchesHashAsync(destinationPath, sourceHash, cancellationToken).ConfigureAwait(false))
            {
                return $"The copied update helper file did not match its source: {fileName}";
            }
        }

        return null;
    }

    private static async Task WriteRequestAsync(
        string requestPath,
        UpdateHelperRequest request,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, SerializerOptions);
        var temporaryPath = $"{requestPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, requestPath);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task<byte[]> CopyDurableAndHashAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
        return hash.GetHashAndReset();
    }

    private static async Task<bool> FileMatchesHashAsync(
        string destinationPath,
        byte[] expectedHash,
        CancellationToken cancellationToken)
    {
        await using var destination = File.OpenRead(destinationPath);
        var destinationHash = await SHA256.HashDataAsync(destination, cancellationToken).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(expectedHash, destinationHash);
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

    private static ApplicationUpdateStartStatus Map(UpdatePackageDownloadStatus status) => status switch
    {
        UpdatePackageDownloadStatus.Unavailable => ApplicationUpdateStartStatus.DownloadUnavailable,
        UpdatePackageDownloadStatus.InvalidPackage => ApplicationUpdateStartStatus.InvalidPackage,
        UpdatePackageDownloadStatus.InsufficientSpace => ApplicationUpdateStartStatus.InsufficientSpace,
        _ => ApplicationUpdateStartStatus.StorageFailure,
    };
}
