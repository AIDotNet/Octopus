using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Mobile.Localization;
using Octopus.Mobile.Shell;
using Octopus.Mobile.Workspace;

namespace Octopus.Mobile.Files;

/// <summary>
/// The uncommitted changes of a project's folder or branch workspace (design/ui/移动端功能扩展方案.html, ⑤ "改动审阅"):
/// each file with what happened to it and how many lines came and went; a file opens its diff. Read-only: nothing is
/// staged or committed from the phone.
/// </summary>
public sealed partial class ChangesViewModel : ObservableObject, IDisposable
{
    /// <summary>The counts of at most this many files are read one diff at a time; the rest show without them.</summary>
    public const int MaxCounted = 60;
    private const int SearchThreshold = 12;

    private readonly IRemoteWorkspace _workspace;
    private readonly INavigator _navigator;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _countCts;
    private IReadOnlyList<ChangeRowViewModel> _allRows = [];
    private int _loads;

    /// <param name="workspacePath">The branch workspace's folder; null for the project's own.</param>
    public ChangesViewModel(RemoteProject project, string? workspacePath, IRemoteWorkspace workspace, INavigator navigator)
    {
        Project = project;
        WorkspacePath = workspacePath;
        _workspace = workspace;
        _navigator = navigator;
        IsRepository = true;
    }

    public RemoteProject Project { get; }
    public string? WorkspacePath { get; }

    [ObservableProperty] public partial bool IsLoading { get; private set; }
    /// <summary>Pulled down to refresh: the list stays while it is read again.</summary>
    [ObservableProperty] public partial bool IsRefreshing { get; set; }
    [ObservableProperty] public partial string? ErrorText { get; private set; }
    [ObservableProperty] public partial bool IsRepository { get; private set; }
    [ObservableProperty] public partial string? Branch { get; private set; }
    [ObservableProperty] public partial string SearchQuery { get; set; } = "";
    /// <summary>Atomically swapped row list bound to the virtualized <c>ListBox</c> (avoids N per-item collection events).</summary>
    [ObservableProperty] public partial IReadOnlyList<ChangeRowViewModel> Rows { get; private set; } = [];

    /// <summary>Under the title: the project and the branch.</summary>
    public string Subtitle => Branch is { Length: > 0 } branch ? $"{Project.Name} · {branch}" : Project.Name;
    public string CountText => string.Format(MobileLangResources.Get(MobileLangResourceKind.ChangesCount), Rows.Count);
    public bool HasError => !string.IsNullOrEmpty(ErrorText);
    public bool HasAnyRows => _allRows.Count > 0;
    public bool HasRows => Rows.Count > 0;
    public bool IsEmpty => !IsLoading && !HasError && IsRepository && Rows.Count == 0;
    public bool IsNotRepository => !IsLoading && !HasError && !IsRepository;
    public bool ShowSearch => !IsLoading && !HasError && (_allRows.Count > SearchThreshold || !string.IsNullOrEmpty(SearchQuery));

    /// <summary>Reads the changes; the line counts the host did not give come after, file by file.</summary>
    public async Task LoadAsync()
    {
        var load = ++_loads;
        CancelPendingCounts();
        // A first load shows the spinner; a refresh keeps the list until the new one is there.
        IsLoading = _allRows.Count == 0;
        ErrorText = null;
        Refresh();
        try
        {
            var changes = await _workspace.GetChangesAsync(Project, WorkspacePath, _lifetime.Token);
            if (load != _loads || _lifetime.IsCancellationRequested) return;
            IsRepository = changes.IsRepository;
            Branch = changes.Branch;
            var built = new ChangeRowViewModel[changes.Changes.Count];
            for (var i = 0; i < changes.Changes.Count; i++) built[i] = new ChangeRowViewModel(changes.Changes[i], Open);
            _allRows = built;
            ApplyFilter();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (IOException e)
        {
            if (load != _loads) return;
            _allRows = [];
            Rows = [];
            ErrorText = e.Message;
        }
        finally
        {
            if (load == _loads && !_lifetime.IsCancellationRequested)
            {
                IsLoading = false;
                IsRefreshing = false;
                Refresh();
            }
        }
        if (load == _loads && !_lifetime.IsCancellationRequested) await CountAsync(load);
    }

    public void Dispose()
    {
        CancelPendingCounts();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    [RelayCommand]
    private void Back() => _navigator.Back();

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    private void Open(ChangeRowViewModel row)
    {
        // A new folder has no diff: its files are what there is to see.
        if (row.Change.IsDirectory) _navigator.ShowFiles(Project, row.Change.FullPath);
        else _navigator.ShowDiff(Project, WorkspacePath, row.Change);
    }

    // The host's status has no line counts: each file's diff tells them, a few at a time, while the list is already there.
    private async Task CountAsync(int load)
    {
        var pending = _allRows.Where(row => !row.HasCounts && !row.Change.IsDirectory).Take(MaxCounted).ToList();
        if (pending.Count == 0) return;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _countCts = cts;
        var token = cts.Token;
        using var gate = new SemaphoreSlim(3);
        try
        {
            await Task.WhenAll(pending.Select(async row =>
            {
                await gate.WaitAsync(token);
                try
                {
                    if (load != _loads || token.IsCancellationRequested) return;
                    var diff = await _workspace.GetDiffAsync(Project, WorkspacePath, row.Change, token);
                    if (load == _loads && !token.IsCancellationRequested && diff.Kind == RemoteDiffKind.Text)
                        row.SetCounts(diff.Added, diff.Removed);
                }
                catch (Exception e) when (e is IOException or OperationCanceledException)
                {
                    // Without counts the row is still right; its diff page says what is wrong.
                }
                finally
                {
                    gate.Release();
                }
            }));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_countCts, cts)) _countCts = null;
            cts.Dispose();
        }
    }

