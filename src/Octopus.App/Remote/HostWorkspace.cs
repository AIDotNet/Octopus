using System.Reflection;
using Avalonia.Threading;
using Octopus.Agents;
using Octopus.App.Activity;
using Octopus.App.Common;
using Octopus.App.Services;
using Octopus.App.Services.Workspaces;
using Octopus.App.Workspace;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.Platform;
using Octopus.Remote.Host;
using Octopus.Remote.Protocol;
using Octopus.Storage;
using Octopus.Terminal;
using Octopus.Terminal.Avalonia;
using TKCode.Terminal.Core;

namespace Octopus.App.Remote;

/// <summary>
/// This computer's workspace as a connected device may use it (design/12-远程操作.md §3): the same projects, items
/// and sessions the window shows. Requests arrive on network threads and are carried out on the UI thread, where the
/// project service lives. What a device adds appears in the window too, but never takes its front tab or project.
/// </summary>
public sealed class HostWorkspace : IHostWorkspace, IDisposable
{
    private readonly IProjectService _projects;
    private readonly IItemLaunchService _launcher;
    private readonly ITerminalConnectionFactory _factory;
    private readonly TerminalSessionRegistry _sessions;
    private readonly ITabContentRegistry _tabs;
    private readonly IItemActivityService _activity;
    private readonly IAgentCatalog _catalog;
    private readonly IAgentScanner _scanner;
    private readonly ISettingsStore _settings;
    private readonly IShellResolver _shells;
    private readonly Octopus.FileSystem.IFileTreeService _files;
    private readonly Octopus.Git.IGitService _git;
    private readonly PathComparer _paths;
    private readonly IWorkspaceManager? _workspaces;
    private readonly IGitHubTaskProvider? _taskProvider;
    private readonly HostConversations _conversations;

    public HostWorkspace(IProjectService projects, IItemLaunchService launcher, ITerminalConnectionFactory factory, TerminalSessionRegistry sessions,
        ITabContentRegistry tabs, IItemActivityService activity, IAgentCatalog catalog, IAgentScanner scanner, ISettingsStore settings, IShellResolver shells,
        Octopus.FileSystem.IFileTreeService files, Octopus.Git.IGitService git, PathComparer paths,
        IWorkspaceManager? workspaces = null, IGitHubTaskProvider? taskProvider = null)
    {
        _files = files;
        _git = git;
        _paths = paths;
        _workspaces = workspaces;
        _taskProvider = taskProvider;
        _projects = projects;
        _launcher = launcher;
        _factory = factory;
        _sessions = sessions;
        _tabs = tabs;
        _activity = activity;
        _catalog = catalog;
        _scanner = scanner;
        _settings = settings;
        _shells = shells;
        _conversations = new HostConversations(tabs);
        projects.Changed += OnProjectsChanged;
        activity.Changed += OnActivityChanged;
    }

    public event Action? Changed;
    public event Action<ItemMessage>? ItemChanged;

    /// <summary>Whether the window shows the dark terminal colors: a session started for a device gets the same ones.</summary>
    public Func<bool> IsDark { get; set; } = static () => true;

    public HostInfoMessage Info => new()
    {
        Name = Environment.MachineName,
        Os = OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "linux",
        AppVersion = AppVersion,
        Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    };

    public static string AppVersion { get; } =
        typeof(HostWorkspace).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public Task<IReadOnlyList<ProjectMessage>> ListProjectsAsync() => OnUi<IReadOnlyList<ProjectMessage>>(() =>
        // Only folders of this computer: a project on an SSH host is this computer's view of yet another machine.
        [.. _projects.Projects.Where(ProjectLocation.IsOnThisComputer).Select(ToMessage)]);

