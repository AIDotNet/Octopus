using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Common;
using Octopus.App.Localization;
using Octopus.App.Services.Workspaces;
using Octopus.Core.Projects;
using Octopus.Git;
using TKCode.Arc;

namespace Octopus.App.Dialogs;

public sealed partial class ExistingBranchOption : ObservableObject
{
    public ExistingBranchOption(GitBranch branch, string? occupiedLabel)
    {
        Branch = branch;
        OccupiedLabel = occupiedLabel;
        Name = branch.Name;
        IsRemote = branch.IsRemote;
        IsOccupied = !string.IsNullOrEmpty(occupiedLabel);
        CanSelect = !IsOccupied;
    }

    public GitBranch Branch { get; }
    public string Name { get; }
    public bool IsRemote { get; }
    public string? OccupiedLabel { get; }
    public bool IsOccupied { get; }
    public bool CanSelect { get; }

    [ObservableProperty] public partial bool IsSelected { get; set; }
}

public sealed partial class TaskChoiceViewModel : ObservableObject
{
    public TaskChoiceViewModel(RemoteTaskItem item)
    {
        Item = item;
        Key = item.Key;
        Title = item.Title;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.HeadBranch)) parts.Add(item.HeadBranch);
        if (!string.IsNullOrWhiteSpace(item.Author)) parts.Add("@" + item.Author);
        if (item.Labels.Count > 0) parts.Add(string.Join(", ", item.Labels.Take(3)));
        Meta = string.Join(" · ", parts);
    }

    public RemoteTaskItem Item { get; }
    public string Key { get; }
    public string Title { get; }
    public string Meta { get; }
    public bool HasMeta => Meta.Length > 0;

    [ObservableProperty] public partial bool IsSelected { get; set; }
}

/// <summary>View model for the "New branch workspace" dialog (Scheme C).</summary>
public sealed partial class CreateWorkspaceViewModel : ObservableObject
{
    private readonly Project _project;
    private readonly string _repositoryRoot;
    private readonly IGitHubTaskProvider _taskProvider;
    private readonly Func<string, string> _computePath;
    private readonly string _homeDirectory;
    private readonly Func<Task<string?>>? _pickFolder;
    private readonly List<ExistingBranchOption> _allExistingBranches = [];
    private List<TaskChoiceViewModel> _loadedIssues = [];
    private List<TaskChoiceViewModel> _loadedPrs = [];
    private bool _issuesLoaded;
    private bool _prsLoaded;

    public CreateWorkspaceViewModel(
        Project project,
        string repositoryRoot,
        IReadOnlyList<GitBranch> branches,
        IReadOnlyList<GitWorktree> worktrees,
        IGitHubTaskProvider taskProvider,
        Func<string, string> computePath,
        string homeDirectory,
        string? defaultAgentName,
        CreateWorkspaceRequest? initial = null,
        string? initialError = null,
        Func<Task<string?>>? pickFolder = null)
    {
        _project = project;
        _repositoryRoot = repositoryRoot;
        _taskProvider = taskProvider;
        _computePath = computePath;
        _homeDirectory = homeDirectory;
        _pickFolder = pickFolder;

        // Build base branch options (local branches first, then remotes; current branch selected first).
        var current = branches.FirstOrDefault(b => !b.IsRemote && b.IsCurrent);
        var defaultBase = initial?.BaseRef
            ?? current?.Name
            ?? branches.FirstOrDefault(b => !b.IsRemote && b.Name is "main" or "master")?.Name
            ?? branches.FirstOrDefault(b => !b.IsRemote)?.Name
            ?? branches.FirstOrDefault()?.Name
            ?? "HEAD";
        foreach (var b in branches)
        {
            BaseBranchOptions.Add(new SelectOption(b.Name, b.Name));
        }
        if (BaseBranchOptions.Count == 0)
        {
            BaseBranchOptions.Add(new SelectOption("HEAD", "HEAD"));
        }
        SelectedBaseBranch = BaseBranchOptions.Any(o => Equals(o.Value, defaultBase))
            ? defaultBase
            : BaseBranchOptions[0].Value?.ToString() ?? "HEAD";

        // Build existing branch list with worktree occupation detection (avoid git worktree checkout conflict).
        var mainBranch = worktrees.FirstOrDefault(w => w.IsMain)?.Branch;
        var otherBranches = worktrees.Where(w => !w.IsMain && !string.IsNullOrEmpty(w.Branch))
            .Select(w => w.Branch!)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var branch in branches)
        {
            string? occupied = null;
            if (!branch.IsRemote)
            {
                if (branch.Name == mainBranch) occupied = AppLangResources.Get(AppLangResourceKind.WorkspaceBranchOccupiedDefault);
                else if (otherBranches.Contains(branch.Name)) occupied = AppLangResources.Get(AppLangResourceKind.WorkspaceBranchOccupiedOther);
            }
            else
            {
                var slash = branch.Name.IndexOf('/');
                var local = slash >= 0 ? branch.Name[(slash + 1)..] : branch.Name;
                if (local == mainBranch) occupied = AppLangResources.Get(AppLangResourceKind.WorkspaceBranchOccupiedDefault);
                else if (otherBranches.Contains(local)) occupied = AppLangResources.Get(AppLangResourceKind.WorkspaceBranchOccupiedOther);
            }
            _allExistingBranches.Add(new ExistingBranchOption(branch, occupied));
        }

