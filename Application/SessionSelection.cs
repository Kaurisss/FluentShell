namespace FluentShell.Core;

public static class SessionSelection
{
    public static T? AfterRemoval<T>(
        IReadOnlyList<T> remaining,
        int removedIndex,
        bool removedWasSelected,
        T? selected)
        where T : class
    {
        if (remaining.Count == 0) return null;
        if (!removedWasSelected && selected is not null) return selected;

        var nextIndex = Math.Min(Math.Max(removedIndex, 0), remaining.Count - 1);
        return remaining[nextIndex];
    }
}