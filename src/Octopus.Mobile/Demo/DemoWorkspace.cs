using System.Collections.ObjectModel;
using Avalonia.Threading;
using Octopus.Core.Projects;
using Octopus.Mobile.Workspace;
using Octopus.Remote.Client;
using Octopus.Remote.Relay;
using Octopus.Remote.Security;
using Octopus.Terminal;

namespace Octopus.Mobile.Demo;

/// <summary>
/// Hosts, projects and sessions made up for the preview and the tests: three paired computers, one that is not paired
/// yet, and six sessions of which one waits for the user. Without an account (<c>localOnly</c>) only the two that need
/// no service are there. Nothing here talks to a network.
/// </summary>
public sealed partial class DemoWorkspace : IRemoteWorkspace
{
    private static readonly AgentOption Claude = new("claude", "Claude", "C", "#D97757", ["Fable 5.2", "Opus 5.5", "Sonnet 5.5"], ["低", "中", "高", "极高"]);
    private static readonly AgentOption Codex = new("codex", "Codex", "X", "#1F9D74", ["GPT-6", "GPT-6 mini"], ["低", "中", "高"]);

    private readonly Dictionary<string, Func<DemoTerminalConnection>> _screens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DemoTerminalFactory> _factories = new(StringComparer.Ordinal);
    private int _nextId = 100;

    /// <summary>How long the made-up host takes to allow a pairing request.</summary>
    public TimeSpan PairingDelay { get; init; } = TimeSpan.FromSeconds(3);

    /// <param name="localOnly">Only the computers a phone without an account has: paired, and reached directly or not at all.</param>
    public DemoWorkspace(bool localOnly = false)
    {
        var mac = new RemoteHost("mac", "MacBook Pro", HostLink.Lan, isPaired: true) { Agents = [Claude, Codex] };
        var octopus = Add(mac, "Octopus", "~/code/Octopus");
        var server = Add(mac, "OctopusServer", "~/code/OctopusServer");
        var arc = Add(mac, "TKCode.Arc", "~/code/TKCode.Arc");

        var handshake = Add(octopus, "远程协议握手实现", Claude, "Fable 5.2 · 极高", SessionActivity.Waiting, "要运行 dotnet test 吗？");
        _screens[handshake.Id] = () => new DemoTerminalConnection(
            DemoScreens.Asking("Octopus", "握手和记录加密写完了，先跑一遍测试向量。", "dotnet test Octopus.Remote.Tests"),
            input => AnswerPermission(handshake, input));

        var resize = Add(octopus, "修复终端尺寸同步", Claude, "Fable 5.2 · 高", SessionActivity.Working, "正在改 TerminalSession.cs");
        _screens[resize.Id] = () => new DemoTerminalConnection(
            DemoScreens.Working("Octopus", "正在把尺寸变化排进输出序列", "src/Octopus.Terminal.Avalonia/TerminalSession.cs"), Echo);

        var watch = Add(octopus, "dotnet watch", null, "zsh", SessionActivity.Idle, null);
        _screens[watch.Id] = () => new DemoTerminalConnection(DemoScreens.Shell("Octopus"), input => Shell("Octopus", input));

        var relay = Add(server, "中转隧道对接", Codex, "GPT-6 · 高", SessionActivity.Working, "运行 dotnet build");
        _screens[relay.Id] = () => new DemoTerminalConnection(
            DemoScreens.Working("OctopusServer", "控制连接和数据连接已经分开", "运行 dotnet build OctopusServer.slnx"), Echo);

        var ghostty = Add(arc, "ghostty 的 iOS 构建", Claude, "Fable 5.2 · 高", SessionActivity.Error, "zig build 失败：找不到 iOS SDK");
        _screens[ghostty.Id] = () => new DemoTerminalConnection(
            DemoScreens.Failed("TKCode.Arc", "运行 tools/terminal/build-ghostty.sh ios-arm64", "zig build 失败：找不到 iOS SDK"), Echo);

        var station = new RemoteHost("station", "工作站", HostLink.Relay, isPaired: true) { Agents = [Codex] };
        var console = Add(station, "web-console", "~/srv/web-console");
        var migrate = Add(console, "升级数据库迁移", Codex, "GPT-6 · 中", SessionActivity.Idle, null);
        // Runs as a GUI conversation on the computer: the phone shows its details, not a terminal.
        Add(console, "接口文档整理", Codex, "GPT-6 · 高", SessionActivity.Idle, "上一轮写好了 docs/api.md", gui: true);
        _screens[migrate.Id] = () => new DemoTerminalConnection(
            DemoScreens.Lines("\u001b[35m●\u001b[0m 迁移已经生成并应用，测试全部通过。", "", "> "), Echo);

        var mini = new RemoteHost("mini", "Mac mini", HostLink.Offline, isPaired: true)
        {
            LastSeen = DateTimeOffset.Now.AddHours(-11),
        };
        var linux = new RemoteHost("linux", "Linux 服务器", HostLink.Relay, isPaired: false);

        Hosts = localOnly ? [mac, mini] : [mac, station, mini, linux];
    }

