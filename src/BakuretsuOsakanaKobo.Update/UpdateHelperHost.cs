using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BakuretsuOsakanaKobo.Update;

internal sealed record UpdateHelperRequest(
    int SchemaVersion,
    string LaunchToken,
    int ParentProcessId,
    long ParentProcessStartTimeUtcTicks,
    string TargetDirectory,
    string ArchivePath,
    long ArchiveBytes,
    string ArchiveSha256);

internal sealed record UpdateHelperReady(int SchemaVersion, string LaunchToken);

internal enum UpdateHelperStatus
{
    Succeeded,
    InvalidRequest,
    ParentExitTimeout,
    InvalidPackage,
    StorageFailure,
    FailedRolledBack,
    FailedRollbackIncomplete,
    RestartFailed,
    UnexpectedFailure,
}

internal sealed record UpdateHelperResult(
    int SchemaVersion,
    UpdateHelperStatus Status,
    string? TechnicalMessage = null,
    IReadOnlyList<string>? RollbackFailures = null,
    bool RestartAttempted = false);

internal enum ParentProcessWaitStatus
{
    Exited,
    TimedOut,
    Failed,
}

internal interface IUpdateFailureNotifier
{
    void Show(string message);
}

internal sealed class NullUpdateFailureNotifier : IUpdateFailureNotifier
{
    internal static NullUpdateFailureNotifier Instance { get; } = new();

    public void Show(string message)
    {
    }
}

internal interface IUpdateProcessController
{
    Task<(ParentProcessWaitStatus Status, string? TechnicalMessage)> WaitForExitAsync(
        int processId,
        long expectedStartTimeUtcTicks,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    (bool Started, string? TechnicalMessage) StartApplication(
        string executablePath,
        string workingDirectory,
        string updateResultPath);
}

internal sealed class SystemUpdateProcessController : IUpdateProcessController
{
    public async Task<(ParentProcessWaitStatus Status, string? TechnicalMessage)> WaitForExitAsync(
        int processId,
        long expectedStartTimeUtcTicks,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            try
            {
                if (process.StartTime.ToUniversalTime().Ticks != expectedStartTimeUtcTicks)
                {
                    return (ParentProcessWaitStatus.Exited, null);
                }
            }
            catch (InvalidOperationException)
            {
                return (ParentProcessWaitStatus.Exited, null);
            }

            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellation.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCancellation.Token).ConfigureAwait(false);
                return (ParentProcessWaitStatus.Exited, null);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested && timeoutCancellation.IsCancellationRequested)
            {
                return (ParentProcessWaitStatus.TimedOut, "The application did not exit before the update timeout.");
            }
        }
        catch (ArgumentException)
        {
            return (ParentProcessWaitStatus.Exited, null);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return (ParentProcessWaitStatus.Failed, exception.Message);
        }
    }

    public (bool Started, string? TechnicalMessage) StartApplication(
        string executablePath,
        string workingDirectory,
        string updateResultPath)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("--update-result");
            startInfo.ArgumentList.Add(updateResultPath);
            using var process = Process.Start(startInfo);
            return process is null
                ? (false, "The updated application process could not be created.")
                : (true, null);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return (false, exception.Message);
        }
    }
}

