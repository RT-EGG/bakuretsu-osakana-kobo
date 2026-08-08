using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BakuretsuOsakanaKobo.Phase3UiMock;

internal static class UiMockValidation
{
    public static void Run(string? reportPath)
    {
        var checks = new List<string>();
        var session = new MockPlaybackSession();

        Require(session.State == MediaUiState.Empty, "Initial state must be empty.");
        Require(!session.CanControlPlayback, "Empty state must disable playback controls.");
        checks.Add("empty-state");

        session.BeginLoading();
        Require(session.State == MediaUiState.Loading, "Open must enter loading state.");
        Require(!session.HasKnownDuration, "Loading state must have unknown duration.");
        checks.Add("loading-state");

        session.CompleteLoading();
        Require(session.State == MediaUiState.Playing, "Successful load must start playback.");
        Require(session.CanControlPlayback && session.HasKnownDuration, "Playing state must enable controls.");
        checks.Add("playing-state");

        session.TogglePlayPause();
        Require(session.State == MediaUiState.Paused, "Toggle must pause playback.");
        session.TogglePlayPause();
        Require(session.State == MediaUiState.Playing, "Second toggle must resume playback.");
        checks.Add("play-pause-toggle");

        session.SeekTo(TimeSpan.FromHours(9));
        Require(session.Position == session.Duration, "Seek must clamp to the media duration.");
        session.SeekTo(TimeSpan.FromSeconds(-1));
        Require(session.Position == TimeSpan.Zero, "Seek must clamp to zero.");
        checks.Add("seek-clamping");

        Require(MockPlaybackSession.FormatTime(new TimeSpan(days: 1, hours: 2, minutes: 3, seconds: 4)) == "26:03:04", "Time formatting must preserve total hours.");
        checks.Add("time-formatting");

        session.SetVolume(600);
        Require(session.VolumePercent == 500, "Volume must clamp to 500%.");
        session.SetVolume(-1);
        Require(session.VolumePercent == 0, "Volume must clamp to 0%.");
        session.ToggleMute();
        Require(session.IsMuted, "Mute must turn on.");
        session.ToggleMute();
        Require(!session.IsMuted, "Mute must turn off.");
        Require(MainWindow.VolumeWheelStepPercent == 5, "One mouse-wheel notch must change volume by 5%.");
        checks.Add("volume-and-mute-boundaries");

        Require(session.SetPlaybackRate(1.5) && session.PlaybackRate == 1.5, "A supported playback rate must be selected.");
        Require(!session.SetPlaybackRate(1.25) && session.PlaybackRate == 1.5, "An unsupported playback rate must be rejected.");
        session.BeginLoading();
        Require(session.PlaybackRate == 1.0, "Opening another video must restore 1.0x playback.");
        session.SetPlaybackRate(0.25);
        session.StepPlaybackRate(-1);
        Require(session.PlaybackRate == 0.25, "Playback-rate stepping must stop at the lower boundary.");
        session.StepPlaybackRate(1);
        Require(session.PlaybackRate == 0.5, "Playback-rate stepping must use the next defined value.");
        session.SetPlaybackRate(2.0);
        session.StepPlaybackRate(1);
        Require(session.PlaybackRate == 2.0, "Playback-rate stepping must stop at the upper boundary.");
        checks.Add("playback-rate-selection");

        Require(PlaybackShortcutMap.Resolve(Key.Space, ModifierKeys.None, false) == PlaybackShortcutAction.TogglePlayPause, "Space must map to play/pause.");
        Require(PlaybackShortcutMap.Resolve(Key.Left, ModifierKeys.None, false) == PlaybackShortcutAction.SeekBackward, "Left must map to backward seek.");
        Require(PlaybackShortcutMap.Resolve(Key.Up, ModifierKeys.None, false) == PlaybackShortcutAction.IncreasePlaybackRate, "Up must map to the next speed.");
        Require(PlaybackShortcutMap.Resolve(Key.Enter, ModifierKeys.Alt, false) == PlaybackShortcutAction.ToggleFullscreen, "Alt+Enter must map to fullscreen.");
        Require(PlaybackShortcutMap.Resolve(Key.Escape, ModifierKeys.None, true) == PlaybackShortcutAction.ExitFullscreen, "Escape must exit fullscreen.");
        Require(PlaybackShortcutMap.Resolve(Key.Space, ModifierKeys.Control, false) == PlaybackShortcutAction.None, "Modified playback keys must not trigger shortcuts.");
        checks.Add("shortcut-mapping");

        Require(MainWindow.SeekShortcutSeconds == 5, "Keyboard seek must move by five seconds.");
        Require(MainWindow.LongPressDurationMilliseconds == 400, "Temporary 2.0x playback must activate after 400 ms.");
        Require(MainWindow.FullscreenControlsAutoHideMilliseconds == 3000, "Fullscreen controls must hide after three seconds.");
        checks.Add("gesture-and-fullscreen-settings");

        session.CompleteLoading(TimeSpan.FromSeconds(30));
        Require(session.Position == TimeSpan.FromSeconds(30), "A valid registered start position must be applied.");
        session.CompleteLoading(TimeSpan.FromHours(2));
        Require(session.Position == TimeSpan.Zero, "An out-of-range registered start position must fall back to zero.");
        checks.Add("registered-start-position");

        Require(SeekUiGeometry.PositionFromPointer(50, 100, 200) == 100, "Pointer position must map to media time.");
        Require(SeekUiGeometry.PositionFromPointer(-10, 100, 200) == 0, "Pointer position must clamp at the start.");
        Require(SeekUiGeometry.PopupOffset(0, 500, 240) == 0, "Thumbnail popup must clamp at the left edge.");
        Require(SeekUiGeometry.PopupOffset(500, 500, 240) == 260, "Thumbnail popup must clamp at the right edge.");
        Require(SeekUiGeometry.MarkerOffset(50, 100, 400, 8) == 196, "Start marker must center on the registered time.");
        Require(SeekUiGeometry.ThumbnailSlot(50, 100, 1.0) == 50, "Hover time must map to the configured thumbnail slot.");
        Require(SeekUiGeometry.ThumbnailSlot(1.24, 100, 0.25) * 0.25 == 1.25, "Quarter-percent previews must map to their actual generated slot.");
        Require(SeekUiGeometry.ThumbnailSlotCount(1.0) == 101, "A 1% interval must include both ends of the media.");
        Require(SeekUiGeometry.ThumbnailSlotCount(0.25) == 401, "A quarter-percent interval must include both ends of the media.");
        Require(MainWindow.ThumbnailPreviewDelayMilliseconds == 180, "Thumbnail waiting state must use the review delay.");
        Require(MainWindow.BackgroundThumbnailStepMilliseconds == 30, "The mock must populate its thumbnail cache in the background.");
        checks.Add("seek-thumbnail-geometry");

        session.ShowError();
        Require(session.State == MediaUiState.Error && !session.CanControlPlayback, "Error state must disable controls.");
        checks.Add("error-state");

        session.CompleteLoading(TimeSpan.FromSeconds(42));
        var durationBeforePlaybackError = session.Duration;
        session.ShowPlaybackError();
        Require(session.State == MediaUiState.Error, "A playback failure must enter the error state.");
        Require(session.Position == TimeSpan.FromSeconds(42) && session.Duration == durationBeforePlaybackError, "A mid-playback failure must preserve the last position and duration.");
        Require(!session.CanControlPlayback, "A mid-playback failure must disable playback controls.");
        checks.Add("mid-playback-error-state");

        var failureDefinitions = Enum.GetValues<ReviewFailureScenario>()
            .Select(ErrorFeedbackCatalog.Get)
            .ToArray();
        Require(failureDefinitions.Length == 8, "Every review failure scenario must have feedback.");
        Require(failureDefinitions.All(definition => !string.IsNullOrWhiteSpace(definition.Message)), "Failure feedback must always explain the problem and next action.");
        Require(failureDefinitions.Count(definition => definition.Severity == NotificationSeverity.Warning) == 2, "Recoverable persistence failures must use warning feedback.");
        Require(failureDefinitions.Single(definition => definition.Outcome == FailureOutcome.PlaybackError).Severity == NotificationSeverity.Error, "A mid-playback failure must use error feedback.");
        checks.Add("failure-feedback-catalog");

        Require(UiAccessibilityValidation.ContrastRatio("#F2F6FC", "#0C1119") >= 4.5, "Primary text must meet the normal-text contrast target.");
        Require(UiAccessibilityValidation.ContrastRatio("#9DABC0", "#0C1119") >= 4.5, "Muted text must meet the normal-text contrast target.");
        Require(UiAccessibilityValidation.ContrastRatio("#07111A", "#47B8FF") >= 4.5, "Primary-button text must meet the normal-text contrast target.");
        Require(UiAccessibilityValidation.ContrastRatio("#FF6B79", "#3A1C28") >= 4.5, "Error indicators must meet the normal-text contrast target.");
        Require(UiAccessibilityValidation.ContrastRatio("#17202C", "#EEF2F7") >= 4.5, "Menu text must meet the normal-text contrast target.");
        checks.Add("theme-contrast");

        var window = new MainWindow
        {
            ShowInTaskbar = false,
        };
        var recentFilesMenu = (MenuItem?)window.FindName("RecentFilesMenuItem");
        if (recentFilesMenu is null)
        {
            throw new InvalidOperationException("Recent-files menu must be present.");
        }

        var removeMissingMenu = recentFilesMenu.Items
            .OfType<MenuItem>()
            .SingleOrDefault(item => Equals(item.Header, "欠損した項目を履歴から削除"));
        if (removeMissingMenu is null)
        {
            throw new InvalidOperationException("Missing-file removal submenu must be present.");
        }

        var removeAllMissingItem = removeMissingMenu.Items
            .OfType<MenuItem>()
            .SingleOrDefault(item => Equals(item.Header, "すべて削除"));
        if (removeAllMissingItem is null)
        {
            throw new InvalidOperationException("Multiple missing files must expose a bulk removal command.");
        }

        removeAllMissingItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, removeAllMissingItem));
        Require(
            !recentFilesMenu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "欠損した項目を履歴から削除")),
            "Bulk removal must remove every missing history entry.");
        Require(
            recentFilesMenu.Items.OfType<MenuItem>().Count(item => item.Tag is not null) == 3,
            "Bulk removal must preserve existing history entries and the clear-history command.");
        checks.Add("recent-missing-bulk-delete-menu");

        window.RunReviewFailureScenario(ReviewFailureScenario.UnsupportedCodecBeforeSwitch);
        var reviewStateText = (TextBlock?)window.FindName("ReviewStateText");
        var notificationToast = (FrameworkElement?)window.FindName("NotificationToast");
        Require(reviewStateText?.Text == "MEDIA-PLAYING", "A pre-switch codec rejection must preserve current playback.");
        Require(notificationToast?.Visibility == Visibility.Visible, "A pre-switch rejection must show non-modal feedback.");
        window.RunReviewFailureScenario(ReviewFailureScenario.CorruptDuringPlayback);
        var playbackErrorOverlay = (FrameworkElement?)window.FindName("PlaybackErrorOverlay");
        Require(reviewStateText?.Text == "MEDIA-ERROR（再生中）", "A mid-playback corruption must expose a distinct error state.");
        Require(playbackErrorOverlay?.Visibility == Visibility.Visible, "A mid-playback corruption must show an in-content recovery action.");
        window.RunReviewFailureScenario(ReviewFailureScenario.MissingFileWithoutMedia);
        Require(reviewStateText?.Text == "MEDIA-EMPTY", "A missing file without current media must return to the empty state.");
        checks.Add("failure-feedback-window-states");

        Require(window.Width == 1000 && window.Height == 650, "The main window must retain its approved initial size.");
        Require(window.MinWidth == 720 && window.MinHeight == 480, "The main window must retain its approved minimum size.");
        var timeText = (FrameworkElement?)window.FindName("TimeText");
        var notificationText = (FrameworkElement?)window.FindName("NotificationToastText");
        Require(System.Windows.Automation.AutomationProperties.GetName(timeText) == "再生時刻", "Playback time must expose an accessible name.");
        Require(
            System.Windows.Automation.AutomationProperties.GetLiveSetting(notificationText)
                == System.Windows.Automation.AutomationLiveSetting.Polite,
            "Non-modal feedback must expose a polite live region.");
        var reviewPanelMenu = (MenuItem?)window.FindName("ReviewPanelMenuItem");
        var reviewPanel = (FrameworkElement?)window.FindName("ReviewPanel");
        if (reviewPanelMenu is null || reviewPanel is null)
        {
            throw new InvalidOperationException("Review-panel controls must be present.");
        }

        reviewPanelMenu.IsChecked = false;
        Require(reviewPanel.Visibility == Visibility.Collapsed, "Automation-driven review-panel toggles must update visibility.");
        reviewPanelMenu.IsChecked = true;
        Require(reviewPanel.Visibility == Visibility.Visible, "The review panel must return when its toggle is checked.");
        checks.Add("main-window-size-and-automation");
        window.Close();
        var settingsWindow = new ThumbnailSettingsWindow(1.25)
        {
            ShowInTaskbar = false,
        };
        Require(settingsWindow.SelectedIntervalPercent == 1.25, "Thumbnail settings must preserve a quarter-percent value.");
        settingsWindow.Close();
        checks.Add("thumbnail-settings-window");

        var playlist = new PlaylistMockModel();
        const string firstPath = @"C:\Videos\first.mp4";
        const string missingPath = @"C:\Videos\missing.mp4";
        const string errorPath = @"C:\Videos\error.wmv";
        const string lastPath = @"C:\Videos\last.wmv";
        playlist.Add(firstPath);
        playlist.Add(missingPath, isMissing: true);
        playlist.Add(errorPath, hasLoadError: true);
        playlist.Add(lastPath);
        playlist.Add(firstPath);
        Require(playlist.Entries.Count == 5, "A playlist must retain duplicate registrations.");
        Require(playlist.StartFromFirst()?.Path == firstPath, "Playlist playback must start from the first playable entry.");
        Require(playlist.Advance()?.Path == lastPath, "Continuous playback must skip missing and load-error entries.");
        var duplicate = playlist.Entries[^1];
        Require(playlist.StartFrom(duplicate) == duplicate, "Starting from a selected playable entry must preserve that registration.");
        playlist.Loop = true;
        Require(playlist.Advance() == playlist.Entries[0], "Loop playback must return to the first playable entry.");
        playlist.MoveToInsertionIndex(duplicate, 0);
        Require(playlist.Entries[0] == duplicate && playlist.Entries[0].Order == 1, "Drag reorder must update both collection order and displayed order.");
        playlist.Remove([duplicate]);
        Require(playlist.Entries.Count == 4 && playlist.Entries.Count(entry => entry.Path == firstPath) == 1, "Removing an entry must remove only the selected registration.");
        playlist.CancelContinuousPlayback();
        Require(playlist.CurrentEntry is null, "Direct main-window opens must cancel continuous playback.");
        checks.Add("playlist-sequencing-and-editing");

        var playlistWindow = new PlaylistWindow
        {
            ShowInTaskbar = false,
        };
        Require(playlistWindow.Width == 680 && playlistWindow.Height == 540, "The playlist window must retain its approved initial size.");
        Require(playlistWindow.MinWidth == 560 && playlistWindow.MinHeight == 400, "The playlist window must retain its approved minimum size.");
        var playlistList = (FrameworkElement?)playlistWindow.FindName("PlaylistList");
        Require(System.Windows.Automation.AutomationProperties.GetName(playlistList) == "プレイリスト項目", "The playlist must expose an accessible name.");
        playlistWindow.Close();
        checks.Add("playlist-window-construction");
        checks.Add("window-construction");

        Require(FileOpenRequestClassifier.Classify([]).Kind == FileOpenRequestKind.None, "Empty drop must be ignored.");
        Require(FileOpenRequestClassifier.Classify([@"C:\動画\sample.MP4"]).Kind == FileOpenRequestKind.SingleSupportedFile, "MP4 drop must be accepted case-insensitively.");
        Require(FileOpenRequestClassifier.Classify([@"C:\動画\sample.wmv"]).Kind == FileOpenRequestKind.SingleSupportedFile, "WMV drop must be accepted.");
        Require(FileOpenRequestClassifier.Classify([@"C:\動画\sample.avi"]).Kind == FileOpenRequestKind.UnsupportedFile, "Unsupported extension must be rejected.");
        Require(FileOpenRequestClassifier.Classify([@"C:\動画\one.mp4", @"C:\動画\two.wmv"]).Kind == FileOpenRequestKind.MultipleFiles, "Multiple-file drop must be rejected without opening either file.");
        checks.Add("file-open-request-classification");

        if (string.IsNullOrWhiteSpace(reportPath))
        {
            return;
        }

        var fullPath = Path.GetFullPath(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(
            fullPath,
            JsonSerializer.Serialize(
                new
                {
                    result = "passed",
                    checks,
                },
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
