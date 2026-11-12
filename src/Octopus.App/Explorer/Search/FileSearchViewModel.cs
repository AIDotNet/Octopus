using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Octopus.App.Common;
using Octopus.App.Localization;
using Octopus.FileSystem;

namespace Octopus.App.Explorer.Search;

/// <summary>The search box above the tree: by file name or by file content, results streamed in as they are found.</summary>
public sealed partial class FileSearchViewModel(IFileSearchService service) : ObservableObject
{
    public static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(250);

    private CancellationTokenSource? _cancel;
    private string? _root;

    public ObservableCollection<SearchRowViewModel> Results { get; } = [];

    [ObservableProperty] public partial string Query { get; set; } = "";

    /// <summary><c>name</c> or <c>content</c>: the value of the segmented control.</summary>
    [ObservableProperty] public partial string ModeKey { get; set; } = "name";

    [ObservableProperty] public partial bool IsSearching { get; private set; }

    /// <summary>Raised with the absolute path of a file to open.</summary>
    public event EventHandler<string>? OpenRequested;

    public SearchMode Mode => ModeKey == "content" ? SearchMode.Content : SearchMode.Name;

    /// <summary>There is something typed: the results replace the tree.</summary>
    public bool IsActive => Query.Trim().Length > 0;

    public bool HasNoMatches => IsActive && !IsSearching && Results.Count == 0;

    public string Placeholder => AppLangResources.Get(Mode == SearchMode.Name
        ? AppLangResourceKind.ExplorerSearchNamePlaceholder
        : AppLangResourceKind.ExplorerSearchContentPlaceholder);

    partial void OnQueryChanged(string value)
    {
        OnPropertyChanged(nameof(IsActive));
        _ = SearchAfterDelayAsync();
    }

    partial void OnModeKeyChanged(string value)
    {
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(Placeholder));
        if (IsActive) _ = SearchNowAsync();
    }

    partial void OnIsSearchingChanged(bool value) => OnPropertyChanged(nameof(HasNoMatches));

    /// <summary>The folder to search; clears the box and the results.</summary>
    public void SetRoot(string? root)
    {
        _cancel?.Cancel();
        _root = root;
        Results.Clear();
        IsSearching = false;
        Query = "";
        OnPropertyChanged(nameof(HasNoMatches));
    }

    private async Task SearchAfterDelayAsync()
    {
        _cancel?.Cancel();
        if (!IsActive)
        {
            Results.Clear();
            IsSearching = false;
            return;
        }
        var cts = _cancel = new CancellationTokenSource();
        try
        {
            await Task.Delay(DebounceDelay, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        await RunAsync(cts);
    }

    /// <summary>Searches now (without the typing delay); a search that is still running is cancelled.</summary>
    public Task SearchNowAsync()
    {
        _cancel?.Cancel();
        if (!IsActive)
        {
            Results.Clear();
            IsSearching = false;
            return Task.CompletedTask;
        }
        return RunAsync(_cancel = new CancellationTokenSource());
    }

    private async Task RunAsync(CancellationTokenSource cts)
    {
        var root = _root;
        var query = Query.Trim();
        var mode = Mode;
        Results.Clear();
        OnPropertyChanged(nameof(HasNoMatches));
        if (root is null || query.Length == 0)
        {
            IsSearching = false;
            return;
        }
        IsSearching = true;
        try
        {
            if (mode == SearchMode.Name)
            {
                await foreach (var entry in service.SearchNamesAsync(root, query, cts.Token))
                {
                    Results.Add(SearchRowViewModel.ForName(entry.FullPath, entry.Name, RelativeDirectory(root, entry.FullPath), entry.IsDirectory));
                }
            }
            else
            {
                string? current = null;
                await foreach (var match in service.SearchContentAsync(root, query, cts.Token))
                {
                    if (match.FilePath != current)
                    {
                        current = match.FilePath;
                        Results.Add(SearchRowViewModel.ForHeader(current, Path.GetFileName(current), RelativeDirectory(root, current)));
                    }
                    Results.Add(SearchRowViewModel.ForMatch(match.FilePath, match.Line, match.Column, match.LineText, query.Length));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // A newer search replaced this one: it owns the state now.
            return;
        }
        if (ReferenceEquals(_cancel, cts)) IsSearching = false;
        OnPropertyChanged(nameof(HasNoMatches));
    }

    /// <summary>Double-click or Enter on a result: files and matching lines open, folders and headings do not.</summary>
    public void RequestOpen(SearchRowViewModel? row)
    {
        if (row is null || row.IsDirectory) return;
        OpenRequested?.Invoke(this, row.Path);
    }

    private static string RelativeDirectory(string root, string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is null) return "";
        var relative = RealPath.Relative(root, directory);
        return relative is null or "." ? "" : relative;
    }
}