    private void CancelPendingCounts()
    {
        var previous = Interlocked.Exchange(ref _countCts, null);
        try { previous?.Cancel(); } catch (ObjectDisposedException) { }
    }

    partial void OnSearchQueryChanged(string value)
    {
        ApplyFilter();
        Refresh();
    }

    private void ApplyFilter()
    {
        var query = SearchQuery.Trim();
        if (query.Length == 0)
        {
            Rows = _allRows;
            return;
        }
        var filtered = new List<ChangeRowViewModel>();
        foreach (var row in _allRows)
        {
            if (row.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.Change.Path.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                filtered.Add(row);
            }
        }
        Rows = filtered;
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(HasAnyRows));
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsNotRepository));
        OnPropertyChanged(nameof(ShowSearch));
    }

    partial void OnBranchChanged(string? value) => OnPropertyChanged(nameof(Subtitle));
}

/// <summary>One changed file: a letter for what happened (M, A, D, R, !), its name and folder, and the line counts.</summary>
public sealed partial class ChangeRowViewModel : ObservableObject
{
    private readonly Action<ChangeRowViewModel> _open;

    public ChangeRowViewModel(RemoteChange change, Action<ChangeRowViewModel> open)
    {
        Change = change;
        _open = open;
        if (change is { Added: { } added, Removed: { } removed }) SetCounts(added, removed);
    }

    public RemoteChange Change { get; }
    public string Name => Change.Name;

    /// <summary>The folder, and whether the change is staged or a whole new folder.</summary>
    public string? Subtitle => string.Join(" · ", new[]
    {
        Change.Folder,
        Change.IsDirectory ? MobileLangResources.Get(MobileLangResourceKind.ChangesNewFolder) : null,
        Change.IsStaged ? MobileLangResources.Get(MobileLangResourceKind.ChangesStaged) : null,
    }.Where(part => !string.IsNullOrEmpty(part))) is { Length: > 0 } text ? text : null;

    /// <summary>A new file reads as added, tracked by git or not (design: M and A).</summary>
    public string Badge => Change.Kind switch
    {
        RemoteChangeKind.Added or RemoteChangeKind.Untracked => "A",
        RemoteChangeKind.Deleted => "D",
        RemoteChangeKind.Renamed => "R",
        RemoteChangeKind.Conflicted => "!",
        _ => "M",
    };

    public bool IsAdded => Change.Kind is RemoteChangeKind.Added or RemoteChangeKind.Untracked;
    public bool IsModified => Change.Kind is RemoteChangeKind.Modified or RemoteChangeKind.Renamed;
    public bool IsRemoved => Change.Kind is RemoteChangeKind.Deleted or RemoteChangeKind.Conflicted;

    [ObservableProperty] public partial bool HasCounts { get; private set; }
    [ObservableProperty] public partial string? AddedText { get; private set; }
    [ObservableProperty] public partial string? RemovedText { get; private set; }
    public bool HasAdded => AddedText is not null;
    public bool HasRemoved => RemovedText is not null;

    /// <summary>"+42" and "−118"; a zero is left out.</summary>
    public void SetCounts(int added, int removed)
    {
        AddedText = added > 0 ? "+" + added.ToString(CultureInfo.InvariantCulture) : null;
        RemovedText = removed > 0 ? "−" + removed.ToString(CultureInfo.InvariantCulture) : null;
        HasCounts = true;
        OnPropertyChanged(nameof(HasAdded));
        OnPropertyChanged(nameof(HasRemoved));
    }

    [RelayCommand]
    private void Open() => _open(this);
}
