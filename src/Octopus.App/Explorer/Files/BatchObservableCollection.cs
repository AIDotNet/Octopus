using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Octopus.App.Explorer.Files;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that can replace its whole contents or signal a batch refresh with a single
/// <see cref="NotifyCollectionChangedAction.Reset"/> instead of firing one notification per row.
/// </summary>
internal sealed class BatchObservableCollection<T> : ObservableCollection<T>
{
    private static readonly PropertyChangedEventArgs CountChanged = new(nameof(Count));
    private static readonly PropertyChangedEventArgs IndexerChanged = new("Item[]");
    private static readonly NotifyCollectionChangedEventArgs ResetChanged = new(NotifyCollectionChangedAction.Reset);

    public void ReplaceAll(IReadOnlyList<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        for (var i = 0; i < items.Count; i++)
        {
            Items.Add(items[i]);
        }
        OnPropertyChanged(CountChanged);
        OnPropertyChanged(IndexerChanged);
        OnCollectionChanged(ResetChanged);
    }

    public void NotifyReset()
    {
        CheckReentrancy();
        OnPropertyChanged(IndexerChanged);
        OnCollectionChanged(ResetChanged);
    }
}
