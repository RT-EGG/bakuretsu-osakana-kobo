using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
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

internal sealed class PlaylistEntriesRemoveRequestedEventArgs(IReadOnlyList<int> indices) : EventArgs
{
    public IReadOnlyList<int> Indices { get; } = indices;
}

internal sealed class PlaylistEntryMoveRequestedEventArgs(int sourceIndex, int insertionIndex) : EventArgs
{
    public int SourceIndex { get; } = sourceIndex;

    public int InsertionIndex { get; } = insertionIndex;
}

internal sealed class PlaylistPlayRequestedEventArgs(int startIndex) : EventArgs
{
    public int StartIndex { get; } = startIndex;
}

public partial class PlaylistWindow : Window
{
    private bool _isInitializing;
    private bool _canPersist;
    private bool _isPersistenceBusy;
    private bool _isPlaybackRequestBusy;
    private string? _currentMediaPath;
    private Point _dragStartPoint;
    private PlaylistEntryPresentation? _draggedEntry;
    private AdornerLayer? _dragAdornerLayer;
    private PlaylistDragAdorner? _dragAdorner;
    private int _dragInsertionIndex;
    private int? _currentPlaybackIndex;
    private IReadOnlySet<int> _loadErrorIndices = new HashSet<int>();

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

    internal event EventHandler<PlaylistEntriesRemoveRequestedEventArgs>? EntriesRemoveRequested;

    internal event EventHandler<PlaylistEntryMoveRequestedEventArgs>? EntryMoveRequested;

    internal event EventHandler<PlaylistPlayRequestedEventArgs>? PlayRequested;

    internal void UpdateCurrentMedia(string? path)
    {
        _currentMediaPath = path;
        UpdatePersistenceControls();
    }

