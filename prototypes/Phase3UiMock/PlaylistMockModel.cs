using System.Collections.ObjectModel;
using System.ComponentModel;

namespace BakuretsuOsakanaKobo.Phase3UiMock;

internal sealed class PlaylistEntryMock : INotifyPropertyChanged
{
    private bool _isCurrent;
    private int _order;

    public PlaylistEntryMock(string path, bool isMissing = false, bool hasLoadError = false)
    {
        Path = path;
        IsMissing = isMissing;
        HasLoadError = hasLoadError;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; }

    public string FileName => System.IO.Path.GetFileName(Path);

    public bool IsMissing { get; }

    public bool HasLoadError { get; }

    public bool CanPlay => !IsMissing && !HasLoadError;

    public string StatusText => IsMissing
        ? "見つかりません"
        : HasLoadError
            ? "読み込み不能"
            : string.Empty;

    public int Order
    {
        get => _order;
        internal set
        {
            if (_order == value)
            {
                return;
            }

            _order = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Order)));
        }
    }

    public bool IsCurrent
    {
        get => _isCurrent;
        internal set
        {
            if (_isCurrent == value)
            {
                return;
            }

            _isCurrent = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
        }
    }
}

internal sealed class PlaylistMockModel
{
    private PlaylistEntryMock? _currentEntry;

    public ObservableCollection<PlaylistEntryMock> Entries { get; } = [];

    public bool Loop { get; set; }

    public PlaylistEntryMock? CurrentEntry
    {
        get => _currentEntry;
        private set
        {
            if (ReferenceEquals(_currentEntry, value))
            {
                return;
            }

            if (_currentEntry is not null)
            {
                _currentEntry.IsCurrent = false;
            }

            _currentEntry = value;
            if (_currentEntry is not null)
            {
                _currentEntry.IsCurrent = true;
            }
        }
    }

    public void Add(string path, bool isMissing = false, bool hasLoadError = false)
    {
        Entries.Add(new PlaylistEntryMock(path, isMissing, hasLoadError));
        Reindex();
    }

    public void Remove(IEnumerable<PlaylistEntryMock> entries)
    {
        foreach (var entry in entries.Distinct().ToArray())
        {
            if (ReferenceEquals(entry, CurrentEntry))
            {
                CurrentEntry = null;
            }

            Entries.Remove(entry);
        }

        Reindex();
    }

    public void MoveToInsertionIndex(PlaylistEntryMock entry, int insertionIndex)
    {
        var oldIndex = Entries.IndexOf(entry);
        if (oldIndex < 0)
        {
            return;
        }

        var newIndex = Math.Clamp(insertionIndex, 0, Entries.Count);
        if (oldIndex < newIndex)
        {
            newIndex--;
        }

        if (oldIndex == newIndex)
        {
            return;
        }

        Entries.Move(oldIndex, newIndex);
        Reindex();
    }

    public PlaylistEntryMock? StartFromFirst() => SelectFirstPlayable(startIndex: 0);

    public PlaylistEntryMock? StartFrom(PlaylistEntryMock entry)
    {
        if (!Entries.Contains(entry) || !entry.CanPlay)
        {
            return null;
        }

        CurrentEntry = entry;
        return CurrentEntry;
    }

    public PlaylistEntryMock? Advance()
    {
        var currentIndex = CurrentEntry is null ? -1 : Entries.IndexOf(CurrentEntry);
        var next = SelectFirstPlayable(currentIndex + 1);
        if (next is not null)
        {
            return next;
        }

        if (Loop)
        {
            return SelectFirstPlayable(startIndex: 0);
        }

        CurrentEntry = null;
        return null;
    }

    public void CancelContinuousPlayback() => CurrentEntry = null;

    public bool HasPlayableEntries => Entries.Any(entry => entry.CanPlay);

    private PlaylistEntryMock? SelectFirstPlayable(int startIndex)
    {
        if (Entries.Count == 0)
        {
            CurrentEntry = null;
            return null;
        }

        for (var index = Math.Max(0, startIndex); index < Entries.Count; index++)
        {
            if (!Entries[index].CanPlay)
            {
                continue;
            }

            CurrentEntry = Entries[index];
            return CurrentEntry;
        }

        CurrentEntry = null;
        return null;
    }

    private void Reindex()
    {
        for (var index = 0; index < Entries.Count; index++)
        {
            Entries[index].Order = index + 1;
        }
    }
}
