using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using BakuretsuOsakanaKobo.Infrastructure.Diagnostics;
using BakuretsuOsakanaKobo.Infrastructure.Errors;
using BakuretsuOsakanaKobo.Infrastructure.Persistence;

namespace BakuretsuOsakanaKobo.ReleaseValidation;

internal static class UpdateConfirmationValidation
{
    private const int YesButtonId = 6;
    private const int NoButtonId = 7;
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

    internal static int Run(string reportPath)
    {
        var fullReportPath = Path.GetFullPath(reportPath);
        var validationRoot = $"{fullReportPath}.update-confirmation-{Guid.NewGuid():N}";
        try
        {
            Directory.CreateDirectory(validationRoot);
            var installedRoot = Path.Combine(validationRoot, "installed");
            var helperRoot = Path.Combine(installedRoot, "updater");
            var workingRoot = Path.Combine(validationRoot, "working");
            Directory.CreateDirectory(helperRoot);
            foreach (var fileName in HelperFiles)
            {
                File.WriteAllText(
                    Path.Combine(helperRoot, fileName),
                    $"validation helper {fileName}",
                    new UTF8Encoding(false));
            }

            var packageBytes = CreatePackage();
            var release = CreateRelease(
                packageBytes.LongLength,
                Convert.ToHexString(SHA256.HashData(packageBytes)));
            var updateCheckService = new AvailableUpdateService(release);
            var packageHandler = new ControlledPackageHandler(packageBytes);
            using var httpClient = new HttpClient(packageHandler) { Timeout = TimeSpan.FromMinutes(1) };
            var helperLauncher = new ControlledReadyLauncher();
            var coordinator = new UpdateApplicationCoordinator(
                new UpdatePackageDownloader(httpClient),
                helperRoot,
                workingRoot,
                helperLauncher);
            var diagnosticLog = new ValidationDiagnosticLog();
            var settingsPath = Path.Combine(validationRoot, "settings.json");
            var appSettings = new AppSettingsRepository(settingsPath);
            _ = appSettings.LoadAsync().GetAwaiter().GetResult();
            Ensure(SemanticVersion.TryParse("1.1.0", out var currentVersion) && currentVersion is not null,
                "The update-confirmation validation version was invalid.");

            var application = new Application();
            Program.AddProductResources(application.Resources);
            var window = new MainWindow
            {
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
            };
            window.ConfigureServices(
                new PortableDataPaths(installedRoot),
                new ErrorReporter(diagnosticLog, new ValidationNotificationSink()),
                playbackBackend: null,
                appSettings: appSettings,
                updateCheckService: updateCheckService,
                currentVersion: currentVersion,
                applicationUpdateCoordinator: coordinator);

            Exception? validationFailure = null;
            DialogObservation? declinedDialog = null;
            DialogObservation? acceptedDialog = null;
            string? downloadNotification = null;
            var windowClosed = false;
            var rejectionKeptWindowOpen = false;
            var downloadKeptWindowOpen = false;
            window.Closed += (_, _) => windowClosed = true;
            window.Loaded += async (_, _) =>
            {
                var menuItem = (MenuItem)window.FindName("CheckForUpdatesMenuItem");
                var notificationBorder = (Border)window.FindName("NotificationBorder");
                var notificationText = (TextBlock)window.FindName("NotificationMessageText");
                var uiThreadId = DialogAutomation.GetCurrentThreadId();
                try
                {
                    Ensure(menuItem.IsEnabled, "The update-check menu was not enabled for confirmation validation.");

                    var declineTask = DialogAutomation.ObserveAndClickAsync(
                        uiThreadId,
                        NoButtonId,
                        TimeSpan.FromSeconds(5));
                    menuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, menuItem));
                    declinedDialog = await declineTask;
                    await WaitUntilAsync(
                        () => updateCheckService.CallCount >= 1 && menuItem.IsEnabled,
                        TimeSpan.FromSeconds(5),
                        "The declined update confirmation did not complete.");
                    rejectionKeptWindowOpen = window.IsVisible && !windowClosed;
                    Ensure(rejectionKeptWindowOpen, "Declining the update confirmation closed the application.");
                    Ensure(packageHandler.RequestCount == 0, "Declining the update confirmation started a download.");
                    Ensure(helperLauncher.CallCount == 0, "Declining the update confirmation started the helper.");

                    var acceptTask = DialogAutomation.ObserveAndClickAsync(
                        uiThreadId,
                        YesButtonId,
                        TimeSpan.FromSeconds(5));
                    menuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, menuItem));
                    acceptedDialog = await acceptTask;
                    await packageHandler.RequestStarted.WaitAsync(TimeSpan.FromSeconds(5));
                    downloadNotification = notificationText.Text;
                    downloadKeptWindowOpen = window.IsVisible &&
                                             !windowClosed &&
                                             notificationBorder.Visibility == Visibility.Visible;
                    Ensure(downloadKeptWindowOpen, "The application closed before update preparation completed.");
                    Ensure(downloadNotification.Contains("ダウンロード", StringComparison.Ordinal),
                        "The update download notification was not visible while the response was pending.");
                    packageHandler.AllowResponse();

