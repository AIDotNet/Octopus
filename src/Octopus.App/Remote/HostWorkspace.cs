using System.Reflection;
using Avalonia.Threading;
using Octopus.Agents;
using Octopus.App.Activity;
using Octopus.App.Common;
using Octopus.App.Services;
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

    public HostWorkspace(IProjectService projects, IItemLaunchService launcher, ITerminalConnectionFactory factory, TerminalSessionRegistry sessions,
        ITabContentRegistry tabs, IItemActivityService activity, IAgentCatalog catalog, IAgentScanner scanner, ISettingsStore settings, IShellResolver shells,
        Octopus.FileSystem.IFileTreeService files, Octopus.Git.IGitService git, PathComparer paths)
    {
        _files = files;
        _git = git;
        _paths = paths;
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
        ProjectItem item;
        if (request.Kind == ItemKinds.Agent)
        {
            var definition = request.AgentId is null ? null : _catalog.Find(request.AgentId);
            if (definition is null || _scanner.Find(definition.Id) is null)
            {
                throw new RemoteException(RemoteMethods.NotFound, $"The agent '{request.AgentId}' is not installed on the host.");
            }
            item = new ProjectItem
            {
                Kind = ProjectItemKind.Agent,
                AgentId = definition.Id,
                Title = definition.Name,
                Launch = new AgentLaunchOptions
                {
                    Model = Blank(request.Model),
                    Effort = Blank(request.Effort),
                    Prompt = Blank(request.Prompt),
                },
            };
        }
        else
        {
            var chosen = _settings.Current.Terminal.Shell;
            var shell = (string.IsNullOrEmpty(chosen) ? null : _shells.Describe(chosen)) ?? _shells.GetDefaultShell();
            item = new ProjectItem { Kind = ProjectItemKind.Terminal, Title = shell.Name };
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
            });
        }
        return list;
    });

    public Task<ISessionAttachment> AttachAsync(string itemId, int columns, int rows, bool nativeSnapshot, bool claimSize, ISessionSink sink) => OnUi<ISessionAttachment>(() =>
    {
        var (project, item) = FindItem(itemId);
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
            FindItem(itemId);
            return _sessions.Find(itemId) ?? throw new RemoteException(RemoteMethods.NotFound, "The item has no terminal yet.");
        }).ConfigureAwait(false);
        await OnUiAsync(session.RestartAsync).ConfigureAwait(false);
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

    public async Task<string> GitAsync(string method, string arguments)
    {
        // The repository a project sits in may be above the project's folder (a subfolder of a larger repository): what
        // is asked for must be a project folder or the repository root of one.
        var root = RemoteGitDispatcher.RootOf(arguments);
        if (!await IsProjectOrItsRepositoryAsync(root).ConfigureAwait(false)) throw new RemoteException(RemoteMethods.NotAllowed, "That folder is not a project of the host.");
        return await RemoteGitDispatcher.CallAsync(_git, method, arguments).ConfigureAwait(false);
    }

    private List<string> LocalRoots() => [.. _projects.Projects.Where(ProjectLocation.IsOnThisComputer).Select(p => p.Path)];

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
    }

    private void OnProjectsChanged(object? sender, ProjectChangedEventArgs e)
    {
        switch (e.Kind)
        {
            case ProjectChangeKind.Projects or ProjectChangeKind.Items or ProjectChangeKind.Order:
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

    private Project FindProject(string projectId) =>
        _projects.Projects.FirstOrDefault(p => p.Id == projectId && ProjectLocation.IsOnThisComputer(p))
        ?? throw new RemoteException(RemoteMethods.NotFound, "The project does not exist on the host.");

    private (Project Project, ProjectItem Item) FindItem(string itemId) =>
        _projects.FindProjectOf(itemId) is { } project && ProjectLocation.IsOnThisComputer(project) && _projects.FindItem(itemId) is { } item && IsSession(item)
            ? (project, item)
            : throw new RemoteException(RemoteMethods.NotFound, "The item does not exist on the host.");

    private static ProjectMessage ToMessage(Project project) =>
        new() { Id = project.Id, Name = project.Name, Path = project.Path, Color = project.Color, Icon = project.Icon };

    private ItemMessage ToMessage(Project project, ProjectItem item) => new()
    {
        Id = item.Id,
        ProjectId = project.Id,
        Kind = item.Kind == ProjectItemKind.Agent ? ItemKinds.Agent : ItemKinds.Terminal,
        Title = item.DisplayTitle,
        AgentId = item.AgentId,
        Model = item.Launch?.Model,
        Effort = item.Launch?.Effort,
        Activity = _activity.Of(item.Id) switch
        {
            ItemActivity.Working => ItemActivities.Working,
            ItemActivity.Waiting => ItemActivities.Waiting,
            ItemActivity.Error => ItemActivities.Error,
            _ => ItemActivities.Idle,
        },
        IsRunning = _sessions.Find(item.Id)?.IsRunning == true,
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
            Snapshot = start.Snapshot;
            (Columns, Rows) = (start.Size.Columns, start.Size.Rows);
            Title = start.Title;
            IsRunning = session.IsRunning;
            ExitCode = CodeOf(session.LastExit);
            session.Exited += OnExited;
            session.SizeOwnerChanged += OnSizeOwnerChanged;
        }

        public ulong Position { get; }
        public bool IsNative { get; }
        public byte[] Snapshot { get; }
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