    public Task<ProjectMessage> CreateProjectAsync(CreateProjectMessage request) => OnUi(() =>
    {
        var path = ExpandHome(request.Path.Trim());
        if (path.Length == 0 || !Path.IsPathRooted(path)) throw new RemoteException(RemoteProtocol.InvalidParams, "The folder must be a full path on the host.");
        if (!Directory.Exists(path))
        {
            if (!request.CreateFolder) throw new RemoteException(RemoteMethods.NotFound, $"The folder '{path}' does not exist on the host.");
            try { Directory.CreateDirectory(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new RemoteException(RemoteMethods.NotAllowed, e.Message); }
        }
        return ToMessage(_projects.AddProject(path, request.Name, activate: false));
    });

    public Task<IReadOnlyList<ItemMessage>> ListItemsAsync(string projectId) => OnUi<IReadOnlyList<ItemMessage>>(() =>
    {
        var project = FindProject(projectId);
        return [.. project.Items.Where(IsSession).Select(item => ToMessage(project, item))];
    });

    public Task<ItemMessage> CreateItemAsync(CreateItemMessage request) => OnUi(() =>
    {
        var project = FindProject(request.ProjectId);
        ProjectWorkspace? workspace = null;
        if (!string.IsNullOrWhiteSpace(request.WorkspaceId))
        {
            workspace = project.Workspaces.FirstOrDefault(w => w.Id == request.WorkspaceId)
                ?? throw new RemoteException(RemoteMethods.NotFound, "The branch workspace does not exist on the host.");
        }

        ProjectItem item;
        if (request.Kind == ItemKinds.Agent)
        {
            var definition = request.AgentId is null ? null : _catalog.Find(request.AgentId);
            if (definition is null || _scanner.Find(definition.Id) is null)
            {
                throw new RemoteException(RemoteMethods.NotFound, $"The agent '{request.AgentId}' is not installed on the host.");
            }
            var prompt = Blank(request.Prompt);
            if (request.InjectTaskPrompt && workspace?.Task is { } task)
            {
                var taskBlock = WorkspaceManager.FormatTaskPrompt(task);
                prompt = prompt is null ? taskBlock : $"{taskBlock}\n\n{prompt}";
            }
            AgentSurface? requestedSurface = request.Surface switch
            {
                ItemSurfaces.Gui when definition.SupportsAcp => AgentSurface.Gui,
                ItemSurfaces.Gui => throw new RemoteException(RemoteMethods.NotAllowed, $"The agent '{definition.Name}' does not support GUI conversations."),
                ItemSurfaces.Terminal => AgentSurface.Terminal,
                _ => null,
            };
            item = new ProjectItem
            {
                Kind = ProjectItemKind.Agent,
                AgentId = definition.Id,
                Title = definition.Name,
                WorkspaceId = workspace?.Id,
                WorkingDirectory = workspace?.Path,
                Surface = requestedSurface,
                Launch = new AgentLaunchOptions
                {
                    Model = Blank(request.Model),
                    Effort = Blank(request.Effort),
                    Prompt = prompt,
                    Surface = requestedSurface,
                },
            };
        }
        else
        {
            var chosen = _settings.Current.Terminal.Shell;
            var shell = (string.IsNullOrEmpty(chosen) ? null : _shells.Describe(chosen)) ?? _shells.GetDefaultShell();
            item = new ProjectItem
            {
                Kind = ProjectItemKind.Terminal,
                Title = shell.Name,
                WorkspaceId = workspace?.Id,
                WorkingDirectory = workspace?.Path,
            };
        }
        // Behind whatever the person at this computer is looking at.
        _projects.AddItem(project, item, activate: false);
        return ToMessage(project, item);
    });

    public async Task RemoveItemAsync(string itemId)
    {
        await OnUi(() =>
        {
            FindItem(itemId);
            _projects.RemoveItem(itemId);
            return true;
        }).ConfigureAwait(false);
        // The window closes the item's tab if it built one; a session that never had a tab is ended here.
        await _tabs.ReleaseAsync(itemId).ConfigureAwait(false);
        await _sessions.CloseAsync(itemId).ConfigureAwait(false);
    }

    public Task RenameItemAsync(string itemId, string? title) => OnUi(() =>
    {
        FindItem(itemId);
        _projects.RenameItem(itemId, Blank(title));
        return true;
    });

    public async Task<WorkspaceOptionsMessage> GetWorkspaceOptionsAsync(string projectId)
    {
        var project = await OnUi(() => FindProject(projectId)).ConfigureAwait(false);
        if (!Directory.Exists(project.Path)) return new WorkspaceOptionsMessage();
        var repoRoot = await _git.FindRepositoryRootAsync(project.Path).ConfigureAwait(false);
        if (repoRoot is null) return new WorkspaceOptionsMessage();

        var branchesTask = _git.GetBranchesAsync(repoRoot);
        var worktreesTask = _git.GetWorktreesAsync(repoRoot);
        Task<TaskProviderResult>? issuesTask = null;
        Task<TaskProviderResult>? prsTask = null;
        CancellationTokenSource? taskLimit = null;
        if (_taskProvider is not null)
        {
            taskLimit = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            issuesTask = SafeLoadTasksAsync(() => _taskProvider.ListIssuesAsync(repoRoot, taskLimit.Token));
            prsTask = SafeLoadTasksAsync(() => _taskProvider.ListPullRequestsAsync(repoRoot, taskLimit.Token));
        }

        await Task.WhenAll(branchesTask, worktreesTask).ConfigureAwait(false);
        var branches = branchesTask.Result;
        var worktrees = worktreesTask.Result;

        var occupied = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var wt in worktrees)
        {
            if (string.IsNullOrWhiteSpace(wt.Branch)) continue;
            occupied[wt.Branch] = wt.IsMain ? project.Name : (wt.Branch ?? Path.GetFileName(wt.Path));
        }

        var defaultBranch = branches.FirstOrDefault(b => !b.IsRemote && b.IsCurrent)?.Name
            ?? branches.FirstOrDefault(b => !b.IsRemote && (b.Name == "main" || b.Name == "master"))?.Name
            ?? branches.FirstOrDefault(b => !b.IsRemote)?.Name;

        var message = new WorkspaceOptionsMessage
        {
            IsGitRepository = true,
            DefaultBranch = defaultBranch,
            WorkspacesDirectory = _workspaces is null
                ? null
                : WorkspacePaths.FormatHomePath(_workspaces.ProjectWorkspacesDirectory(project), _workspaces.HomeDirectory),
            Branches =
            [
                .. branches.Select(b =>
                {
                    var localName = b.IsRemote && b.Name.IndexOf('/') is var slash and >= 0 ? b.Name[(slash + 1)..] : b.Name;
                    occupied.TryGetValue(b.IsRemote ? localName : b.Name, out var by);
                    return new WorkspaceBranchMessage
                    {
                        Name = b.Name,
                        IsRemote = b.IsRemote,
                        IsCurrent = b.IsCurrent,
                        OccupiedBy = by,
                    };
                }),
            ],
        };

        if (issuesTask is not null && prsTask is not null)
        {
            try
            {
                await Task.WhenAll(issuesTask, prsTask).ConfigureAwait(false);
                var issues = issuesTask.Result;
                var prs = prsTask.Result;
                message.GhAvailable = issues.IsAvailable || prs.IsAvailable;
                message.Issues = [.. issues.Items.Select(ToTaskOptionMessage)];
                message.PullRequests = [.. prs.Items.Select(ToTaskOptionMessage)];
            }
            finally
            {
                taskLimit?.Dispose();
            }
        }

        return message;
    }