internal sealed class UpdateHelperHost
{
    internal const string RequestFileName = "update-request.json";
    internal const string ReadyFileName = "update-ready.json";
    internal const string ResultFileName = "update-result.json";
    internal const string ArchiveFileName = "BakuretsuOsakanaKobo-win-x64.zip";
    private const int ContractSchemaVersion = 1;
    private const int MaximumContractBytes = 64 * 1024;
    private static readonly TimeSpan ParentExitTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };
    private readonly IUpdateProcessController _processController;
    private readonly UpdateArchiveStager _archiveStager;
    private readonly UpdateFileTransaction _transaction;
    private readonly IUpdateFailureNotifier _failureNotifier;

    internal UpdateHelperHost(
        IUpdateProcessController? processController = null,
        UpdateArchiveStager? archiveStager = null,
        UpdateFileTransaction? transaction = null,
        IUpdateFailureNotifier? failureNotifier = null)
    {
        _processController = processController ?? new SystemUpdateProcessController();
        _archiveStager = archiveStager ?? new UpdateArchiveStager();
        _transaction = transaction ?? new UpdateFileTransaction();
        _failureNotifier = failureNotifier ?? NullUpdateFailureNotifier.Instance;
    }

    internal async Task<UpdateHelperResult> RunAsync(
        string requestPath,
        CancellationToken cancellationToken = default)
    {
        var location = ValidateRequestLocation(requestPath);
        if (location.ErrorMessage is not null)
        {
            return Invalid(location.ErrorMessage);
        }

        UpdateHelperResult result;
        var parentExited = false;
        var restartAttempted = false;
        var preserveBackup = false;
        string? validatedTargetRoot = null;
        try
        {
            var requestRead = await ReadRequestAsync(location.RequestPath!, cancellationToken).ConfigureAwait(false);
            if (requestRead.Request is null)
            {
                result = Invalid(requestRead.ErrorMessage ?? "The update request was invalid.");
                CleanupPreparedFiles(location.WorkingDirectory!, preserveBackup: false);
                await WriteResultAsync(location.ResultPath!, result, cancellationToken).ConfigureAwait(false);
                return result;
            }

            var request = requestRead.Request;
            var validation = ValidateRequest(request, location.WorkingDirectory!);
            if (validation is not null)
            {
                result = Invalid(validation);
                CleanupPreparedFiles(location.WorkingDirectory!, preserveBackup: false);
                await WriteResultAsync(location.ResultPath!, result, cancellationToken).ConfigureAwait(false);
                return result;
            }

            await WriteReadyAsync(
                location.ReadyPath!,
                new UpdateHelperReady(ContractSchemaVersion, request.LaunchToken),
                cancellationToken).ConfigureAwait(false);

            var parentWait = await _processController.WaitForExitAsync(
                request.ParentProcessId,
                request.ParentProcessStartTimeUtcTicks,
                ParentExitTimeout,
                cancellationToken).ConfigureAwait(false);
            if (parentWait.Status != ParentProcessWaitStatus.Exited)
            {
                result = new UpdateHelperResult(
                    ContractSchemaVersion,
                    parentWait.Status == ParentProcessWaitStatus.TimedOut
                        ? UpdateHelperStatus.ParentExitTimeout
                        : UpdateHelperStatus.UnexpectedFailure,
                    parentWait.TechnicalMessage);
                CleanupPreparedFiles(location.WorkingDirectory!);
                await WriteResultAsync(location.ResultPath!, result, cancellationToken).ConfigureAwait(false);
                NotifyFailure(
                    "更新を完了できませんでした。アプリが動作中の場合はそのまま利用できます。" +
                    "改善しない場合はGitHub Releaseから手動で更新してください。");
                return result;
            }

            parentExited = true;
            var targetRoot = Path.GetFullPath(request.TargetDirectory);
            validatedTargetRoot = targetRoot;
            var stagedDirectory = Path.Combine(location.WorkingDirectory!, "staged");
            var backupDirectory = Path.Combine(location.WorkingDirectory!, "backup");
            var stage = await _archiveStager.StageAsync(
                request.ArchivePath,
                request.ArchiveBytes,
                request.ArchiveSha256,
                stagedDirectory,
                cancellationToken).ConfigureAwait(false);
            if (stage.Status != UpdateArchiveStageStatus.Prepared || stage.Files is null)
            {
                result = new UpdateHelperResult(
                    ContractSchemaVersion,
                    stage.Status == UpdateArchiveStageStatus.StorageFailure
                        ? UpdateHelperStatus.StorageFailure
                        : UpdateHelperStatus.InvalidPackage,
                    stage.TechnicalMessage);
            }
            else
            {
                var currentManifest = await UpdateArchiveStager.ReadInstalledManifestAsync(
                    Path.Combine(targetRoot, UpdateFileTransaction.ManifestName),
                    cancellationToken).ConfigureAwait(false);
                if (currentManifest.Files is null)
                {
                    result = new UpdateHelperResult(
                        ContractSchemaVersion,
                        UpdateHelperStatus.InvalidPackage,
                        currentManifest.ErrorMessage ?? "The installed release manifest was invalid.");
                }
                else
                {
                    var transaction = await _transaction.ApplyAsync(
                        targetRoot,
                        stagedDirectory,
                        backupDirectory,
                        currentManifest.Files,
                        stage.Files,
                        cancellationToken).ConfigureAwait(false);
                    result = FromTransaction(transaction);
                }
            }

            preserveBackup = result.Status == UpdateHelperStatus.FailedRollbackIncomplete;
            CleanupPreparedFiles(location.WorkingDirectory!, preserveBackup);
            if (result.Status != UpdateHelperStatus.FailedRollbackIncomplete)
            {
                result = await RestartApplicationAsync(
                    targetRoot,
                    location.ResultPath!,
                    result).ConfigureAwait(false);
                restartAttempted = result.RestartAttempted;
            }
            else
            {
                await WriteResultBestEffortAsync(location.ResultPath!, result).ConfigureAwait(false);
                NotifyFailure(
                    "更新の復旧を完了できませんでした。アプリを起動せず、次のバックアップを保持したまま、" +
                    $"GitHub Releaseから手動で更新してください。\n\n{Path.Combine(location.WorkingDirectory!, "backup")}");
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = new UpdateHelperResult(
                ContractSchemaVersion,
                UpdateHelperStatus.UnexpectedFailure,
                "The update helper was cancelled.");
            CleanupPreparedFiles(location.WorkingDirectory!, preserveBackup);
            if (parentExited && !restartAttempted && validatedTargetRoot is not null)
            {
                result = await RestartApplicationAsync(
                    validatedTargetRoot,
                    location.ResultPath!,
                    result).ConfigureAwait(false);
            }
            else
            {
                await WriteResultBestEffortAsync(location.ResultPath!, result).ConfigureAwait(false);
            }

            return result;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or
                ArgumentException or NotSupportedException)
        {
            result = new UpdateHelperResult(
                ContractSchemaVersion,
                UpdateHelperStatus.UnexpectedFailure,
                exception.Message);
            CleanupPreparedFiles(location.WorkingDirectory!, preserveBackup);
            if (parentExited && !restartAttempted && validatedTargetRoot is not null)
            {
                result = await RestartApplicationAsync(
                    validatedTargetRoot,
                    location.ResultPath!,
                    result).ConfigureAwait(false);
            }
            else
            {
                await WriteResultBestEffortAsync(location.ResultPath!, result).ConfigureAwait(false);
            }

            return result;
        }
    }

    private static (
        string? RequestPath,
        string? ReadyPath,
        string? ResultPath,
        string? WorkingDirectory,
        string? ErrorMessage)
        ValidateRequestLocation(string requestPath)
    {
        if (string.IsNullOrWhiteSpace(requestPath))
        {
            return (null, null, null, null, "The update request path was empty.");
        }

        try
        {
            var fullPath = Path.GetFullPath(requestPath);
            var workingDirectory = Path.GetDirectoryName(fullPath)!;
            var directoryName = Path.GetFileName(workingDirectory);
            if (!string.Equals(Path.GetFileName(fullPath), RequestFileName, StringComparison.Ordinal) ||
                directoryName.Length != 39 ||
                !directoryName.StartsWith("update-", StringComparison.Ordinal) ||
                !directoryName[7..].All(Uri.IsHexDigit) ||
                !Directory.Exists(workingDirectory) ||
                PathContainsReparsePoint(fullPath))
            {
                return (null, null, null, null, "The update request was outside an owned working directory.");
            }

            return (
                fullPath,
                Path.Combine(workingDirectory, ReadyFileName),
                Path.Combine(workingDirectory, ResultFileName),
                workingDirectory,
                null);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return (null, null, null, null, exception.Message);
        }
    }

    private static async Task<(UpdateHelperRequest? Request, string? ErrorMessage)> ReadRequestAsync(
        string requestPath,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(requestPath) ||
                new FileInfo(requestPath).Length is <= 0 or > MaximumContractBytes)
            {
                return (null, "The update request file was missing or too large.");
            }

            var bytes = await File.ReadAllBytesAsync(requestPath, cancellationToken).ConfigureAwait(false);
            return (JsonSerializer.Deserialize<UpdateHelperRequest>(bytes, SerializerOptions), null);
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return (null, exception.Message);
        }
    }

    private static string? ValidateRequest(UpdateHelperRequest request, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(request.ArchivePath) ||
            string.IsNullOrWhiteSpace(request.TargetDirectory) ||
            string.IsNullOrWhiteSpace(request.ArchiveSha256) ||
            request.LaunchToken is not { Length: 32 } ||
            !request.LaunchToken.All(Uri.IsHexDigit))
        {
            return "The update request did not contain all required fields.";
        }

        var archivePath = Path.GetFullPath(request.ArchivePath);
        var targetDirectory = Path.GetFullPath(request.TargetDirectory);
        if (request.SchemaVersion != ContractSchemaVersion ||
            request.ParentProcessId <= 0 ||
            request.ParentProcessStartTimeUtcTicks <= 0 ||
            request.ArchiveBytes is <= 0 or > UpdateArchiveStager.MaximumArchiveBytes ||
            request.ArchiveSha256 is not { Length: 64 } ||
            !request.ArchiveSha256.All(Uri.IsHexDigit) ||
            !string.Equals(archivePath, Path.Combine(workingDirectory, ArchiveFileName), StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(targetDirectory) ||
            PathsOverlap(targetDirectory, workingDirectory) ||
            PathContainsReparsePoint(targetDirectory))
        {
            return "The update request fields were outside the approved boundary.";
        }

        return null;
    }

    private async Task<UpdateHelperResult> RestartApplicationAsync(
        string targetDirectory,
        string resultPath,
        UpdateHelperResult priorResult)
    {
        var executablePath = Path.Combine(targetDirectory, "BakuretsuOsakanaKobo.exe");
        var plannedResult = priorResult with { RestartAttempted = true };
        await WriteResultBestEffortAsync(resultPath, plannedResult).ConfigureAwait(false);
        if (!File.Exists(executablePath) || PathContainsReparsePoint(executablePath))
        {
            var failedResult = new UpdateHelperResult(
                ContractSchemaVersion,
                UpdateHelperStatus.RestartFailed,
                $"{priorResult.Status}: The application executable was unavailable for restart.",
                priorResult.RollbackFailures,
                RestartAttempted: true);
            await WriteResultBestEffortAsync(resultPath, failedResult).ConfigureAwait(false);
            NotifyFailure(
                "更新後のアプリを起動できませんでした。配置フォルダーを確認し、" +
                "改善しない場合はGitHub Releaseから手動で更新してください。");
            return failedResult;
        }

        var restart = _processController.StartApplication(executablePath, targetDirectory, resultPath);
        if (restart.Started)
        {
            return plannedResult;
        }

        var restartFailedResult = new UpdateHelperResult(
                ContractSchemaVersion,
                UpdateHelperStatus.RestartFailed,
                $"{priorResult.Status}: {restart.TechnicalMessage}",
                priorResult.RollbackFailures,
                RestartAttempted: true);
        await WriteResultBestEffortAsync(resultPath, restartFailedResult).ConfigureAwait(false);
        NotifyFailure(
            "更新後のアプリを起動できませんでした。配置フォルダーを確認し、" +
            "改善しない場合はGitHub Releaseから手動で更新してください。");
        return restartFailedResult;
    }

    private void NotifyFailure(string message)
    {
        try
        {
            _failureNotifier.Show(message);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }

    private static Task WriteReadyAsync(
        string readyPath,
        UpdateHelperReady ready,
        CancellationToken cancellationToken) =>
        WriteContractAsync(readyPath, JsonSerializer.SerializeToUtf8Bytes(ready, SerializerOptions), cancellationToken);

    private static UpdateHelperResult FromTransaction(UpdateFileTransactionResult transaction) =>
        transaction.Status switch
        {
            UpdateFileTransactionStatus.Succeeded =>
                new UpdateHelperResult(ContractSchemaVersion, UpdateHelperStatus.Succeeded),
            UpdateFileTransactionStatus.FailedRolledBack =>
                new UpdateHelperResult(
                    ContractSchemaVersion,
                    UpdateHelperStatus.FailedRolledBack,
                    transaction.TechnicalMessage),
            UpdateFileTransactionStatus.FailedRollbackIncomplete =>
                new UpdateHelperResult(
                    ContractSchemaVersion,
                    UpdateHelperStatus.FailedRollbackIncomplete,
                    transaction.TechnicalMessage,
                    transaction.RollbackFailures),
            UpdateFileTransactionStatus.ValidationFailed =>
                new UpdateHelperResult(
                    ContractSchemaVersion,
                    UpdateHelperStatus.InvalidPackage,
                    transaction.TechnicalMessage),
            _ => new UpdateHelperResult(
                ContractSchemaVersion,
                UpdateHelperStatus.UnexpectedFailure,
                transaction.TechnicalMessage),
        };

    private static async Task WriteResultAsync(
        string resultPath,
        UpdateHelperResult result,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, SerializerOptions);
        await WriteContractAsync(resultPath, bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteContractAsync(
        string path,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
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

            File.Move(temporaryPath, path, overwrite: true);
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

    private static async Task WriteResultBestEffortAsync(string resultPath, UpdateHelperResult result)
    {
        try
        {
            await WriteResultAsync(resultPath, result, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void CleanupPreparedFiles(string workingDirectory, bool preserveBackup = false)
    {
        var directories = preserveBackup ? new[] { "staged" } : new[] { "staged", "backup" };
        foreach (var directory in directories)
        {
            try
            {
                var path = Path.Combine(workingDirectory, directory);
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        try
        {
            File.Delete(Path.Combine(workingDirectory, ArchiveFileName));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool PathsOverlap(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase) ||
        left.StartsWith(EnsureTrailingSeparator(right), StringComparison.OrdinalIgnoreCase) ||
        right.StartsWith(EnsureTrailingSeparator(left), StringComparison.OrdinalIgnoreCase);

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

    private static string EnsureTrailingSeparator(string path) =>
        Path.EndsInDirectorySeparator(path) ? path : $"{path}{Path.DirectorySeparatorChar}";

    private static UpdateHelperResult Invalid(string message) =>
        new(ContractSchemaVersion, UpdateHelperStatus.InvalidRequest, message);
}
