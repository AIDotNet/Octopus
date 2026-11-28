using System.Collections.ObjectModel;

namespace Octopus.Mobile.Common;

/// <summary>
/// Minimal in-place diff synchronization for <see cref="ObservableCollection{T}"/> on the UI thread.
/// Avoids <c>Clear() + N×Add()</c> storms that trigger <c>O(N²)</c> view-model re-subscription and full visual-tree
/// rebuilds when refreshing hosts, projects, groups, sessions, or bots after returning from background.
/// </summary>
internal static class MobileCollectionSync
{
    public static bool SyncByReference<T>(ObservableCollection<T> collection, IReadOnlyList<T> target) where T : class =>
        Sync(collection, target, ReferenceEqualityComparer.Instance);

    public static bool SyncByValue<T>(ObservableCollection<T> collection, IReadOnlyList<T> target) =>
        Sync(collection, target, EqualityComparer<T>.Default);

    private static bool Sync<T>(ObservableCollection<T> collection, IReadOnlyList<T> target, IEqualityComparer<T> comparer)
    {
        if (collection.Count == target.Count)
        {
            var same = true;
            for (var i = 0; i < target.Count; i++)
            {
                if (!comparer.Equals(collection[i], target[i]))
                {
                    same = false;
                    break;
                }
            }
            if (same) return false;
        }

        if (target.Count == 0)
        {
            collection.Clear();
            return true;
        }

        var wanted = new HashSet<T>(target, comparer);
        for (var i = collection.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(collection[i]))
            {
                collection.RemoveAt(i);
            }
        }

        for (var i = 0; i < target.Count; i++)
        {
            var desired = target[i];
            if (i < collection.Count && comparer.Equals(collection[i], desired))
            {
                continue;
            }

            var existingIndex = -1;
            for (var j = i + 1; j < collection.Count; j++)
            {
                if (comparer.Equals(collection[j], desired))
                {
                    existingIndex = j;
                    break;
                }
            }

            if (existingIndex >= 0)
            {
                collection.Move(existingIndex, i);
            }
            else
            {
                collection.Insert(i, desired);
            }
        }

        while (collection.Count > target.Count)
        {
            collection.RemoveAt(collection.Count - 1);
        }

        return true;
    }
}