        // After-creation options.
        if (!string.IsNullOrEmpty(defaultAgentName))
        {
            AfterCreateOptions.Add(new SelectOption("agent", AppLangResources.Format(AppLangResourceKind.WorkspaceAfterAgent, defaultAgentName)));
        }
        AfterCreateOptions.Add(new SelectOption("terminal", AppLangResources.Get(AppLangResourceKind.WorkspaceAfterTerminal)));
        AfterCreateOptions.Add(new SelectOption("none", AppLangResources.Get(AppLangResourceKind.WorkspaceAfterNone)));

        SelectedAfterCreate = initial?.AfterCreate switch
        {
            WorkspaceAfterCreate.Terminal => "terminal",
            WorkspaceAfterCreate.None => "none",
            WorkspaceAfterCreate.DefaultAgent when !string.IsNullOrEmpty(defaultAgentName) => "agent",
            _ => !string.IsNullOrEmpty(defaultAgentName) ? "agent" : "terminal",
        };

        // Restore previous draft if reopened after validation or git failure.
        if (initial is not null)
        {
            BranchMode = initial.CreateBranch ? "new" : "existing";
            if (initial.CreateBranch) BranchName = initial.BranchName;
            else SelectExistingByName(initial.BranchName);
            InjectTaskPrompt = initial.InjectTaskPrompt;
            if (!string.IsNullOrWhiteSpace(initial.CustomBaseDirectory))
            {
                CustomBaseDirectory = initial.CustomBaseDirectory;
            }
            if (initial.Task is { } task)
            {
                if (task.Kind is WorkspaceTaskKind.Url or WorkspaceTaskKind.Note)
                {
                    TaskTab = "custom";
                    CustomTaskUrl = task.Url ?? "";
                    CustomTaskNote = task.Title;
                }
            }
        }
        else
        {
            var firstFree = _allExistingBranches.FirstOrDefault(b => b.CanSelect);
            if (firstFree is not null) SelectExistingBranch(firstFree);
        }

