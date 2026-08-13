namespace BakuretsuOsakanaKobo;

internal static class PlaylistPlaybackSequence
{
    public static IReadOnlyList<int> GetExistingCandidateIndices(
        IReadOnlyList<string> entries,
        int startIndex,
        Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(fileExists);
        if (startIndex < 0 || startIndex > entries.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(startIndex));
        }

        return Enumerable.Range(startIndex, entries.Count - startIndex)
            .Where(index => fileExists(entries[index]))
            .ToArray();
    }

    public static int? RemapAfterRemoval(int? index, IReadOnlyList<int> removedIndices)
    {
        ArgumentNullException.ThrowIfNull(removedIndices);
        if (index is null || removedIndices.Contains(index.Value))
        {
            return null;
        }

        return index.Value - removedIndices.Count(removed => removed < index.Value);
    }

    public static int? RemapAfterMove(int? index, int sourceIndex, int insertionIndex)
    {
        if (index is null)
        {
            return null;
        }

        var destinationIndex = sourceIndex < insertionIndex
            ? insertionIndex - 1
            : insertionIndex;
        if (index.Value == sourceIndex)
        {
            return destinationIndex;
        }

        if (sourceIndex < destinationIndex &&
            index.Value > sourceIndex && index.Value <= destinationIndex)
        {
            return index.Value - 1;
        }

        if (destinationIndex < sourceIndex &&
            index.Value >= destinationIndex && index.Value < sourceIndex)
        {
            return index.Value + 1;
        }

        return index;
    }
}
