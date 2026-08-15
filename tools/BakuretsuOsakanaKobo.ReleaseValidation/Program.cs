using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using BakuretsuOsakanaKobo.Infrastructure.Diagnostics;
using BakuretsuOsakanaKobo.Infrastructure.Errors;
using BakuretsuOsakanaKobo.Infrastructure.Persistence;
using BakuretsuOsakanaKobo.Playback;
using LibVLCSharp.Shared;
using NAudio.CoreAudioApi;

namespace BakuretsuOsakanaKobo.ReleaseValidation;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: BakuretsuOsakanaKobo.ReleaseValidation <video> <report.json>");
            return 2;
        }

        if (Environment.GetEnvironmentVariable("BOK_THUMBNAIL_WORKER_VALIDATION") == "1")
        {
            return RunThumbnailWorkerValidationAsync(args[0], args[1]).GetAwaiter().GetResult();
        }

        var processClock = Stopwatch.StartNew();
        var validateProfiles = Environment.GetEnvironmentVariable("BOK_VIDEO_PROFILE_VALIDATION") == "1";
        var validateGestures = Environment.GetEnvironmentVariable("BOK_GESTURE_VALIDATION") == "1";
        var validateFullscreen = Environment.GetEnvironmentVariable("BOK_FULLSCREEN_VALIDATION") == "1";
        var validateShortcuts = Environment.GetEnvironmentVariable("BOK_SHORTCUT_VALIDATION") == "1";
        var validateStartPositions = Environment.GetEnvironmentVariable("BOK_START_POSITION_VALIDATION") == "1";
        var validateFileDrop = Environment.GetEnvironmentVariable("BOK_FILE_DROP_VALIDATION") == "1";
        var validateRecentFiles = Environment.GetEnvironmentVariable("BOK_RECENT_FILES_VALIDATION") == "1";
        var validatePlaylist = Environment.GetEnvironmentVariable("BOK_PLAYLIST_VALIDATION") == "1";
        var validateThumbnailSettings = Environment.GetEnvironmentVariable("BOK_THUMBNAIL_SETTINGS_VALIDATION") == "1";
        var validateThumbnailHover = Environment.GetEnvironmentVariable("BOK_THUMBNAIL_HOVER_VALIDATION") == "1";
        var profileFilePath = $"{Path.GetFullPath(args[1])}.video-profiles.json";
        VideoProfileRepository? videoProfiles = null;
        if (validateProfiles || validateStartPositions)
        {
            videoProfiles = new VideoProfileRepository(profileFilePath);
            videoProfiles.LoadAsync().GetAwaiter().GetResult();
            if (validateProfiles)
            {
                videoProfiles.Set(args[0], 275, isMuted: true);
            }

            if (validateStartPositions)
            {
                videoProfiles.Set(args[0], 100, isMuted: true);
                videoProfiles.SetStartPosition(args[0], 12_000);
            }

            var initialSave = videoProfiles.SaveAsync().GetAwaiter().GetResult();
            Ensure(initialSave.Success, initialSave.ErrorMessage ?? "Initial video profile save failed.");
        }

        var recentFilePath = $"{Path.GetFullPath(args[1])}.recent-files.json";
        RecentFileRepository? recentFiles = null;
        string? secondaryVideoPath = null;
        if (validateRecentFiles)
        {
            var reportDirectory = Path.GetDirectoryName(Path.GetFullPath(args[1]))!;
            Directory.CreateDirectory(reportDirectory);
            secondaryVideoPath = Path.Combine(
                reportDirectory,
                $"recent-secondary{Path.GetExtension(args[0])}");
            File.Copy(args[0], secondaryVideoPath, overwrite: true);
            recentFiles = new RecentFileRepository(recentFilePath);
            recentFiles.LoadAsync().GetAwaiter().GetResult();
            var clear = recentFiles.ClearAsync().GetAwaiter().GetResult();
            Ensure(clear.Success, clear.ErrorMessage ?? "Initial recent-file cleanup failed.");
            foreach (var path in new[]
                     {
                         Path.Combine(reportDirectory, "missing-three.wmv"),
                         Path.Combine(reportDirectory, "missing-two.mp4"),
                         Path.Combine(reportDirectory, "missing-one.wmv"),
                         secondaryVideoPath,
                     })
            {
                var save = recentFiles.RecordSuccessfulOpenAsync(path).GetAwaiter().GetResult();
                Ensure(save.Success, save.ErrorMessage ?? "Initial recent-file save failed.");
            }
        }

        var playlistFilePath = $"{Path.GetFullPath(args[1])}.playlist.json";
        PlaylistRepository? playlist = null;
        string? playlistManualOpenPath = null;
        if (validatePlaylist)
        {
            var reportDirectory = Path.GetDirectoryName(Path.GetFullPath(args[1]))!;
            Directory.CreateDirectory(reportDirectory);
            playlistManualOpenPath = Path.Combine(
                reportDirectory,
                $"playlist-manual-open{Path.GetExtension(args[0])}");
            File.Copy(args[0], playlistManualOpenPath, overwrite: true);
            playlist = new PlaylistRepository(playlistFilePath);
            playlist.LoadAsync().GetAwaiter().GetResult();
            var existingPath = Path.GetFullPath(args[0]);
            var saveEntries = playlist.ReplaceEntriesAsync(
            [
                existingPath,
                Path.Combine(reportDirectory, "missing-playlist.wmv"),
                existingPath,
            ]).GetAwaiter().GetResult();
            Ensure(saveEntries.Success, saveEntries.ErrorMessage ?? "Initial playlist save failed.");
            var saveLoop = playlist.SetLoopAsync(true).GetAwaiter().GetResult();
            Ensure(saveLoop.Success, saveLoop.ErrorMessage ?? "Initial playlist loop save failed.");
        }

        var appSettingsFilePath = $"{Path.GetFullPath(args[1])}.settings.json";
        AppSettingsRepository? appSettings = null;
        if (validateThumbnailSettings)
        {
            appSettings = new AppSettingsRepository(appSettingsFilePath);
            appSettings.LoadAsync().GetAwaiter().GetResult();
            var saveSettings = appSettings.SetThumbnailIntervalPercentAsync(1.25).GetAwaiter().GetResult();
            Ensure(saveSettings.Success, saveSettings.ErrorMessage ?? "Initial thumbnail settings save failed.");
        }

        var application = new Application();
        AddProductResources(application.Resources);
        var window = new MainWindow
        {
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        var backend = new LibVlcPlaybackBackend();
        backend.SetMuted(true);
        var notificationSink = new RecordingNotificationSink();
        var diagnosticLog = new RecordingDiagnosticLog();
        var thumbnailGenerationService = validateThumbnailHover
            ? new ThumbnailGenerationService()
            : null;
        window.ConfigureServices(
            new PortableDataPaths(AppContext.BaseDirectory),
            new ErrorReporter(diagnosticLog, notificationSink),
            backend,
            videoProfiles,
            recentFiles,
            playlist,
            appSettings,
            thumbnailGenerationService);

        var exitCode = 1;
        ProfileDelayValidation? profileDelayValidation = null;
        StartPositionValidation? startPositionValidation = null;
        FileDropValidation? fileDropValidation = null;
        RecentFileValidation? recentFileValidation = null;
        PlaylistValidation? playlistValidation = null;
        ThumbnailSettingsValidation? thumbnailSettingsValidation = null;
        ThumbnailHoverValidation? thumbnailHoverValidation = null;
        window.Loaded += async (_, _) =>
        {
            var windowLoadedMilliseconds = processClock.Elapsed.TotalMilliseconds;
            try
            {
                var seekSlider = (Slider)window.FindName("SeekSlider");
                var startPositionMarker = (FrameworkElement)window.FindName("StartPositionMarker");
                var volumeSlider = (Slider)window.FindName("VolumeSlider");
                var muteButton = (Button)window.FindName("MuteButton");
                var volumeText = (TextBlock)window.FindName("VolumeText");
                var videoSurface = (FrameworkElement)window.FindName("VideoInteractionSurface");
                var videoRegion = (FrameworkElement)window.FindName("VideoSurface");
                var playbackRateText = (TextBlock)window.FindName("PlaybackRateText");
                Ensure(!volumeSlider.IsEnabled && !muteButton.IsEnabled, "Volume controls must start disabled.");
                var openMetrics = await MeasureOpenAsync(window, seekSlider, backend, args[0]);
                var lengthMilliseconds = backend.LengthMilliseconds;
                if (validateThumbnailHover)
                {
                    thumbnailHoverValidation = await ValidateThumbnailHoverAsync(
                        window,
                        seekSlider,
                        lengthMilliseconds);
                    exitCode = 0;
                    return;
                }

                if (validateThumbnailSettings)
                {
                    thumbnailSettingsValidation = await ValidateThumbnailSettingsAsync(
                        window,
                        backend,
                        appSettings!,
                        args[0]);
                    exitCode = 0;
                    return;
                }

                if (validatePlaylist)
                {
                    playlistValidation = await ValidatePlaylistAsync(
                        window,
                        backend,
                        playlist!,
                        notificationSink,
                        diagnosticLog,
                        playlistManualOpenPath!);
                    exitCode = 0;
                    return;
                }

                if (validateRecentFiles)
                {
                    recentFileValidation = await ValidateRecentFilesAsync(
                        window,
                        backend,
                        recentFiles!,
                        secondaryVideoPath!);
                    exitCode = 0;
                    return;
                }

                if (validateProfiles)
                {
                    profileDelayValidation = await ValidateVideoProfilesAsync(
                        volumeSlider,
                        backend,
                        profileFilePath,
                        notificationSink);
                    exitCode = 0;
                    return;
                }

                if (validateStartPositions)
                {
                    startPositionValidation = await ValidateStartPositionsAsync(
                        window,
                        videoSurface,
                        seekSlider,
                        startPositionMarker,
                        backend,
                        videoProfiles!,
                        profileFilePath,
                        notificationSink);
                    exitCode = 0;
                    return;
                }

                if (validateFileDrop)
                {
                    fileDropValidation = await ValidateFileDropAsync(
                        window,
                        videoSurface,
                        seekSlider,
                        backend,
                        args[0]);
                    exitCode = 0;
                    return;
                }

                if (Environment.GetEnvironmentVariable("BOK_AUDIO_DRAIN_VALIDATION") == "1")
                {
                    var drainDiagnostics = await ValidateNaturalDrainAsync(backend);
                    var drainReport = new
                    {
                        success = true,
                        video = Path.GetFullPath(args[0]),
                        muted = backend.IsMuted,
                        processStartToWindowLoadedMs = windowLoadedMilliseconds,
                        openToTimelineReadyMs = openMetrics.TimelineReadyMilliseconds,
                        openToAudioOutputReadyMs = openMetrics.AudioOutputReadyMilliseconds,
                        lengthMilliseconds,
                        audioDiagnostics = drainDiagnostics,
                    };
                    WriteReport(args[1], drainReport);
                    Console.WriteLine(JsonSerializer.Serialize(drainReport));
                    exitCode = 0;
                    return;
                }

                var seek50Milliseconds = await MeasureSeekAsync(
                    seekSlider,
                    backend,
                    normalizedPosition: 0.5,
                    lengthMilliseconds);
                var seek90Milliseconds = await MeasureSeekAsync(
                    seekSlider,
                    backend,
                    normalizedPosition: 0.9,
                    lengthMilliseconds);

                var playbackRateValidation = await ValidatePlaybackRateAsync(
                    window,
                    playbackRateText,
                    videoSurface,
                    backend,
                    args[0]);
                var shortcutValidation = validateShortcuts
                    ? await ValidatePlaybackShortcutsAsync(
                        window,
                        videoSurface,
                        seekSlider,
                        volumeSlider,
                        playbackRateText,
                        backend)
                    : null;
                var fullscreenValidation = validateFullscreen
                    ? await ValidateFullscreenAsync(window, videoRegion, videoSurface, volumeSlider, backend)
                    : null;
                var temporaryPlaybackRateValidation = validateGestures
                    ? await ValidateTemporaryPlaybackRateGestureAsync(
                        window,
                        videoRegion,
                        videoSurface,
                        backend)
                    : null;

                var volumeValidation = ValidateVolumeControls(
                    volumeSlider,
                    muteButton,
                    volumeText,
                    videoSurface,
                    backend);
                var audibleValidation = Environment.GetEnvironmentVariable("BOK_AUDIBLE_VOLUME_VALIDATION") == "1"
                    ? await ValidateAudibleVolumeAsync(backend)
                    : null;

                var report = new
                {
                    success = true,
                    video = Path.GetFullPath(args[0]),
                    muted = backend.IsMuted,
                    processStartToWindowLoadedMs = windowLoadedMilliseconds,
                    openToTimelineReadyMs = openMetrics.TimelineReadyMilliseconds,
                    openToAudioOutputReadyMs = openMetrics.AudioOutputReadyMilliseconds,
                    seek50PercentMs = seek50Milliseconds,
                    seek90PercentMs = seek90Milliseconds,
                    lengthMilliseconds,
                    playbackRateValidation,
                    shortcutValidation,
                    fullscreenValidation,
                    temporaryPlaybackRateValidation,
                    volumeValidation,
                    audibleValidation,
                    audioDiagnostics = backend.AudioDiagnostics,
                };
                WriteReport(args[1], report);
                Console.WriteLine(JsonSerializer.Serialize(report));
                exitCode = 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                foreach (var diagnosticEvent in diagnosticLog.Events)
                {
                    Console.Error.WriteLine(
                        $"{diagnosticEvent.Severity} {diagnosticEvent.EventName}: " +
                        $"{diagnosticEvent.Message} {diagnosticEvent.Exception}");
                }
            }
            finally
            {
                window.Close();
            }
        };

        application.Run(window);
        var shutdownDiagnostics = backend.AudioDiagnostics;
        if (shutdownDiagnostics.RenderThreadAlive || shutdownDiagnostics.Failed)
        {
            Console.Error.WriteLine(
                $"Audio shutdown failed: threadAlive={shutdownDiagnostics.RenderThreadAlive}, failed={shutdownDiagnostics.Failed}.");
            return 1;
        }

        if (validateProfiles && exitCode == 0)
        {
            using var verifier = new VideoProfileRepository(profileFilePath);
            var loaded = verifier.LoadAsync().GetAwaiter().GetResult();
            Ensure(loaded.Warning is null, loaded.Warning ?? "Final video profile load failed.");
            Ensure(verifier.TryGet(args[0], out var finalProfile), "Final video profile was not saved.");
            Ensure(finalProfile.VolumePercent == 180, "Normal-close profile save did not persist 180%.");
            Ensure(finalProfile.IsMuted, "Normal-close profile save did not preserve mute.");
            var report = new
            {
                success = true,
                video = Path.GetFullPath(args[0]),
                profileFilePath,
                saveDelaySeconds = MainWindow.VideoProfileSaveDelay.TotalSeconds,
                delayedSave = profileDelayValidation,
                normalClose = new
                {
                    volumePercent = finalProfile.VolumePercent,
                    muted = finalProfile.IsMuted,
                },
                audioDiagnostics = shutdownDiagnostics,
            };
            WriteReport(args[1], report);
            Console.WriteLine(JsonSerializer.Serialize(report));
        }

        if (validateStartPositions && exitCode == 0)
        {
            using var verifier = new VideoProfileRepository(profileFilePath);
            var loaded = verifier.LoadAsync().GetAwaiter().GetResult();
            Ensure(loaded.Warning is null, loaded.Warning ?? "Final start-position profile load failed.");
            Ensure(verifier.TryGet(args[0], out var finalProfile), "Final start-position profile was not saved.");
            Ensure(
                finalProfile.StartPositionMilliseconds == 15_000,
                "Normal close replaced the registered start position with the last playback position.");
            var report = new
            {
                success = true,
                video = Path.GetFullPath(args[0]),
                profileFilePath,
                startPositionValidation,
                finalRegisteredStartPositionMilliseconds = finalProfile.StartPositionMilliseconds,
                lastPlaybackPositionWasNotSaved = true,
                audioDiagnostics = shutdownDiagnostics,
            };
            WriteReport(args[1], report);
            Console.WriteLine(JsonSerializer.Serialize(report));
        }

        if (validateFileDrop && exitCode == 0)
        {
            var report = new
            {
                success = true,
                video = Path.GetFullPath(args[0]),
                fileDropValidation,
                audioDiagnostics = shutdownDiagnostics,
            };
            WriteReport(args[1], report);
            Console.WriteLine(JsonSerializer.Serialize(report));
        }

        if (validateRecentFiles && exitCode == 0)
        {
            using var verifier = new RecentFileRepository(recentFilePath);
            var loaded = verifier.LoadAsync().GetAwaiter().GetResult();
            Ensure(loaded.Warning is null, loaded.Warning ?? "Final recent-file history load failed.");
            Ensure(verifier.GetFiles().Count == 0, "Clear history was not persisted.");
            var report = new
            {
                success = true,
                video = Path.GetFullPath(args[0]),
                recentFilePath,
                recentFileValidation,
                finalHistoryCount = verifier.GetFiles().Count,
                audioDiagnostics = shutdownDiagnostics,
            };
            WriteReport(args[1], report);
            Console.WriteLine(JsonSerializer.Serialize(report));
        }

        if (validatePlaylist && exitCode == 0)
        {
            using var verifier = new PlaylistRepository(playlistFilePath);
            var loaded = verifier.LoadAsync().GetAwaiter().GetResult();
            Ensure(loaded.Warning is null, loaded.Warning ?? "Final playlist load failed.");
            var snapshot = verifier.GetSnapshot();
            Ensure(snapshot.Entries.Count == 3, "Final playlist removal and reorder were not persisted.");
            Ensure(!snapshot.Loop, "Final playlist loop state was not persisted as off.");
            var report = new
            {
                success = true,
                video = Path.GetFullPath(args[0]),
                playlistFilePath,
                playlistValidation,
                finalEntryCount = snapshot.Entries.Count,
                finalLoop = snapshot.Loop,
                audioDiagnostics = shutdownDiagnostics,
            };
            WriteReport(args[1], report);
            Console.WriteLine(JsonSerializer.Serialize(report));
        }

        if (validateThumbnailSettings && exitCode == 0)
        {
            using var verifier = new AppSettingsRepository(appSettingsFilePath);
            var loaded = verifier.LoadAsync().GetAwaiter().GetResult();
            Ensure(loaded.Warning is null, loaded.Warning ?? "Final thumbnail settings load failed.");
            Ensure(
                verifier.GetSnapshot().ThumbnailIntervalPercent == 2.25,
                "Final thumbnail interval was not persisted.");
            var report = new
            {
                success = true,
                video = Path.GetFullPath(args[0]),
                appSettingsFilePath,
                thumbnailSettingsValidation,
                finalThumbnailIntervalPercent = verifier.GetSnapshot().ThumbnailIntervalPercent,
                audioDiagnostics = shutdownDiagnostics,
            };
            WriteReport(args[1], report);
            Console.WriteLine(JsonSerializer.Serialize(report));
        }

        if (validateThumbnailHover && exitCode == 0)
        {
            var report = new
            {
                success = true,
                video = Path.GetFullPath(args[0]),
                thumbnailHoverValidation,
                audioDiagnostics = shutdownDiagnostics,
            };
            WriteReport(args[1], report);
            Console.WriteLine(JsonSerializer.Serialize(report));
        }

        return exitCode;
    }

    private static async Task<int> RunThumbnailWorkerValidationAsync(
        string videoPath,
        string reportPath)
    {
        var fullVideoPath = Path.GetFullPath(videoPath);
        var errors = new ConcurrentQueue<string>();
        const long durationHint = 60_000;

        var generationFailures = new ConcurrentQueue<string>();
        await using var service = new ThumbnailGenerationService(
            exception => errors.Enqueue(exception.Message));
        service.GenerationFailed += (_, eventArgs) =>
            generationFailures.Enqueue(eventArgs.Exception.Message);

        var cancellationClock = Stopwatch.StartNew();
        var cancelledRun = service.StartSession(fullVideoPath, durationHint, 0.25);
        await WaitUntilAsync(
            () => cancelledRun.Count >= 1,
            TimeSpan.FromSeconds(10),
            "The first thumbnail session did not produce a frame.");
        var priorityClock = Stopwatch.StartNew();
        cancelledRun.RequestPriority(45_000);
        await WaitUntilAsync(
            () => cancelledRun.TryGet(45_000, out _),
            TimeSpan.FromSeconds(10),
            "The priority thumbnail was not generated.");
        var priorityMilliseconds = priorityClock.Elapsed.TotalMilliseconds;
        var frameCountAtPriority = cancelledRun.Count;
        Ensure(frameCountAtPriority <= 4,
            $"The priority thumbnail did not overtake background generation: {frameCountAtPriority} frames.");
        Console.WriteLine("First thumbnail received; replacing session.");
        var completedRun = service.StartSession(fullVideoPath, durationHint, 5);
        await AssertCanceledAsync(cancelledRun.Completion, TimeSpan.FromSeconds(5));
        var cancellationMilliseconds = cancellationClock.Elapsed.TotalMilliseconds;
        Console.WriteLine($"First session cancelled in {cancellationMilliseconds:0.0} ms.");
        await completedRun.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        Console.WriteLine("Replacement thumbnail session completed.");

        var frames = completedRun.GetSnapshot();
        Ensure(frames.Count == 21, $"Expected 21 cached thumbnails, got {frames.Count}.");
        Ensure(frames.All(frame => frame.Width == 320), "A cached thumbnail had an unexpected width.");
        Ensure(frames.All(frame => frame.BgraPixels.Length == frame.Stride * frame.Height),
            "A cached thumbnail had an invalid BGRA buffer length.");
        Ensure(frames.Select(frame => Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(frame.BgraPixels)))
            .Distinct(StringComparer.Ordinal)
            .Count() > 1,
            "The generated thumbnail cache did not contain distinct images.");
        Ensure(generationFailures.IsEmpty, string.Join(" | ", generationFailures));

        var stopClock = Stopwatch.StartNew();
        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var stopMilliseconds = stopClock.Elapsed.TotalMilliseconds;
        Ensure(errors.IsEmpty, string.Join(" | ", errors));

        var report = new
        {
            success = true,
            video = fullVideoPath,
            durationHintMilliseconds = durationHint,
            priorityTargetMilliseconds = 45_000,
            priorityMilliseconds,
            frameCountAtPriority,
            cancelledGenerationId = cancelledRun.GenerationId,
            cancelledFrameCount = cancelledRun.Count,
            cancellationMilliseconds,
            completedGenerationId = completedRun.GenerationId,
            completedFrameCount = frames.Count,
            firstTargetMilliseconds = frames.First().TargetMilliseconds,
            lastTargetMilliseconds = frames.Last().TargetMilliseconds,
            width = frames.First().Width,
            height = frames.First().Height,
            stopMilliseconds,
            generationFailureCount = generationFailures.Count,
            callbackFailureCount = errors.Count,
        };
        WriteReport(reportPath, report);
        Console.WriteLine(JsonSerializer.Serialize(report));
        return 0;
    }

    private static async Task AssertCanceledAsync(Task task, TimeSpan timeout)
    {
        try
        {
            await task.WaitAsync(timeout);
            throw new InvalidOperationException("The replaced thumbnail session completed instead of cancelling.");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task<ThumbnailHoverValidation> ValidateThumbnailHoverAsync(
        MainWindow window,
        Slider seekSlider,
        long durationMilliseconds)
    {
        var popup = (Popup)window.FindName("SeekThumbnailPopup");
        var previewImage = (Image)window.FindName("ThumbnailPreviewImage");
        var loadingOverlay = (Border)window.FindName("ThumbnailLoadingOverlay");
        var timeText = (TextBlock)window.FindName("ThumbnailTimeText");
        var run = window.ThumbnailGenerationRun ??
                  throw new InvalidOperationException("The product thumbnail session was not started.");
        seekSlider.UpdateLayout();
        Ensure(seekSlider.ActualWidth > 240, "The seek slider was too narrow for popup validation.");

        var pointerX = seekSlider.ActualWidth * 0.77;
        var positionMilliseconds = SeekUiGeometry.PositionFromPointer(
            pointerX,
            seekSlider.ActualWidth,
            durationMilliseconds);
        var targetMilliseconds = SeekUiGeometry.ThumbnailTargetMilliseconds(
            positionMilliseconds,
            durationMilliseconds,
            run.IntervalPercent);
        var priorityClock = Stopwatch.StartNew();
        window.UpdateSeekThumbnail(pointerX);
        var initiallyLoading = loadingOverlay.Visibility == Visibility.Visible;
        Ensure(popup.IsOpen, "The seek thumbnail popup did not open.");
        Ensure(!popup.IsHitTestVisible && !popup.Focusable,
            "The seek thumbnail popup must not accept mouse or focus input.");
        Ensure(Math.Abs(popup.VerticalOffset - -183) < 0.1,
            $"Unexpected thumbnail popup vertical offset: {popup.VerticalOffset}.");
        Ensure(timeText.Text == PlaybackTimelinePresentation.FormatMilliseconds((long)positionMilliseconds),
            "The thumbnail popup time did not match the hover position.");

        await WaitUntilAsync(
            () => run.TryGet(targetMilliseconds, out _) && previewImage.Source is not null,
            TimeSpan.FromSeconds(10),
            "The hover-priority frame was not displayed.");
        var priorityDisplayMilliseconds = priorityClock.Elapsed.TotalMilliseconds;
        Ensure(run.TryGet(targetMilliseconds, out var frame),
            "The displayed hover frame was not present in the session cache.");
        Ensure(frame!.Width == 320 && frame.Height == 180,
            $"Unexpected hover frame size: {frame.Width}x{frame.Height}.");
        Ensure(loadingOverlay.Visibility == Visibility.Collapsed,
            "The loading overlay remained visible after the frame arrived.");

        window.UpdateSeekThumbnail(0);
        var leftOffset = popup.HorizontalOffset;
        window.UpdateSeekThumbnail(seekSlider.ActualWidth);
        var rightOffset = popup.HorizontalOffset;
        Ensure(Math.Abs(leftOffset) < 0.1, $"Left popup clamp was {leftOffset}.");
        Ensure(Math.Abs(rightOffset - (seekSlider.ActualWidth - 240)) < 0.1,
            $"Right popup clamp was {rightOffset}.");
        window.CloseSeekThumbnail();
        Ensure(!popup.IsOpen, "The seek thumbnail popup did not close.");

        return new ThumbnailHoverValidation(
            initiallyLoading,
            targetMilliseconds,
            priorityDisplayMilliseconds,
            run.Count,
            frame.Width,
            frame.Height,
            popup.VerticalOffset,
            leftOffset,
            rightOffset,
            popup.IsHitTestVisible,
            popup.IsOpen);
    }

    private static async Task<ThumbnailSettingsValidation> ValidateThumbnailSettingsAsync(
        MainWindow window,
        LibVlcPlaybackBackend backend,
        AppSettingsRepository repository,
        string videoPath)
    {
        var menuItem = (MenuItem)window.FindName("ThumbnailSettingsMenuItem");
        Ensure(menuItem.IsEnabled, "Thumbnail settings menu was not enabled.");
        Ensure(window.ThumbnailIntervalPercent == 1.25, "Persisted thumbnail interval was not restored.");
        Ensure(
            window.ThumbnailSession is { IntervalPercent: 1.25 },
            "The initial video did not freeze the restored thumbnail interval.");

        double dialogWidth = 0;
        double dialogHeight = 0;
        _ = window.Dispatcher.BeginInvoke(() =>
        {
            var dialog = Application.Current.Windows.OfType<ThumbnailSettingsWindow>().Single();
            dialogWidth = dialog.Width;
            dialogHeight = dialog.Height;
            Ensure(
                dialogWidth == 420 && dialogHeight == 230,
                "Thumbnail settings dialog dimensions changed from the approved UI.");
            var slider = (Slider)dialog.FindName("IntervalSlider");
            slider.Value = 2.25;
            Ensure(
                ((TextBlock)dialog.FindName("IntervalValueText")).Text == "2.25%",
                "Thumbnail interval text did not follow the quarter-percent slider value.");
            ((Button)dialog.FindName("OkButton")).RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent));
        });
        menuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, menuItem));
        await WaitUntilAsync(
            () => repository.GetSnapshot().ThumbnailIntervalPercent == 2.25 && menuItem.IsEnabled,
            TimeSpan.FromSeconds(5),
            "Thumbnail interval was not saved from the product dialog.");
        Ensure(
            window.ThumbnailSession is { IntervalPercent: 1.25 },
            "Changing thumbnail settings altered the current video session.");

        Ensure(await window.OpenVideoAsync(videoPath), "Could not reopen the video for thumbnail session validation.");
        Ensure(
            window.ThumbnailSession is { IntervalPercent: 2.25 } session &&
            string.Equals(
                session.VideoPath,
                Path.GetFullPath(videoPath),
                StringComparison.OrdinalIgnoreCase),
            "The next video did not freeze the updated thumbnail interval.");

        _ = window.Dispatcher.BeginInvoke(() =>
        {
            var dialog = Application.Current.Windows.OfType<ThumbnailSettingsWindow>().Single();
            ((Slider)dialog.FindName("IntervalSlider")).Value = 3.5;
            dialog.DialogResult = false;
        });
        menuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, menuItem));
        Ensure(
            repository.GetSnapshot().ThumbnailIntervalPercent == 2.25 &&
            window.ThumbnailIntervalPercent == 2.25,
            "Cancelling thumbnail settings changed the saved value.");

        return new ThumbnailSettingsValidation(
            InitialIntervalPercent: 1.25,
            SavedIntervalPercent: 2.25,
            CurrentSessionStayedAtInitialValue: true,
            NextOpenUsedSavedValue: true,
            CancelPreservedSavedValue: true,
            DialogWidth: dialogWidth,
            DialogHeight: dialogHeight,
            FinalMuted: backend.IsMuted);
    }

    private static async Task<PlaylistValidation> ValidatePlaylistAsync(
        MainWindow window,
        LibVlcPlaybackBackend backend,
        PlaylistRepository repository,
        RecordingNotificationSink notificationSink,
        RecordingDiagnosticLog diagnosticLog,
        string manualOpenPath)
    {
        var playlistMenu = (MenuItem)window.FindName("PlaylistMenuItem");
        playlistMenu.IsChecked = true;
        await WaitUntilAsync(
            () => Application.Current.Windows.OfType<PlaylistWindow>().Count() == 1,
            TimeSpan.FromSeconds(5),
            "Playlist window was not shown.");
        var playlistWindow = Application.Current.Windows.OfType<PlaylistWindow>().Single();
        var playlistList = (ListBox)playlistWindow.FindName("PlaylistList");
        var loopToggle = (System.Windows.Controls.Primitives.ToggleButton)playlistWindow.FindName("LoopToggle");
        var addCurrentButton = (Button)playlistWindow.FindName("AddCurrentButton");
        var removeSelectedButton = (Button)playlistWindow.FindName("RemoveSelectedButton");
        var notificationText = (TextBlock)playlistWindow.FindName("NotificationText");
        var entries = playlistList.Items.Cast<PlaylistEntryPresentation>().ToArray();
        Ensure(playlistWindow.Width == 680 && playlistWindow.Height == 540, "Playlist window initial size changed.");
        Ensure(playlistWindow.MinWidth == 560 && playlistWindow.MinHeight == 400, "Playlist window minimum size changed.");
        Ensure(entries.Length == 3, "Persisted playlist entries were not displayed.");
        Ensure(entries.Select(entry => entry.Order).SequenceEqual([1, 2, 3]), "Playlist order was not displayed.");
        Ensure(entries[0].Path == entries[2].Path, "Duplicate playlist entries were not preserved.");
        Ensure(!entries[0].IsMissing && entries[1].IsMissing && !entries[2].IsMissing, "Missing playlist state was incorrect.");
        Ensure(loopToggle.IsChecked == true, "Persisted loop-on state was not restored.");
        Ensure(Equals(loopToggle.Content, "↻  ループ ON"), "Restored loop label was incorrect.");
        Ensure(addCurrentButton.IsEnabled, "Add-current must be enabled while a video is open.");

        addCurrentButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, addCurrentButton));
        await WaitUntilAsync(
            () => repository.GetSnapshot().Entries.Count == 4 && addCurrentButton.IsEnabled,
            TimeSpan.FromSeconds(5),
            "Current video was not appended to the playlist.");
        Ensure(
            repository.GetSnapshot().Entries.Count(path =>
                string.Equals(path, entries[0].Path, StringComparison.OrdinalIgnoreCase)) == 3,
            "Adding the current video did not preserve a third duplicate entry.");

        var unsupportedPath = Path.Combine(Path.GetDirectoryName(entries[0].Path)!, "ignored.txt");
        var dragOver = RaiseFileDragEvent(
            playlistWindow,
            DragDrop.PreviewDragOverEvent,
            [entries[0].Path, unsupportedPath]);
        Ensure(
            dragOver.Handled && dragOver.Effects == DragDropEffects.Copy,
            "Mixed playlist drop did not advertise Copy.");
        var drop = RaiseFileDragEvent(
            playlistWindow,
            DragDrop.PreviewDropEvent,
            [entries[0].Path, unsupportedPath]);
        Ensure(drop.Handled, "Playlist drop was not handled.");
        await WaitUntilAsync(
            () => repository.GetSnapshot().Entries.Count == 5 && addCurrentButton.IsEnabled,
            TimeSpan.FromSeconds(5),
            "Supported item from a mixed playlist drop was not appended.");
        Ensure(
            notificationText.Text.Contains("1件を追加", StringComparison.Ordinal) &&
            notificationText.Text.Contains("1件は追加しませんでした", StringComparison.Ordinal),
            "Mixed playlist drop summary was incorrect.");

        var sourceVideoPath = entries[0].Path;
        var entriesBeforeRemoval = playlistList.Items.Cast<PlaylistEntryPresentation>().ToArray();
        playlistList.SelectedItems.Add(entriesBeforeRemoval[2]);
        playlistList.SelectedItems.Add(entriesBeforeRemoval[3]);
        Ensure(removeSelectedButton.IsEnabled, "Remove-selected was not enabled for multiple selection.");
        removeSelectedButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, removeSelectedButton));
        await WaitUntilAsync(
            () => repository.GetSnapshot().Entries.Count == 3 &&
                  notificationText.Text.Contains("元の動画ファイルは削除していません", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5),
            "Multiple selected playlist registrations were not removed.");
        Ensure(File.Exists(sourceVideoPath), "Removing a playlist registration deleted the source video.");
        Ensure(
            notificationText.Text.Contains("2件をプレイリストから削除", StringComparison.Ordinal) &&
            notificationText.Text.Contains("元の動画ファイルは削除していません", StringComparison.Ordinal),
            "Playlist removal notification did not explain source-file preservation.");

        var orderBeforeMove = repository.GetSnapshot().Entries.ToArray();
        var firstEntry = playlistList.Items.Cast<PlaylistEntryPresentation>().First();
        var moveOver = RaisePlaylistDragEvent(playlistList, DragDrop.DragOverEvent, firstEntry);
        Ensure(
            moveOver.Handled && moveOver.Effects == DragDropEffects.Move,
            "Playlist reorder did not advertise Move.");
        var moveDrop = RaisePlaylistDragEvent(playlistList, DragDrop.DropEvent, firstEntry);
        Ensure(moveDrop.Handled, "Playlist reorder drop was not handled.");
        var expectedMovedOrder = orderBeforeMove.Skip(1).Append(orderBeforeMove[0]).ToArray();
        await WaitUntilAsync(
            () => repository.GetSnapshot().Entries.SequenceEqual(expectedMovedOrder) && addCurrentButton.IsEnabled,
            TimeSpan.FromSeconds(5),
            "Playlist item was not moved to the requested insertion index.");
        Ensure(
            notificationText.Text.Contains("再生順を変更", StringComparison.Ordinal),
            "Playlist reorder completion was not announced.");

        loopToggle.IsChecked = false;
        await WaitUntilAsync(
            () => !repository.GetSnapshot().Loop && loopToggle.IsEnabled,
            TimeSpan.FromSeconds(5),
            "Loop-off state was not saved before end-of-list validation.");

        var corruptPath = Path.Combine(
            Path.GetDirectoryName(repository.FilePath)!,
            "unsupported-playlist.mkv");
        await File.WriteAllTextAsync(corruptPath, "not a media file");
        Ensure((await repository.AddEntriesAsync([corruptPath])).Success, "Could not add corrupt playlist fixture.");
        Ensure(
            (await repository.MoveToInsertionIndexAsync(3, 1)).Success,
            "Could not position corrupt playlist fixture.");
        playlistWindow.CompletePersistence(repository.GetSnapshot().Entries, enablePersistence: true);
        var playPlaylistButton = (Button)playlistWindow.FindName("PlayPlaylistButton");
        Ensure(playPlaylistButton.IsEnabled, "Playlist play was not enabled with playable entries.");
        var notificationCountBeforePlayback = notificationSink.Notifications.Count;
        playPlaylistButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, playPlaylistButton));
        await WaitUntilAsync(
            () =>
            {
                var playbackEntries = playlistList.Items.Cast<PlaylistEntryPresentation>().ToArray();
                return playbackEntries.Length == 4 &&
                       playbackEntries[1].HasLoadError &&
                       playbackEntries[2].IsCurrent &&
                       backend.IsPlaying &&
                       playPlaylistButton.IsEnabled;
            },
            TimeSpan.FromSeconds(10),
            "Playlist playback did not skip missing and unreadable entries.");
        Ensure(
            notificationSink.Notifications.Count == notificationCountBeforePlayback,
            "A skipped playlist load error was shown as a user error.");
        Ensure(
            diagnosticLog.Events.Any(item =>
                item.EventName.StartsWith("playlist-playback-", StringComparison.Ordinal) &&
                string.Equals(item.TargetPath, corruptPath, StringComparison.OrdinalIgnoreCase)),
            "A skipped playlist load error was not recorded in diagnostics.");

        backend.Seek(0.995);
        await WaitUntilAsync(
            () => playlistList.Items.Cast<PlaylistEntryPresentation>().ElementAt(3).IsCurrent &&
                  playPlaylistButton.IsEnabled,
            TimeSpan.FromSeconds(10),
            "Natural end did not advance to the next duplicate registration.");
        backend.Seek(0.995);
        await WaitUntilAsync(
            () => playlistList.Items.Cast<PlaylistEntryPresentation>().All(entry => !entry.IsCurrent) &&
                  playPlaylistButton.IsEnabled &&
                  notificationText.Text.Contains("末尾に到達", StringComparison.Ordinal),
            TimeSpan.FromSeconds(10),
            "Playlist playback did not stop and announce the end of the list.");

        playlistList.UpdateLayout();
        playlistList.ScrollIntoView(playlistList.Items[3]);
        playlistList.UpdateLayout();
        var lastItem = (ListBoxItem?)playlistList.ItemContainerGenerator.ContainerFromIndex(3)
            ?? throw new InvalidOperationException("The last playlist item was not realized.");
        RaiseDoubleClick(playlistList, lastItem);
        await WaitUntilAsync(
            () => playlistList.Items.Cast<PlaylistEntryPresentation>().ElementAt(3).IsCurrent &&
                  playPlaylistButton.IsEnabled,
            TimeSpan.FromSeconds(10),
            "Double-click did not start playback from the selected registration.");
        backend.Seek(0.995);
        await WaitUntilAsync(
            () => playlistList.Items.Cast<PlaylistEntryPresentation>().All(entry => !entry.IsCurrent) &&
                  playPlaylistButton.IsEnabled,
            TimeSpan.FromSeconds(10),
            "Double-click playback did not finish at the end of the list.");

        Ensure((await repository.RemoveAtIndicesAsync([1])).Success, "Could not remove corrupt playlist fixture.");
        playlistWindow.CompletePersistence(repository.GetSnapshot().Entries, enablePersistence: true);

        loopToggle.IsChecked = true;
        await WaitUntilAsync(
            () => repository.GetSnapshot().Loop && loopToggle.IsEnabled,
            TimeSpan.FromSeconds(5),
            "Loop-on state was not saved for wrap validation.");

        Ensure(
            (await repository.ReplaceEntriesAsync([corruptPath])).Success,
            "Could not prepare the no-playable playlist fixture.");
        playlistWindow.CompletePersistence(repository.GetSnapshot().Entries, enablePersistence: true);
        playPlaylistButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, playPlaylistButton));
        await WaitUntilAsync(
            () => playlistList.Items.Cast<PlaylistEntryPresentation>().Single().HasLoadError &&
                  playPlaylistButton.IsEnabled &&
                  notificationText.Text.Contains("再生可能な項目がありません", StringComparison.Ordinal),
            TimeSpan.FromSeconds(10),
            "A looped playlist with no playable entries did not stop with guidance.");
        var noPlayableNotification = notificationText.Text;
        await Task.Delay(500);
        Ensure(
            notificationText.Text == noPlayableNotification &&
            playlistList.Items.Cast<PlaylistEntryPresentation>().All(entry => !entry.IsCurrent),
            "The no-playable playlist did not remain stopped after its single notification.");

        var loopValidationEntries = new[]
        {
            Path.GetFullPath(manualOpenPath),
            expectedMovedOrder[1],
            sourceVideoPath,
        };
        Ensure(
            (await repository.ReplaceEntriesAsync(loopValidationEntries)).Success,
            "Could not prepare distinct playlist entries for loop validation.");
        playlistWindow.CompletePersistence(repository.GetSnapshot().Entries, enablePersistence: true);
        playlistList.UpdateLayout();
        playlistList.ScrollIntoView(playlistList.Items[2]);
        playlistList.UpdateLayout();
        var loopStartItem = (ListBoxItem?)playlistList.ItemContainerGenerator.ContainerFromIndex(2)
            ?? throw new InvalidOperationException("The loop-start playlist item was not realized.");
        RaiseDoubleClick(playlistList, loopStartItem);
        await WaitUntilAsync(
            () => playlistList.Items.Cast<PlaylistEntryPresentation>().ElementAt(2).IsCurrent &&
                  playPlaylistButton.IsEnabled,
            TimeSpan.FromSeconds(10),
            "Loop validation did not start from the final registration.");
        backend.Seek(0.995);
        try
        {
            await WaitUntilAsync(
                () => playlistList.Items.Cast<PlaylistEntryPresentation>().First().IsCurrent &&
                      playPlaylistButton.IsEnabled &&
                      backend.IsPlaying,
                TimeSpan.FromSeconds(10),
                "Loop-on playback did not wrap to the first playable registration.");
        }
        catch (TimeoutException exception)
        {
            var currentFlags = string.Join(
                ",",
                playlistList.Items.Cast<PlaylistEntryPresentation>().Select(entry => entry.IsCurrent));
            throw new TimeoutException(
                $"{exception.Message} loop={repository.GetSnapshot().Loop}, current=[{currentFlags}], " +
                $"playing={backend.IsPlaying}, path={backend.CurrentPath}, notification={notificationText.Text}",
                exception);
        }

        var videoSurface = (FrameworkElement)window.FindName("VideoInteractionSurface");
        var manualOpenDrop = RaiseFileDragEvent(
            videoSurface,
            DragDrop.PreviewDropEvent,
            [Path.GetFullPath(sourceVideoPath)]);
        Ensure(manualOpenDrop.Handled, "The main-window manual-open drop was not handled.");
        await WaitUntilAsync(
            () => string.Equals(
                      backend.CurrentPath,
                      Path.GetFullPath(sourceVideoPath),
                      StringComparison.OrdinalIgnoreCase) &&
                  backend.IsPlaying &&
                  playlistList.Items.Cast<PlaylistEntryPresentation>().All(entry => !entry.IsCurrent),
            TimeSpan.FromSeconds(10),
            "Opening a video from the main window did not cancel continuous playlist playback.");
        backend.Seek(0.995);
        await WaitUntilAsync(
            () => !backend.IsPlaying,
            TimeSpan.FromSeconds(10),
            "The manually opened video did not reach its natural end.");
        Ensure(
            playlistList.Items.Cast<PlaylistEntryPresentation>().All(entry => !entry.IsCurrent),
            "Playlist playback resumed after the manually opened video ended.");

        Ensure(
            (await repository.ReplaceEntriesAsync(expectedMovedOrder)).Success,
            "Could not restore final playlist entries after loop validation.");
        playlistWindow.CompletePersistence(repository.GetSnapshot().Entries, enablePersistence: true);

        loopToggle.IsChecked = false;
        await WaitUntilAsync(
            () => !repository.GetSnapshot().Loop && loopToggle.IsEnabled,
            TimeSpan.FromSeconds(5),
            "Final loop-off state was not saved.");

        playlistWindow.Close();
        await WaitUntilAsync(
            () => Application.Current.Windows.OfType<PlaylistWindow>().Count() == 0 && !playlistMenu.IsChecked,
            TimeSpan.FromSeconds(5),
            "Closing the playlist window did not update the View menu.");
        playlistMenu.IsChecked = true;
        await WaitUntilAsync(
            () => Application.Current.Windows.OfType<PlaylistWindow>().Count() == 1,
            TimeSpan.FromSeconds(5),
            "Playlist window could not be reopened.");
        var reopenedWindow = Application.Current.Windows.OfType<PlaylistWindow>().Single();
        var reopenedLoopToggle =
            (System.Windows.Controls.Primitives.ToggleButton)reopenedWindow.FindName("LoopToggle");
        Ensure(reopenedLoopToggle.IsChecked == false, "Reopened playlist window did not reflect loop off.");
        Ensure(
            ((ListBox)reopenedWindow.FindName("PlaylistList")).Items
                .Cast<PlaylistEntryPresentation>()
                .Select(entry => entry.Path)
                .SequenceEqual(expectedMovedOrder),
            "Reopened playlist window did not reflect removed and reordered entries.");

        return new PlaylistValidation(
            EntryCount: entries.Length,
            CurrentVideoAdded: true,
            MixedDropAddedSupportedOnly: true,
            MultipleSelectionRemoved: true,
            SourceFilesPreserved: true,
            DragReorderPersisted: true,
            PlaySkippedMissingAndLoadError: true,
            NaturalEndAdvanced: true,
            DoubleClickStartedSelected: true,
            EndOfListStopped: true,
            LoopWrappedToFirstPlayable: true,
            NoPlayableStoppedOnce: true,
            MainWindowOpenCanceledContinuousPlayback: true,
            FinalEntryCount: repository.GetSnapshot().Entries.Count,
            DuplicateEntriesPreserved: true,
            MissingEntriesMarked: true,
            InitialLoopRestored: true,
            LoopOffPersisted: true,
            SingleWindowReopened: true,
            InitialWidth: playlistWindow.Width,
            InitialHeight: playlistWindow.Height,
            MinimumWidth: playlistWindow.MinWidth,
            MinimumHeight: playlistWindow.MinHeight);
    }

    private static async Task<RecentFileValidation> ValidateRecentFilesAsync(
        MainWindow window,
        LibVlcPlaybackBackend backend,
        RecentFileRepository repository,
        string secondaryVideoPath)
    {
        var recentMenu = (MenuItem)window.FindName("RecentFilesMenuItem");
        Ensure(recentMenu.Items.Count == 9, "Recent-file menu did not include five files and management commands.");
        var fileItems = recentMenu.Items.OfType<MenuItem>().Take(5).ToArray();
        Ensure(fileItems.Count(item => item.IsEnabled) == 2, "Only existing recent files must be enabled.");
        Ensure(
            fileItems.Count(item => item.Header?.ToString()?.EndsWith("（見つかりません）", StringComparison.Ordinal) == true) == 3,
            "Missing recent files were not labelled.");
        Ensure(
            fileItems.All(item => item.ReadLocalValue(Control.ForegroundProperty) == DependencyProperty.UnsetValue),
            "Dynamic recent-file items must inherit the menu foreground.");

        fileItems[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, fileItems[1]));
        await WaitUntilAsync(
            () => string.Equals(backend.CurrentPath, secondaryVideoPath, StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(10),
            "Selecting an existing recent file did not reopen it.");
        await WaitUntilAsync(
            () => string.Equals(repository.GetFiles()[0], secondaryVideoPath, StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(5),
            "Reopened recent file was not moved to the front.");

        var removeMenu = recentMenu.Items.OfType<MenuItem>()
            .Single(item => Equals(item.Header, "欠損した項目を履歴から削除"));
        var individualRemove = (MenuItem)removeMenu.Items[0];
        var removedPath = individualRemove.ToolTip!.ToString()!;
        individualRemove.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, individualRemove));
        await WaitUntilAsync(
            () =>
                !repository.GetFiles().Contains(removedPath, StringComparer.OrdinalIgnoreCase) &&
                recentMenu.IsEnabled,
            TimeSpan.FromSeconds(5),
            "Individual missing recent file was not removed.");

        removeMenu = recentMenu.Items.OfType<MenuItem>()
            .Single(item => Equals(item.Header, "欠損した項目を履歴から削除"));
        var removeAllMissing = removeMenu.Items.OfType<MenuItem>()
            .Single(item => Equals(item.Header, "すべて削除"));
        removeAllMissing.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, removeAllMissing));
        await WaitUntilAsync(
            () => repository.GetFiles().All(File.Exists) && recentMenu.IsEnabled,
            TimeSpan.FromSeconds(5),
            "Bulk missing-file removal did not preserve only existing paths.");

        var clearHistory = recentMenu.Items.OfType<MenuItem>()
            .Single(item => Equals(item.Header, "履歴をすべて消去"));
        clearHistory.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, clearHistory));
        await WaitUntilAsync(
            () => repository.GetFiles().Count == 0 &&
                  recentMenu.IsEnabled &&
                  recentMenu.Items.Count == 1,
            TimeSpan.FromSeconds(5),
            "Clear history did not remove every path.");
        Ensure(
            recentMenu.Items.Count == 1 &&
            recentMenu.Items[0] is MenuItem { IsEnabled: false } emptyItem &&
            Equals(emptyItem.Header, "（履歴はありません）"),
            "Cleared history did not render the empty-state item.");

        return new RecentFileValidation(
            InitialFileCount: 5,
            EnabledFileCount: 2,
            MissingFileCount: 3,
            ReopenedPath: backend.CurrentPath!,
            IndividualRemovalPersisted: true,
            BulkMissingRemovalPersisted: true,
            ClearHistoryPersisted: true,
            ForegroundInherited: true);
    }

    private static async Task<ProfileDelayValidation> ValidateVideoProfilesAsync(
        Slider volumeSlider,
        LibVlcPlaybackBackend backend,
        string profileFilePath,
        RecordingNotificationSink notificationSink)
    {
        Ensure(backend.VolumePercent == 275, "Saved video volume was not restored before playback.");
        Ensure(backend.IsMuted, "Saved mute state was not restored before playback.");

        volumeSlider.Value = 320;
        Ensure(backend.VolumePercent == 320, "Profile validation slider did not set 320%.");
        await Task.Delay(MainWindow.VideoProfileSaveDelay + TimeSpan.FromMilliseconds(500));

        using (var verifier = new VideoProfileRepository(profileFilePath))
        {
            var loaded = await verifier.LoadAsync();
            Ensure(loaded.Warning is null, loaded.Warning ?? "Delayed video profile load failed.");
            Ensure(verifier.TryGet(backend.CurrentPath!, out var delayedProfile), "Delayed video profile was not saved.");
            Ensure(delayedProfile.VolumePercent == 320, "Delayed video profile did not persist 320%.");
            Ensure(delayedProfile.IsMuted, "Delayed video profile did not preserve mute.");
        }

        var notificationCount = notificationSink.Notifications.Count;
        await using (var lockedProfile = new FileStream(
                         profileFilePath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read))
        {
            volumeSlider.Value = 310;
            await Task.Delay(MainWindow.VideoProfileSaveDelay + TimeSpan.FromMilliseconds(500));
            Ensure(backend.IsPlaying, "Playback stopped after a video profile save failure.");
            Ensure(backend.VolumePercent == 310, "Volume changed after a video profile save failure.");
            Ensure(
                notificationSink.Notifications.Count > notificationCount,
                "A video profile save failure did not surface a warning.");
        }

        volumeSlider.Value = 180;
        Ensure(backend.VolumePercent == 180, "Profile validation slider did not set 180% before close.");
        return new ProfileDelayValidation(
            RestoredVolumePercent: 275,
            RestoredMuted: true,
            SavedVolumePercent: 320,
            SavedMuted: true,
            WriteFailureKeptPlaying: true);
    }

    private static async Task<StartPositionValidation> ValidateStartPositionsAsync(
        MainWindow window,
        FrameworkElement videoSurface,
        Slider seekSlider,
        FrameworkElement startPositionMarker,
        LibVlcPlaybackBackend backend,
        VideoProfileRepository videoProfiles,
        string profileFilePath,
        RecordingNotificationSink notificationSink)
    {
        Ensure(backend.IsMuted, "Start-position validation must remain muted.");
        await WaitUntilAsync(
            () => backend.TimeMilliseconds is >= 11_500 and <= 15_000,
            TimeSpan.FromSeconds(3),
            "The registered 12-second start position was not restored.");
        await WaitUntilAsync(
            () => MarkerMatches(startPositionMarker, 12_000, backend.LengthMilliseconds, seekSlider.ActualWidth),
            TimeSpan.FromSeconds(3),
            "The initial registered start-position marker was not displayed at 12 seconds.");
        backend.Pause();
        var initialRestoredMilliseconds = backend.TimeMilliseconds;
        var initialMarkerOffset = Canvas.GetLeft(startPositionMarker);

        seekSlider.Value = 0.5;
        var registrationTargetMilliseconds = backend.LengthMilliseconds / 2;
        await WaitUntilAsync(
            () => Math.Abs(backend.TimeMilliseconds - registrationTargetMilliseconds) <= 250,
            TimeSpan.FromSeconds(3),
            "Could not prepare the current position for registration.");

        var contextMenu = videoSurface.ContextMenu ??
            throw new InvalidOperationException("The video context menu was not found.");
        var startPositionItem = contextMenu.Items
            .OfType<MenuItem>()
            .Single(item => Equals(item.Header, "現在位置を再生開始位置に設定"));
        contextMenu.PlacementTarget = videoSurface;
        contextMenu.IsOpen = true;
        Ensure(startPositionItem.IsEnabled, "The start-position menu item did not become enabled.");
        contextMenu.IsOpen = false;
        var notificationCount = notificationSink.Notifications.Count;
        startPositionItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await WaitUntilAsync(
            () => videoProfiles.TryGet(backend.CurrentPath!, out var profile) &&
                  profile.StartPositionMilliseconds is not null &&
                  Math.Abs(profile.StartPositionMilliseconds.Value - registrationTargetMilliseconds) <= 250,
            TimeSpan.FromSeconds(3),
            "The menu command did not update the registered start position.");
        await WaitUntilAsync(
            () => MarkerMatches(
                startPositionMarker,
                registrationTargetMilliseconds,
                backend.LengthMilliseconds,
                seekSlider.ActualWidth,
                toleranceMilliseconds: 250),
            TimeSpan.FromSeconds(3),
            "The start-position marker did not move immediately after registration.");
        var registeredMarkerOffset = Canvas.GetLeft(startPositionMarker);
        var notificationText = (TextBlock)window.FindName("NotificationMessageText");
        await WaitUntilAsync(
            () => notificationText.Text.StartsWith("再生開始位置を ", StringComparison.Ordinal),
            TimeSpan.FromSeconds(3),
            "The registration confirmation was not shown.");
        var persistedRegistrationMilliseconds = await WaitForPersistedStartPositionAsync(
            profileFilePath,
            backend.CurrentPath!,
            registrationTargetMilliseconds,
            toleranceMilliseconds: 250);
        Ensure(
            notificationSink.Notifications.Count == notificationCount,
            "Successful start-position registration emitted an error notification.");

        var outOfRangeMilliseconds = backend.LengthMilliseconds + 1_000;
        videoProfiles.SetStartPosition(backend.CurrentPath!, outOfRangeMilliseconds);
        Ensure((await videoProfiles.SaveAsync()).Success, "Could not save the out-of-range validation profile.");
        await window.OpenVideoAsync(backend.CurrentPath!);
        await Task.Delay(300);
        Ensure(
            backend.TimeMilliseconds <= 2_000,
            "An out-of-range registered position did not fall back to the beginning.");
        Ensure(
            startPositionMarker.Visibility == Visibility.Collapsed,
            "An out-of-range registered start-position marker remained visible.");
        var outOfRangeFallbackMilliseconds = backend.TimeMilliseconds;

        videoProfiles.SetStartPosition(backend.CurrentPath!, 15_000);
        Ensure((await videoProfiles.SaveAsync()).Success, "Could not save the final 15-second start position.");
        await window.OpenVideoAsync(backend.CurrentPath!);
        await WaitUntilAsync(
            () => backend.TimeMilliseconds is >= 14_500 and <= 18_000,
            TimeSpan.FromSeconds(3),
            "The final 15-second start position was not restored.");
        await WaitUntilAsync(
            () => MarkerMatches(startPositionMarker, 15_000, backend.LengthMilliseconds, seekSlider.ActualWidth),
            TimeSpan.FromSeconds(3),
            "The final registered start-position marker was not displayed at 15 seconds.");
        var finalRestoredMilliseconds = backend.TimeMilliseconds;
        var markerOffsetBeforeResize = Canvas.GetLeft(startPositionMarker);
        var seekWidthBeforeResize = seekSlider.ActualWidth;
        window.Width += 160;
        window.UpdateLayout();
        await WaitUntilAsync(
            () => seekSlider.ActualWidth > seekWidthBeforeResize + 100 &&
                  MarkerMatches(startPositionMarker, 15_000, backend.LengthMilliseconds, seekSlider.ActualWidth),
            TimeSpan.FromSeconds(3),
            "The registered start-position marker did not follow the resized seek bar.");
        var markerOffsetAfterResize = Canvas.GetLeft(startPositionMarker);
        var seekWidthAfterResize = seekSlider.ActualWidth;
        Ensure(
            markerOffsetAfterResize > markerOffsetBeforeResize,
            "The registered start-position marker offset did not change after resizing.");

        backend.Pause();
        seekSlider.Value = 0.75;
        await WaitUntilAsync(
            () => Math.Abs(backend.TimeMilliseconds - (backend.LengthMilliseconds * 0.75)) <= 250,
            TimeSpan.FromSeconds(3),
            "Could not move away from the registered position before normal close.");
        var lastPlaybackPositionMilliseconds = backend.TimeMilliseconds;

        return new StartPositionValidation(
            InitialRegisteredMilliseconds: 12_000,
            InitialRestoredMilliseconds: initialRestoredMilliseconds,
            InitialMarkerOffset: initialMarkerOffset,
            PersistedRegistrationMilliseconds: persistedRegistrationMilliseconds,
            RegisteredMarkerOffset: registeredMarkerOffset,
            OutOfRangeRegisteredMilliseconds: outOfRangeMilliseconds,
            OutOfRangeFallbackMilliseconds: outOfRangeFallbackMilliseconds,
            OutOfRangeMarkerHidden: true,
            FinalRegisteredMilliseconds: 15_000,
            FinalRestoredMilliseconds: finalRestoredMilliseconds,
            SeekWidthBeforeResize: seekWidthBeforeResize,
            MarkerOffsetBeforeResize: markerOffsetBeforeResize,
            SeekWidthAfterResize: seekWidthAfterResize,
            MarkerOffsetAfterResize: markerOffsetAfterResize,
            LastPlaybackPositionMilliseconds: lastPlaybackPositionMilliseconds,
            ConfirmationMessage: notificationText.Text,
            FinalMuted: backend.IsMuted);
    }

    private static bool MarkerMatches(
        FrameworkElement marker,
        long positionMilliseconds,
        long durationMilliseconds,
        double trackWidth,
        long toleranceMilliseconds = 0)
    {
        if (marker.Visibility != Visibility.Visible || durationMilliseconds <= 0 || trackWidth <= 0)
        {
            return false;
        }

        var markerOffset = Canvas.GetLeft(marker);
        if (!double.IsFinite(markerOffset))
        {
            return false;
        }

        var minimumPosition = Math.Max(0, positionMilliseconds - toleranceMilliseconds);
        var maximumPosition = Math.Min(durationMilliseconds, positionMilliseconds + toleranceMilliseconds);
        var minimumOffset = ExpectedMarkerOffset(minimumPosition, durationMilliseconds, trackWidth, marker.Width);
        var maximumOffset = ExpectedMarkerOffset(maximumPosition, durationMilliseconds, trackWidth, marker.Width);
        return markerOffset >= minimumOffset - 1 && markerOffset <= maximumOffset + 1;
    }

    private static double ExpectedMarkerOffset(
        long positionMilliseconds,
        long durationMilliseconds,
        double trackWidth,
        double markerWidth)
    {
        var center = Math.Clamp((double)positionMilliseconds / durationMilliseconds, 0, 1) * trackWidth;
        return Math.Clamp(center - (markerWidth / 2), 0, Math.Max(0, trackWidth - markerWidth));
    }

    private static async Task<long> WaitForPersistedStartPositionAsync(
        string profileFilePath,
        string videoPath,
        long expectedMilliseconds,
        long toleranceMilliseconds)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            using var verifier = new VideoProfileRepository(profileFilePath);
            var loaded = await verifier.LoadAsync();
            if (loaded.Warning is null &&
                verifier.TryGet(videoPath, out var profile) &&
                profile.StartPositionMilliseconds is { } startPositionMilliseconds &&
                Math.Abs(startPositionMilliseconds - expectedMilliseconds) <= toleranceMilliseconds)
            {
                return startPositionMilliseconds;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("The registered start position was not persisted within three seconds.");
    }

    private static async Task<FileDropValidation> ValidateFileDropAsync(
        MainWindow window,
        FrameworkElement videoSurface,
        Slider seekSlider,
        LibVlcPlaybackBackend backend,
        string videoPath)
    {
        Ensure(backend.IsMuted, "File-drop validation must remain muted.");
        var overlay = (FrameworkElement)window.FindName("DropTargetOverlay");
        var dropTargetText = (TextBlock)window.FindName("DropTargetText");
        var notificationText = (TextBlock)window.FindName("NotificationMessageText");

        var supportedEnter = RaiseFileDragEvent(
            videoSurface,
            DragDrop.PreviewDragEnterEvent,
            [Path.GetFullPath(videoPath)]);
        Ensure(supportedEnter.Handled, "The supported-file drag-enter event was not handled.");
        Ensure(supportedEnter.Effects == DragDropEffects.Copy, "A supported file did not advertise the Copy effect.");
        Ensure(overlay.Visibility == Visibility.Visible, "The approved file-drop overlay was not visible.");
        Ensure(dropTargetText.Text == "動画をドロップして開く", "The supported-file drop feedback was incorrect.");

        var dragLeave = RaiseFileDragEvent(
            videoSurface,
            DragDrop.PreviewDragLeaveEvent,
            [Path.GetFullPath(videoPath)]);
        Ensure(dragLeave.Handled, "The drag-leave event was not handled.");
        Ensure(overlay.Visibility == Visibility.Collapsed, "The file-drop overlay remained visible after drag leave.");

        backend.Pause();
        seekSlider.Value = 0.4;
        var preservedTargetMilliseconds = backend.LengthMilliseconds * 0.4;
        await WaitUntilAsync(
            () => Math.Abs(backend.TimeMilliseconds - preservedTargetMilliseconds) <= 250,
            TimeSpan.FromSeconds(3),
            "Could not prepare playback state for rejected file drops.");
        var originalPath = backend.CurrentPath;
        var originalRate = backend.Rate;
        var positionBeforeRejectedDrops = backend.TimeMilliseconds;

        var multipleDrop = RaiseFileDragEvent(
            videoSurface,
            DragDrop.PreviewDropEvent,
            [Path.GetFullPath(videoPath), Path.GetFullPath(videoPath)]);
        Ensure(multipleDrop.Handled, "The multiple-file drop was not handled.");
        Ensure(
            notificationText.Text == "メインウィンドウでは1ファイルだけ指定してください。",
            "The multiple-file guidance was not shown.");
        Ensure(backend.CurrentPath == originalPath, "A multiple-file drop changed the current video.");
        Ensure(!backend.IsPlaying, "A multiple-file drop changed the paused playback state.");
        Ensure(
            Math.Abs(backend.TimeMilliseconds - positionBeforeRejectedDrops) <= 250,
            "A multiple-file drop changed the playback position.");

        var unsupportedDrop = RaiseFileDragEvent(
            videoSurface,
            DragDrop.PreviewDropEvent,
            [Path.ChangeExtension(Path.GetFullPath(videoPath), ".avi")]);
        Ensure(unsupportedDrop.Handled, "The unsupported-file drop was not handled.");
        Ensure(
            notificationText.Text == "MP4またはWMVファイルを指定してください。",
            "The unsupported-file guidance was not shown.");
        Ensure(backend.CurrentPath == originalPath, "An unsupported-file drop changed the current video.");
        Ensure(!backend.IsPlaying, "An unsupported-file drop changed the paused playback state.");
        Ensure(PlaybackRate.AreEqual(backend.Rate, originalRate), "A rejected file drop changed playback rate.");
        Ensure(
            !window.IsTemporaryPlaybackRatePending && !window.IsTemporaryPlaybackRateActive,
            "A rejected external file drop conflicted with the temporary playback-rate gesture.");

        Ensure(backend.TrySetRate(2.0f), "Could not prepare the single-file drop rate-reset check.");
        var singleDrop = RaiseFileDragEvent(
            videoSurface,
            DragDrop.PreviewDropEvent,
            [Path.GetFullPath(videoPath)]);
        Ensure(singleDrop.Handled, "The single-file drop was not handled.");
        await WaitUntilAsync(
            () => backend.CurrentPath == Path.GetFullPath(videoPath) &&
                  backend.IsPlaying &&
                  PlaybackRate.AreEqual(backend.Rate, PlaybackRate.Default) &&
                  backend.TimeMilliseconds < 5_000,
            TimeSpan.FromSeconds(6),
            "A supported single-file drop did not reopen and autoplay through the product path.");
        Ensure(overlay.Visibility == Visibility.Collapsed, "The file-drop overlay remained after a successful drop.");
        Ensure(
            !window.IsTemporaryPlaybackRatePending && !window.IsTemporaryPlaybackRateActive,
            "A supported external file drop activated the temporary playback-rate gesture.");

        return new FileDropValidation(
            SupportedDragEffect: supportedEnter.Effects.ToString(),
            ApprovedOverlayShown: true,
            MultipleDropPreservedPath: true,
            MultipleDropPreservedPositionMilliseconds: positionBeforeRejectedDrops,
            UnsupportedDropPreservedPath: true,
            SingleDropAutoplayed: true,
            SingleDropResetRate: backend.Rate,
            TemporaryRateGestureInactive: true,
            FinalMuted: backend.IsMuted);
    }

    private static DragEventArgs RaiseFileDragEvent(
        FrameworkElement target,
        RoutedEvent routedEvent,
        string[] paths)
    {
        var data = new DataObject(DataFormats.FileDrop, paths);
        var eventArgs = (DragEventArgs?)Activator.CreateInstance(
            typeof(DragEventArgs),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [
                data,
                DragDropKeyStates.None,
                DragDropEffects.Copy,
                target,
                new Point(target.ActualWidth / 2, target.ActualHeight / 2),
            ],
            culture: null) ?? throw new InvalidOperationException("Could not construct WPF drag event arguments.");
        eventArgs.RoutedEvent = routedEvent;
        target.RaiseEvent(eventArgs);
        return eventArgs;
    }

    private static DragEventArgs RaisePlaylistDragEvent(
        FrameworkElement target,
        RoutedEvent routedEvent,
        PlaylistEntryPresentation entry)
    {
        var data = new DataObject(typeof(PlaylistEntryPresentation), entry);
        var eventArgs = (DragEventArgs?)Activator.CreateInstance(
            typeof(DragEventArgs),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [
                data,
                DragDropKeyStates.LeftMouseButton,
                DragDropEffects.Move,
                target,
                new Point(target.ActualWidth / 2, Math.Max(0, target.ActualHeight - 2)),
            ],
            culture: null) ?? throw new InvalidOperationException("Could not construct WPF drag event arguments.");
        eventArgs.RoutedEvent = routedEvent;
        target.RaiseEvent(eventArgs);
        return eventArgs;
    }

    private static void RaiseDoubleClick(ListBox list, ListBoxItem item)
    {
        var eventArgs = new MouseButtonEventArgs(
            Mouse.PrimaryDevice,
            Environment.TickCount,
            MouseButton.Left)
        {
            RoutedEvent = Control.MouseDoubleClickEvent,
            Source = item,
        };
        list.RaiseEvent(eventArgs);
    }

    private static async Task<RealtimeAudioDiagnostics> ValidateNaturalDrainAsync(
        LibVlcPlaybackBackend backend)
    {
        await WaitUntilAsync(
            () => backend.AudioDiagnostics is { CompletedDrainCount: > 0, PendingLimiterFrames: 0, Failed: false },
            TimeSpan.FromSeconds(10),
            "The product PCM/WASAPI path did not drain after natural playback completion.");
        var diagnostics = backend.AudioDiagnostics;
        Ensure(diagnostics.UnderrunCount == 0, "Natural drain reported a PCM underrun.");
        Ensure(diagnostics.OverflowCount == 0, "Natural drain reported a PCM overflow.");
        Ensure(diagnostics.NonFiniteInputSamples == 0, "Natural drain received non-finite PCM input.");
        Ensure(diagnostics.NonFiniteOutputSamples == 0, "Natural drain produced non-finite PCM output.");
        Ensure(diagnostics.OverRangeSamples == 0, "Natural drain produced an out-of-range PCM sample.");
        Ensure(
            diagnostics.InputFrames == diagnostics.BufferedFrames + diagnostics.DiscardedLimiterFrames,
            "Natural drain did not account for every input frame.");
        var flushedFrames = diagnostics.FlushedBytes /
            (sizeof(float) * RealtimeVolumeProcessor.Channels);
        Ensure(
            diagnostics.BufferedFrames == diagnostics.ConsumedFrames + flushedFrames,
            $"Natural drain did not account for every buffered frame: buffered={diagnostics.BufferedFrames}, consumed={diagnostics.ConsumedFrames}, flushed={flushedFrames}.");
        return diagnostics;
    }

    private static async Task<object> ValidateAudibleVolumeAsync(LibVlcPlaybackBackend backend)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var endpointVolume = endpoint.AudioEndpointVolume.MasterVolumeLevelScalar;
        var endpointMuted = endpoint.AudioEndpointVolume.Mute;
        Ensure(!endpointMuted, "Audible validation requires the default endpoint to be unmuted.");
        Ensure(
            endpointVolume <= 0.5 + 0.000001,
            $"Audible validation requires endpoint volume at 50% or lower; current value is {endpointVolume:P1}.");

        backend.SetVolumePercent(PlaybackVolume.MaximumPercent);
        try
        {
            backend.SetMuted(false);
            backend.Play();
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
        finally
        {
            backend.SetMuted(true);
            backend.Pause();
        }

        var diagnostics = backend.AudioDiagnostics;
        var ceiling = Math.Pow(10, RealtimeVolumeProcessor.BoostedCeilingDecibels / 20);
        Ensure(!diagnostics.Failed, "The audible PCM/WASAPI path reported a failure.");
        Ensure(diagnostics.OverRangeSamples == 0, "The audible PCM/WASAPI path exceeded full scale.");
        Ensure(diagnostics.Peak <= ceiling + 0.000001, "The audible PCM/WASAPI path exceeded the -1 dBFS ceiling.");

        return new
        {
            durationSeconds = 3,
            volumePercent = PlaybackVolume.MaximumPercent,
            endpointVolumePercent = endpointVolume * 100,
            peak = diagnostics.Peak,
            ceiling,
            diagnostics.OverRangeSamples,
            finalMuted = backend.IsMuted,
        };
    }

    private static object ValidateVolumeControls(
        Slider volumeSlider,
        Button muteButton,
        TextBlock volumeText,
        UIElement videoSurface,
        LibVlcPlaybackBackend backend)
    {
        Ensure(volumeSlider.IsEnabled && muteButton.IsEnabled, "Volume controls did not become enabled.");
        Ensure(backend.IsMuted, "Validation playback must remain muted.");

        var requestMilliseconds = new List<double>();
        foreach (var percent in new[] { 65, 110, 250, 495, 320 })
        {
            var clock = Stopwatch.StartNew();
            volumeSlider.Value = percent;
            Ensure(backend.VolumePercent == percent, $"The slider did not update backend volume to {percent}%.");
            requestMilliseconds.Add(clock.Elapsed.TotalMilliseconds);
        }

        Ensure(volumeText.Text == "320%", "The volume label did not update to 320%.");
        Ensure(requestMilliseconds.Max() <= 100, "A volume request took longer than 100 ms to reach the backend.");

        backend.Pause();
        muteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Ensure(!backend.IsMuted, "The mute button did not unmute paused playback.");
        muteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Ensure(backend.IsMuted, "The mute button did not restore mute.");

        volumeSlider.Value = 495;
        var wheelUp = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent,
        };
        videoSurface.RaiseEvent(wheelUp);
        Ensure(wheelUp.Handled && backend.VolumePercent == 500, "Wheel-up did not reach 500%.");

        var wheelAboveMaximum = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent,
        };
        videoSurface.RaiseEvent(wheelAboveMaximum);
        Ensure(backend.VolumePercent == 500, "Wheel-up exceeded the 500% product limit.");

        var wheelDown = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent,
        };
        videoSurface.RaiseEvent(wheelDown);
        Ensure(wheelDown.Handled && backend.VolumePercent == 495, "Wheel-down did not reduce volume by 5%.");

        return new
        {
            requestRuns = requestMilliseconds.Count,
            requestMilliseconds,
            maximumRequestMilliseconds = requestMilliseconds.Max(),
            muteToggle = true,
            wheelStepPercent = PlaybackVolume.WheelStepPercent,
            maximumPercent = PlaybackVolume.MaximumPercent,
            finalPercent = backend.VolumePercent,
            finalMuted = backend.IsMuted,
        };
    }

    private static async Task<object> ValidatePlaybackRateAsync(
        MainWindow window,
        TextBlock playbackRateText,
        FrameworkElement videoSurface,
        LibVlcPlaybackBackend backend,
        string path)
    {
        var contextMenu = videoSurface.ContextMenu ??
            throw new InvalidOperationException("The video context menu was not found.");
        var rateMenu = contextMenu.Items
            .OfType<MenuItem>()
            .Single(item => Equals(item.Header, "再生速度"));
        var rateItems = rateMenu.Items.OfType<MenuItem>().ToArray();
        Ensure(rateMenu.IsEnabled, "The playback-rate menu did not become enabled.");
        Ensure(PlaybackRate.AreEqual(backend.Rate, PlaybackRate.Default), "Playback did not start at 1.0x.");
        Ensure(playbackRateText.Text == "1.0×", "The playback-rate label did not start at 1.0x.");

        contextMenu.PlacementTarget = videoSurface;
        contextMenu.IsOpen = true;
        Ensure(
            rateItems.Single(item => Equals(item.Tag, "1.0")).IsChecked,
            "The context menu did not check the current 1.0x rate.");
        contextMenu.IsOpen = false;

        var requestMilliseconds = new List<double>();
        foreach (var rate in PlaybackRate.Supported)
        {
            var item = rateItems.Single(candidate =>
                candidate.Tag is string text &&
                PlaybackRate.TryParse(text, out var candidateRate) &&
                PlaybackRate.AreEqual(candidateRate, rate));
            var clock = Stopwatch.StartNew();
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Ensure(PlaybackRate.AreEqual(backend.Rate, rate), $"The {rate}x request did not reach the backend.");
            requestMilliseconds.Add(clock.Elapsed.TotalMilliseconds);
            Ensure(item.IsChecked, $"The context menu did not check the current {rate}x rate.");
            Ensure(playbackRateText.Text == PlaybackRate.Format(rate), $"The playback-rate label did not show {rate}x.");
        }

        Ensure(requestMilliseconds.Max() <= 100, "A playback-rate request took longer than 100 ms to reach the backend.");
        await window.OpenVideoAsync(path);
        Ensure(PlaybackRate.AreEqual(backend.Rate, PlaybackRate.Default), "Opening a new video did not reset the rate to 1.0x.");
        Ensure(playbackRateText.Text == "1.0×", "The playback-rate label did not reset to 1.0x.");
        Ensure(
            rateItems.Single(item => Equals(item.Tag, "1.0")).IsChecked,
            "The context menu did not restore the 1.0x check after opening a video.");
        Ensure(backend.IsMuted, "Playback-rate validation must remain muted.");

        return new
        {
            supportedRates = PlaybackRate.Supported,
            requestRuns = requestMilliseconds.Count,
            requestMilliseconds,
            maximumRequestMilliseconds = requestMilliseconds.Max(),
            resetOnOpen = backend.Rate,
            finalDisplay = playbackRateText.Text,
            finalMuted = backend.IsMuted,
        };
    }

    private static async Task<object> ValidatePlaybackShortcutsAsync(
        MainWindow window,
        FrameworkElement videoSurface,
        Slider seekSlider,
        Slider volumeSlider,
        TextBlock playbackRateText,
        LibVlcPlaybackBackend backend)
    {
        Ensure(backend.IsMuted, "Shortcut validation must remain muted.");
        Ensure(NativeMethods.GetCursorPos(out var originalCursor), "Could not read the cursor position.");
        window.Topmost = true;
        window.Activate();
        NativeMethods.SetForegroundWindow(new WindowInteropHelper(window).Handle);

        try
        {
            MoveCursor(videoSurface.PointToScreen(
                new Point(videoSurface.ActualWidth / 2, videoSurface.ActualHeight / 2)));
            NativeMethods.MouseLeftClick();
            await WaitUntilAsync(
                () => ReferenceEquals(Keyboard.FocusedElement, videoSurface),
                TimeSpan.FromSeconds(1),
                "A real click did not focus the video surface for shortcut validation.");

            backend.Play();
            await WaitUntilAsync(
                () => backend.IsPlaying,
                TimeSpan.FromSeconds(3),
                "Playback did not start before Space shortcut validation.");

            NativeMethods.PressKey(NativeMethods.VirtualKeySpace);
            await WaitUntilAsync(
                () => !backend.IsPlaying,
                TimeSpan.FromSeconds(3),
                "Space did not pause playback.");
            NativeMethods.PressKey(NativeMethods.VirtualKeySpace);
            await WaitUntilAsync(
                () => backend.IsPlaying,
                TimeSpan.FromSeconds(3),
                "Space did not resume playback.");
            NativeMethods.PressKey(NativeMethods.VirtualKeySpace);
            await WaitUntilAsync(
                () => !backend.IsPlaying,
                TimeSpan.FromSeconds(3),
                "Space did not pause playback before seek validation.");

            seekSlider.Value = 0.5;
            await WaitUntilAsync(
                () => Math.Abs(backend.TimeMilliseconds - (backend.LengthMilliseconds * 0.5)) <= 250,
                TimeSpan.FromSeconds(3),
                "Could not prepare the timeline for arrow-key validation.");

            var beforeLeftMilliseconds = backend.TimeMilliseconds;
            var expectedLeftMilliseconds = Math.Max(0, beforeLeftMilliseconds - 5_000);
            NativeMethods.PressKey(NativeMethods.VirtualKeyLeft);
            await WaitUntilAsync(
                () => Math.Abs(backend.TimeMilliseconds - expectedLeftMilliseconds) <= 250,
                TimeSpan.FromSeconds(3),
                "Left did not seek backward by five seconds.");

            var beforeRightMilliseconds = backend.TimeMilliseconds;
            var expectedRightMilliseconds = Math.Min(backend.LengthMilliseconds, beforeRightMilliseconds + 5_000);
            NativeMethods.PressKey(NativeMethods.VirtualKeyRight);
            await WaitUntilAsync(
                () => Math.Abs(backend.TimeMilliseconds - expectedRightMilliseconds) <= 250,
                TimeSpan.FromSeconds(3),
                "Right did not seek forward by five seconds.");

            Ensure(backend.TrySetRate(PlaybackRate.Default), "Could not prepare 1.0x for arrow-key validation.");
            NativeMethods.PressKey(NativeMethods.VirtualKeyUp);
            await WaitUntilAsync(
                () => PlaybackRate.AreEqual(backend.Rate, 1.5f) && playbackRateText.Text == "1.5×",
                TimeSpan.FromSeconds(3),
                "Up did not increase the playback rate to 1.5x.");
            NativeMethods.PressKey(NativeMethods.VirtualKeyUp);
            await WaitUntilAsync(
                () => PlaybackRate.AreEqual(backend.Rate, 2.0f) && playbackRateText.Text == "2.0×",
                TimeSpan.FromSeconds(3),
                "Up did not increase the playback rate to 2.0x.");
            NativeMethods.PressKey(NativeMethods.VirtualKeyUp);
            await Task.Delay(100);
            Ensure(PlaybackRate.AreEqual(backend.Rate, 2.0f), "Up exceeded the maximum playback rate.");
            NativeMethods.PressKey(NativeMethods.VirtualKeyDown);
            await WaitUntilAsync(
                () => PlaybackRate.AreEqual(backend.Rate, 1.5f) && playbackRateText.Text == "1.5×",
                TimeSpan.FromSeconds(3),
                "Down did not decrease the playback rate to 1.5x.");
            NativeMethods.PressKey(NativeMethods.VirtualKeyUp);
            await WaitUntilAsync(
                () => PlaybackRate.AreEqual(backend.Rate, 2.0f),
                TimeSpan.FromSeconds(3),
                "Up did not restore the playback rate to 2.0x.");

            volumeSlider.Value = 100;
            Ensure(volumeSlider.Focus(), "Could not focus the volume slider for focus-priority validation.");
            NativeMethods.PressKey(NativeMethods.VirtualKeyUp);
            await WaitUntilAsync(
                () => backend.VolumePercent == 101,
                TimeSpan.FromSeconds(3),
                "Up did not retain the volume slider's standard behavior while it had focus.");
            Ensure(
                PlaybackRate.AreEqual(backend.Rate, 2.0f),
                "A playback shortcut overrode the focused volume slider.");

            Ensure(videoSurface.Focus(), "Could not restore video-surface focus after shortcut validation.");
            Ensure(backend.TrySetRate(PlaybackRate.Default), "Could not restore 1.0x after shortcut validation.");
            backend.Play();

            return new
            {
                playPauseToggles = 3,
                seekStepSeconds = 5,
                leftTargetMilliseconds = expectedLeftMilliseconds,
                rightTargetMilliseconds = expectedRightMilliseconds,
                maximumRate = 2.0,
                focusedVolumePercent = backend.VolumePercent,
                focusedSliderKeptRate = 2.0,
                finalRate = backend.Rate,
                finalMuted = backend.IsMuted,
            };
        }
        finally
        {
            NativeMethods.SetCursorPos(originalCursor.X, originalCursor.Y);
            window.Topmost = false;
        }
    }

    private static async Task<object> ValidateTemporaryPlaybackRateGestureAsync(
        MainWindow window,
        FrameworkElement videoRegion,
        FrameworkElement videoSurface,
        LibVlcPlaybackBackend backend)
    {
        Ensure(backend.TrySetRate(0.5f), "Could not prepare 0.5x for temporary-rate validation.");
        Ensure(backend.IsMuted, "Temporary-rate validation must remain muted.");
        var contextMenu = videoSurface.ContextMenu ??
            throw new InvalidOperationException("The video context menu was not found.");
        var target = videoRegion.PointToScreen(
            new Point(videoRegion.ActualWidth / 2, videoRegion.ActualHeight / 2));
        Ensure(NativeMethods.GetCursorPos(out var originalCursor), "Could not read the current cursor position.");
        var activationClock = new Stopwatch();

        window.Topmost = true;
        window.Activate();
        NativeMethods.SetForegroundWindow(new WindowInteropHelper(window).Handle);
        await Task.Delay(100);
        try
        {
            MoveCursor(target);
            activationClock.Start();
            NativeMethods.MouseLeftDown();
            await WaitUntilAsync(
                () => window.IsTemporaryPlaybackRatePending,
                TimeSpan.FromMilliseconds(200),
                "The real left-button press did not reach the WPF video interaction surface.");
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            Ensure(PlaybackRate.AreEqual(backend.Rate, 0.5f), "Temporary 2.0x activated before 400 ms.");
            await WaitUntilAsync(
                () => PlaybackRate.AreEqual(backend.Rate, 2.0f),
                TimeSpan.FromSeconds(1),
                "Holding the video surface did not activate temporary 2.0x playback.");
            var activationMilliseconds = activationClock.Elapsed.TotalMilliseconds;
            Ensure(activationMilliseconds >= 350, "Temporary 2.0x activated materially before 400 ms.");

            MoveCursor(new Point(target.X + 40, target.Y + 40));
            await Task.Delay(TimeSpan.FromSeconds(2.5));
            Ensure(
                PlaybackRate.AreEqual(backend.Rate, 2.0f),
                "Movement after activation cancelled temporary 2.0x.");
            Ensure(!backend.AudioDiagnostics.Failed, "Temporary 2.0x caused the PCM/WASAPI path to fail.");
            Ensure(backend.AudioDiagnostics.OverflowCount == 0, "Temporary 2.0x overflowed the bounded PCM buffer.");
            NativeMethods.MouseLeftUp();
            await WaitUntilAsync(
                () => PlaybackRate.AreEqual(backend.Rate, 0.5f),
                TimeSpan.FromSeconds(1),
                "Left-button release did not restore the pre-hold rate.");

            MoveCursor(target);
            NativeMethods.MouseLeftDown();
            await Task.Delay(100);
            MoveCursor(new Point(
                target.X + SystemParameters.MinimumHorizontalDragDistance + 10,
                target.Y));
            await Task.Delay(TemporaryPlaybackRateGesture.HoldDuration + TimeSpan.FromMilliseconds(150));
            Ensure(PlaybackRate.AreEqual(backend.Rate, 0.5f), "Pre-activation drag movement triggered temporary 2.0x.");
            NativeMethods.MouseLeftUp();

            MoveCursor(target);
            NativeMethods.MouseLeftDown();
            await Task.Delay(100);
            NativeMethods.MouseLeftUp();
            await Task.Delay(TemporaryPlaybackRateGesture.HoldDuration + TimeSpan.FromMilliseconds(100));
            Ensure(PlaybackRate.AreEqual(backend.Rate, 0.5f), "A normal click triggered temporary 2.0x.");

            NativeMethods.MouseLeftClick();
            await Task.Delay(80);
            NativeMethods.MouseLeftClick();
            await Task.Delay(TemporaryPlaybackRateGesture.HoldDuration + TimeSpan.FromMilliseconds(100));
            Ensure(PlaybackRate.AreEqual(backend.Rate, 0.5f), "A double click triggered temporary 2.0x.");
            Ensure(window.IsFullscreen, "A real double click did not enter fullscreen.");
            await Task.Delay(600);
            MoveCursor(videoRegion.PointToScreen(
                new Point(videoRegion.ActualWidth / 2, videoRegion.ActualHeight / 2)));
            NativeMethods.MouseLeftClick();
            await Task.Delay(80);
            NativeMethods.MouseLeftClick();
            await WaitUntilAsync(
                () => !window.IsFullscreen,
                TimeSpan.FromSeconds(1),
                "A second real double click did not exit fullscreen.");

            NativeMethods.MouseRightClick();
            await WaitUntilAsync(
                () => contextMenu.IsOpen,
                TimeSpan.FromSeconds(1),
                "A real right click did not open the video context menu.");
            Ensure(PlaybackRate.AreEqual(backend.Rate, 0.5f), "A right click triggered temporary 2.0x.");
            contextMenu.IsOpen = false;
            await Task.Delay(250);
            window.Activate();
            NativeMethods.SetForegroundWindow(new WindowInteropHelper(window).Handle);

            backend.SetVolumePercent(100);
            NativeMethods.MouseWheel(120);
            await WaitUntilAsync(
                () => backend.VolumePercent == 105,
                TimeSpan.FromSeconds(1),
                "A real mouse-wheel notch did not reach the video interaction surface.");
            NativeMethods.MouseWheel(-120);
            await WaitUntilAsync(
                () => backend.VolumePercent == 100,
                TimeSpan.FromSeconds(1),
                "A real reverse mouse-wheel notch did not restore the volume.");

            MoveCursor(target);
            NativeMethods.MouseLeftDown();
            await WaitUntilAsync(
                () => window.IsTemporaryPlaybackRatePending,
                TimeSpan.FromMilliseconds(200),
                "A left-button press after closing the context menu did not reach the video surface.");
            await WaitUntilAsync(
                () => PlaybackRate.AreEqual(backend.Rate, 2.0f),
                TimeSpan.FromSeconds(1),
                "Could not activate temporary 2.0x before focus-loss validation.");
            NativeMethods.SendMessage(
                new WindowInteropHelper(window).Handle,
                NativeMethods.WindowMessageActivateApplication,
                0,
                0);
            await WaitUntilAsync(
                () => PlaybackRate.AreEqual(backend.Rate, 0.5f),
                TimeSpan.FromSeconds(1),
                "Window deactivation did not restore the pre-hold rate.");
            NativeMethods.MouseLeftUp();
            await Task.Delay(100);

            Ensure(backend.TrySetRate(PlaybackRate.Default), "Could not restore 1.0x after gesture validation.");
            return new
            {
                holdDurationMilliseconds = TemporaryPlaybackRateGesture.HoldDuration.TotalMilliseconds,
                activationMilliseconds,
                restoredRate = backend.Rate,
                dragBeforeActivationCancelled = true,
                movementAfterActivationPreserved = true,
                normalClickIgnored = true,
                doubleClickIgnored = true,
                rightClickOpenedContextMenu = true,
                realMouseWheelHandled = true,
                focusLossRestored = true,
                finalMuted = backend.IsMuted,
            };
        }
        finally
        {
            NativeMethods.MouseLeftUp();
            contextMenu.IsOpen = false;
            NativeMethods.SetCursorPos(originalCursor.X, originalCursor.Y);
            window.Topmost = false;
        }
    }

    private static async Task<object> ValidateFullscreenAsync(
        MainWindow window,
        FrameworkElement videoRegion,
        FrameworkElement videoSurface,
        Slider volumeSlider,
        LibVlcPlaybackBackend backend)
    {
        Ensure(backend.IsMuted, "Fullscreen validation must remain muted.");
        var windowHandle = new WindowInteropHelper(window).Handle;
        var initialState = window.WindowState;
        var initialStyle = window.WindowStyle;
        var initialResizeMode = window.ResizeMode;
        var initialBounds = window.RestoreBounds;
        Ensure(NativeMethods.GetWindowRect(windowHandle, out var initialNativeBounds), "Could not read the initial window bounds.");
        var initialMonitor = NativeMethods.MonitorFromWindow(
            windowHandle,
            NativeMethods.MonitorDefaultToNearest);
        Ensure(initialMonitor != 0, "Could not identify the window's current monitor.");
        var monitorInfo = NativeMethods.GetMonitorInformation(initialMonitor);
        var contextMenu = videoSurface.ContextMenu ??
            throw new InvalidOperationException("The video context menu was not found.");
        var fullscreenMenuItem = (MenuItem)window.FindName("FullscreenMenuItem");
        var mainMenu = (Menu)window.FindName("MainMenu");
        var playbackControls = (FrameworkElement)window.FindName("PlaybackControls");
        Ensure(NativeMethods.GetCursorPos(out var originalCursor), "Could not read the current cursor position.");

        window.Topmost = true;
        window.Activate();
        NativeMethods.SetForegroundWindow(windowHandle);
        await Task.Delay(100);
        try
        {
            MoveCursor(videoRegion.PointToScreen(
                new Point(videoRegion.ActualWidth / 2, videoRegion.ActualHeight / 2)));
            NativeMethods.MouseLeftClick();
            await Task.Delay(80);
            NativeMethods.MouseLeftClick();
            await WaitUntilAsync(
                () => window.IsFullscreen,
                TimeSpan.FromSeconds(1),
                "A real double click did not enter fullscreen.");
            await Task.Delay(150);
            Ensure(window.WindowStyle == WindowStyle.None, "Fullscreen did not remove the window frame.");
            Ensure(window.ResizeMode == ResizeMode.NoResize, "Fullscreen did not disable resizing.");
            Ensure(window.WindowState == WindowState.Maximized, "Fullscreen did not maximize the window.");
            Ensure(mainMenu.Visibility == Visibility.Collapsed, "Fullscreen did not hide the main menu.");
            Ensure(Grid.GetRow(videoRegion) == 0 && Grid.GetRowSpan(videoRegion) == 4, "Video did not fill the fullscreen layout.");
            Ensure(playbackControls.Visibility == Visibility.Visible, "Playback controls were not visible on fullscreen entry.");
            Ensure(
                NativeMethods.MonitorFromWindow(windowHandle, NativeMethods.MonitorDefaultToNearest) == initialMonitor,
                "Fullscreen moved to a different monitor.");
            Ensure(NativeMethods.GetWindowRect(windowHandle, out var fullscreenBounds), "Could not read fullscreen bounds.");
            Ensure(
                fullscreenBounds.Equals(monitorInfo.Monitor),
                $"Fullscreen bounds {fullscreenBounds} did not match monitor bounds {monitorInfo.Monitor}.");

            await Task.Delay(FullscreenControlsState.AutoHideDelay - TimeSpan.FromMilliseconds(350));
            Ensure(playbackControls.Visibility == Visibility.Visible, "Playback controls hid before the approved three-second delay.");
            await WaitUntilAsync(
                () => playbackControls.Visibility == Visibility.Collapsed,
                TimeSpan.FromSeconds(1),
                "Playback controls did not hide after three seconds of fullscreen inactivity.");

            MoveCursor(videoRegion.PointToScreen(
                new Point(videoRegion.ActualWidth / 2, videoRegion.ActualHeight / 3)));
            await WaitUntilAsync(
                () => playbackControls.Visibility == Visibility.Visible,
                TimeSpan.FromSeconds(1),
                "Real mouse movement did not show the hidden playback controls.");
            MoveCursor(playbackControls.PointToScreen(
                new Point(playbackControls.ActualWidth / 2, playbackControls.ActualHeight / 2)));
            await WaitUntilAsync(
                () => playbackControls.IsMouseOver,
                TimeSpan.FromSeconds(1),
                "The real pointer did not enter the fullscreen playback controls.");
            await Task.Delay(FullscreenControlsState.AutoHideDelay + TimeSpan.FromMilliseconds(250));
            Ensure(playbackControls.Visibility == Visibility.Visible, "Playback controls hid while the pointer was over them.");

            MoveCursor(videoRegion.PointToScreen(
                new Point(videoRegion.ActualWidth / 2, videoRegion.ActualHeight / 3)));
            await WaitUntilAsync(
                () => !playbackControls.IsMouseOver,
                TimeSpan.FromSeconds(1),
                "The real pointer did not leave the fullscreen playback controls.");
            await Task.Delay(FullscreenControlsState.AutoHideDelay - TimeSpan.FromMilliseconds(300));
            Ensure(playbackControls.Visibility == Visibility.Visible, "Playback controls hid before three seconds after pointer leave.");
            await WaitUntilAsync(
                () => playbackControls.Visibility == Visibility.Collapsed,
                TimeSpan.FromSeconds(1),
                "Playback controls did not hide after the pointer left them.");

            MoveCursor(new Point(
                videoRegion.PointToScreen(new Point(videoRegion.ActualWidth / 2, videoRegion.ActualHeight / 3)).X + 20,
                videoRegion.PointToScreen(new Point(videoRegion.ActualWidth / 2, videoRegion.ActualHeight / 3)).Y));
            await WaitUntilAsync(
                () => playbackControls.Visibility == Visibility.Visible,
                TimeSpan.FromSeconds(1),
                "Mouse movement did not show controls before context-menu validation.");
            NativeMethods.MouseRightClick();
            await WaitUntilAsync(
                () => contextMenu.IsOpen,
                TimeSpan.FromSeconds(1),
                "A real right click did not open the fullscreen context menu.");
            await Task.Delay(FullscreenControlsState.AutoHideDelay + TimeSpan.FromMilliseconds(250));
            Ensure(playbackControls.Visibility == Visibility.Visible, "Playback controls hid while the context menu was open.");
            contextMenu.IsOpen = false;
            await Task.Delay(FullscreenControlsState.AutoHideDelay - TimeSpan.FromMilliseconds(300));
            Ensure(playbackControls.Visibility == Visibility.Visible, "Playback controls hid before three seconds after context-menu close.");
            await WaitUntilAsync(
                () => playbackControls.Visibility == Visibility.Collapsed,
                TimeSpan.FromSeconds(1),
                "Playback controls did not hide after the context menu closed.");

            await Task.Delay(600);
            MoveCursor(videoRegion.PointToScreen(
                new Point(videoRegion.ActualWidth / 2, videoRegion.ActualHeight / 2)));
            NativeMethods.MouseLeftClick();
            await Task.Delay(80);
            NativeMethods.MouseLeftClick();
            await WaitUntilAsync(
                () => !window.IsFullscreen,
                TimeSpan.FromSeconds(1),
                "A second real double click did not exit fullscreen.");
            EnsureWindowPresentationRestored(
                window,
                mainMenu,
                videoRegion,
                initialState,
                initialStyle,
                initialResizeMode,
                initialBounds);

            MoveCursor(videoRegion.PointToScreen(
                new Point(videoRegion.ActualWidth / 2, videoRegion.ActualHeight / 2)));
            NativeMethods.MouseRightClick();
            await WaitUntilAsync(
                () => contextMenu.IsOpen,
                TimeSpan.FromSeconds(1),
                "A real right click did not open the video context menu.");
            Ensure(!fullscreenMenuItem.IsChecked, "The fullscreen menu item was checked before entry.");
            fullscreenMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await WaitUntilAsync(
                () => window.IsFullscreen,
                TimeSpan.FromSeconds(1),
                "The context-menu command did not enter fullscreen.");
            contextMenu.IsOpen = false;
            NativeMethods.PressKey(NativeMethods.VirtualKeyEscape);
            await WaitUntilAsync(
                () => !window.IsFullscreen,
                TimeSpan.FromSeconds(1),
                "Escape did not exit fullscreen.");

            volumeSlider.Focus();
            NativeMethods.PressAltEnter();
            await WaitUntilAsync(
                () => window.IsFullscreen,
                TimeSpan.FromSeconds(1),
                "Alt+Enter did not enter fullscreen while an input control had focus.");
            NativeMethods.PressAltEnter();
            await WaitUntilAsync(
                () => !window.IsFullscreen,
                TimeSpan.FromSeconds(1),
                "Alt+Enter did not exit fullscreen.");
            EnsureWindowPresentationRestored(
                window,
                mainMenu,
                videoRegion,
                initialState,
                initialStyle,
                initialResizeMode,
                initialBounds);

            window.WindowState = WindowState.Maximized;
            await WaitUntilAsync(
                () => window.WindowState == WindowState.Maximized,
                TimeSpan.FromSeconds(1),
                "Could not prepare a maximized window for restore validation.");
            NativeMethods.PressAltEnter();
            await WaitUntilAsync(
                () => window.IsFullscreen,
                TimeSpan.FromSeconds(1),
                "Alt+Enter did not enter fullscreen from a maximized window.");
            NativeMethods.PressKey(NativeMethods.VirtualKeyEscape);
            await WaitUntilAsync(
                () => !window.IsFullscreen && window.WindowState == WindowState.Maximized,
                TimeSpan.FromSeconds(1),
                "Fullscreen exit did not restore the maximized window state.");
            window.WindowState = WindowState.Normal;
            await WaitUntilAsync(
                () => window.WindowState == initialState,
                TimeSpan.FromSeconds(1),
                "Could not restore the validation window to its initial state.");
            EnsureWindowPresentationRestored(
                window,
                mainMenu,
                videoRegion,
                initialState,
                initialStyle,
                initialResizeMode,
                initialBounds);

            var displayValidations = await ValidateFullscreenAcrossMonitorsAsync(
                window,
                windowHandle,
                videoRegion,
                playbackControls,
                initialMonitor,
                initialNativeBounds);

            return new
            {
                currentMonitorHandle = initialMonitor.ToInt64(),
                monitorBounds = ToReportBounds(monitorInfo.Monitor),
                fullscreenBounds = ToReportBounds(fullscreenBounds),
                doubleClickEnteredAndExited = true,
                contextMenuEntered = true,
                escapeExited = true,
                altEnterEnteredAndExitedWithInputFocus = true,
                maximizedStateRestored = true,
                autoHideDelayMilliseconds = FullscreenControlsState.AutoHideDelay.TotalMilliseconds,
                idleAutoHide = true,
                mouseMovementShowedControls = true,
                controlsHoverPreventedHide = true,
                contextMenuPreventedHide = true,
                displayValidations,
                restoredState = window.WindowState.ToString(),
                restoredBounds = window.RestoreBounds,
                finalMuted = backend.IsMuted,
            };
        }
        finally
        {
            contextMenu.IsOpen = false;
            if (window.IsFullscreen)
            {
                NativeMethods.PressKey(NativeMethods.VirtualKeyEscape);
                await WaitUntilAsync(
                    () => !window.IsFullscreen,
                    TimeSpan.FromSeconds(1),
                    "Fullscreen cleanup failed.");
            }

            NativeMethods.SetCursorPos(originalCursor.X, originalCursor.Y);
            window.Topmost = false;
        }
    }

    private static async Task<IReadOnlyList<object>> ValidateFullscreenAcrossMonitorsAsync(
        MainWindow window,
        nint windowHandle,
        FrameworkElement videoRegion,
        FrameworkElement playbackControls,
        nint initialMonitor,
        NativeRect initialNativeBounds)
    {
        var monitors = NativeMethods.GetMonitors();
        Ensure(monitors.Count >= 2, "Multiple-display validation requires at least two active monitors.");
        var results = new List<object>(monitors.Count);
        var observedDpis = new HashSet<uint>();
        try
        {
            foreach (var monitor in monitors)
            {
                NativeMethods.MoveToMonitor(windowHandle, monitor);
                await WaitUntilAsync(
                    () => NativeMethods.MonitorFromWindow(windowHandle, NativeMethods.MonitorDefaultToNearest) == monitor.Handle,
                    TimeSpan.FromSeconds(2),
                    "The product window did not move to the target monitor.");
                await Task.Delay(300);
                var dpi = NativeMethods.GetDpiForWindow(windowHandle);
                observedDpis.Add(dpi);
                Ensure(NativeMethods.GetWindowRect(windowHandle, out var normalBounds), "Could not read the moved window bounds.");
                Ensure(window.ActualWidth >= window.MinWidth && window.ActualHeight >= window.MinHeight, "The product layout fell below its minimum size after a DPI transition.");
                Ensure(videoRegion.ActualWidth > 0 && videoRegion.ActualHeight > 0, "The video region collapsed after a DPI transition.");
                Ensure(playbackControls.ActualWidth > 0 && playbackControls.ActualHeight > 0, "The playback controls collapsed after a DPI transition.");

                window.Activate();
                NativeMethods.SetForegroundWindow(windowHandle);
                NativeMethods.PressAltEnter();
                await WaitUntilAsync(
                    () => window.IsFullscreen,
                    TimeSpan.FromSeconds(1),
                    "Alt+Enter did not enter fullscreen on a target monitor.");
                Ensure(NativeMethods.GetWindowRect(windowHandle, out var fullscreenBounds), "Could not read multi-monitor fullscreen bounds.");
                Ensure(
                    fullscreenBounds.Equals(monitor.Info.Monitor),
                    $"Fullscreen bounds {fullscreenBounds} did not match target monitor bounds {monitor.Info.Monitor}.");
                NativeMethods.PressKey(NativeMethods.VirtualKeyEscape);
                await WaitUntilAsync(
                    () => !window.IsFullscreen,
                    TimeSpan.FromSeconds(1),
                    "Escape did not exit fullscreen on a target monitor.");
                Ensure(NativeMethods.GetWindowRect(windowHandle, out var restoredBounds), "Could not read restored multi-monitor bounds.");
                Ensure(
                    restoredBounds.Equals(normalBounds),
                    $"Fullscreen exit changed the moved window bounds from {normalBounds} to {restoredBounds}.");

                results.Add(new
                {
                    monitorHandle = monitor.Handle.ToInt64(),
                    dpi,
                    monitorBounds = ToReportBounds(monitor.Info.Monitor),
                    normalBounds = ToReportBounds(normalBounds),
                    fullscreenBounds = ToReportBounds(fullscreenBounds),
                    restoredBounds = ToReportBounds(restoredBounds),
                    layoutWidth = window.ActualWidth,
                    layoutHeight = window.ActualHeight,
                });
            }

            Ensure(observedDpis.Count >= 2, "The active monitors did not expose distinct DPI values for product validation.");
            return results;
        }
        finally
        {
            if (window.IsFullscreen)
            {
                NativeMethods.PressKey(NativeMethods.VirtualKeyEscape);
                await WaitUntilAsync(
                    () => !window.IsFullscreen,
                    TimeSpan.FromSeconds(1),
                    "Multi-monitor fullscreen cleanup failed.");
            }

            NativeMethods.MoveWindow(
                windowHandle,
                initialNativeBounds.Left,
                initialNativeBounds.Top);
            await WaitUntilAsync(
                () => NativeMethods.MonitorFromWindow(windowHandle, NativeMethods.MonitorDefaultToNearest) == initialMonitor,
                TimeSpan.FromSeconds(2),
                "The validation window did not return to its initial monitor.");
            await Task.Delay(300);
        }
    }

    private static void EnsureWindowPresentationRestored(
        MainWindow window,
        Menu mainMenu,
        FrameworkElement videoRegion,
        WindowState initialState,
        WindowStyle initialStyle,
        ResizeMode initialResizeMode,
        Rect initialBounds)
    {
        Ensure(window.WindowState == initialState, "Fullscreen exit did not restore the window state.");
        Ensure(window.WindowStyle == initialStyle, "Fullscreen exit did not restore the window style.");
        Ensure(window.ResizeMode == initialResizeMode, "Fullscreen exit did not restore the resize mode.");
        Ensure(mainMenu.Visibility == Visibility.Visible, "Fullscreen exit did not restore the main menu.");
        Ensure(Grid.GetRow(videoRegion) == 1 && Grid.GetRowSpan(videoRegion) == 1, "Fullscreen exit did not restore the video layout.");
        Ensure(AreClose(window.RestoreBounds, initialBounds), "Fullscreen exit did not restore the window bounds.");
    }

    private static bool AreClose(Rect left, Rect right) =>
        Math.Abs(left.Left - right.Left) < 1 &&
        Math.Abs(left.Top - right.Top) < 1 &&
        Math.Abs(left.Width - right.Width) < 1 &&
        Math.Abs(left.Height - right.Height) < 1;

    private static object ToReportBounds(NativeRect bounds) => new
    {
        bounds.Left,
        bounds.Top,
        bounds.Right,
        bounds.Bottom,
    };

    private static void MoveCursor(Point point)
    {
        Ensure(
            NativeMethods.SetCursorPos((int)Math.Round(point.X), (int)Math.Round(point.Y)),
            "Could not move the cursor for gesture validation.");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static async Task<OpenMetrics> MeasureOpenAsync(
        MainWindow window,
        Slider seekSlider,
        LibVlcPlaybackBackend backend,
        string path)
    {
        var clock = Stopwatch.StartNew();
        var videoOutput = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        var playbackClock = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnVideoOutput(object? sender, MediaPlayerVoutEventArgs eventArgs) =>
            videoOutput.TrySetResult(clock.Elapsed.TotalMilliseconds);

        void OnTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs eventArgs)
        {
            if (eventArgs.Time >= 100)
            {
                playbackClock.TrySetResult(clock.Elapsed.TotalMilliseconds);
            }
        }

        backend.MediaPlayer.Vout += OnVideoOutput;
        backend.MediaPlayer.TimeChanged += OnTimeChanged;
        try
        {
            await window.OpenVideoAsync(path);
            var ready = await Task.WhenAll(videoOutput.Task, playbackClock.Task).WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(
                () => seekSlider.IsEnabled && backend.LengthMilliseconds > 0,
                TimeSpan.FromSeconds(5),
                "The product timeline did not become ready.");
            var timelineReadyMilliseconds = ready.Max();
            await WaitUntilAsync(
                () => backend.AudioDiagnostics is { CallbackCount: > 0, OutputStarted: true, Failed: false },
                TimeSpan.FromSeconds(5),
                "The product PCM/WASAPI path did not become ready.");
            return new OpenMetrics(timelineReadyMilliseconds, clock.Elapsed.TotalMilliseconds);
        }
        finally
        {
            backend.MediaPlayer.Vout -= OnVideoOutput;
            backend.MediaPlayer.TimeChanged -= OnTimeChanged;
        }
    }

    private static async Task<double> MeasureSeekAsync(
        Slider seekSlider,
        IPlaybackBackend backend,
        double normalizedPosition,
        long lengthMilliseconds)
    {
        var targetMilliseconds = (long)(lengthMilliseconds * normalizedPosition);
        var clock = Stopwatch.StartNew();
        seekSlider.Value = normalizedPosition;
        await WaitUntilAsync(
            () => backend.TimeMilliseconds >= Math.Min(lengthMilliseconds, targetMilliseconds + 100),
            TimeSpan.FromSeconds(3),
            $"The {normalizedPosition:P0} seek did not reach its target.");
        return clock.Elapsed.TotalMilliseconds;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string failureMessage)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        if (!condition())
        {
            throw new TimeoutException(failureMessage);
        }
    }

    private static void WriteReport(string path, object report)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void AddProductResources(ResourceDictionary resources)
    {
        resources["AppBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(0x0C, 0x11, 0x19));
        resources["TextBrush"] = new SolidColorBrush(Color.FromRgb(0xF2, 0xF6, 0xFC));
        resources["MutedTextBrush"] = new SolidColorBrush(Color.FromRgb(0x9D, 0xAB, 0xC0));
        resources["PanelBrush"] = new SolidColorBrush(Color.FromRgb(0x15, 0x1D, 0x29));
        resources["PanelRaisedBrush"] = new SolidColorBrush(Color.FromRgb(0x1C, 0x26, 0x34));
        resources["BorderBrush"] = new SolidColorBrush(Color.FromRgb(0x34, 0x41, 0x56));
        resources["PrimaryBrush"] = new SolidColorBrush(Color.FromRgb(0x47, 0xB8, 0xFF));
        resources["PrimaryHoverBrush"] = new SolidColorBrush(Color.FromRgb(0x70, 0xC8, 0xFF));
        var playlistButtonStyle = new Style(typeof(Button));
        playlistButtonStyle.Setters.Add(new Setter(Control.ForegroundProperty, resources["TextBrush"]));
        playlistButtonStyle.Setters.Add(new Setter(Control.BackgroundProperty, resources["PanelRaisedBrush"]));
        playlistButtonStyle.Setters.Add(new Setter(Control.BorderBrushProperty, resources["BorderBrush"]));
        playlistButtonStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(14, 8, 14, 8)));
        resources["PlaylistButtonStyle"] = playlistButtonStyle;
        var playlistPrimaryButtonStyle = new Style(typeof(Button), playlistButtonStyle);
        playlistPrimaryButtonStyle.Setters.Add(new Setter(Control.BackgroundProperty, resources["PrimaryBrush"]));
        playlistPrimaryButtonStyle.Setters.Add(new Setter(Control.BorderBrushProperty, resources["PrimaryBrush"]));
        resources["PlaylistPrimaryButtonStyle"] = playlistPrimaryButtonStyle;
    }

    private sealed class RecordingDiagnosticLog : IDiagnosticLog
    {
        public ConcurrentQueue<DiagnosticEvent> Events { get; } = new();

        public DiagnosticWriteResult Write(DiagnosticEvent diagnosticEvent)
        {
            Events.Enqueue(diagnosticEvent);
            return new(true, null);
        }

        public void Dispose()
        {
        }
    }

    private readonly record struct OpenMetrics(
        double TimelineReadyMilliseconds,
        double AudioOutputReadyMilliseconds);

    private readonly record struct ProfileDelayValidation(
        int RestoredVolumePercent,
        bool RestoredMuted,
        int SavedVolumePercent,
        bool SavedMuted,
        bool WriteFailureKeptPlaying);

    private readonly record struct StartPositionValidation(
        long InitialRegisteredMilliseconds,
        long InitialRestoredMilliseconds,
        double InitialMarkerOffset,
        long PersistedRegistrationMilliseconds,
        double RegisteredMarkerOffset,
        long OutOfRangeRegisteredMilliseconds,
        long OutOfRangeFallbackMilliseconds,
        bool OutOfRangeMarkerHidden,
        long FinalRegisteredMilliseconds,
        long FinalRestoredMilliseconds,
        double SeekWidthBeforeResize,
        double MarkerOffsetBeforeResize,
        double SeekWidthAfterResize,
        double MarkerOffsetAfterResize,
        long LastPlaybackPositionMilliseconds,
        string ConfirmationMessage,
        bool FinalMuted);

    private readonly record struct FileDropValidation(
        string SupportedDragEffect,
        bool ApprovedOverlayShown,
        bool MultipleDropPreservedPath,
        long MultipleDropPreservedPositionMilliseconds,
        bool UnsupportedDropPreservedPath,
        bool SingleDropAutoplayed,
        float SingleDropResetRate,
        bool TemporaryRateGestureInactive,
        bool FinalMuted);

    private readonly record struct RecentFileValidation(
        int InitialFileCount,
        int EnabledFileCount,
        int MissingFileCount,
        string ReopenedPath,
        bool IndividualRemovalPersisted,
        bool BulkMissingRemovalPersisted,
        bool ClearHistoryPersisted,
        bool ForegroundInherited);

    private readonly record struct ThumbnailSettingsValidation(
        double InitialIntervalPercent,
        double SavedIntervalPercent,
        bool CurrentSessionStayedAtInitialValue,
        bool NextOpenUsedSavedValue,
        bool CancelPreservedSavedValue,
        double DialogWidth,
        double DialogHeight,
        bool FinalMuted);

    private readonly record struct ThumbnailHoverValidation(
        bool InitiallyLoading,
        long TargetMilliseconds,
        double PriorityDisplayMilliseconds,
        int CachedFrameCount,
        int PixelWidth,
        int PixelHeight,
        double VerticalOffset,
        double LeftOffset,
        double RightOffset,
        bool IsHitTestVisible,
        bool IsOpenAfterClose);

    private readonly record struct PlaylistValidation(
        int EntryCount,
        bool CurrentVideoAdded,
        bool MixedDropAddedSupportedOnly,
        bool MultipleSelectionRemoved,
        bool SourceFilesPreserved,
        bool DragReorderPersisted,
        bool PlaySkippedMissingAndLoadError,
        bool NaturalEndAdvanced,
        bool DoubleClickStartedSelected,
        bool EndOfListStopped,
        bool LoopWrappedToFirstPlayable,
        bool NoPlayableStoppedOnce,
        bool MainWindowOpenCanceledContinuousPlayback,
        int FinalEntryCount,
        bool DuplicateEntriesPreserved,
        bool MissingEntriesMarked,
        bool InitialLoopRestored,
        bool LoopOffPersisted,
        bool SingleWindowReopened,
        double InitialWidth,
        double InitialHeight,
        double MinimumWidth,
        double MinimumHeight);

    private sealed class RecordingNotificationSink : IUserNotificationSink
    {
        public List<UserNotification> Notifications { get; } = [];

        public void Show(UserNotification notification)
        {
            Notifications.Add(notification);
        }
    }

    private static class NativeMethods
    {
        private const uint MouseEventLeftDown = 0x0002;
        private const uint MouseEventLeftUp = 0x0004;
        private const uint MouseEventRightDown = 0x0008;
        private const uint MouseEventRightUp = 0x0010;
        private const uint MouseEventWheel = 0x0800;
        private const uint KeyEventKeyUp = 0x0002;
        private const uint SetWindowPositionNoSize = 0x0001;
        private const uint SetWindowPositionNoZOrder = 0x0004;
        private const uint SetWindowPositionShowWindow = 0x0040;
        private const byte VirtualKeyMenu = 0x12;
        private const byte VirtualKeyReturn = 0x0D;
        internal const byte VirtualKeyEscape = 0x1B;
        internal const byte VirtualKeySpace = 0x20;
        internal const byte VirtualKeyLeft = 0x25;
        internal const byte VirtualKeyUp = 0x26;
        internal const byte VirtualKeyRight = 0x27;
        internal const byte VirtualKeyDown = 0x28;
        internal const uint MonitorDefaultToNearest = 0x00000002;
        internal const int WindowMessageActivateApplication = 0x001C;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out NativePoint point);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(nint windowHandle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(nint windowHandle, out NativeRect rectangle);

        [DllImport("user32.dll")]
        internal static extern nint MonitorFromWindow(nint windowHandle, uint flags);

        [DllImport("user32.dll")]
        internal static extern uint GetDpiForWindow(nint windowHandle);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplayMonitors(
            nint deviceContext,
            nint clipRectangle,
            MonitorEnumProcedure callback,
            nint data);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(
            nint windowHandle,
            nint insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

        [DllImport("user32.dll")]
        internal static extern nint SendMessage(nint windowHandle, int message, nint wordParameter, nint longParameter);

        [DllImport("user32.dll", EntryPoint = "mouse_event")]
        private static extern void MouseEvent(uint flags, uint dx, uint dy, uint data, nuint extraInfo);

        [DllImport("user32.dll", EntryPoint = "keybd_event")]
        private static extern void KeyEvent(byte virtualKey, byte scanCode, uint flags, nuint extraInfo);

        internal static void MouseLeftDown() => MouseEvent(MouseEventLeftDown, 0, 0, 0, 0);

        internal static void MouseLeftUp() => MouseEvent(MouseEventLeftUp, 0, 0, 0, 0);

        internal static void MouseLeftClick()
        {
            MouseLeftDown();
            MouseLeftUp();
        }

        internal static void MouseRightClick()
        {
            MouseEvent(MouseEventRightDown, 0, 0, 0, 0);
            MouseEvent(MouseEventRightUp, 0, 0, 0, 0);
        }

        internal static void MouseWheel(int delta) =>
            MouseEvent(MouseEventWheel, 0, 0, unchecked((uint)delta), 0);

        internal static void PressKey(byte virtualKey)
        {
            KeyEvent(virtualKey, 0, 0, 0);
            KeyEvent(virtualKey, 0, KeyEventKeyUp, 0);
        }

        internal static void PressAltEnter()
        {
            KeyEvent(VirtualKeyMenu, 0, 0, 0);
            PressKey(VirtualKeyReturn);
            KeyEvent(VirtualKeyMenu, 0, KeyEventKeyUp, 0);
        }

        internal static MonitorInfo GetMonitorInformation(nint monitor)
        {
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            Ensure(GetMonitorInfo(monitor, ref info), "Could not read monitor bounds.");
            return info;
        }

        internal static IReadOnlyList<MonitorDescriptor> GetMonitors()
        {
            var monitors = new List<MonitorDescriptor>();
            MonitorEnumProcedure callback = (monitor, _, _, _) =>
            {
                monitors.Add(new MonitorDescriptor(monitor, GetMonitorInformation(monitor)));
                return true;
            };
            Ensure(
                EnumDisplayMonitors(0, 0, callback, 0),
                $"Could not enumerate active monitors: {Marshal.GetLastWin32Error()}.");
            return monitors;
        }

        internal static void MoveToMonitor(nint windowHandle, MonitorDescriptor monitor)
        {
            Ensure(GetWindowRect(windowHandle, out var windowBounds), "Could not read bounds before moving the window.");
            var width = windowBounds.Right - windowBounds.Left;
            var height = windowBounds.Bottom - windowBounds.Top;
            var workWidth = monitor.Info.WorkArea.Right - monitor.Info.WorkArea.Left;
            var workHeight = monitor.Info.WorkArea.Bottom - monitor.Info.WorkArea.Top;
            var left = monitor.Info.WorkArea.Left + Math.Max(0, (workWidth - width) / 2);
            var top = monitor.Info.WorkArea.Top + Math.Max(0, (workHeight - height) / 2);
            MoveWindow(windowHandle, left, top);
        }

        internal static void MoveWindow(nint windowHandle, int left, int top)
        {
            Ensure(
                SetWindowPos(
                    windowHandle,
                    0,
                    left,
                    top,
                    0,
                    0,
                    SetWindowPositionNoSize | SetWindowPositionNoZOrder | SetWindowPositionShowWindow),
                $"Could not move the product window: {Marshal.GetLastWin32Error()}.");
        }

        private delegate bool MonitorEnumProcedure(nint monitor, nint deviceContext, nint rectangle, nint data);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeRect : IEquatable<NativeRect>
    {
        internal readonly int Left;
        internal readonly int Top;
        internal readonly int Right;
        internal readonly int Bottom;

        public bool Equals(NativeRect other) =>
            Left == other.Left && Top == other.Top && Right == other.Right && Bottom == other.Bottom;

        public override bool Equals(object? value) => value is NativeRect other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Left, Top, Right, Bottom);

        public override string ToString() => $"({Left},{Top})-({Right},{Bottom})";
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        internal uint Size;
        internal NativeRect Monitor;
        internal NativeRect WorkArea;
        internal uint Flags;
    }

    private readonly record struct MonitorDescriptor(nint Handle, MonitorInfo Info);
}
