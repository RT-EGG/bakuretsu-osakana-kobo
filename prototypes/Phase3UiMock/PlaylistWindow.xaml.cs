using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace BakuretsuOsakanaKobo.Phase3UiMock;

internal sealed class PlaylistPlayRequestedEventArgs(PlaylistEntryMock entry) : EventArgs
{
    public PlaylistEntryMock Entry { get; } = entry;
}

public partial class PlaylistWindow : Window
{
    private readonly PlaylistMockModel _model = new();
    private Point _dragStartPoint;
    private PlaylistEntryMock? _draggedEntry;
    private PlaylistDragAdorner? _dragAdorner;
    private AdornerLayer? _dragAdornerLayer;
    private int _dragInsertionIndex;
    private string? _currentMediaPath;

    public PlaylistWindow()
    {
        InitializeComponent();
        _model.Add(@"C:\Videos\海辺の記録.mp4");
        _model.Add(@"D:\移動済み\見つからない動画.mp4", isMissing: true);
        _model.Add(@"C:\Videos\壊れた動画.mp4", hasLoadError: true);
        _model.Add(@"C:\Videos\操作説明.wmv");
        _model.Add(@"C:\Videos\海辺の記録.mp4");
        PlaylistList.ItemsSource = _model.Entries;
        _model.Entries.CollectionChanged += Entries_OnCollectionChanged;
        Closed += PlaylistWindow_OnClosed;
        UpdateControls();
    }

    internal event EventHandler<PlaylistPlayRequestedEventArgs>? PlayRequested;

    public void UpdateCurrentMedia(string? path)
    {
        _currentMediaPath = path;
        AddCurrentButton.IsEnabled = !string.IsNullOrWhiteSpace(path);
    }

    public void CancelContinuousPlayback()
    {
        _model.CancelContinuousPlayback();
        UpdateControls();
    }

    public void ContinueAfterNaturalEnd() => ContinueAfterCurrent();

    private void PlayPlaylistButton_OnClick(object sender, RoutedEventArgs e)
    {
        var entry = _model.StartFromFirst();
        if (entry is null)
        {
            ShowNotification("再生可能な項目がありません。欠損項目を確認してください。");
            UpdateControls();
            return;
        }

        RequestPlayback(entry);
    }

