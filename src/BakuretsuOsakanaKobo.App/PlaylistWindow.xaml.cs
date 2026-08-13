using System.Windows;

namespace BakuretsuOsakanaKobo;

internal sealed class PlaylistLoopChangedEventArgs(bool loop) : EventArgs
{
    public bool Loop { get; } = loop;
}

public partial class PlaylistWindow : Window
{
    private bool _isInitializing;

    public PlaylistWindow(IReadOnlyList<string> entries, bool loop, bool canPersistLoop)
    {
        _isInitializing = true;
        InitializeComponent();
        PlaylistList.ItemsSource = PlaylistPresentation.From(entries);
        EmptyPlaylistPanel.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LoopToggle.IsChecked = loop;
        LoopToggle.Content = loop ? "↻  ループ ON" : "↻  ループ OFF";
        LoopToggle.IsEnabled = canPersistLoop;
        _isInitializing = false;
    }

    internal event EventHandler<PlaylistLoopChangedEventArgs>? LoopChanged;

    internal void CompleteLoopSave()
    {
        if (IsLoaded)
        {
            LoopToggle.IsEnabled = true;
        }
    }

    internal void DisablePersistenceControls() => LoopToggle.IsEnabled = false;

    private void LoopToggle_OnChanged(object sender, RoutedEventArgs eventArgs)
    {
        var loop = LoopToggle.IsChecked == true;
        LoopToggle.Content = loop ? "↻  ループ ON" : "↻  ループ OFF";
        if (_isInitializing)
        {
            return;
        }

        LoopToggle.IsEnabled = false;
        LoopChanged?.Invoke(this, new PlaylistLoopChangedEventArgs(loop));
    }
}