    internal void CompletePersistence(
        IReadOnlyList<string> entries,
        int addedCount = 0,
        int rejectedCount = 0,
        bool enablePersistence = false,
        string? notification = null)
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
        else if (notification is not null)
        {
            ShowNotification(notification);
        }
    }

    internal void DisablePersistenceControls()
    {
        _isPersistenceBusy = true;
        UpdatePersistenceControls();
    }

    internal void UpdatePlaybackState(int? currentIndex, IReadOnlySet<int> loadErrorIndices)
    {
        _currentPlaybackIndex = currentIndex;
        _loadErrorIndices = new HashSet<int>(loadErrorIndices);
        if (IsLoaded)
        {
            UpdateEntries(PlaylistList.Items.Cast<PlaylistEntryPresentation>()
                .Select(entry => entry.Path)
                .ToArray());
        }
    }

    internal void ShowPlaybackNotification(string message) => ShowNotification(message);

    internal void SetPlaybackRequestBusy(bool isBusy)
    {
        _isPlaybackRequestBusy = isBusy;
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
        if (eventArgs.Data.GetDataPresent(typeof(PlaylistEntryPresentation)))
        {
            return;
        }

        var request = GetDropRequest(eventArgs.Data);
        eventArgs.Effects = !_isPersistenceBusy && _canPersist && request.Paths.Count > 0
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        eventArgs.Handled = true;
    }

    private void Window_OnPreviewDrop(object sender, DragEventArgs eventArgs)
    {
        if (eventArgs.Data.GetDataPresent(typeof(PlaylistEntryPresentation)))
        {
            return;
        }

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
        var canMutate = _canPersist && !_isPersistenceBusy && !_isPlaybackRequestBusy;
        LoopToggle.IsEnabled = canMutate;
        AddFilesButton.IsEnabled = canMutate;
        AddCurrentButton.IsEnabled = canMutate && _currentMediaPath is not null;
        RemoveSelectedButton.IsEnabled = canMutate && PlaylistList.SelectedItems.Count > 0;
        AllowDrop = canMutate;
        PlaylistList.AllowDrop = canMutate;
        PlayPlaylistButton.IsEnabled = !_isPlaybackRequestBusy && PlaylistList.Items
            .Cast<PlaylistEntryPresentation>()
            .Any(entry => !entry.IsMissing);
    }

    private void UpdateEntries(IReadOnlyList<string> entries)
    {
        PlaylistList.ItemsSource = PlaylistPresentation.From(
            entries,
            File.Exists,
            _currentPlaybackIndex,
            _loadErrorIndices);
        EmptyPlaylistPanel.Visibility = entries.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ShowNotification(string message)
    {
        NotificationText.Text = message;
        NotificationBar.Visibility = Visibility.Visible;
    }

    private void RemoveSelectedButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_isPersistenceBusy || !_canPersist)
        {
            return;
        }

        var indices = PlaylistList.SelectedItems
            .Cast<PlaylistEntryPresentation>()
            .Select(entry => entry.Index)
            .Order()
            .ToArray();
        if (indices.Length == 0)
        {
            return;
        }

        BeginPersistence();
        EntriesRemoveRequested?.Invoke(this, new PlaylistEntriesRemoveRequestedEventArgs(indices));
    }

    private void PlayPlaylistButton_OnClick(object sender, RoutedEventArgs eventArgs) =>
        RequestPlayback(startIndex: 0);

    private void PlaylistList_OnMouseDoubleClick(object sender, MouseButtonEventArgs eventArgs)
    {
        if (ItemsControl.ContainerFromElement(
                PlaylistList,
                eventArgs.OriginalSource as DependencyObject)
            is not ListBoxItem { DataContext: PlaylistEntryPresentation entry })
        {
            return;
        }

        if (entry.IsMissing)
        {
            ShowNotification("この項目は再生できません。ファイルの場所を確認してください。");
            eventArgs.Handled = true;
            return;
        }

        RequestPlayback(entry.Index);
        eventArgs.Handled = true;
    }

    private void RequestPlayback(int startIndex)
    {
        NotificationBar.Visibility = Visibility.Collapsed;
        PlayRequested?.Invoke(this, new PlaylistPlayRequestedEventArgs(startIndex));
    }

    private void PlaylistList_OnSelectionChanged(object sender, SelectionChangedEventArgs eventArgs) =>
        UpdatePersistenceControls();

    private void PlaylistList_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        _dragStartPoint = eventArgs.GetPosition(PlaylistList);
        _draggedEntry = ItemsControl.ContainerFromElement(
                PlaylistList,
                eventArgs.OriginalSource as DependencyObject)
            is ListBoxItem item
            ? item.DataContext as PlaylistEntryPresentation
            : null;
    }

    private void PlaylistList_OnMouseMove(object sender, MouseEventArgs eventArgs)
    {
        if (_isPersistenceBusy || !_canPersist ||
            eventArgs.LeftButton != MouseButtonState.Pressed || _draggedEntry is null)
        {
            return;
        }

        var point = eventArgs.GetPosition(PlaylistList);
        if (Math.Abs(point.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var draggedEntry = _draggedEntry;
        _draggedEntry = null;
        ShowDragAdorner(draggedEntry, point);
        try
        {
            DragDrop.DoDragDrop(PlaylistList, draggedEntry, DragDropEffects.Move);
        }
        finally
        {
            HideDragAdorner();
        }
    }

    private void PlaylistList_OnDragOver(object sender, DragEventArgs eventArgs)
    {
        if (!_isPersistenceBusy && _canPersist &&
            eventArgs.Data.GetDataPresent(typeof(PlaylistEntryPresentation)))
        {
            UpdateDragInsertion(
                eventArgs.GetPosition(PlaylistList),
                eventArgs.OriginalSource as DependencyObject);
            eventArgs.Effects = DragDropEffects.Move;
            eventArgs.Handled = true;
        }
    }

    private void PlaylistList_OnDrop(object sender, DragEventArgs eventArgs)
    {
        if (_isPersistenceBusy || !_canPersist ||
            eventArgs.Data.GetData(typeof(PlaylistEntryPresentation)) is not PlaylistEntryPresentation draggedEntry)
        {
            return;
        }

        UpdateDragInsertion(
            eventArgs.GetPosition(PlaylistList),
            eventArgs.OriginalSource as DependencyObject);
        BeginPersistence();
        EntryMoveRequested?.Invoke(
            this,
            new PlaylistEntryMoveRequestedEventArgs(draggedEntry.Index, _dragInsertionIndex));
        eventArgs.Handled = true;
    }

    private void ShowDragAdorner(PlaylistEntryPresentation entry, Point pointer)
    {
        _dragAdornerLayer = AdornerLayer.GetAdornerLayer(PlaylistList);
        if (_dragAdornerLayer is null)
        {
            return;
        }

        _dragInsertionIndex = entry.Index;
        _dragAdorner = new PlaylistDragAdorner(PlaylistList, entry.FileName)
        {
            IsHitTestVisible = false,
        };
        _dragAdornerLayer.Add(_dragAdorner);
        UpdateDragInsertion(pointer, null);
    }

    private void HideDragAdorner()
    {
        if (_dragAdorner is not null)
        {
            _dragAdornerLayer?.Remove(_dragAdorner);
        }

        _dragAdorner = null;
        _dragAdornerLayer = null;
    }

    private void UpdateDragInsertion(Point pointer, DependencyObject? originalSource)
    {
        var item = originalSource is null
            ? null
            : ItemsControl.ContainerFromElement(PlaylistList, originalSource) as ListBoxItem;
        double insertionY;
        if (item is not null)
        {
            var itemIndex = PlaylistList.ItemContainerGenerator.IndexFromContainer(item);
            var middle = item.TranslatePoint(new Point(0, item.ActualHeight / 2), PlaylistList).Y;
            var insertAfter = pointer.Y >= middle;
            _dragInsertionIndex = itemIndex + (insertAfter ? 1 : 0);
            insertionY = item.TranslatePoint(
                new Point(0, insertAfter ? item.ActualHeight : 0),
                PlaylistList).Y;
        }
        else
        {
            _dragInsertionIndex = PlaylistList.Items.Count;
            insertionY = PlaylistList.ActualHeight - 2;
        }

        _dragAdorner?.Update(pointer, insertionY);
    }

    protected override void OnClosed(EventArgs eventArgs)
    {
        HideDragAdorner();
        base.OnClosed(eventArgs);
    }

    private sealed class PlaylistDragAdorner(FrameworkElement adornedElement, string fileName)
        : Adorner(adornedElement)
    {
        private readonly Typeface _typeface = new("Segoe UI");
        private Point _pointer;
        private double _insertionY;

        public void Update(Point pointer, double insertionY)
        {
            _pointer = pointer;
            _insertionY = insertionY;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var primaryBrush = new SolidColorBrush(Color.FromRgb(71, 184, 255));
            var insertionPen = new Pen(primaryBrush, 3);
            drawingContext.DrawLine(
                insertionPen,
                new Point(4, _insertionY),
                new Point(AdornedElement.RenderSize.Width - 4, _insertionY));

            var text = new FormattedText(
                fileName,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                _typeface,
                13,
                Brushes.White,
                VisualTreeHelper.GetDpi(this).PixelsPerDip)
            {
                MaxTextWidth = 230,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            var width = Math.Min(260, Math.Max(150, text.Width + 28));
            const double height = 42;
            var x = Math.Clamp(
                _pointer.X + 14,
                4,
                Math.Max(4, AdornedElement.RenderSize.Width - width - 4));
            var y = Math.Clamp(
                _pointer.Y + 14,
                4,
                Math.Max(4, AdornedElement.RenderSize.Height - height - 4));
            drawingContext.PushOpacity(0.86);
            drawingContext.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromRgb(28, 38, 52)),
                new Pen(primaryBrush, 1),
                new Rect(x, y, width, height),
                6,
                6);
            drawingContext.DrawText(text, new Point(x + 14, y + 12));
            drawingContext.Pop();
        }
    }
}