    private void PlaylistList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(PlaylistList, e.OriginalSource as DependencyObject)
            is not ListBoxItem { DataContext: PlaylistEntryMock selected })
        {
            return;
        }

        var entry = _model.StartFrom(selected);
        if (entry is null)
        {
            ShowNotification("この項目は再生できません。ファイルの場所または形式を確認してください。");
            return;
        }

        RequestPlayback(entry);
        e.Handled = true;
    }

    private void RequestPlayback(PlaylistEntryMock entry)
    {
        NotificationBar.Visibility = Visibility.Collapsed;
        PlaylistList.SelectedItem = entry;
        PlaylistList.ScrollIntoView(entry);
        UpdateControls();
        PlayRequested?.Invoke(this, new PlaylistPlayRequestedEventArgs(entry));
    }

    private void AddCurrentButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentMediaPath))
        {
            return;
        }

        _model.Add(_currentMediaPath);
        ShowNotification($"{Path.GetFileName(_currentMediaPath)} を追加しました。同じ動画の重複登録も保持します。");
    }

    private void AddFilesButton_OnClick(object sender, RoutedEventArgs e)
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
            AddPaths(dialog.FileNames);
        }
    }

    private void AddPaths(IEnumerable<string> paths)
    {
        var allPaths = paths.ToArray();
        var supportedPaths = allPaths.Where(IsSupportedVideo).ToArray();
        foreach (var path in supportedPaths)
        {
            _model.Add(path);
        }

        if (supportedPaths.Length > 0)
        {
            ShowNotification($"{supportedPaths.Length}件をプレイリストへ追加しました。");
        }

        var unsupportedCount = allPaths.Length - supportedPaths.Length;
        if (unsupportedCount > 0)
        {
            ShowNotification($"MP4またはWMVではない{unsupportedCount}件は追加しませんでした。");
        }
    }

    private static bool IsSupportedVideo(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".wmv", StringComparison.OrdinalIgnoreCase);
    }

    private void RemoveSelectedButton_OnClick(object sender, RoutedEventArgs e)
    {
        var selected = PlaylistList.SelectedItems.Cast<PlaylistEntryMock>().ToArray();
        if (selected.Length == 0)
        {
            return;
        }

        _model.Remove(selected);
        ShowNotification($"{selected.Length}件をプレイリストから削除しました。元の動画ファイルは削除していません。");
    }

    private void LoopToggle_OnChanged(object sender, RoutedEventArgs e)
    {
        _model.Loop = LoopToggle.IsChecked == true;
        LoopToggle.Content = _model.Loop ? "↻  ループ ON" : "↻  ループ OFF";
    }

    private void SimulateEndButton_OnClick(object sender, RoutedEventArgs e) => ContinueAfterCurrent();

    private void ContinueAfterCurrent()
    {
        if (_model.CurrentEntry is null)
        {
            ShowNotification("先にプレイリストの再生を開始してください。");
            return;
        }

        var next = _model.Advance();
        if (next is not null)
        {
            RequestPlayback(next);
            return;
        }

        ShowNotification(_model.HasPlayableEntries
            ? "プレイリストの末尾に到達したため停止しました。"
            : "再生可能な項目がないため停止しました。");
        UpdateControls();
    }

    private void PlaylistList_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(PlaylistList);
        _draggedEntry = ItemsControl.ContainerFromElement(PlaylistList, e.OriginalSource as DependencyObject)
            is ListBoxItem item
            ? item.DataContext as PlaylistEntryMock
            : null;
    }

    private void PlaylistList_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedEntry is null)
        {
            return;
        }

        var point = e.GetPosition(PlaylistList);
        if (Math.Abs(point.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(point.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
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

    private void PlaylistList_OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(PlaylistEntryMock)))
        {
            UpdateDragInsertion(e.GetPosition(PlaylistList), e.OriginalSource as DependencyObject);
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void PlaylistList_OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(PlaylistEntryMock)) is not PlaylistEntryMock draggedEntry)
        {
            return;
        }

        UpdateDragInsertion(e.GetPosition(PlaylistList), e.OriginalSource as DependencyObject);
        _model.MoveToInsertionIndex(draggedEntry, _dragInsertionIndex);
        PlaylistList.SelectedItem = draggedEntry;
        ShowNotification("再生順を変更しました。");

        e.Handled = true;
    }

    private void ShowDragAdorner(PlaylistEntryMock entry, Point pointer)
    {
        _dragAdornerLayer = AdornerLayer.GetAdornerLayer(PlaylistList);
        if (_dragAdornerLayer is null)
        {
            return;
        }

        _dragInsertionIndex = _model.Entries.IndexOf(entry);
        _dragAdorner = new PlaylistDragAdorner(PlaylistList, entry.FileName);
        _dragAdorner.IsHitTestVisible = false;
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
            var insertAfter = pointer.Y >= item.TranslatePoint(new Point(0, item.ActualHeight / 2), PlaylistList).Y;
            _dragInsertionIndex = itemIndex + (insertAfter ? 1 : 0);
            insertionY = item.TranslatePoint(new Point(0, insertAfter ? item.ActualHeight : 0), PlaylistList).Y;
        }
        else
        {
            _dragInsertionIndex = _model.Entries.Count;
            insertionY = PlaylistList.ActualHeight - 2;
        }

        _dragAdorner?.Update(pointer, insertionY);
    }

    private void Window_OnPreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Window_OnPreviewDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        var paths = e.Data.GetData(DataFormats.FileDrop) as string[] ?? [];
        AddPaths(paths);
        e.Handled = true;
    }

    private void PlaylistList_OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateControls();

    private void Entries_OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateControls();

    private void UpdateControls()
    {
        EmptyPlaylistPanel.Visibility = _model.Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PlayPlaylistButton.IsEnabled = _model.HasPlayableEntries;
        RemoveSelectedButton.IsEnabled = PlaylistList.SelectedItems.Count > 0;
        SimulateEndButton.IsEnabled = _model.CurrentEntry is not null;
        AddCurrentButton.IsEnabled = !string.IsNullOrWhiteSpace(_currentMediaPath);
    }

    private void ShowNotification(string message)
    {
        NotificationText.Text = message;
        NotificationBar.Visibility = Visibility.Visible;
    }

    private void PlaylistWindow_OnClosed(object? sender, EventArgs e)
    {
        HideDragAdorner();
        _model.Entries.CollectionChanged -= Entries_OnCollectionChanged;
        Closed -= PlaylistWindow_OnClosed;
    }

    private sealed class PlaylistDragAdorner(FrameworkElement adornedElement, string fileName) : Adorner(adornedElement)
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
            drawingContext.DrawLine(insertionPen, new Point(4, _insertionY), new Point(AdornedElement.RenderSize.Width - 4, _insertionY));

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
            var height = 42d;
            var x = Math.Clamp(_pointer.X + 14, 4, Math.Max(4, AdornedElement.RenderSize.Width - width - 4));
            var y = Math.Clamp(_pointer.Y + 14, 4, Math.Max(4, AdornedElement.RenderSize.Height - height - 4));
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