                    await helperLauncher.LaunchStarted.WaitAsync(TimeSpan.FromSeconds(10));
                    Ensure(window.IsVisible && !windowClosed,
                        "The application closed before the helper reported readiness.");
                    helperLauncher.ValidateCapturedRequest(installedRoot);
                    helperLauncher.AllowReady();
                }
                catch (Exception exception)
                {
                    validationFailure = exception;
                    packageHandler.AllowResponse();
                    helperLauncher.AllowReady();
                    window.Close();
                }
            };

            application.Run(window);
            if (validationFailure is not null)
            {
                throw new InvalidOperationException("The real-WPF update confirmation validation failed.", validationFailure);
            }

            Ensure(windowClosed, "The window did not complete normal shutdown after helper readiness.");
            Ensure(updateCheckService.CallCount == 2, "The validation did not run two manual update checks.");
            Ensure(packageHandler.RequestCount == 1, "The accepted confirmation did not perform exactly one download.");
            Ensure(helperLauncher.CallCount == 1, "The accepted confirmation did not launch exactly one helper.");
            Ensure(diagnosticLog.Events.Any(item => item.EventName == "update-helper-ready"),
                "Normal shutdown did not record helper readiness.");
            ValidateDialog(declinedDialog, release);
            ValidateDialog(acceptedDialog, release);

            var report = new
            {
                realWpfWindow = true,
                realNativeConfirmationDialogs = 2,
                release = release.TagName,
                declinedWithoutDownload = rejectionKeptWindowOpen,
                acceptedDownloadRequests = packageHandler.RequestCount,
                downloadNotification,
                downloadKeptWindowOpen,
                helperLaunchCalls = helperLauncher.CallCount,
                helperRequestValidated = true,
                closedOnlyAfterReady = windowClosed,
                helperReadyDiagnosticRecorded = true,
                declinedDialogText = declinedDialog!.Text,
                acceptedDialogText = acceptedDialog!.Text,
            };
            WriteReport(fullReportPath, report);
            Console.WriteLine(JsonSerializer.Serialize(report));
            return 0;
        }
        catch (Exception exception)
        {
            var report = new { error = exception.ToString() };
            WriteReport(fullReportPath, report);
            Console.Error.WriteLine(JsonSerializer.Serialize(report));
            return 1;
        }
        finally
        {
            DeleteDirectoryBestEffort(validationRoot);
        }
    }

    private static void ValidateDialog(DialogObservation? observation, UpdateRelease release)
    {
        if (observation is null)
        {
            throw new InvalidOperationException("The update confirmation dialog was not observed.");
        }

        Ensure(observation.Title.Contains(ApplicationInfo.DisplayName, StringComparison.Ordinal),
            "The update confirmation dialog title did not identify the application.");
        Ensure(observation.Text.Contains(release.TagName, StringComparison.Ordinal),
            "The update confirmation dialog did not show the release version.");
        Ensure(observation.Text.Contains("Validation release notes", StringComparison.Ordinal),
            "The update confirmation dialog did not show the release summary.");
        Ensure(observation.Text.Contains("終了", StringComparison.Ordinal) &&
               observation.Text.Contains("再起動", StringComparison.Ordinal),
            "The update confirmation dialog did not explain shutdown and restart.");
    }

    private static UpdateRelease CreateRelease(long packageBytes, string packageSha256)
    {
        Ensure(SemanticVersion.TryParse("1.2.0", out var version) && version is not null,
            "The validation release version was invalid.");
        return new UpdateRelease(
            version!,
            "v1.2.0",
            "Validation release",
            "Validation release notes for the explicit confirmation gate.",
            new Uri("https://github.com/RT-EGG/bakuretsu-osakana-kobo/releases/tag/v1.2.0"),
            new UpdateAsset(
                GitHubReleaseClient.AssetName,
                packageBytes,
                packageSha256,
                new Uri("https://github.com/RT-EGG/bakuretsu-osakana-kobo/releases/download/v1.2.0/BakuretsuOsakanaKobo-win-x64.zip")));
    }

    private static byte[] CreatePackage()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["BakuretsuOsakanaKobo.exe"] = Encoding.UTF8.GetBytes("validation executable"),
            ["validation.dll"] = Encoding.UTF8.GetBytes("validation library"),
        };
        var inventory = files.Select(file => new
        {
            path = file.Key,
            bytes = file.Value.LongLength,
            sha256 = Convert.ToHexString(SHA256.HashData(file.Value)),
        }).ToArray();
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            validationOnly = false,
            distributionMode = "FrameworkDependent",
            runtimeIdentifier = "win-x64",
            publishSingleFile = false,
            totalFiles = inventory.Length,
            totalBytes = inventory.Sum(file => file.bytes),
            files = inventory,
        }, JsonOptions);

        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Key, CompressionLevel.Fastest);
                using var destination = entry.Open();
                destination.Write(file.Value);
            }

            var manifestEntry = archive.CreateEntry("release-manifest.json", CompressionLevel.Fastest);
            using var manifestDestination = manifestEntry.Open();
            manifestDestination.Write(manifest);
        }

        return stream.ToArray();
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout, string timeoutMessage)
    {
        var started = Stopwatch.StartNew();
        while (started.Elapsed < timeout)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException(timeoutMessage);
    }

    private static void WriteReport(string path, object report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(report, JsonOptions), new UTF8Encoding(false));
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

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class AvailableUpdateService(UpdateRelease release) : IUpdateCheckService
    {
        private int _callCount;

        internal int CallCount => Volatile.Read(ref _callCount);

        public Task<UpdateCheckResult> CheckAsync(
            SemanticVersion currentVersion,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, release));
        }
    }

    private sealed class ControlledPackageHandler(byte[] packageBytes) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _requestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allowResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requestCount;

        internal int RequestCount => Volatile.Read(ref _requestCount);

        internal Task RequestStarted => _requestStarted.Task;

        internal void AllowResponse() => _allowResponse.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            _requestStarted.TrySetResult();
            await _allowResponse.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(packageBytes),
            };
        }
    }

    private sealed class ControlledReadyLauncher : IUpdateHelperLauncher
    {
        private readonly TaskCompletionSource _launchStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allowReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;
        private string? _requestPath;
        private string? _readyPath;
        private string? _launchToken;

        internal int CallCount => Volatile.Read(ref _callCount);

        internal Task LaunchStarted => _launchStarted.Task;

        internal void AllowReady() => _allowReady.TrySetResult();

        public async Task<(bool Ready, string? TechnicalMessage)> LaunchAndWaitForReadyAsync(
            string executablePath,
            string workingDirectory,
            string requestPath,
            string readyPath,
            string launchToken,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            _requestPath = requestPath;
            _readyPath = readyPath;
            _launchToken = launchToken;
            _launchStarted.TrySetResult();
            await _allowReady.Task.WaitAsync(cancellationToken);
            return (true, null);
        }

        internal void ValidateCapturedRequest(string expectedTargetRoot)
        {
            var requestPath = _requestPath ??
                throw new InvalidOperationException("The accepted confirmation did not create an update request.");
            Ensure(File.Exists(requestPath),
                "The accepted confirmation did not create an update request.");
            Ensure(_readyPath is not null &&
                   string.Equals(Path.GetFileName(_readyPath), "update-ready.json", StringComparison.Ordinal),
                "The accepted confirmation did not provide the helper readiness path.");
            Ensure(_launchToken is { Length: 32 } && _launchToken.All(Uri.IsHexDigit),
                "The accepted confirmation did not create a valid launch token.");
            using var document = JsonDocument.Parse(File.ReadAllBytes(requestPath));
            var root = document.RootElement;
            Ensure(root.GetProperty("schemaVersion").GetInt32() == 1,
                "The accepted confirmation wrote an invalid request schema.");
            Ensure(string.Equals(root.GetProperty("launchToken").GetString(), _launchToken, StringComparison.Ordinal),
                "The update request launch token did not match the launcher token.");
            Ensure(string.Equals(
                    Path.GetFullPath(root.GetProperty("targetDirectory").GetString()!),
                    Path.GetFullPath(expectedTargetRoot),
                    StringComparison.OrdinalIgnoreCase),
                "The update request did not target the validation installation.");
        }
    }

    private sealed class ValidationDiagnosticLog : IDiagnosticLog
    {
        internal ConcurrentQueue<DiagnosticEvent> Events { get; } = new();

        public DiagnosticWriteResult Write(DiagnosticEvent diagnosticEvent)
        {
            Events.Enqueue(diagnosticEvent);
            return DiagnosticWriteResult.Succeeded();
        }

        public void Dispose()
        {
        }
    }

    private sealed class ValidationNotificationSink : IUserNotificationSink
    {
        public void Show(UserNotification notification)
        {
        }
    }

    private sealed record DialogObservation(string Title, string Text);

    private static class DialogAutomation
    {
        private const uint WindowClose = 0x0010;
        private const uint ButtonClick = 0x00F5;

        internal static uint GetCurrentThreadId() => NativeGetCurrentThreadId();

        internal static async Task<DialogObservation> ObserveAndClickAsync(
            uint threadId,
            int buttonId,
            TimeSpan timeout)
        {
            var started = Stopwatch.StartNew();
            var lastDialog = nint.Zero;
            while (started.Elapsed < timeout)
            {
                var dialog = FindDialog(threadId);
                if (dialog != nint.Zero)
                {
                    lastDialog = dialog;
                    var title = ReadWindowText(dialog);
                    var texts = new List<string>();
                    _ = EnumChildWindows(dialog, (child, _) =>
                    {
                        var text = ReadWindowText(child);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            texts.Add(text);
                        }

                        return true;
                    }, nint.Zero);
                    var button = GetDlgItem(dialog, buttonId);
                    if (button != nint.Zero)
                    {
                        if (!PostMessage(button, ButtonClick, nint.Zero, nint.Zero))
                        {
                            throw new InvalidOperationException(
                                $"Could not click update confirmation button {buttonId}: {Marshal.GetLastWin32Error()}.");
                        }

                        return new DialogObservation(title, string.Join("\n", texts));
                    }
                }

                await Task.Delay(20).ConfigureAwait(false);
            }

            if (lastDialog != nint.Zero)
            {
                _ = PostMessage(lastDialog, WindowClose, nint.Zero, nint.Zero);
            }

            throw new TimeoutException($"The update confirmation dialog button {buttonId} was not found.");
        }

        private static nint FindDialog(uint threadId)
        {
            var found = nint.Zero;
            _ = EnumThreadWindows(threadId, (window, _) =>
            {
                var className = new StringBuilder(64);
                _ = GetClassName(window, className, className.Capacity);
                if (string.Equals(className.ToString(), "#32770", StringComparison.Ordinal))
                {
                    found = window;
                    return false;
                }

                return true;
            }, nint.Zero);
            return found;
        }

        private static string ReadWindowText(nint window)
        {
            var length = GetWindowTextLength(window);
            var text = new StringBuilder(length + 1);
            _ = GetWindowText(window, text, text.Capacity);
            return text.ToString();
        }

        private delegate bool EnumWindowCallback(nint window, nint parameter);

#pragma warning disable SYSLIB1054 // Validation-only Win32 dialog automation does not enable unsafe code.
        [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
        private static extern uint NativeGetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumThreadWindows(uint threadId, EnumWindowCallback callback, nint parameter);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumChildWindows(nint parent, EnumWindowCallback callback, nint parameter);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetClassName(nint window, StringBuilder className, int maximumCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetWindowText(nint window, StringBuilder text, int maximumCount);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowTextLength(nint window);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern nint GetDlgItem(nint dialog, int itemId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(nint window, uint message, nint wordParameter, nint longParameter);
#pragma warning restore SYSLIB1054
    }
}
