using System.Windows;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BakuretsuOsakanaKobo;

internal sealed class PlaylistLoopChangedEventArgs(bool loop) : EventArgs
{
    public bool Loop { get; } = loop;
}

internal sealed class PlaylistEntriesAddRequestedEventArgs(
    IReadOnlyList<string> paths,
    int rejectedCount) : EventArgs
{
    public IReadOnlyList<string> Paths { get; } = paths;

    public int RejectedCount { get; } = rejectedCount;
}

public partial class PlaylistWindow : Window
{
    private bool _isInitializing;
    private bool _canPersist;
    private bool _isPersistenceBusy;
    private string? _currentMediaPath;

    public PlaylistWindow(
        IReadOnlyList<string> entries,
        bool loop,
        bool canPersist,
        string? currentMediaPath)
    {
        _isInitializing = true;
        InitializeComponent();
        _canPersist = canPersist;
        _currentMediaPath = currentMediaPath;
        UpdateEntries(entries);
        LoopToggle.IsChecked = loop;
        LoopToggle.Content = loop ? "↻  ループ ON" : "↻  ループ OFF";
        _isInitializing = false;
        UpdatePersistenceControls();
    }

    internal event EventHandler<PlaylistLoopChangedEventArgs>? LoopChanged;

    internal event EventHandler<PlaylistEntriesAddRequestedEventArgs>? EntriesAddRequested;

    internal void UpdateCurrentMedia(string? path)
    {
        _currentMediaPath = path;
        UpdatePersistenceControls();
    }

    internal void CompletePersistence(
        IReadOnlyList<string> entries,
        int addedCount = 0,
        int rejectedCount = 0,
        bool enablePersistence = false)
    {
        if (enablePersistence)
        {
            _canPersist = true;
        }

        if (!IsLoaded)
        {
            return;
        }

        UpdateEntries(entries);
        _isPersistenceBusy = false;
        UpdatePersistenceControls();
        if (addedCount > 0)
        {
            ShowNotification(rejectedCount > 0
                ? $"{addedCount}件を追加しました。MP4またはWMVではない{rejectedCount}件は追加しませんでした。"
                : $"{addedCount}件をプレイリストへ追加しました。");
        }
    }

    internal void DisablePersistenceControls()
    {
        _isPersistenceBusy = true;
        UpdatePersistenceControls();
    }

    private void LoopToggle_OnChanged(object sender, RoutedEventArgs eventArgs)
    {
        var loop = LoopToggle.IsChecked == true;
        LoopToggle.Content = loop ? "↻  ループ ON" : "↻  ループ OFF";
        if (_isInitializing)
        {
            return;
        }

        BeginPersistence();
        LoopChanged?.Invoke(this, new PlaylistLoopChangedEventArgs(loop));
    }

    private void AddCurrentButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_currentMediaPath is not null)
        {
            RequestAdd([_currentMediaPath]);
        }
    }

    private void AddFilesButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        var dialog = new OpenFileDialog
        {
            Title = "プレイリストへ動画を追加",
            Filter = "動画ファイル (*.mp4;*.wmv)|*.mp4;*.wmv|MP4 ファイル (*.mp4)|*.mp4|WMV ファイル (*.wmv)|*.wmv",
            CheckFileExists = true,
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) == true)
        {
            RequestAdd(dialog.FileNames);
        }
    }

    private void Window_OnPreviewDragOver(object sender, DragEventArgs eventArgs)
    {
        var request = GetDropRequest(eventArgs.Data);
        eventArgs.Effects = !_isPersistenceBusy && _canPersist && request.Paths.Count > 0
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        eventArgs.Handled = true;
    }

    private void Window_OnPreviewDrop(object sender, DragEventArgs eventArgs)
    {
        var request = GetDropRequest(eventArgs.Data);
        if (!_isPersistenceBusy && _canPersist)
        {
            RequestAdd(request.Paths, request.RejectedCount);
        }

        eventArgs.Handled = true;
    }

    private static PlaylistAddRequest GetDropRequest(IDataObject data)
    {
        try
        {
            return data.GetDataPresent(DataFormats.FileDrop) &&
                   data.GetData(DataFormats.FileDrop) is string[] paths
                ? PlaylistAddRequest.From(paths)
                : PlaylistAddRequest.From([]);
        }
        catch (Exception exception) when (exception is ExternalException or InvalidOperationException)
        {
            return PlaylistAddRequest.From([]);
        }
    }

    private void RequestAdd(IEnumerable<string> paths, int additionalRejectedCount = 0)
    {
        if (_isPersistenceBusy || !_canPersist)
        {
            return;
        }

        var request = PlaylistAddRequest.From(paths);
        var rejectedCount = request.RejectedCount + additionalRejectedCount;
        if (request.Paths.Count == 0)
        {
            if (rejectedCount > 0)
            {
                ShowNotification($"MP4またはWMVではない{rejectedCount}件は追加しませんでした。");
            }

            return;
        }

        BeginPersistence();
        EntriesAddRequested?.Invoke(
            this,
            new PlaylistEntriesAddRequestedEventArgs(request.Paths, rejectedCount));
    }

    private void BeginPersistence()
    {
        _isPersistenceBusy = true;
        UpdatePersistenceControls();
    }

    private void UpdatePersistenceControls()
    {
        var canMutate = _canPersist && !_isPersistenceBusy;
        LoopToggle.IsEnabled = canMutate;
        AddFilesButton.IsEnabled = canMutate;
        AddCurrentButton.IsEnabled = canMutate && _currentMediaPath is not null;
        AllowDrop = canMutate;
    }

    private void UpdateEntries(IReadOnlyList<string> entries)
    {
        PlaylistList.ItemsSource = PlaylistPresentation.From(entries);
        EmptyPlaylistPanel.Visibility = entries.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ShowNotification(string message)
    {
        NotificationText.Text = message;
        NotificationBar.Visibility = Visibility.Visible;
    }
}
