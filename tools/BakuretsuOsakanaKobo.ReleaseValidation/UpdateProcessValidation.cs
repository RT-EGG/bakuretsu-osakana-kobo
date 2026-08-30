using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BakuretsuOsakanaKobo.ReleaseValidation;

internal static class UpdateProcessValidation
{
    private const string ArchiveFileName = "BakuretsuOsakanaKobo-win-x64.zip";
    private const string ManifestFileName = "release-manifest.json";
    private const string RequestFileName = "update-request.json";
    private const string ReadyFileName = "update-ready.json";
    private const string ResultFileName = "update-result.json";
    private const int SucceededStatus = 0;
    private const int FailedRolledBackStatus = 5;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private static readonly string[] HelperFiles =
    [
        "BakuretsuOsakanaKobo.Updater.exe",
        "BakuretsuOsakanaKobo.Updater.dll",
        "BakuretsuOsakanaKobo.Updater.deps.json",
        "BakuretsuOsakanaKobo.Updater.runtimeconfig.json",
        "BakuretsuOsakanaKobo.Update.dll",
    ];

    internal static async Task<int> RunAsync(string helperSourceDirectory, string reportPath)
    {
        var fullReportPath = Path.GetFullPath(reportPath);
        var validationRoot = $"{fullReportPath}.update-process-{Guid.NewGuid():N}";
        var originalRestartProbe = Environment.GetEnvironmentVariable("BOK_UPDATE_RESTART_PROBE");
        try
        {
            var helperRoot = Path.GetFullPath(helperSourceDirectory);
            ValidateHelperFiles(helperRoot);
            Directory.CreateDirectory(validationRoot);

            var success = await RunScenarioAsync(
                validationRoot,
                helperRoot,
                "success",
                injectRollbackFailure: false).ConfigureAwait(false);
            var rollback = await RunScenarioAsync(
                validationRoot,
                helperRoot,
                "rollback",
                injectRollbackFailure: true).ConfigureAwait(false);

            var report = new
            {
                success,
                rollback,
                realUpdaterProcess = true,
                parentIdentityChecked = true,
                readyObservedBeforeParentExit = true,
                dataPreserved = true,
                unknownFilePreserved = true,
                restartProbeObserved = true,
                ownedWorkingDirectoryCleaned = true,
            };
            WriteJsonDurable(fullReportPath, report);
            Console.WriteLine(JsonSerializer.Serialize(report));
            return 0;
        }
        catch (Exception exception)
        {
            var report = new { error = exception.ToString() };
            WriteJsonDurable(fullReportPath, report);
            Console.Error.WriteLine(JsonSerializer.Serialize(report));
            return 1;
        }
        finally
        {
            Environment.SetEnvironmentVariable("BOK_UPDATE_RESTART_PROBE", originalRestartProbe);
            DeleteDirectoryBestEffort(validationRoot);
        }
    }

