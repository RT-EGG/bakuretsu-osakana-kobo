using System.IO;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace BakuretsuOsakanaKobo.Phase3UiMock;

public partial class MainWindow : Window
{
    private const string ApplicationTitle = "爆裂おさかな工房";
    private const string DefaultMockFilePath = @"C:\Videos\サンプル動画.mp4";
    internal const int VolumeWheelStepPercent = 5;
    internal const int SeekShortcutSeconds = 5;
    internal const int LongPressDurationMilliseconds = 400;
    internal const int FullscreenControlsAutoHideMilliseconds = 3000;
    internal const int ThumbnailPreviewDelayMilliseconds = 180;
    internal const int BackgroundThumbnailStepMilliseconds = 30;
    internal const double ThumbnailPreviewWidth = 240;
    internal const double ThumbnailPreviewHeight = 175;
    internal const double ThumbnailPreviewGap = 8;

    private readonly MockPlaybackSession _session = new();
    private readonly DispatcherTimer _playbackTimer;
    private readonly DispatcherTimer _loadingTimer;
    private readonly DispatcherTimer _longPressTimer;
    private readonly DispatcherTimer _fullscreenControlsTimer;
    private readonly DispatcherTimer _thumbnailPreviewTimer;
    private readonly DispatcherTimer _backgroundThumbnailTimer;
    private readonly Dictionary<string, TimeSpan> _startPositions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _generatedThumbnailSlots = [];
    private readonly List<RecentFileMock> _recentFiles =
    [
        new(@"C:\Videos\海辺の記録.mp4", false),
        new(@"C:\Videos\操作説明.wmv", false),
        new(@"D:\移動済み\見つからない動画.mp4", true),
        new(@"E:\削除済み\もう一つの動画.wmv", true),
    ];
    private bool _isSeeking;
    private bool _isLongPressPending;
    private bool _isTemporaryPlaybackRate;
    private bool _isFullscreen;
    private Point _longPressStartPoint;
    private double _playbackRateBeforeLongPress = 1.0;
    private WindowState _windowStateBeforeFullscreen;
    private WindowStyle _windowStyleBeforeFullscreen;
    private ResizeMode _resizeModeBeforeFullscreen;
    private TimeSpan _pendingThumbnailPosition;
    private int _pendingThumbnailSlot;
    private int _nextBackgroundThumbnailSlot;
    private double _thumbnailIntervalPercent = 1.0;
    private double _activeThumbnailIntervalPercent = 1.0;
    private string _currentFilePath = DefaultMockFilePath;
    private PlaylistWindow? _playlistWindow;

    public MainWindow()
    {
        InitializeComponent();

        _playbackTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _playbackTimer.Tick += PlaybackTimer_OnTick;

        _loadingTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(900),
        };
        _loadingTimer.Tick += LoadingTimer_OnTick;

