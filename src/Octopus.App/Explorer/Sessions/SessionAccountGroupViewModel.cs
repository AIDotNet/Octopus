using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Agents.Sessions;

namespace Octopus.App.Explorer.Sessions;

/// <summary>The sessions of one account, under a header that folds them away.</summary>
public sealed partial class SessionAccountGroupViewModel(AgentSessionAccount account, IEnumerable<SessionRowViewModel> rows) : ObservableObject
{
    public AgentSessionAccount Account { get; } = account;
    public string Label => Account.Label;

    /// <summary>The folder, with the home folder written as <c>~</c>.</summary>
    public string Directory { get; } = Home(account.Directory);

    public ObservableCollection<SessionRowViewModel> Rows { get; } = [.. rows];
    public int Count => Rows.Count;

    [ObservableProperty] public partial bool IsExpanded { get; set; } = true;

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    private static string Home(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length > 0 && path.StartsWith(home, StringComparison.Ordinal) ? "~" + path[home.Length..] : path;
    }
}