        Error = initialError;
        RebuildExistingBranches();
        UpdatePathPreview();
    }

    public ObservableCollection<SelectOption> BaseBranchOptions { get; } = [];
    public ObservableCollection<SelectOption> AfterCreateOptions { get; } = [];
    public ObservableCollection<ExistingBranchOption> FilteredBranches { get; } = [];
    public ObservableCollection<TaskChoiceViewModel> FilteredTasks { get; } = [];

    /// <summary><c>none</c>, <c>issue</c>, <c>pr</c>, or <c>custom</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRemoteTaskTab), nameof(IsCustomTaskTab), nameof(HasTaskBinding))]
    public partial string TaskTab { get; set; } = "none";

    public bool IsRemoteTaskTab => TaskTab is "issue" or "pr";
    public bool IsCustomTaskTab => TaskTab == "custom";
    public bool HasTaskBinding => TaskTab != "none";

    [ObservableProperty] public partial string TaskQuery { get; set; } = "";
    [ObservableProperty] public partial bool IsLoadingTasks { get; private set; }
    [ObservableProperty] public partial string? TaskStatusMessage { get; private set; }
    [ObservableProperty] public partial TaskChoiceViewModel? SelectedTask { get; private set; }
    [ObservableProperty] public partial string CustomTaskUrl { get; set; } = "";
    [ObservableProperty] public partial string CustomTaskNote { get; set; } = "";
    [ObservableProperty] public partial bool InjectTaskPrompt { get; set; } = true;

    public bool HasTaskStatus => !string.IsNullOrEmpty(TaskStatusMessage);
    public bool ShowTaskEmpty => IsRemoteTaskTab && !IsLoadingTasks && !HasTaskStatus && FilteredTasks.Count == 0;

    /// <summary><c>new</c> or <c>existing</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNewBranchMode), nameof(IsExistingBranchMode))]
    public partial string BranchMode { get; set; } = "new";

    public bool IsNewBranchMode => BranchMode == "new";
    public bool IsExistingBranchMode => BranchMode == "existing";

    [ObservableProperty] public partial string SelectedBaseBranch { get; set; } = "HEAD";
    [ObservableProperty] public partial string BranchName { get; set; } = "";
    [ObservableProperty] public partial string BranchQuery { get; set; } = "";
    [ObservableProperty] public partial ExistingBranchOption? SelectedExistingBranch { get; private set; }
    [ObservableProperty] public partial string SelectedAfterCreate { get; set; } = "terminal";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCustomBaseDirectory))]
    public partial string? CustomBaseDirectory { get; set; }
    public bool HasCustomBaseDirectory => !string.IsNullOrWhiteSpace(CustomBaseDirectory);
    public bool CanChangeDirectory => _pickFolder is not null;

    [ObservableProperty] public partial string PreviewPath { get; private set; } = "";
    [ObservableProperty] public partial string? SlashNote { get; private set; }
    public bool HasSlashNote => !string.IsNullOrEmpty(SlashNote);

    [ObservableProperty] public partial string? Error { get; set; }
    public bool HasError => !string.IsNullOrEmpty(Error);

    partial void OnCustomBaseDirectoryChanged(string? value)
    {
        Error = null;
        UpdatePathPreview();
    }

    partial void OnTaskTabChanged(string value)
    {
        if (value is "issue" or "pr")
        {
            _ = LoadTasksForTabAsync(value);
        }
        else
        {
            TaskStatusMessage = null;
            OnPropertyChanged(nameof(HasTaskStatus));
            OnPropertyChanged(nameof(ShowTaskEmpty));
        }
    }

    partial void OnTaskQueryChanged(string value) => RebuildFilteredTasks();
    partial void OnBranchModeChanged(string value) { Error = null; UpdatePathPreview(); }
    partial void OnBranchNameChanged(string value) { Error = null; UpdatePathPreview(); }
    partial void OnBranchQueryChanged(string value) => RebuildExistingBranches();
    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));
    partial void OnSlashNoteChanged(string? value) => OnPropertyChanged(nameof(HasSlashNote));
    partial void OnTaskStatusMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(HasTaskStatus));
        OnPropertyChanged(nameof(ShowTaskEmpty));
    }
    partial void OnIsLoadingTasksChanged(bool value) => OnPropertyChanged(nameof(ShowTaskEmpty));

    [RelayCommand]
    public void SelectTask(TaskChoiceViewModel? choice)
    {
        if (choice is null) return;
        foreach (var item in _loadedIssues) item.IsSelected = ReferenceEquals(item, choice);
        foreach (var item in _loadedPrs) item.IsSelected = ReferenceEquals(item, choice);
        SelectedTask = choice;
        Error = null;

        // If a PR's head branch already exists as an unoccupied local/remote branch, select it directly; otherwise suggest creating it.
        if (choice.Item.Kind == WorkspaceTaskKind.GitHubPr && !string.IsNullOrWhiteSpace(choice.Item.HeadBranch))
        {
            var head = choice.Item.HeadBranch.Trim();
            var match = _allExistingBranches.FirstOrDefault(b => b.CanSelect && (b.Name == head || b.Name == "origin/" + head));
            if (match is not null)
            {
                BranchMode = "existing";
                SelectExistingBranch(match);
                return;
            }
        }

        BranchMode = "new";
        BranchName = choice.Item.SuggestedBranch();
    }

    [RelayCommand]
    public void SelectExistingBranch(ExistingBranchOption? option)
    {
        if (option is null || !option.CanSelect) return;
        foreach (var b in _allExistingBranches) b.IsSelected = ReferenceEquals(b, option);
        SelectedExistingBranch = option;
        Error = null;
        UpdatePathPreview();
    }

    private void SelectExistingByName(string name)
    {
        var match = _allExistingBranches.FirstOrDefault(b => b.CanSelect && b.Name == name);
        if (match is not null) SelectExistingBranch(match);
    }

    private async Task LoadTasksForTabAsync(string tab)
    {
        var isPr = tab == "pr";
        if ((isPr && _prsLoaded) || (!isPr && _issuesLoaded))
        {
            TaskStatusMessage = null;
            RebuildFilteredTasks();
            return;
        }
        IsLoadingTasks = true;
        TaskStatusMessage = null;
        FilteredTasks.Clear();
        try
        {
            var result = isPr
                ? await _taskProvider.ListPullRequestsAsync(_repositoryRoot)
                : await _taskProvider.ListIssuesAsync(_repositoryRoot);
            UiContext.Run(() =>
            {
                if (TaskTab != tab) return;
                if (!result.IsAvailable)
                {
                    TaskStatusMessage = AppLangResources.Get(AppLangResourceKind.WorkspaceTaskGhMissing);
                    return;
                }
                if (!string.IsNullOrEmpty(result.Message))
                {
                    TaskStatusMessage = result.Message;
                    return;
                }
                var list = result.Items.Select(i => new TaskChoiceViewModel(i)).ToList();
                if (isPr) { _loadedPrs = list; _prsLoaded = true; }
                else { _loadedIssues = list; _issuesLoaded = true; }
                RebuildFilteredTasks();
            });
        }
        finally
        {
            UiContext.Run(() => IsLoadingTasks = false);
        }
    }

    private void RebuildFilteredTasks()
    {
        FilteredTasks.Clear();
        var source = TaskTab == "pr" ? _loadedPrs : _loadedIssues;
        var q = TaskQuery.Trim();
        foreach (var item in source)
        {
            if (q.Length > 0
                && !item.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !item.Key.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !item.Meta.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            FilteredTasks.Add(item);
        }
        OnPropertyChanged(nameof(ShowTaskEmpty));
    }

    private void RebuildExistingBranches()
    {
        FilteredBranches.Clear();
        var q = BranchQuery.Trim();
        foreach (var option in _allExistingBranches)
        {
            if (q.Length > 0 && !option.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            FilteredBranches.Add(option);
        }
    }

    [RelayCommand]
    public async Task ChangeDirectoryAsync()
    {
        if (_pickFolder is null) return;
        var picked = await _pickFolder();
        if (!string.IsNullOrWhiteSpace(picked))
        {
            CustomBaseDirectory = picked.Trim();
        }
    }

    [RelayCommand]
    public void ResetDirectory() => CustomBaseDirectory = null;

    private void UpdatePathPreview()
    {
        var raw = IsNewBranchMode ? BranchName.Trim() : SelectedExistingBranch?.Name ?? "";
        var effective = !IsNewBranchMode && SelectedExistingBranch is { IsRemote: true } && raw.IndexOf('/') is var s and >= 0
            ? raw[(s + 1)..]
            : raw;
        var previewBranch = effective.Length > 0 ? effective : "new-branch";
        string full;
        if (HasCustomBaseDirectory && WorkspacePaths.TryResolveRootDirectory(CustomBaseDirectory, _homeDirectory, out var customBase))
        {
            full = WorkspacePaths.BuildWorkspacePathInDirectory(customBase, previewBranch, Directory.Exists);
        }
        else
        {
            full = _computePath(previewBranch);
        }
        PreviewPath = WorkspacePaths.FormatHomePath(full, _homeDirectory);
        SlashNote = effective.Contains('/')
            ? AppLangResources.Format(AppLangResourceKind.WorkspacePathSlashNote, effective)
            : null;
    }

    /// <summary>Validates form inputs and returns the request; sets <see cref="Error"/> and returns null when incomplete.</summary>
    public CreateWorkspaceRequest? BuildRequest()
    {
        var customDir = HasCustomBaseDirectory ? CustomBaseDirectory!.Trim() : null;
        if (IsNewBranchMode)
        {
            var name = BranchName.Trim();
            if (name.Length == 0)
            {
                Error = AppLangResources.Get(AppLangResourceKind.WorkspaceValidationBranchRequired);
                return null;
            }
            return new CreateWorkspaceRequest(
                CreateBranch: true,
                BranchName: name,
                BaseRef: SelectedBaseBranch,
                IsRemoteBranch: false,
                Task: BuildTaskLink(),
                InjectTaskPrompt: InjectTaskPrompt,
                AfterCreate: ParseAfterCreate(),
                CustomBaseDirectory: customDir);
        }
        if (SelectedExistingBranch is null || !SelectedExistingBranch.CanSelect)
        {
            Error = AppLangResources.Get(AppLangResourceKind.WorkspaceValidationExistingRequired);
            return null;
        }
        return new CreateWorkspaceRequest(
            CreateBranch: false,
            BranchName: SelectedExistingBranch.Name,
            BaseRef: null,
            IsRemoteBranch: SelectedExistingBranch.IsRemote,
            Task: BuildTaskLink(),
            InjectTaskPrompt: InjectTaskPrompt,
            AfterCreate: ParseAfterCreate(),
            CustomBaseDirectory: customDir);
    }

    private WorkspaceTaskLink? BuildTaskLink()
    {
        if (TaskTab is "issue" or "pr" && SelectedTask is not null)
        {
            return SelectedTask.Item.ToTaskLink();
        }
        if (TaskTab == "custom")
        {
            var url = CustomTaskUrl.Trim();
            var note = CustomTaskNote.Trim();
            if (url.Length == 0 && note.Length == 0) return null;
            if (url.Length > 0)
            {
                var key = ExtractUrlKey(url);
                return new WorkspaceTaskLink
                {
                    Kind = WorkspaceTaskKind.Url,
                    Key = key,
                    Title = note.Length > 0 ? note : url,
                    Url = url,
                };
            }
            return new WorkspaceTaskLink
            {
                Kind = WorkspaceTaskKind.Note,
                Key = "Note",
                Title = note,
            };
        }
        return null;
    }

    private WorkspaceAfterCreate ParseAfterCreate() => SelectedAfterCreate switch
    {
        "agent" => WorkspaceAfterCreate.DefaultAgent,
        "none" => WorkspaceAfterCreate.None,
        _ => WorkspaceAfterCreate.Terminal,
    };

    internal static string ExtractUrlKey(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2 && segments[^2] is "issues" && int.TryParse(segments[^1], out var issueNum)) return $"#{issueNum}";
            if (segments.Length >= 2 && segments[^2] is "pull" && int.TryParse(segments[^1], out var prNum)) return $"PR #{prNum}";
            if (segments.Length >= 1 && segments[^1].Length is >= 2 and <= 18) return segments[^1];
        }
        return "LINK";
    }
}