    public ObservableCollection<RemoteHost> Hosts { get; }

    public Task<RemoteSession> CreateSessionAsync(RemoteProject project, NewSessionRequest request, CancellationToken cancellationToken = default)
    {
        var agent = request.AgentId is null ? null : project.Host.Agents.FirstOrDefault(a => a.Id == request.AgentId);
        var workspace = string.IsNullOrEmpty(request.WorkspaceId) ? null : project.Workspaces.FirstOrDefault(w => w.Id == request.WorkspaceId);
        var prompt = string.IsNullOrWhiteSpace(request.Prompt) ? null : request.Prompt.Trim();
        if (agent is not null && request.InjectTaskPrompt && workspace is { HasTask: true })
        {
            var taskHeader = $"{workspace.TaskKey}: {workspace.TaskTitle}".Trim();
            prompt = prompt is null ? taskHeader : $"{taskHeader}\n\n{prompt}";
        }
        var title = agent is null ? "zsh" : prompt is null ? agent.Name : FirstLine(prompt, 40);
        var detail = agent is null
            ? "zsh"
            : string.Join(" · ", new[] { request.Model ?? agent.Models.FirstOrDefault(), request.Effort }.Where(part => !string.IsNullOrEmpty(part)));
        var session = Add(project, title, agent, detail, agent is not null && prompt is not null ? SessionActivity.Working : SessionActivity.Idle, prompt is null ? null : "正在读代码", workspace);
        var folder = workspace is not null ? $"{project.Name} ({workspace.DisplayName})" : project.Name;
        _screens[session.Id] = agent is null
            ? () => new DemoTerminalConnection(DemoScreens.Prompt(folder), input => Shell(folder, input))
            : () => new DemoTerminalConnection(
                prompt is null ? "> " : DemoScreens.Working(folder, prompt, "正在读代码"), Echo);
        return Task.FromResult(session);
    }

    public Task<RemoteWorkspaceOptions> GetWorkspaceOptionsAsync(RemoteProject project, CancellationToken cancellationToken = default)
    {
        if (!project.IsGitRepository) return Task.FromResult(RemoteWorkspaceOptions.Empty);
        var occupied = project.Workspaces.ToDictionary(w => w.Branch, w => w.DisplayName, StringComparer.Ordinal);
        var def = project.DefaultBranch ?? "main";
        occupied.TryAdd(def, project.Name);
        var branches = new List<RemoteBranchOption>
        {
            new(def, IsRemote: false, IsCurrent: true, OccupiedBy: occupied.GetValueOrDefault(def)),
            new("feat/mobile-workspaces", IsRemote: false, IsCurrent: false, OccupiedBy: occupied.GetValueOrDefault("feat/mobile-workspaces")),
            new("origin/release/v1.2", IsRemote: true, IsCurrent: false, OccupiedBy: occupied.GetValueOrDefault("release/v1.2")),
        };
        var issues = new List<RemoteTaskOption>
        {
            new("issue", "#142", "Support git worktree branch workspaces", "https://github.com/AIDotNet/Octopus/issues/142", "Create isolated worktrees under ~/.octopus/workspaces.", "feat/142-support-git-worktree-branch-workspaces", null),
            new("issue", "#156", "Fix terminal resize race on mobile reconnect", "https://github.com/AIDotNet/Octopus/issues/156", "Ensure ClaimSize applies cleanly after reconnect.", "fix/156-fix-terminal-resize-race", null),
        };
        var prs = new List<RemoteTaskOption>
        {
            new("pr", "PR #138", "feat: mobile remote workspace sync", "https://github.com/AIDotNet/Octopus/pull/138", "Sync host workspaces over remote protocol.", "feat/mobile-workspace-sync", "feat/mobile-workspace-sync"),
        };
        return Task.FromResult(new RemoteWorkspaceOptions(true, def, branches, true, issues, prs));
    }