    public async Task<WorkspaceMessage> CreateWorkspaceAsync(CreateWorkspaceMessage request)
    {
        if (_workspaces is null)
        {
            throw new RemoteException(RemoteProtocol.MethodNotFound, "Branch workspaces are not enabled on this host.");
        }
        var project = await OnUi(() => FindProject(request.ProjectId)).ConfigureAwait(false);
        var branch = request.BranchName.Trim();
        if (branch.Length == 0)
        {
            throw new RemoteException(RemoteProtocol.InvalidParams, "A branch name is required.");
        }

        WorkspaceTaskLink? task = null;
        if (!string.IsNullOrWhiteSpace(request.TaskKind) || !string.IsNullOrWhiteSpace(request.TaskTitle) || !string.IsNullOrWhiteSpace(request.TaskUrl))
        {
            var kind = request.TaskKind switch
            {
                "issue" => WorkspaceTaskKind.GitHubIssue,
                "pr" => WorkspaceTaskKind.GitHubPr,
                "url" => WorkspaceTaskKind.Url,
                _ => !string.IsNullOrWhiteSpace(request.TaskUrl) ? WorkspaceTaskKind.Url : WorkspaceTaskKind.Note,
            };
            task = new WorkspaceTaskLink
            {
                Kind = kind,
                Key = string.IsNullOrWhiteSpace(request.TaskKey)
                    ? (kind == WorkspaceTaskKind.Url ? "LINK" : "NOTE")
                    : request.TaskKey.Trim(),
                Title = Blank(request.TaskTitle) ?? Blank(request.TaskUrl) ?? "",
                Url = Blank(request.TaskUrl),
                Body = Blank(request.TaskBody),
            };
        }

        var result = await _workspaces.CreateAsync(project, new CreateWorkspaceRequest(
            request.CreateBranch,
            branch,
            Blank(request.BaseRef),
            request.IsRemoteBranch,
            task,
            InjectTaskPrompt: false,
            AfterCreate: WorkspaceAfterCreate.None)).ConfigureAwait(false);

        if (!result.Succeeded || result.Workspace is null)
        {
            throw new RemoteException(RemoteMethods.StartFailed, result.Error ?? "Failed to create branch workspace.");
        }
        return ToWorkspaceMessage(result.Workspace);
    }

    private static async Task<TaskProviderResult> SafeLoadTasksAsync(Func<Task<TaskProviderResult>> load)
    {
        try { return await load().ConfigureAwait(false); }
        catch (Exception e) when (e is OperationCanceledException or IOException) { return new TaskProviderResult(false, null, []); }
    }

    private static WorkspaceTaskOptionMessage ToTaskOptionMessage(RemoteTaskItem item) => new()
    {
        Kind = item.Kind == WorkspaceTaskKind.GitHubPr ? "pr" : "issue",
        Key = item.Key,
        Title = item.Title,
        Url = item.Url,
        Body = item.ToTaskLink().Body,
        SuggestedBranch = item.SuggestedBranch(),
        HeadBranch = item.HeadBranch,
    };

    public Task<IReadOnlyList<AgentMessage>> ListAgentsAsync() => OnUi<IReadOnlyList<AgentMessage>>(() =>
    {
        var agents = _settings.Current.Agents;
        var list = new List<AgentMessage>();
        foreach (var installation in _scanner.Current.Installations)
        {
            if (_catalog.Find(installation.AgentId) is not { } definition || !agents.ProfileOf(definition.Id).Enabled) continue;
            var capabilities = definition.Capabilities;
            list.Add(new AgentMessage
            {
                Id = definition.Id,
                Name = definition.Name,
                Models = [.. (capabilities.Model?.Options ?? []).Select(option => new AgentChoiceMessage { Id = option.Id, Label = option.DisplayLabel })],
                AllowCustomModel = capabilities.Model?.AllowCustom ?? false,
                EffortLabel = capabilities.Effort is null ? null : capabilities.Effort.Label ?? "Effort",
                Efforts = [.. (capabilities.Effort?.Options ?? []).Select(option => new AgentChoiceMessage { Id = option.Id, Label = option.DisplayLabel })],
                TakesPrompt = capabilities.Prompt is not null,
                IsDefault = definition.Id == agents.DefaultAgentId,
                SupportsAcp = definition.SupportsAcp,
                DefaultSurface = definition.SupportsAcp && AgentSurfaceResolver.Resolve(definition, agents) == AgentSurface.Gui
                    ? ItemSurfaces.Gui
                    : ItemSurfaces.Terminal,
            });
        }
        return list;
    });