    internal static int RunParentProbe(string exitSignalPath)
    {
        try
        {
            var fullSignalPath = Path.GetFullPath(exitSignalPath);
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(30))
            {
                if (File.Exists(fullSignalPath))
                {
                    return 0;
                }

                Thread.Sleep(20);
            }

            return 3;
        }
        catch
        {
            return 4;
        }
    }

    internal static int RunRestartProbe(string resultPath)
    {
        try
        {
            var markerPath = Environment.GetEnvironmentVariable("BOK_UPDATE_RESTART_PROBE");
            if (string.IsNullOrWhiteSpace(markerPath))
            {
                return 5;
            }

            var fullMarkerPath = Path.GetFullPath(markerPath);
            var fullResultPath = Path.GetFullPath(resultPath);
            var workingDirectory = Path.GetDirectoryName(fullResultPath)!;
            var scenarioRoot = Path.GetDirectoryName(fullMarkerPath)!;
            var workingName = Path.GetFileName(workingDirectory);
            if (!string.Equals(Path.GetFileName(fullResultPath), ResultFileName, StringComparison.Ordinal) ||
                !string.Equals(Path.GetDirectoryName(workingDirectory), scenarioRoot, StringComparison.OrdinalIgnoreCase) ||
                workingName.Length != 39 ||
                !workingName.StartsWith("update-", StringComparison.Ordinal) ||
                !workingName[7..].All(Uri.IsHexDigit) ||
                PathContainsReparsePoint(workingDirectory))
            {
                return 8;
            }

            var result = JsonSerializer.Deserialize<HelperResult>(File.ReadAllBytes(fullResultPath), JsonOptions)
                ?? throw new InvalidDataException("The helper result could not be read by the restart probe.");
            var targetRoot = AppContext.BaseDirectory;
            var marker = new RestartMarker(
                result.Status,
                result.RestartAttempted,
                File.ReadAllText(Path.Combine(targetRoot, "version.txt")),
                File.ReadAllText(Path.Combine(targetRoot, "data", "settings.json")),
                File.ReadAllText(Path.Combine(targetRoot, "user-note.txt")));

            var cleaned = DeleteDirectoryWithRetry(workingDirectory, TimeSpan.FromSeconds(5));
            WriteJsonDurable(fullMarkerPath, marker with { WorkingDirectoryCleaned = cleaned });
            return cleaned ? 0 : 6;
        }
        catch
        {
            return 7;
        }
    }

    private static async Task<ScenarioReport> RunScenarioAsync(
        string validationRoot,
        string helperSourceDirectory,
        string scenarioName,
        bool injectRollbackFailure)
    {
        var scenarioRoot = Path.Combine(validationRoot, scenarioName);
        var targetRoot = Path.Combine(scenarioRoot, "target");
        var nextRoot = Path.Combine(scenarioRoot, "next");
        var workingDirectory = Path.Combine(scenarioRoot, $"update-{Guid.NewGuid():N}");
        var helperDirectory = Path.Combine(workingDirectory, "helper");
        var parentExitSignal = Path.Combine(scenarioRoot, "parent-exit.signal");
        var restartMarkerPath = Path.Combine(scenarioRoot, "restart-marker.json");
        Directory.CreateDirectory(targetRoot);
        Directory.CreateDirectory(nextRoot);
        Directory.CreateDirectory(helperDirectory);

        CreateProbeRelease(targetRoot, version: "old", includeOldOnly: true, includeFailurePayload: false);
        Directory.CreateDirectory(Path.Combine(targetRoot, "data"));
        File.WriteAllText(Path.Combine(targetRoot, "data", "settings.json"), "preserved-settings", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(targetRoot, "user-note.txt"), "preserved-user-note", new UTF8Encoding(false));
        CreateProbeRelease(
            nextRoot,
            version: "new",
            includeOldOnly: false,
            includeFailurePayload: injectRollbackFailure);

        var archivePath = Path.Combine(workingDirectory, ArchiveFileName);
        CreateArchive(nextRoot, archivePath);
        CopyHelper(helperSourceDirectory, helperDirectory);
        var archiveBytes = new FileInfo(archivePath).Length;
        var archiveHash = await HashFileAsync(archivePath).ConfigureAwait(false);

        Process? parentToCleanup = null;
        Process? updaterToCleanup = null;
        try
        {
            var parent = StartCurrentExecutable("--update-parent-probe", parentExitSignal);
            parentToCleanup = parent;
            var parentStartTimeUtcTicks = parent.StartTime.ToUniversalTime().Ticks;
            var launchToken = Guid.NewGuid().ToString("N");
            var requestPath = Path.Combine(workingDirectory, RequestFileName);
            WriteJsonDurable(requestPath, new
            {
                schemaVersion = 1,
                launchToken,
                parentProcessId = parent.Id,
                parentProcessStartTimeUtcTicks = parentStartTimeUtcTicks,
                targetDirectory = targetRoot,
                archivePath,
                archiveBytes,
                archiveSha256 = archiveHash,
            });

            Environment.SetEnvironmentVariable("BOK_UPDATE_RESTART_PROBE", restartMarkerPath);
            var updater = StartProcess(
                Path.Combine(helperDirectory, "BakuretsuOsakanaKobo.Updater.exe"),
                helperDirectory,
                requestPath);
            updaterToCleanup = updater;
            var readyPath = Path.Combine(workingDirectory, ReadyFileName);
            var ready = await WaitForJsonAsync<HelperReady>(readyPath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            Ensure(ready.SchemaVersion == 1 && string.Equals(ready.LaunchToken, launchToken, StringComparison.Ordinal),
                "The real update helper readiness contract did not match its request.");
            Ensure(!parent.HasExited, "The parent probe exited before the update helper became ready.");
            Ensure(File.ReadAllText(Path.Combine(targetRoot, "version.txt")) == "old",
                "The target changed before the parent process exited.");

            Task? collisionTask = null;
            if (injectRollbackFailure)
            {
                collisionTask = InjectLateInstallCollisionAsync(
                    workingDirectory,
                    Path.Combine(targetRoot, "zz-fail.bin"));
            }

            File.WriteAllText(parentExitSignal, "exit", new UTF8Encoding(false));
            await WaitForExitAsync(parent, TimeSpan.FromSeconds(10), "The parent probe did not exit.").ConfigureAwait(false);
            if (collisionTask is not null)
            {
                await collisionTask.ConfigureAwait(false);
            }

            await WaitForExitAsync(updater, TimeSpan.FromSeconds(30), "The real update helper did not exit.").ConfigureAwait(false);
            var expectedUpdaterExitCode = injectRollbackFailure ? 1 : 0;
            Ensure(
                updater.ExitCode == expectedUpdaterExitCode,
                $"The real update helper exited with code {updater.ExitCode}; expected {expectedUpdaterExitCode}.");
            var restart = await WaitForJsonAsync<RestartMarker>(restartMarkerPath, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            Ensure(restart.WorkingDirectoryCleaned, "The restarted probe did not clean the owned working directory.");
            Ensure(!Directory.Exists(workingDirectory), "The owned update working directory remained after restart.");
            Ensure(restart.RestartAttempted, "The update helper did not record a restart attempt.");
            Ensure(restart.DataValue == "preserved-settings", "The update changed persistent data.");
            Ensure(restart.UnknownFileValue == "preserved-user-note", "The update changed a manifest-external file.");

            if (injectRollbackFailure)
            {
                var collisionDirectory = Path.Combine(targetRoot, "zz-fail.bin");
                Ensure(Directory.Exists(collisionDirectory), "The late install collision was not created.");
                Directory.Delete(collisionDirectory);
                Ensure(restart.Status == FailedRolledBackStatus, "The real update helper did not report a complete rollback.");
                Ensure(restart.Version == "old", "The restarted probe did not run the restored old release.");
                Ensure(File.Exists(Path.Combine(targetRoot, "old-only.txt")), "Rollback did not restore the old-only file.");
                Ensure(!File.Exists(Path.Combine(targetRoot, "new-only.txt")), "Rollback retained a new-only file.");
                Ensure(!File.Exists(Path.Combine(targetRoot, "middle-payload.bin")), "Rollback retained the failure payload.");
            }
            else
            {
                Ensure(restart.Status == SucceededStatus, "The real update helper did not report success.");
                Ensure(restart.Version == "new", "The restarted probe did not run the new release.");
                Ensure(!File.Exists(Path.Combine(targetRoot, "old-only.txt")), "The update retained an obsolete owned file.");
                Ensure(File.Exists(Path.Combine(targetRoot, "new-only.txt")), "The update did not install a new owned file.");
            }

            return new ScenarioReport(
                Status: restart.Status,
                RestartAttempted: restart.RestartAttempted,
                RestartedVersion: restart.Version,
                ParentExitCode: parent.ExitCode,
                UpdaterExitCode: updater.ExitCode,
                WorkingDirectoryCleaned: restart.WorkingDirectoryCleaned);
        }
        finally
        {
            if (updaterToCleanup is not null)
            {
                TryTerminate(updaterToCleanup);
                updaterToCleanup.Dispose();
            }

            if (parentToCleanup is not null)
            {
                TryTerminate(parentToCleanup);
                parentToCleanup.Dispose();
            }
        }
    }

    private static void CreateProbeRelease(
        string root,
        string version,
        bool includeOldOnly,
        bool includeFailurePayload)
    {
        Directory.CreateDirectory(root);
        var sourceRoot = AppContext.BaseDirectory;
        CopyFile(
            Path.Combine(sourceRoot, "BakuretsuOsakanaKobo.ReleaseValidation.exe"),
            Path.Combine(root, "BakuretsuOsakanaKobo.exe"));
        foreach (var sourcePath in Directory.EnumerateFiles(sourceRoot, "*.dll", SearchOption.TopDirectoryOnly))
        {
            CopyFile(sourcePath, Path.Combine(root, Path.GetFileName(sourcePath)));
        }

        foreach (var suffix in new[] { ".deps.json", ".runtimeconfig.json" })
        {
            var fileName = $"BakuretsuOsakanaKobo.ReleaseValidation{suffix}";
            CopyFile(Path.Combine(sourceRoot, fileName), Path.Combine(root, fileName));
        }

        File.WriteAllText(Path.Combine(root, "version.txt"), version, new UTF8Encoding(false));
        if (includeOldOnly)
        {
            File.WriteAllText(Path.Combine(root, "old-only.txt"), "old-owned", new UTF8Encoding(false));
        }
        else
        {
            File.WriteAllText(Path.Combine(root, "new-only.txt"), "new-owned", new UTF8Encoding(false));
        }

        if (includeFailurePayload)
        {
            using var payload = new FileStream(
                Path.Combine(root, "middle-payload.bin"),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            payload.SetLength(32L * 1024 * 1024);
            File.WriteAllText(Path.Combine(root, "zz-fail.bin"), "collision-target", new UTF8Encoding(false));
        }

        WriteManifest(root);
    }

    private static void WriteManifest(string root)
    {
        var inventory = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetFileName(path), ManifestFileName, StringComparison.Ordinal))
            .Select(path => new ManifestEntry(
                Path.GetRelativePath(root, path).Replace('\\', '/'),
                new FileInfo(path).Length,
                HashFileAsync(path).GetAwaiter().GetResult()))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();
        WriteJsonDurable(Path.Combine(root, ManifestFileName), new
        {
            validationOnly = false,
            distributionMode = "FrameworkDependent",
            runtimeIdentifier = "win-x64",
            publishSingleFile = false,
            totalFiles = inventory.Length,
            totalBytes = inventory.Sum(file => file.Bytes),
            files = inventory.Select(file => new { path = file.Path, bytes = file.Bytes, sha256 = file.Sha256 }),
        });
    }

    private static void CreateArchive(string sourceRoot, string archivePath)
    {
        using var stream = new FileStream(archivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var sourcePath in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceRoot, sourcePath).Replace('\\', '/');
            var entry = archive.CreateEntry(relativePath, CompressionLevel.Fastest);
            using var source = File.OpenRead(sourcePath);
            using var destination = entry.Open();
            source.CopyTo(destination);
        }
    }

    private static async Task InjectLateInstallCollisionAsync(string workingDirectory, string collisionPath)
    {
        var finalBackupPath = Path.Combine(workingDirectory, "backup", ManifestFileName);
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(20))
        {
            if (File.Exists(finalBackupPath))
            {
                Directory.CreateDirectory(collisionPath);
                return;
            }

            await Task.Delay(1).ConfigureAwait(false);
        }

        throw new TimeoutException("The real updater did not complete its backup before collision injection.");
    }

    private static Process StartCurrentExecutable(string firstArgument, string secondArgument) =>
        StartProcess(
            Path.Combine(AppContext.BaseDirectory, "BakuretsuOsakanaKobo.ReleaseValidation.exe"),
            AppContext.BaseDirectory,
            firstArgument,
            secondArgument);

    private static Process StartProcess(string executablePath, string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {executablePath}.");
    }

    private static async Task WaitForExitAsync(Process process, TimeSpan timeout, string timeoutMessage)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            TryTerminate(process);
            throw new TimeoutException(timeoutMessage);
        }
    }

    private static async Task<T> WaitForJsonAsync<T>(string path, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        Exception? lastFailure = null;
        while (clock.Elapsed < timeout)
        {
            try
            {
                if (File.Exists(path))
                {
                    var value = JsonSerializer.Deserialize<T>(await File.ReadAllBytesAsync(path).ConfigureAwait(false), JsonOptions);
                    if (value is not null)
                    {
                        return value;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                lastFailure = exception;
            }

            await Task.Delay(20).ConfigureAwait(false);
        }

        throw new TimeoutException($"Timed out waiting for JSON at {path}.", lastFailure);
    }

    private static void ValidateHelperFiles(string helperRoot)
    {
        Ensure(Directory.Exists(helperRoot), "The updater output directory did not exist.");
        foreach (var fileName in HelperFiles)
        {
            Ensure(File.Exists(Path.Combine(helperRoot, fileName)), $"The updater output was missing {fileName}.");
        }
    }

    private static void CopyHelper(string sourceRoot, string destinationRoot)
    {
        foreach (var fileName in HelperFiles)
        {
            CopyFile(Path.Combine(sourceRoot, fileName), Path.Combine(destinationRoot, fileName));
        }
    }

    private static void CopyFile(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Copy(sourcePath, destinationPath, overwrite: false);
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false));
    }

    private static void WriteJsonDurable<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static bool DeleteDirectoryWithRetry(string path, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return true;
                }

                Directory.Delete(path, recursive: true);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }

        return !Directory.Exists(path);
    }

    private static void DeleteDirectoryBestEffort(string path)
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
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed record ManifestEntry(string Path, long Bytes, string Sha256);

    private sealed record HelperReady(int SchemaVersion, string LaunchToken);

    private sealed record HelperResult(int SchemaVersion, int Status, bool RestartAttempted);

    private sealed record RestartMarker(
        int Status,
        bool RestartAttempted,
        string Version,
        string DataValue,
        string UnknownFileValue,
        bool WorkingDirectoryCleaned = false);

    private sealed record ScenarioReport(
        int Status,
        bool RestartAttempted,
        string RestartedVersion,
        int ParentExitCode,
        int UpdaterExitCode,
        bool WorkingDirectoryCleaned);
}
