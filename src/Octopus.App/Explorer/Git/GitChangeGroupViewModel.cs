using System.Collections.ObjectModel;

namespace Octopus.App.Explorer.Git;

/// <summary>A titled group of changes: staged, changed or untracked.</summary>
public sealed class GitChangeGroupViewModel(string title, IEnumerable<GitChangeRowViewModel> rows)
{
    public string Title { get; } = title;
    public ObservableCollection<GitChangeRowViewModel> Rows { get; } = new(rows);
    public int Count => Rows.Count;
}