    public Task<ISessionAttachment> AttachAsync(string itemId, int columns, int rows, bool nativeSnapshot, bool claimSize, ISessionSink sink) => OnUi<ISessionAttachment>(() =>
    {
        var (project, item) = FindItem(itemId);
        // Planning it as a terminal would start a second copy of the agent beside the GUI tab's own.
        if (IsGui(item)) throw NoTerminal();
        var size = new TerminalSize(columns, rows, 0, 0);
        TerminalSession session;
        try
        {
            var dark = IsDark();
            session = _sessions.GetOrStart(item.Id, _launcher.Plan(project, item), _factory, size, TerminalPalettes.For(dark),
                TerminalPalettes.SchemeFor(dark), Math.Max(100, _settings.Current.Terminal.ScrollbackLines), out var created);
            if (created)
            {
                // No tab reports for it yet: its title and its end feed the activity of the item from here.
                session.TitleChanged += title => _activity.ReportTitle(item.Id, title);
                session.Exited += _ => _activity.ReportExit(item.Id);
                Changed?.Invoke();
            }
        }
        catch (Exception e) when (e is not RemoteException)
        {
            throw new RemoteException(RemoteMethods.StartFailed, e.Message);
        }
        return new SessionAttachment(session, sink, nativeSnapshot, claimSize, size);
    });

    public async Task RestartAsync(string itemId)
    {
        var session = await OnUi(() =>
        {
            if (IsGui(FindItem(itemId).Item)) throw NoTerminal();
            return _sessions.Find(itemId) ?? throw new RemoteException(RemoteMethods.NotFound, "The item has no terminal yet.");
        }).ConfigureAwait(false);
        await OnUiAsync(session.RestartAsync).ConfigureAwait(false);
    }

    // ---- GUI conversations (AcpMessages.cs) ----

    public Task<IAcpAttachment> AttachConversationAsync(string itemId, Action<AcpUpdateMessage> updated) => OnUi(() =>
    {
        var (project, item) = FindConversation(itemId);
        return _conversations.Attach(project, item, updated);
    });