        _longPressTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(LongPressDurationMilliseconds),
        };
        _longPressTimer.Tick += LongPressTimer_OnTick;

        _fullscreenControlsTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(FullscreenControlsAutoHideMilliseconds),
        };
        _fullscreenControlsTimer.Tick += FullscreenControlsTimer_OnTick;

        _thumbnailPreviewTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(ThumbnailPreviewDelayMilliseconds),
        };
        _thumbnailPreviewTimer.Tick += ThumbnailPreviewTimer_OnTick;

        _backgroundThumbnailTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(BackgroundThumbnailStepMilliseconds),
        };
        _backgroundThumbnailTimer.Tick += BackgroundThumbnailTimer_OnTick;

        _session.Changed += Session_OnChanged;
        VolumeSlider.ValueChanged += VolumeSlider_OnValueChanged;
        ReviewSpeedComboBox.SelectionChanged += ReviewSpeedComboBox_OnSelectionChanged;
        Closed += MainWindow_OnClosed;
        RebuildRecentFilesMenu();
        RenderState();
    }

    private void OpenFileMenuItem_OnClick(object sender, RoutedEventArgs e) => ShowOpenFileDialog();

    private void OpenFileButton_OnClick(object sender, RoutedEventArgs e) => ShowOpenFileDialog();

    private void OpenReviewMockButton_OnClick(object sender, RoutedEventArgs e) => BeginMockOpen(DefaultMockFilePath);

    private void ShowOpenFileDialog()
    {
        var dialog = new OpenFileDialog
        {
            Title = "動画を開く",
            Filter = "動画ファイル (*.mp4;*.wmv)|*.mp4;*.wmv|MP4 ファイル (*.mp4)|*.mp4|WMV ファイル (*.wmv)|*.wmv",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) == true)
        {
            BeginMockOpen(dialog.FileName);
        }
    }

    private void BeginMockOpen(string path, bool fromPlaylist = false)
    {
        if (!fromPlaylist)
        {
            _playlistWindow?.CancelContinuousPlayback();
        }

        CloseSeekThumbnail();
        _backgroundThumbnailTimer.Stop();
        _generatedThumbnailSlots.Clear();
        _nextBackgroundThumbnailSlot = 0;
        _currentFilePath = path;
        _activeThumbnailIntervalPercent = _thumbnailIntervalPercent;
        LoadingFileNameText.Text = Path.GetFileName(path);
        NotificationToast.Visibility = Visibility.Collapsed;
        _loadingTimer.Stop();
        _session.BeginLoading();
        _loadingTimer.Start();
    }

    private void LoadingTimer_OnTick(object? sender, EventArgs e)
    {
        _loadingTimer.Stop();
        _startPositions.TryGetValue(_currentFilePath, out var startPosition);
        _session.CompleteLoading(startPosition);
        AddRecentFile(_currentFilePath);
        StartBackgroundThumbnailGeneration();
    }

    private void PlaybackTimer_OnTick(object? sender, EventArgs e)
    {
        var wasPlaying = _session.State == MediaUiState.Playing;
        _session.Advance(_playbackTimer.Interval);
        if (wasPlaying
            && _session.State == MediaUiState.Paused
            && _session.HasKnownDuration
            && _session.Position == _session.Duration)
        {
            _playlistWindow?.ContinueAfterNaturalEnd();
        }
    }

    private void PlayPauseButton_OnClick(object sender, RoutedEventArgs e) => _session.TogglePlayPause();

    private void MuteButton_OnClick(object sender, RoutedEventArgs e) => _session.ToggleMute();

    private void VolumeSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        _session.SetVolume((int)Math.Round(e.NewValue));

    private void VideoSurface_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_session.CanControlPlayback || e.Delta == 0)
        {
            return;
        }

        _session.SetVolume(_session.VolumePercent + (e.Delta > 0 ? VolumeWheelStepPercent : -VolumeWheelStepPercent));
        e.Handled = true;
    }

    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var action = PlaybackShortcutMap.Resolve(key, Keyboard.Modifiers, _isFullscreen);
        if (action == PlaybackShortcutAction.None)
        {
            return;
        }

        if (action == PlaybackShortcutAction.ToggleFullscreen)
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }

        if (action == PlaybackShortcutAction.ExitFullscreen)
        {
            ExitFullscreen();
            e.Handled = true;
            return;
        }

        if (IsShortcutInputFocused() || !_session.CanControlPlayback)
        {
            return;
        }

        switch (action)
        {
            case PlaybackShortcutAction.TogglePlayPause:
                _session.TogglePlayPause();
                break;
            case PlaybackShortcutAction.SeekBackward:
                _session.SeekTo(_session.Position - TimeSpan.FromSeconds(SeekShortcutSeconds));
                break;
            case PlaybackShortcutAction.SeekForward:
                _session.SeekTo(_session.Position + TimeSpan.FromSeconds(SeekShortcutSeconds));
                break;
            case PlaybackShortcutAction.IncreasePlaybackRate:
                _session.StepPlaybackRate(1);
                break;
            case PlaybackShortcutAction.DecreasePlaybackRate:
                _session.StepPlaybackRate(-1);
                break;
        }

        e.Handled = true;
    }

    private static bool IsShortcutInputFocused() => Keyboard.FocusedElement is DependencyObject focusedElement
        && HasInputAncestor(focusedElement);

    private static bool HasInputAncestor(DependencyObject? source)
    {
        for (var current = source; current is not null; current = GetParent(current))
        {
            if (current is ButtonBase or Slider or ComboBox or TextBoxBase or PasswordBox or MenuItem)
            {
                return true;
            }
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject current) => current is Visual or System.Windows.Media.Media3D.Visual3D
        ? VisualTreeHelper.GetParent(current)
        : LogicalTreeHelper.GetParent(current);

    private void VideoSurface_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_session.CanControlPlayback || HasInputAncestor(e.OriginalSource as DependencyObject))
        {
            return;
        }

        VideoSurface.Focus();
        if (e.ClickCount >= 2)
        {
            EndLongPressGesture();
            ToggleFullscreen();
            e.Handled = true;
            return;
        }

        _longPressStartPoint = e.GetPosition(VideoSurface);
        _isLongPressPending = true;
        _longPressTimer.Stop();
        _longPressTimer.Start();
        Mouse.Capture(VideoSurface);
    }

    private void VideoSurface_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isLongPressPending || _isTemporaryPlaybackRate)
        {
            return;
        }

        var currentPoint = e.GetPosition(VideoSurface);
        if (Math.Abs(currentPoint.X - _longPressStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(currentPoint.Y - _longPressStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            EndLongPressGesture();
        }
    }

    private void VideoSurface_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var wasGestureActive = _isLongPressPending || _isTemporaryPlaybackRate;
        EndLongPressGesture();
        e.Handled = wasGestureActive;
    }

    private void LongPressTimer_OnTick(object? sender, EventArgs e)
    {
        _longPressTimer.Stop();
        if (!_isLongPressPending || Mouse.LeftButton != MouseButtonState.Pressed || !_session.CanControlPlayback)
        {
            EndLongPressGesture();
            return;
        }

        _isLongPressPending = false;
        _isTemporaryPlaybackRate = true;
        _playbackRateBeforeLongPress = _session.PlaybackRate;
        _session.SetPlaybackRate(2.0);
    }

    private void EndLongPressGesture()
    {
        _longPressTimer.Stop();
        _isLongPressPending = false;
        if (_isTemporaryPlaybackRate)
        {
            _isTemporaryPlaybackRate = false;
            _session.SetPlaybackRate(_playbackRateBeforeLongPress);
        }

        if (Mouse.Captured == VideoSurface)
        {
            Mouse.Capture(null);
        }
    }

    private void Window_OnDeactivated(object? sender, EventArgs e)
    {
        EndLongPressGesture();
        CloseSeekThumbnail();
    }

    private void Window_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_isFullscreen)
        {
            ShowFullscreenControls();
        }
    }

    private void VideoContextMenu_OnOpened(object sender, RoutedEventArgs e)
    {
        _fullscreenControlsTimer.Stop();
        var speedMenu = VideoContextMenu.Items.OfType<MenuItem>().First();
        speedMenu.IsEnabled = _session.CanControlPlayback;
        foreach (var rateItem in speedMenu.Items.OfType<MenuItem>())
        {
            rateItem.IsChecked = double.TryParse(
                rateItem.Tag as string,
                System.Globalization.CultureInfo.InvariantCulture,
                out var rate) && Math.Abs(rate - _session.PlaybackRate) < double.Epsilon;
        }

        var fullscreenItem = VideoContextMenu.Items.OfType<MenuItem>().Last();
        fullscreenItem.IsChecked = _isFullscreen;
        var startPositionItem = VideoContextMenu.Items
            .OfType<MenuItem>()
            .Single(item => Equals(item.Tag, "set-start-position"));
        startPositionItem.IsEnabled = _session.CanControlPlayback;
    }

    private void VideoContextMenu_OnClosed(object sender, RoutedEventArgs e)
    {
        if (_isFullscreen)
        {
            ShowFullscreenControls();
        }
    }

    private void VideoContextMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } menuItem)
        {
            return;
        }

        if (tag == "fullscreen")
        {
            ToggleFullscreen();
            menuItem.IsChecked = _isFullscreen;
        }
        else if (tag == "set-start-position" && _session.CanControlPlayback)
        {
            CloseSeekThumbnail();
            _startPositions[_currentFilePath] = _session.Position;
            UpdateStartPositionMarker();
            ShowNotification(
                $"再生開始位置を {MockPlaybackSession.FormatTime(_session.Position)} に設定しました。",
                isError: false);
        }
        else if (_session.CanControlPlayback
                 && double.TryParse(tag, System.Globalization.CultureInfo.InvariantCulture, out var playbackRate))
        {
            _session.SetPlaybackRate(playbackRate);
            menuItem.IsChecked = true;
        }

        e.Handled = true;
    }

    private void ToggleFullscreen()
    {
        if (_isFullscreen)
        {
            ExitFullscreen();
        }
        else
        {
            EnterFullscreen();
        }
    }

    private void EnterFullscreen()
    {
        if (_isFullscreen)
        {
            return;
        }

        EndLongPressGesture();
        CloseSeekThumbnail();
        _windowStateBeforeFullscreen = WindowState;
        _windowStyleBeforeFullscreen = WindowStyle;
        _resizeModeBeforeFullscreen = ResizeMode;
        WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        MainMenu.Visibility = Visibility.Collapsed;
        ReviewPanel.Visibility = Visibility.Collapsed;
        Grid.SetRow(VideoSurface, 0);
        Grid.SetRowSpan(VideoSurface, 3);
        Grid.SetRow(PlaybackControls, 1);
        PlaybackControls.VerticalAlignment = VerticalAlignment.Bottom;
        PlaybackControls.Opacity = 0.94;
        WindowState = WindowState.Maximized;
        _isFullscreen = true;
        ShowFullscreenControls();
    }

    private void ExitFullscreen()
    {
        if (!_isFullscreen)
        {
            return;
        }

        _fullscreenControlsTimer.Stop();
        CloseSeekThumbnail();
        PlaybackControls.Visibility = Visibility.Visible;
        WindowState = WindowState.Normal;
        WindowStyle = _windowStyleBeforeFullscreen;
        ResizeMode = _resizeModeBeforeFullscreen;
        Grid.SetRow(VideoSurface, 1);
        Grid.SetRowSpan(VideoSurface, 1);
        Grid.SetRow(PlaybackControls, 2);
        PlaybackControls.VerticalAlignment = VerticalAlignment.Stretch;
        PlaybackControls.Opacity = 1;
        MainMenu.Visibility = Visibility.Visible;
        ReviewPanel.Visibility = ReviewPanelMenuItem.IsChecked ? Visibility.Visible : Visibility.Collapsed;
        _isFullscreen = false;
        WindowState = _windowStateBeforeFullscreen;
    }

    private void ShowFullscreenControls()
    {
        PlaybackControls.Visibility = Visibility.Visible;
        _fullscreenControlsTimer.Stop();
        _fullscreenControlsTimer.Start();
    }

    private void FullscreenControlsTimer_OnTick(object? sender, EventArgs e)
    {
        _fullscreenControlsTimer.Stop();
        if (!_isFullscreen)
        {
            return;
        }

        if (PlaybackControls.IsMouseOver || VideoContextMenu.IsOpen)
        {
            _fullscreenControlsTimer.Start();
            return;
        }

        PlaybackControls.Visibility = Visibility.Collapsed;
        CloseSeekThumbnail();
    }

    private void ReviewSpeedComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReviewSpeedComboBox.SelectedItem is ComboBoxItem { Tag: string rateText }
            && double.TryParse(rateText, System.Globalization.CultureInfo.InvariantCulture, out var playbackRate))
        {
            _session.SetPlaybackRate(playbackRate);
        }
    }

    private void SeekSlider_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _isSeeking = true;

    private void SeekSlider_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isSeeking)
        {
            return;
        }

        _isSeeking = false;
        _session.SeekTo(TimeSpan.FromSeconds(SeekSlider.Value));
    }

    private void SeekSlider_OnMouseEnter(object sender, MouseEventArgs e) => UpdateSeekThumbnail(e.GetPosition(SeekSlider).X);

    private void SeekSlider_OnMouseMove(object sender, MouseEventArgs e) => UpdateSeekThumbnail(e.GetPosition(SeekSlider).X);

    private void SeekSlider_OnMouseLeave(object sender, MouseEventArgs e) => CloseSeekThumbnail();

    private void SeekSlider_OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateStartPositionMarker();

    private void UpdateSeekThumbnail(double pointerX)
    {
        if (!_session.CanControlPlayback || !_session.HasKnownDuration || SeekSlider.ActualWidth <= 0)
        {
            CloseSeekThumbnail();
            return;
        }

        var positionSeconds = SeekUiGeometry.PositionFromPointer(
            pointerX,
            SeekSlider.ActualWidth,
            _session.Duration.TotalSeconds);
        _pendingThumbnailPosition = TimeSpan.FromSeconds(positionSeconds);
        _pendingThumbnailSlot = SeekUiGeometry.ThumbnailSlot(
            positionSeconds,
            _session.Duration.TotalSeconds,
            _activeThumbnailIntervalPercent);
        ThumbnailTimeText.Text = MockPlaybackSession.FormatTime(_pendingThumbnailPosition);
        SeekThumbnailPopup.HorizontalOffset = SeekUiGeometry.PopupOffset(
            pointerX,
            SeekSlider.ActualWidth,
            ThumbnailPreviewWidth);
        SeekThumbnailPopup.VerticalOffset = -(ThumbnailPreviewHeight + ThumbnailPreviewGap);
        SeekThumbnailPopup.IsOpen = true;
        _thumbnailPreviewTimer.Stop();
        if (_generatedThumbnailSlots.Contains(_pendingThumbnailSlot))
        {
            ShowGeneratedThumbnail();
        }
        else
        {
            ThumbnailLoadingOverlay.Visibility = Visibility.Visible;
            ThumbnailPreviewArtwork.Opacity = 0.38;
            _thumbnailPreviewTimer.Start();
        }
    }

    private void ThumbnailPreviewTimer_OnTick(object? sender, EventArgs e)
    {
        _thumbnailPreviewTimer.Stop();
        if (!SeekThumbnailPopup.IsOpen || !_session.HasKnownDuration)
        {
            return;
        }

        _generatedThumbnailSlots.Add(_pendingThumbnailSlot);
        ShowGeneratedThumbnail();
    }

    private void ShowGeneratedThumbnail()
    {
        var percent = Math.Min(_pendingThumbnailSlot * _activeThumbnailIntervalPercent, 100);
        ThumbnailFrameText.Text = $"{percent:0.##}%";
        ThumbnailPreviewArtwork.Opacity = 1;
        ThumbnailLoadingOverlay.Visibility = Visibility.Collapsed;
    }

    private void StartBackgroundThumbnailGeneration()
    {
        _backgroundThumbnailTimer.Stop();
        if (!_session.HasKnownDuration)
        {
            return;
        }

        _backgroundThumbnailTimer.Start();
    }

    private void BackgroundThumbnailTimer_OnTick(object? sender, EventArgs e)
    {
        if (!_session.HasKnownDuration || _isSeeking)
        {
            return;
        }

        var slotCount = SeekUiGeometry.ThumbnailSlotCount(_activeThumbnailIntervalPercent);
        while (_nextBackgroundThumbnailSlot < slotCount
               && _generatedThumbnailSlots.Contains(_nextBackgroundThumbnailSlot))
        {
            _nextBackgroundThumbnailSlot++;
        }

        if (_nextBackgroundThumbnailSlot < slotCount)
        {
            var generatedSlot = _nextBackgroundThumbnailSlot;
            _generatedThumbnailSlots.Add(generatedSlot);
            _nextBackgroundThumbnailSlot++;
            if (SeekThumbnailPopup.IsOpen
                && ThumbnailLoadingOverlay.Visibility == Visibility.Visible
                && generatedSlot == _pendingThumbnailSlot)
            {
                _thumbnailPreviewTimer.Stop();
                ShowGeneratedThumbnail();
            }
        }

        if (_nextBackgroundThumbnailSlot >= slotCount)
        {
            _backgroundThumbnailTimer.Stop();
        }
    }

    private void CloseSeekThumbnail()
    {
        _thumbnailPreviewTimer.Stop();
        SeekThumbnailPopup.IsOpen = false;
    }

    private void UpdateStartPositionMarker()
    {
        if (!_session.HasKnownDuration
            || !_startPositions.TryGetValue(_currentFilePath, out var startPosition)
            || startPosition > _session.Duration
            || SeekSlider.ActualWidth <= 0)
        {
            StartPositionMarker.Visibility = Visibility.Collapsed;
            return;
        }

        StartPositionMarker.Visibility = Visibility.Visible;
        Canvas.SetLeft(
            StartPositionMarker,
            SeekUiGeometry.MarkerOffset(
                startPosition.TotalSeconds,
                _session.Duration.TotalSeconds,
                SeekSlider.ActualWidth,
                StartPositionMarker.Width));
    }

    private void ThumbnailSettingsMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ThumbnailSettingsWindow(_thumbnailIntervalPercent)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _thumbnailIntervalPercent = dialog.SelectedIntervalPercent;
        ShowNotification(
            $"サムネイル生成間隔を {_thumbnailIntervalPercent:0.00}% に変更しました。次に開く動画から適用します。",
            isError: false);
    }

    private void ReviewStateButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string stateName } || !Enum.TryParse<MediaUiState>(stateName, out var state))
        {
            return;
        }

        _loadingTimer.Stop();
        NotificationToast.Visibility = Visibility.Collapsed;

        switch (state)
        {
            case MediaUiState.Empty:
                _session.Reset();
                break;
            case MediaUiState.Loading:
                _session.BeginLoading();
                break;
            case MediaUiState.Playing:
                _session.CompleteLoading();
                break;
            case MediaUiState.Paused:
                _session.CompleteLoading();
                _session.TogglePlayPause();
                break;
            case MediaUiState.Error:
                _session.ShowError();
                ShowNotification("動画を開けませんでした。別のファイルを選択してください。", isError: true);
                break;
        }
    }

    private void ReviewPanelMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem)
        {
            ReviewPanel.Visibility = menuItem.IsChecked ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void PlaylistMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (PlaylistMenuItem.IsChecked)
        {
            ShowPlaylistWindow();
            return;
        }

        _playlistWindow?.Close();
    }

    private void ShowPlaylistWindow()
    {
        if (_playlistWindow is not null)
        {
            _playlistWindow.Activate();
            return;
        }

        var playlistWindow = new PlaylistWindow
        {
            Owner = this,
        };
        playlistWindow.PlayRequested += PlaylistWindow_OnPlayRequested;
        playlistWindow.Closed += PlaylistWindow_OnClosed;
        playlistWindow.UpdateCurrentMedia(_session.CanControlPlayback ? _currentFilePath : null);
        _playlistWindow = playlistWindow;
        playlistWindow.Show();
    }

    private void PlaylistWindow_OnPlayRequested(object? sender, PlaylistPlayRequestedEventArgs e) =>
        BeginMockOpen(e.Entry.Path, fromPlaylist: true);

    private void PlaylistWindow_OnClosed(object? sender, EventArgs e)
    {
        if (_playlistWindow is not null)
        {
            _playlistWindow.PlayRequested -= PlaylistWindow_OnPlayRequested;
            _playlistWindow.Closed -= PlaylistWindow_OnClosed;
            _playlistWindow = null;
        }

        PlaylistMenuItem.IsChecked = false;
    }

    private void RecentFilesMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not MenuItem menuItem)
        {
            return;
        }

        switch (menuItem.Tag)
        {
            case RecentFileMock { IsMissing: false } recentFile:
                BeginMockOpen(recentFile.Path);
                e.Handled = true;
                break;
            case RemoveRecentAction removeAction:
                _recentFiles.Remove(removeAction.RecentFile);
                RebuildRecentFilesMenu();
                e.Handled = true;
                break;
            case RemoveAllMissingAction:
                _recentFiles.RemoveAll(file => file.IsMissing);
                RebuildRecentFilesMenu();
                e.Handled = true;
                break;
            case ClearRecentAction:
                _recentFiles.Clear();
                RebuildRecentFilesMenu();
                e.Handled = true;
                break;
        }
    }

    private void RebuildRecentFilesMenu()
    {
        RecentFilesMenuItem.Items.Clear();

        if (_recentFiles.Count == 0)
        {
            RecentFilesMenuItem.Items.Add(new MenuItem { Header = "（履歴はありません）", IsEnabled = false });
            return;
        }

        foreach (var recentFile in _recentFiles)
        {
            var menuItem = new MenuItem
            {
                Header = recentFile.IsMissing
                    ? $"{Path.GetFileName(recentFile.Path)}（見つかりません）"
                    : Path.GetFileName(recentFile.Path),
                ToolTip = recentFile.Path,
                Tag = recentFile,
                IsEnabled = !recentFile.IsMissing,
            };
            RecentFilesMenuItem.Items.Add(menuItem);
        }

        var missingFiles = _recentFiles.Where(file => file.IsMissing).ToList();
        if (missingFiles.Count > 0)
        {
            RecentFilesMenuItem.Items.Add(new Separator());
            var removeMissingMenu = new MenuItem { Header = "欠損した項目を履歴から削除" };
            foreach (var recentFile in missingFiles)
            {
                var removeItem = new MenuItem
                {
                    Header = Path.GetFileName(recentFile.Path),
                    ToolTip = recentFile.Path,
                    Tag = new RemoveRecentAction(recentFile),
                };
                removeMissingMenu.Items.Add(removeItem);
            }

            if (missingFiles.Count > 1)
            {
                removeMissingMenu.Items.Add(new Separator());
                removeMissingMenu.Items.Add(new MenuItem
                {
                    Header = "すべて削除",
                    Tag = new RemoveAllMissingAction(),
                });
            }

            RecentFilesMenuItem.Items.Add(removeMissingMenu);
        }

        RecentFilesMenuItem.Items.Add(new Separator());
        var clearItem = new MenuItem { Header = "履歴をすべて消去", Tag = new ClearRecentAction() };
        RecentFilesMenuItem.Items.Add(clearItem);
    }

    private void AddRecentFile(string path)
    {
        _recentFiles.RemoveAll(file => string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase));
        _recentFiles.Insert(0, new RecentFileMock(path, false));
        if (_recentFiles.Count > 10)
        {
            _recentFiles.RemoveRange(10, _recentFiles.Count - 10);
        }

        RebuildRecentFilesMenu();
    }

    private void OpenDataFolderMenuItem_OnClick(object sender, RoutedEventArgs e) =>
        ShowNotification("製品版では、実行ファイルと同じ場所の data フォルダーを開きます。", isError: false);

    private void Window_OnPreviewDragEnter(object sender, DragEventArgs e) => UpdateDropFeedback(e);

    private void Window_OnPreviewDragOver(object sender, DragEventArgs e) => UpdateDropFeedback(e);

    private void Window_OnPreviewDragLeave(object sender, DragEventArgs e) => DropTargetOverlay.Visibility = Visibility.Collapsed;

    private void Window_OnPreviewDrop(object sender, DragEventArgs e)
    {
        DropTargetOverlay.Visibility = Visibility.Collapsed;
        var request = ClassifyDrop(e.Data);
        e.Handled = true;

        switch (request.Kind)
        {
            case FileOpenRequestKind.SingleSupportedFile:
                BeginMockOpen(request.Path!);
                break;
            case FileOpenRequestKind.MultipleFiles:
                ShowNotification("メインウィンドウでは1ファイルだけ指定してください。", isError: false);
                break;
            case FileOpenRequestKind.UnsupportedFile:
                ShowNotification("MP4またはWMVファイルを指定してください。", isError: true);
                break;
        }
    }

    private void UpdateDropFeedback(DragEventArgs e)
    {
        var request = ClassifyDrop(e.Data);
        DropTargetOverlay.Visibility = request.Kind == FileOpenRequestKind.None ? Visibility.Collapsed : Visibility.Visible;
        e.Effects = request.Kind == FileOpenRequestKind.SingleSupportedFile ? DragDropEffects.Copy : DragDropEffects.None;
        DropTargetText.Text = request.Kind switch
        {
            FileOpenRequestKind.SingleSupportedFile => "動画をドロップして開く",
            FileOpenRequestKind.MultipleFiles => "1ファイルだけ指定してください",
            FileOpenRequestKind.UnsupportedFile => "MP4またはWMVを指定してください",
            _ => string.Empty,
        };
        e.Handled = true;
    }

    private static FileOpenRequest ClassifyDrop(IDataObject data)
    {
        var paths = data.GetDataPresent(DataFormats.FileDrop)
            ? data.GetData(DataFormats.FileDrop) as string[] ?? []
            : [];
        return FileOpenRequestClassifier.Classify(paths);
    }

    private void ShowNotification(string message, bool isError)
    {
        NotificationToastText.Text = message;
        NotificationToastIcon.Text = isError ? "!" : "i";
        NotificationToastIcon.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isError ? "#FF6B79" : "#70C8FF"));
        NotificationToast.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isError ? "#9E4050" : "#41769B"));
        NotificationToast.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isError ? "#F52A1B25" : "#F5182A38"));
        NotificationToast.Visibility = Visibility.Visible;
    }

    private void CloseToastButton_OnClick(object sender, RoutedEventArgs e) => NotificationToast.Visibility = Visibility.Collapsed;

    private void ExitMenuItem_OnClick(object sender, RoutedEventArgs e) => Close();

    private void Session_OnChanged(object? sender, EventArgs e) => RenderState();

    private void RenderState()
    {
        var state = _session.State;

        _playlistWindow?.UpdateCurrentMedia(_session.CanControlPlayback ? _currentFilePath : null);

        EmptyStatePanel.Visibility = state == MediaUiState.Empty ? Visibility.Visible : Visibility.Collapsed;
        LoadingStatePanel.Visibility = state == MediaUiState.Loading ? Visibility.Visible : Visibility.Collapsed;
        ErrorStatePanel.Visibility = state == MediaUiState.Error ? Visibility.Visible : Visibility.Collapsed;
        MockVideoArtwork.Visibility = state is MediaUiState.Playing or MediaUiState.Paused ? Visibility.Visible : Visibility.Collapsed;

        if (!_session.CanControlPlayback)
        {
            CloseSeekThumbnail();
            _backgroundThumbnailTimer.Stop();
        }
        else if (_generatedThumbnailSlots.Count < SeekUiGeometry.ThumbnailSlotCount(_activeThumbnailIntervalPercent)
                 && !_backgroundThumbnailTimer.IsEnabled)
        {
            StartBackgroundThumbnailGeneration();
        }

        PlayPauseButton.IsEnabled = _session.CanControlPlayback;
        SeekSlider.IsEnabled = _session.CanControlPlayback && _session.HasKnownDuration;
        MuteButton.IsEnabled = _session.CanControlPlayback;
        VolumeSlider.IsEnabled = _session.CanControlPlayback;
        SeekSlider.Maximum = _session.HasKnownDuration ? _session.Duration.TotalSeconds : 1;
        VolumeSlider.Value = _session.VolumePercent;
        VolumeText.Text = $"{_session.VolumePercent}%";
        MuteButton.Content = _session.IsMuted ? "🔇" : "🔊";
        MuteButton.ToolTip = _session.IsMuted ? "ミュート解除" : "ミュート";
        MuteButton.SetValue(
            System.Windows.Automation.AutomationProperties.NameProperty,
            _session.IsMuted ? "ミュート解除" : "ミュート");
        PlaybackRateText.Text = $"{_session.PlaybackRate:0.0#}×";
        var matchingRateItem = ReviewSpeedComboBox.Items
            .OfType<ComboBoxItem>()
            .First(item => double.TryParse(
                item.Tag as string,
                System.Globalization.CultureInfo.InvariantCulture,
                out var itemRate) && Math.Abs(itemRate - _session.PlaybackRate) < double.Epsilon);
        if (!ReferenceEquals(ReviewSpeedComboBox.SelectedItem, matchingRateItem))
        {
            ReviewSpeedComboBox.SelectedItem = matchingRateItem;
        }

        if (!_isSeeking)
        {
            SeekSlider.Value = _session.HasKnownDuration ? _session.Position.TotalSeconds : 0;
        }

        if (_session.HasKnownDuration)
        {
            TimeText.Text = $"{MockPlaybackSession.FormatTime(_session.Position)} / {MockPlaybackSession.FormatTime(_session.Duration)}";
        }
        else
        {
            TimeText.Text = "--:--:-- / --:--:--";
        }

        UpdateStartPositionMarker();

        switch (state)
        {
            case MediaUiState.Empty:
                Title = ApplicationTitle;
                PlayPauseButton.Content = "▶";
                PlayPauseButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "再生");
                break;
            case MediaUiState.Loading:
                Title = $"{Path.GetFileName(_currentFilePath)} — {ApplicationTitle}";
                PlayPauseButton.Content = "▶";
                PlayPauseButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "再生");
                break;
            case MediaUiState.Playing:
                Title = $"{Path.GetFileName(_currentFilePath)} — {ApplicationTitle}";
                PlayPauseButton.Content = "Ⅱ";
                PlayPauseButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "一時停止");
                break;
            case MediaUiState.Paused:
                Title = $"{Path.GetFileName(_currentFilePath)} — {ApplicationTitle}";
                PlayPauseButton.Content = "▶";
                PlayPauseButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "再生");
                break;
            case MediaUiState.Error:
                Title = ApplicationTitle;
                PlayPauseButton.Content = "▶";
                PlayPauseButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "再生");
                break;
        }

        ReviewStateText.Text = $"MEDIA-{state.ToString().ToUpperInvariant()}";

        if (state == MediaUiState.Playing)
        {
            _playbackTimer.Start();
        }
        else
        {
            _playbackTimer.Stop();
        }
    }

    private void MainWindow_OnClosed(object? sender, EventArgs e)
    {
        if (_playlistWindow is not null)
        {
            _playlistWindow.PlayRequested -= PlaylistWindow_OnPlayRequested;
            _playlistWindow.Closed -= PlaylistWindow_OnClosed;
            _playlistWindow.Close();
            _playlistWindow = null;
        }

        _loadingTimer.Stop();
        _playbackTimer.Stop();
        _longPressTimer.Stop();
        _fullscreenControlsTimer.Stop();
        _thumbnailPreviewTimer.Stop();
        _backgroundThumbnailTimer.Stop();
        SeekThumbnailPopup.IsOpen = false;
        _loadingTimer.Tick -= LoadingTimer_OnTick;
        _playbackTimer.Tick -= PlaybackTimer_OnTick;
        _longPressTimer.Tick -= LongPressTimer_OnTick;
        _fullscreenControlsTimer.Tick -= FullscreenControlsTimer_OnTick;
        _thumbnailPreviewTimer.Tick -= ThumbnailPreviewTimer_OnTick;
        _backgroundThumbnailTimer.Tick -= BackgroundThumbnailTimer_OnTick;
        VolumeSlider.ValueChanged -= VolumeSlider_OnValueChanged;
        ReviewSpeedComboBox.SelectionChanged -= ReviewSpeedComboBox_OnSelectionChanged;
        _session.Changed -= Session_OnChanged;
        Closed -= MainWindow_OnClosed;

    }

    private sealed record RecentFileMock(string Path, bool IsMissing);

    private sealed record RemoveRecentAction(RecentFileMock RecentFile);

    private sealed record RemoveAllMissingAction;

    private sealed record ClearRecentAction;
}
