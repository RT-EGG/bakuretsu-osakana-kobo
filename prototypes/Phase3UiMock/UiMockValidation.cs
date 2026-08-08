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

        session.ShowError();
        Require(session.State == MediaUiState.Error && !session.CanControlPlayback, "Error state must disable controls.");
        checks.Add("error-state");

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
        window.Close();
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