    public Task<RemoteBranchWorkspace> CreateWorkspaceAsync(RemoteProject project, CreateBranchWorkspaceRequest request, CancellationToken cancellationToken = default)
    {
        var raw = request.BranchName.Trim();
        var localBranch = !request.CreateBranch && request.IsRemoteBranch && raw.IndexOf('/') is var slash and >= 0
            ? raw[(slash + 1)..]
            : raw;
        var path = $"~/.octopus/workspaces/{WorkspacePaths.SanitizeSegment(project.Name)}/{WorkspacePaths.SanitizeSegment(localBranch)}";
        var workspace = new RemoteBranchWorkspace(
            "w" + _nextId++,
            localBranch,
            localBranch,
            path,
            request.TaskKind,
            request.TaskKey,
            request.TaskTitle,
            request.TaskUrl);
        project.Workspaces.Add(workspace);
        return Task.FromResult(workspace);
    }

    public Task RenameSessionAsync(RemoteSession session, string? title, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(title)) session.Title = title.Trim();
        return Task.CompletedTask;
    }

    public Task CloseSessionAsync(RemoteSession session, CancellationToken cancellationToken = default)
    {
        session.Project.Sessions.Remove(session);
        _factories.Remove(session.Id);
        return Task.CompletedTask;
    }

    public Task RestartSessionAsync(RemoteSession session, CancellationToken cancellationToken = default)
    {
        Restarted.Add(session.Id);
        return Task.CompletedTask;
    }

    /// <summary>The sessions asked to restart, in order, for tests.</summary>
    internal List<string> Restarted { get; } = [];

    // The made-up computers' folders: the same small tree on each, under a home of /Users/demo.
    private const string DemoHome = "/Users/demo";
    private readonly Dictionary<string, List<string>> _folders = new(StringComparer.Ordinal)
    {
        ["/"] = ["Users", "opt", "tmp"],
        ["/Users"] = ["demo"],
        ["/Users/demo"] = ["code", "Documents", "Downloads"],
        ["/Users/demo/code"] = ["Octopus", "OctopusServer", "TKCode.Arc", "playground", "website"],
    };

    public Task<RemoteFolderListing> BrowseFolderAsync(RemoteHost host, string? path, CancellationToken cancellationToken = default)
    {
        var current = string.IsNullOrEmpty(path) ? DemoHome : path.TrimEnd('/') is { Length: > 0 } trimmed ? trimmed : "/";
        var parent = current == "/" ? null : current[..current.LastIndexOf('/')] is { Length: > 0 } up ? up : "/";
        var projects = host.Projects.Select(project => project.Path.Replace("~", DemoHome, StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        var folders = _folders.GetValueOrDefault(current, []).Select(name =>
        {
            var full = current == "/" ? "/" + name : current + "/" + name;
            return new RemoteFolderEntry(name, full, "folder", projects.Contains(full));
        }).ToList();
        RemoteFolderEntry[] roots = [new("demo", DemoHome, "home", false), new("/", "/", "drive", false)];
        return Task.FromResult(new RemoteFolderListing(current, parent, "/", DemoHome, roots, folders));
    }

    public Task<string> CreateFolderAsync(RemoteHost host, string parentPath, string name, CancellationToken cancellationToken = default)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0 || trimmed.Contains('/')) throw new IOException("文件夹名不能为空，也不能含有 /。");
        if (!_folders.TryGetValue(parentPath, out var children)) _folders[parentPath] = children = [];
        if (children.Contains(trimmed, StringComparer.Ordinal)) throw new IOException($"“{trimmed}”已经存在。");
        children.Add(trimmed);
        return Task.FromResult(parentPath == "/" ? "/" + trimmed : parentPath + "/" + trimmed);
    }

    public Task<RemoteProject> CreateProjectAsync(RemoteHost host, string path, CancellationToken cancellationToken = default)
    {
        var shown = path.StartsWith(DemoHome, StringComparison.Ordinal) ? "~" + path[DemoHome.Length..] : path;
        if (host.Projects.FirstOrDefault(project => project.Path == shown) is { } known) return Task.FromResult(known);
        var name = path.TrimEnd('/').Split('/').LastOrDefault(part => part.Length > 0) ?? path;
        return Task.FromResult(Add(host, name, shown));
    }

    public ITerminalConnectionFactory OpenTerminal(RemoteSession session)
    {
        if (!_factories.TryGetValue(session.Id, out var factory))
        {
            var create = _screens.GetValueOrDefault(session.Id) ?? (() => new DemoTerminalConnection("> ", Echo));
            _factories[session.Id] = factory = new DemoTerminalFactory(create);
        }
        return factory;
    }

    /// <summary>The connection the terminal page of a session last opened, for tests.</summary>
    internal DemoTerminalConnection? ConnectionOf(RemoteSession session) => _factories.GetValueOrDefault(session.Id)?.Last;

    public Task ClaimSizeAsync(RemoteSession session, int columns, int rows)
    {
        session.SizeOwner = null;
        session.Columns = columns;
        session.Rows = rows;
        return Task.CompletedTask;
    }

    public Task ReleaseSizeAsync(RemoteSession session)
    {
        session.SizeOwner = session.Project.Host.Name;
        session.Columns = 132;
        session.Rows = 40;
        return Task.CompletedTask;
    }

    public PairingAttempt BeginPairing(RemoteHost host)
    {
        var cancel = new CancellationTokenSource();
        return new PairingAttempt(Task.FromResult("482913"), AllowAsync(host, cancel.Token), cancel.Cancel);
    }

    // A made-up computer at whatever address was typed: a number first, as a real one shows, unless it is a pairing link.
    public PairingAttempt AddHost(string target)
    {
        var cancel = new CancellationTokenSource();
        var code = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var isLink = target.Trim().StartsWith(PairingLink.Scheme + "://pair?", StringComparison.OrdinalIgnoreCase);
        var address = isLink ? null : RemoteHostConnections.ParseAddress(target);
        if (!isLink && address is null)
        {
            code.SetCanceled();
            return new PairingAttempt(code.Task, Task.FromResult(PairingOutcome.NotAnAddress), cancel.Cancel);
        }
        if (!isLink) code.SetResult("482913");
        var host = new RemoteHost("added" + _nextId++, address?.Host ?? "Studio", HostLink.Lan, isPaired: false);
        return new PairingAttempt(code.Task, AddAsync(host, code, cancel.Token), cancel.Cancel);
    }

    public Task RefreshAsync() => Task.CompletedTask;

    // The made-up terminals are this app's own: nothing to mirror.
    public bool MirrorsTerminals => false;

    private async Task<PairingOutcome> AllowAsync(RemoteHost host, CancellationToken cancellationToken)
    {
        try { await Task.Delay(PairingDelay, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return PairingOutcome.Cancelled; }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            host.IsPaired = true;
            if (host.Agents.Count == 0) host.Agents = [Claude];
            if (host.Projects.Count == 0) Add(host, "infra", "~/infra");
        });
        return PairingOutcome.Paired;
    }

    private async Task<PairingOutcome> AddAsync(RemoteHost host, TaskCompletionSource<string> code, CancellationToken cancellationToken)
    {
        var outcome = await AllowAsync(host, cancellationToken).ConfigureAwait(false);
        if (outcome == PairingOutcome.Paired) await Dispatcher.UIThread.InvokeAsync(() => Hosts.Add(host));
        // A link never shows a number.
        code.TrySetCanceled();
        return outcome;
    }

    private static RemoteProject Add(RemoteHost host, string name, string path)
    {
        var project = new RemoteProject(host, host.Id + "/" + name, name, path)
        {
            IsGitRepository = true,
            DefaultBranch = "main",
        };
        host.Projects.Add(project);
        return project;
    }

    private RemoteSession Add(RemoteProject project, string title, AgentOption? agent, string detail, SessionActivity activity, string? preview, RemoteBranchWorkspace? workspace = null, bool gui = false)
    {
        var parts = agent is null ? [] : detail.Split(" · ");
        var session = new RemoteSession(project, "s" + _nextId++, agent is null ? SessionKind.Terminal : SessionKind.Agent, title)
        {
            AgentId = agent?.Id,
            Model = parts.ElementAtOrDefault(0),
            Effort = parts.ElementAtOrDefault(1),
            IsGui = gui,
            WorkspaceId = workspace?.Id,
            WorkspaceBranch = workspace?.DisplayName,
            WorkspaceTaskKey = workspace?.TaskKey,
            Detail = detail,
            Activity = activity,
            Preview = preview,
            Columns = 132,
            Rows = 40,
            SizeOwner = project.Host.Name,
        };
        project.Sessions.Add(session);
        return session;
    }

    // The made-up agent's permission question: 1 and 2 let it run, 3 and Esc stop it.
    private static string? AnswerPermission(RemoteSession session, string input)
    {
        if (session.Activity != SessionActivity.Waiting) return Echo(input);
        switch (input)
        {
            case "1" or "2" or "\r":
                Dispatcher.UIThread.Post(() =>
                {
                    session.Activity = SessionActivity.Working;
                    session.Preview = "运行 dotnet test";
                });
                return "\r\n\r\n\u001b[35m●\u001b[0m 正在运行 dotnet test Octopus.Remote.Tests…\r\n";
            case "3" or "\u001b":
                Dispatcher.UIThread.Post(() =>
                {
                    session.Activity = SessionActivity.Idle;
                    session.Preview = null;
                });
                return "\r\n\r\n\u001b[90m没有运行。告诉我接下来怎么做。\u001b[0m\r\n\r\n> ";
            default:
                return null;
        }
    }

    // What a line editor would show: typed text as it is, Enter as a new line, Backspace erasing one cell.
    private static string? Echo(string input) => input switch
    {
        "\r" => "\r\n> ",
        "\u007f" => "\b \b",
        _ when input.Length > 0 && input[0] == '\u001b' => null,
        _ => input.Replace("\r", "\r\n> "),
    };

    private static string? Shell(string folder, string input) => input switch
    {
        "\r" => "\r\n" + DemoScreens.Prompt(folder),
        "\u0003" => "^C\r\n" + DemoScreens.Prompt(folder),
        "\u007f" => "\b \b",
        _ when input.Length > 0 && input[0] == '\u001b' => null,
        _ => input.Replace("\r", "\r\n" + DemoScreens.Prompt(folder)),
    };

    private static string FirstLine(string text, int max)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length <= max ? line : line[..max];
    }
}