    public async Task PromptAsync(AcpPromptMessage prompt)
    {
        var (project, item, roots) = await OnUi(() =>
        {
            var (project, item) = FindConversation(prompt.ItemId);
            return (project, item, project.Workspaces.Select(w => w.Path).Prepend(project.Path).ToList());
        }).ConfigureAwait(false);
        // What is attached must be the conversation's own project: a device uploads a picture there first.
        var paths = new List<string>();
        foreach (var path in prompt.Attachments ?? [])
        {
            string full;
            try { full = Path.GetFullPath(ExpandHome(path.Trim())); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { throw new RemoteException(RemoteProtocol.InvalidParams, e.Message); }
            if (!roots.Any(root => IsWithin(root, full))) throw new RemoteException(RemoteMethods.NotAllowed, "Only files of the conversation's project can be attached.");
            if (!File.Exists(full)) throw new RemoteException(RemoteMethods.NotFound, $"The file '{full}' does not exist on the host.");
            paths.Add(full);
        }
        await OnUiAsync(() => _conversations.PromptAsync(project, item, prompt.Text, paths)).ConfigureAwait(false);
    }

    public async Task CancelTurnAsync(string itemId)
    {
        var (project, item) = await OnUi(() => FindConversation(itemId)).ConfigureAwait(false);
        await OnUiAsync(() => _conversations.CancelAsync(project, item)).ConfigureAwait(false);
    }

    public Task RespondAsync(AcpRespondMessage answer) => OnUi(() =>
    {
        var (project, item) = FindConversation(answer.ItemId);
        _conversations.Respond(project, item, answer);
        return true;
    });

    public Task SetConversationOptionAsync(AcpSetOptionMessage option) => OnUi(() =>
    {
        var (project, item) = FindConversation(option.ItemId);
        _conversations.SetOption(project, item, option);
        return true;
    });

    private (Project Project, ProjectItem Item) FindConversation(string itemId)
    {
        var found = FindItem(itemId);
        return IsGui(found.Item) ? found : throw new RemoteException(RemoteMethods.NotAllowed, "This item runs in a terminal on the host; it has no GUI conversation to show.");
    }

    // ---- files and git of the projects' folders ----

    /// <summary>How long changes under a watched folder collect before the device is told once.</summary>
    private static readonly TimeSpan WatchDelay = TimeSpan.FromMilliseconds(600);

    public async Task<string> ResolveAsync(string path)
    {
        var full = await InsideProjectAsync(path).ConfigureAwait(false);
        return Directory.Exists(full) ? full : throw new RemoteException(RemoteMethods.NotFound, $"The folder '{full}' does not exist on the host.");
    }

    public async Task<IReadOnlyList<FileEntryMessage>> ListAsync(string directory)
    {
        var full = await InsideProjectAsync(directory).ConfigureAwait(false);
        return await IoAsync<IReadOnlyList<FileEntryMessage>>(() =>
            [.. _files.List(full).Select(entry => new FileEntryMessage { Name = entry.Name, Path = entry.FullPath, IsDirectory = entry.IsDirectory, IsHidden = entry.IsHidden })]).ConfigureAwait(false);
    }

    public async Task<bool> ExistsAsync(string path)
    {
        var full = await InsideProjectAsync(path).ConfigureAwait(false);
        return File.Exists(full) || Directory.Exists(full);
    }

    public async Task<(byte[] Data, long Size)> ReadAsync(string path, long offset, int length)
    {
        var full = await InsideProjectAsync(path).ConfigureAwait(false);
        return await IoAsync(() =>
        {
            if (!File.Exists(full)) throw new RemoteException(RemoteMethods.NotFound, $"The file '{full}' does not exist on the host.");
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var size = stream.Length;
            if (offset >= size) return (Array.Empty<byte>(), size);
            stream.Position = offset;
            var data = new byte[(int)Math.Min(length, size - offset)];
            stream.ReadExactly(data);
            return (data, size);
        }).ConfigureAwait(false);
    }

    public async Task WriteAsync(string path, string upload, byte[] data, bool last)
    {
        var full = await InsideProjectAsync(path).ConfigureAwait(false);
        if (!upload.All(char.IsAsciiLetterOrDigit)) throw new RemoteException(RemoteProtocol.InvalidParams, "The upload id may only have letters and digits.");
        await IoAsync(() =>
        {
            // The pieces collect beside the file; the file itself is whole (old or new) at every moment.
            var temporary = full + ".octopus-" + upload + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Append, FileAccess.Write, FileShare.None))
            {
                stream.Write(data);
            }
            if (!last) return true;
            if (!OperatingSystem.IsWindows() && File.Exists(full)) File.SetUnixFileMode(temporary, File.GetUnixFileMode(full));
            File.Move(temporary, full, overwrite: true);
            return true;
        }).ConfigureAwait(false);
    }

    public async Task<string?> UploadAsync(string itemId, string upload, string name, byte[] data, bool last)
    {
        // Only for a terminal of this computer: the picture's path is handed to its program, which reads it from here.
        await OnUi(() =>
        {
            if (IsGui(FindItem(itemId).Item)) throw NoTerminal();
            return true;
        }).ConfigureAwait(false);
        if (!upload.All(char.IsAsciiLetterOrDigit)) throw new RemoteException(RemoteProtocol.InvalidParams, "The upload id may only have letters and digits.");
        var extension = ClipboardImages.ImageExtension(name)
            ?? throw new RemoteException(RemoteProtocol.InvalidParams, "Only pictures (png, jpg, gif or webp) can be uploaded.");
        return await IoAsync(() =>
        {
            var folder = ClipboardImages.DefaultFolder;
            Directory.CreateDirectory(folder);
            var partial = Path.Combine(folder, $"upload-{upload}.part");
            using (var stream = new FileStream(partial, FileMode.Append, FileAccess.Write, FileShare.None))
            {
                if (stream.Length + data.Length > RemoteMethods.UploadMaxBytes)
                {
                    stream.Dispose();
                    File.Delete(partial);
                    throw new RemoteException(RemoteProtocol.InvalidParams, "The picture is too large.");
                }
                stream.Write(data);
            }
            if (!last) return (string?)null;
            ClipboardImages.Cleanup(folder);
            var path = ClipboardImages.NewPath(folder, extension);
            File.Move(partial, path);
            return path;
        }).ConfigureAwait(false);
    }

    public async Task CreateFileAsync(string path) => await Wrap(_files.CreateFileAsync(await InsideProjectAsync(path).ConfigureAwait(false))).ConfigureAwait(false);

    public async Task CreateDirectoryAsync(string path) => await Wrap(_files.CreateDirectoryAsync(await InsideProjectAsync(path).ConfigureAwait(false))).ConfigureAwait(false);

    public async Task RenameAsync(string path, string newPath) =>
        await Wrap(_files.RenameAsync(await InsideProjectAsync(path).ConfigureAwait(false), await InsideProjectAsync(newPath).ConfigureAwait(false))).ConfigureAwait(false);

    public async Task DeleteAsync(string path, bool isDirectory)
    {
        var full = await InsideProjectAsync(path).ConfigureAwait(false);
        var roots = await OnUi(LocalRoots).ConfigureAwait(false);
        if (roots.Any(root => _paths.Equals(root, full))) throw new RemoteException(RemoteMethods.NotAllowed, "A project's own folder is not deleted from another device.");
        await Wrap(_files.DeleteAsync(full, isDirectory)).ConfigureAwait(false);
    }

    public async Task<IDisposable> WatchAsync(string folder, Action changed) =>
        _files.WatchChanges(await InsideProjectAsync(folder).ConfigureAwait(false), changed, WatchDelay);

    public async Task<BrowseFolderResultMessage> BrowseFolderAsync(BrowseFolderMessage request)
    {
        var projects = await OnUi<IReadOnlyList<Octopus.FileSystem.FolderBrowseProject>>(() =>
            [.. _projects.Projects.Where(ProjectLocation.IsOnThisComputer).Select(p => new Octopus.FileSystem.FolderBrowseProject(p.Name, p.Path))]).ConfigureAwait(false);
        var browser = new Octopus.FileSystem.LocalFolderBrowser(Environment.MachineName, () => projects, _paths);
        try
        {
            var page = await browser.BrowseAsync(request.Path, request.ListRoots, request.FallbackToParent).ConfigureAwait(false);
            return new BrowseFolderResultMessage
            {
                Path = page.CurrentPath,
                Parent = page.ParentPath,
                Separator = page.Separator,
                Home = page.HomePath,
                Roots = [.. page.Roots.Select(ToBrowseEntry)],
                Entries = [.. page.Entries.Select(ToBrowseEntry)],
            };
        }
        catch (DirectoryNotFoundException e) { throw new RemoteException(RemoteMethods.NotFound, e.Message); }
        catch (ArgumentException e) { throw new RemoteException(RemoteProtocol.InvalidParams, e.Message); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new RemoteException(RemoteMethods.NotAllowed, e.Message); }
    }

    public async Task<string> BrowseCreateDirectoryAsync(CreateBrowseFolderMessage request)
    {
        var browser = new Octopus.FileSystem.LocalFolderBrowser(Environment.MachineName, pathComparer: _paths);
        try
        {
            return await browser.CreateFolderAsync(request.ParentPath, request.Name).ConfigureAwait(false);
        }
        catch (DirectoryNotFoundException e) { throw new RemoteException(RemoteMethods.NotFound, e.Message); }
        catch (ArgumentException e) { throw new RemoteException(RemoteProtocol.InvalidParams, e.Message); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new RemoteException(RemoteMethods.NotAllowed, e.Message); }
    }

    private static BrowseEntryMessage ToBrowseEntry(Octopus.FileSystem.FolderBrowseEntry entry) => new()
    {
        Name = entry.Name,
        Path = entry.Path,
        IsHidden = entry.IsHidden,
        IsExistingProject = entry.IsExistingProject,
        Kind = entry.Kind switch
        {
            Octopus.FileSystem.FolderEntryKind.Drive => "drive",
            Octopus.FileSystem.FolderEntryKind.Home => "home",
            _ => "folder",
        },
    };

    public async Task<string> GitAsync(string method, string arguments)
    {
        // The repository a project sits in may be above the project's folder (a subfolder of a larger repository): what
        // is asked for must be a project folder or the repository root of one.
        var root = RemoteGitDispatcher.RootOf(arguments);
        if (!await IsProjectOrItsRepositoryAsync(root).ConfigureAwait(false)) throw new RemoteException(RemoteMethods.NotAllowed, "That folder is not a project of the host.");
        return await RemoteGitDispatcher.CallAsync(_git, method, arguments).ConfigureAwait(false);
    }

    private List<string> LocalRoots() =>
    [
        .. _projects.Projects
            .Where(ProjectLocation.IsOnThisComputer)
            .SelectMany(p => p.Workspaces.Select(w => w.Path).Prepend(p.Path)),
    ];

    /// <summary>The full path, when it is a project's folder or lies in one; anything else is refused (design/12 §5.4).</summary>
    private async Task<string> InsideProjectAsync(string path)
    {
        string full;
        try { full = Path.GetFullPath(ExpandHome(path.Trim())); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { throw new RemoteException(RemoteProtocol.InvalidParams, e.Message); }
        var roots = await OnUi(LocalRoots).ConfigureAwait(false);
        return roots.Any(root => IsWithin(root, full)) ? full : throw new RemoteException(RemoteMethods.NotAllowed, "Only the folders of the host's projects can be reached.");
    }

    private bool IsWithin(string root, string path)
    {
        root = Path.TrimEndingDirectorySeparator(root);
        if (_paths.Equals(root, path)) return true;
        var prefix = root + Path.DirectorySeparatorChar;
        return path.Length > prefix.Length && _paths.Equals(prefix, path[..prefix.Length]);
    }

    private async Task<bool> IsProjectOrItsRepositoryAsync(string root)
    {
        string full;
        try { full = Path.GetFullPath(root); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        var roots = await OnUi(LocalRoots).ConfigureAwait(false);
        if (roots.Any(project => IsWithin(project, full))) return true;
        foreach (var project in roots.Where(project => IsWithin(full, Path.TrimEndingDirectorySeparator(project))))
        {
            if (await _git.FindRepositoryRootAsync(project).ConfigureAwait(false) is { } repository && _paths.Equals(Path.TrimEndingDirectorySeparator(repository), Path.TrimEndingDirectorySeparator(full))) return true;
        }
        return false;
    }

    // File work off the UI thread, with what the system says about a failure sent back as the reason.
    private static async Task<T> IoAsync<T>(Func<T> action)
    {
        try { return await Task.Run(action).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new RemoteException(RemoteMethods.NotAllowed, e.Message); }
    }

    private static async Task Wrap(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new RemoteException(RemoteMethods.NotAllowed, e.Message); }
    }

    public void Dispose()
    {
        _projects.Changed -= OnProjectsChanged;
        _activity.Changed -= OnActivityChanged;
        _conversations.Dispose();
    }

    private void OnProjectsChanged(object? sender, ProjectChangedEventArgs e)
    {
        switch (e.Kind)
        {
            case ProjectChangeKind.Projects or ProjectChangeKind.Items or ProjectChangeKind.Order or ProjectChangeKind.Workspaces:
                Changed?.Invoke();
                break;
            case ProjectChangeKind.ItemTitle when e.Item is { } item && e.Project is { } project && IsSession(item):
                ItemChanged?.Invoke(ToMessage(project, item));
                break;
        }
    }

    private void OnActivityChanged(object? sender, string itemId)
    {
        if (_projects.FindProjectOf(itemId) is { } project && _projects.FindItem(itemId) is { } item && IsSession(item))
        {
            ItemChanged?.Invoke(ToMessage(project, item));
        }
    }

    private static bool IsSession(ProjectItem item) => item.Kind is ProjectItemKind.Terminal or ProjectItemKind.Agent;

    /// <summary>An agent that runs as an ACP conversation in a GUI tab: it has no terminal a device could attach to.</summary>
    private static bool IsGui(ProjectItem item) => item.Kind == ProjectItemKind.Agent && item.Surface == Octopus.Core.Agents.AgentSurface.Gui;

    private static RemoteException NoTerminal() =>
        new(RemoteMethods.NotAllowed, "This agent runs as a GUI conversation on the host; it has no terminal to show.");

    private Project FindProject(string projectId) =>
        _projects.Projects.FirstOrDefault(p => p.Id == projectId && ProjectLocation.IsOnThisComputer(p))
        ?? throw new RemoteException(RemoteMethods.NotFound, "The project does not exist on the host.");

    private (Project Project, ProjectItem Item) FindItem(string itemId) =>
        _projects.FindProjectOf(itemId) is { } project && ProjectLocation.IsOnThisComputer(project) && _projects.FindItem(itemId) is { } item && IsSession(item)
            ? (project, item)
            : throw new RemoteException(RemoteMethods.NotFound, "The item does not exist on the host.");

    private static ProjectMessage ToMessage(Project project)
    {
        var (isGit, defaultBranch) = InspectGitRepository(project.Path);
        return new ProjectMessage
        {
            Id = project.Id,
            Name = project.Name,
            Path = project.Path,
            Color = project.Color,
            Icon = project.Icon,
            IsGitRepository = isGit,
            DefaultBranch = defaultBranch,
            Workspaces = project.Workspaces.Count > 0 ? [.. project.Workspaces.Select(ToWorkspaceMessage)] : null,
        };
    }

    private static WorkspaceMessage ToWorkspaceMessage(ProjectWorkspace workspace) => new()
    {
        Id = workspace.Id,
        Name = workspace.Name,
        Branch = workspace.Branch,
        Path = workspace.Path,
        TaskKind = workspace.Task?.Kind switch
        {
            WorkspaceTaskKind.GitHubIssue => "issue",
            WorkspaceTaskKind.GitHubPr => "pr",
            WorkspaceTaskKind.Url => "url",
            WorkspaceTaskKind.Note => "note",
            _ => null,
        },
        TaskKey = workspace.Task?.Key,
        TaskTitle = workspace.Task?.Title,
        TaskUrl = workspace.Task?.Url,
    };

    private static (bool IsGit, string? Branch) InspectGitRepository(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return (false, null);
        try
        {
            for (var dir = new DirectoryInfo(path); dir is not null; dir = dir.Parent)
            {
                var dotGit = Path.Combine(dir.FullName, ".git");
                if (Directory.Exists(dotGit))
                {
                    return (true, ReadHeadBranch(Path.Combine(dotGit, "HEAD")));
                }
                if (File.Exists(dotGit))
                {
                    // Linked worktree / submodule pointer file: "gitdir: <path>"
                    var first = File.ReadLines(dotGit).FirstOrDefault()?.Trim();
                    if (first is not null && first.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
                    {
                        var gitDir = first["gitdir:".Length..].Trim();
                        if (!Path.IsPathRooted(gitDir)) gitDir = Path.GetFullPath(Path.Combine(dir.FullName, gitDir));
                        return (true, ReadHeadBranch(Path.Combine(gitDir, "HEAD")));
                    }
                    return (true, null);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
        return (false, null);
    }

    private static string? ReadHeadBranch(string headFile)
    {
        try
        {
            if (!File.Exists(headFile)) return null;
            var line = File.ReadLines(headFile).FirstOrDefault()?.Trim();
            const string prefix = "ref: refs/heads/";
            return line is not null && line.StartsWith(prefix, StringComparison.Ordinal)
                ? line[prefix.Length..].Trim()
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private ItemMessage ToMessage(Project project, ProjectItem item) => new()
    {
        Id = item.Id,
        ProjectId = project.Id,
        Kind = item.Kind == ProjectItemKind.Agent ? ItemKinds.Agent : ItemKinds.Terminal,
        Title = item.DisplayTitle,
        AgentId = item.AgentId,
        Model = item.Launch?.Model,
        Effort = item.Launch?.Effort,
        WorkspaceId = item.WorkspaceId,
        WorkspaceBranch = item.WorkspaceId is null ? null : project.Workspaces.FirstOrDefault(w => w.Id == item.WorkspaceId)?.DisplayName,
        Activity = _activity.Of(item.Id) switch
        {
            ItemActivity.Working => ItemActivities.Working,
            ItemActivity.Waiting => ItemActivities.Waiting,
            ItemActivity.Error => ItemActivities.Error,
            _ => ItemActivities.Idle,
        },
        IsRunning = _sessions.Find(item.Id)?.IsRunning == true,
        Surface = IsGui(item) ? ItemSurfaces.Gui : item.Kind == ProjectItemKind.Agent ? ItemSurfaces.Terminal : null,
    };

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string ExpandHome(string path) =>
        path == "~" ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        : path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
            : path;

    private static Task<T> OnUi<T>(Func<T> action) =>
        Dispatcher.UIThread.CheckAccess() ? Run(action) : Dispatcher.UIThread.InvokeAsync(action).GetTask();

    private static Task<T> Run<T>(Func<T> action)
    {
        try { return Task.FromResult(action()); }
        catch (Exception e) { return Task.FromException<T>(e); }
    }

    private static Task OnUiAsync(Func<Task> action) =>
        Dispatcher.UIThread.CheckAccess() ? action() : Dispatcher.UIThread.InvokeAsync(action);

    /// <summary>One device's view of one session: the session's output and events go to the sink, the device's input and size requests come in.</summary>
    private sealed class SessionAttachment : ISessionAttachment, ITerminalTap
    {
        private readonly TerminalSession _session;
        private readonly ISessionSink _sink;
        private readonly IDisposable _tap;
        // The size this view last asked for: applied again when the say comes back to it.
        private TerminalSize _wanted;
        private int _disposed;

        public SessionAttachment(TerminalSession session, ISessionSink sink, bool native, bool claimSize, TerminalSize size)
        {
            _session = session;
            _sink = sink;
            _wanted = size;
            // First the size, then the snapshot: what the device gets is already laid out for its view.
            if (claimSize) session.ClaimSize(this, size);
            _tap = session.Tap(this, native ? TerminalSnapshotFormat.Native : TerminalSnapshotFormat.Vt, out var start);
            Position = start.Position;
            IsNative = start.Format == TerminalSnapshotFormat.Native;
            _snapshot = start.Snapshot;
            SnapshotLength = start.Snapshot.Length;
            (Columns, Rows) = (start.Size.Columns, start.Size.Rows);
            Title = start.Title;
            IsRunning = session.IsRunning;
            ExitCode = CodeOf(session.LastExit);
            session.Exited += OnExited;
            session.SizeOwnerChanged += OnSizeOwnerChanged;
        }

        private byte[]? _snapshot;

        public ulong Position { get; }
        public bool IsNative { get; }
        public int SnapshotLength { get; }
        public byte[] TakeSnapshot() => Interlocked.Exchange(ref _snapshot, null) ?? [];
        public int Columns { get; }
        public int Rows { get; }
        public string? Title { get; }
        public bool IsRunning { get; }
        public int? ExitCode { get; }
        public bool OwnsSize => ReferenceEquals(_session.SizeOwner, this);

        public void Input(ReadOnlySpan<byte> bytes) => _session.Send(bytes);

        public (bool Applied, int Columns, int Rows) Resize(int columns, int rows, bool claim)
        {
            var size = new TerminalSize(columns, rows, 0, 0);
            _wanted = size;
            var applied = true;
            if (claim) _session.ClaimSize(this, size);
            else applied = _session.Resize(size, this);
            var now = _session.Size;
            return (applied, now.Columns, now.Rows);
        }

        public (int Columns, int Rows) ReleaseSize()
        {
            _session.ReleaseSize(this);
            var now = _session.Size;
            return (now.Columns, now.Rows);
        }

        public void OnOutput(ulong position, ReadOnlySpan<byte> bytes) => _sink.Output(position, bytes);
        public void OnResize(ulong position, TerminalSize size) => _sink.Resized(position, size.Columns, size.Rows);
        public void OnReset(ulong position) => _sink.Reset(position);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _session.Exited -= OnExited;
            _session.SizeOwnerChanged -= OnSizeOwnerChanged;
            _tap.Dispose();
            // The window's own view decides the size again.
            _session.ReleaseSize(this);
        }

        private void OnExited(Octopus.Core.Terminals.TerminalExit exit) => _sink.Exited(CodeOf(exit));

        // The view that had the say left and it came back to this one: the terminal takes this view's size again.
        private void OnSizeOwnerChanged()
        {
            if (OwnsSize) _session.Resize(_wanted, this);
        }

        private static int? CodeOf(Octopus.Core.Terminals.TerminalExit? exit) =>
            exit is { } ended ? ended.ExitCode ?? (ended.Signal is { } signal ? 128 + signal : null) : null;
    }
}