/// <summary>
/// An account that is signed in from the start; signing in again takes a moment and always works. It has an official
/// service unless told otherwise, and can be used without an account like the real one.
/// </summary>
public sealed class DemoAccount : IAccountSession
{
    /// <summary>How long the made-up browser confirmation takes.</summary>
    public TimeSpan SignInDelay { get; init; } = TimeSpan.FromSeconds(3);

    public bool IsSignedIn { get; private set; } = true;
    public bool IsLocalOnly => !IsSignedIn && _localOnly;
    public string? Name => IsSignedIn ? "Demo" : null;
    public string? OfficialServer { get; init; } = "https://octoterm.si";
    public string Server { get; private set; } = "";
    public bool SignInEnded => false;
    public event EventHandler? Changed;

    private bool _localOnly;

    public Task<SignInChallenge> BeginSignInAsync(string server, CancellationToken cancellationToken = default)
    {
        if (RelayAccountClient.ParseUrl(server) is not { } url) throw new IOException("That is not an address.");
        // Anything but the official service is one of the person's own, remembered for next time.
        if (url != RelayAccountClient.ParseUrl(OfficialServer)) Server = server.Trim();
        var cancel = new CancellationTokenSource();
        return Task.FromResult(new SignInChallenge("WDJB-MJHT", new Uri(url, "device?code=WDJB-MJHT"), ConfirmAsync(cancel.Token), cancel.Cancel));
    }

    public void SignOut()
    {
        if (!IsSignedIn) return;
        IsSignedIn = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void UseLocalOnly() => SetLocalOnly(true);

    public void LeaveLocalOnly() => SetLocalOnly(false);

    private void SetLocalOnly(bool value)
    {
        if (_localOnly == value) return;
        _localOnly = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<SignInOutcome> ConfirmAsync(CancellationToken cancellationToken)
    {
        try { await Task.Delay(SignInDelay, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return SignInOutcome.Cancelled; }
        IsSignedIn = true;
        Changed?.Invoke(this, EventArgs.Empty);
        return SignInOutcome.SignedIn;
    }
}
