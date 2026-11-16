using System.Collections.ObjectModel;
using Avalonia.Threading;
using Octopus.Mobile.Workspace;
using Octopus.Terminal;

namespace Octopus.Mobile.Demo;

/// <summary>
/// Hosts, projects and sessions made up for the preview and the tests: three paired computers, one that is not paired
/// yet, and six sessions of which one waits for the user. Nothing here talks to a network.
/// </summary>
public sealed class DemoWorkspace : IRemoteWorkspace
{
    private static readonly AgentOption Claude = new("claude", "Claude", "C", "#D97757", ["Fable 5.2", "Opus 5.5", "Sonnet 5.5"], ["低", "中", "高", "极高"]);
    private static readonly AgentOption Codex = new("codex", "Codex", "X", "#1F9D74", ["GPT-6", "GPT-6 mini"], ["低", "中", "高"]);

    private readonly Dictionary<string, Func<DemoTerminalConnection>> _screens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DemoTerminalFactory> _factories = new(StringComparer.Ordinal);
    private int _nextId = 100;

    /// <summary>How long the made-up host takes to allow a pairing request.</summary>
    public TimeSpan PairingDelay { get; init; } = TimeSpan.FromSeconds(3);

    public DemoWorkspace()
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
        _screens[migrate.Id] = () => new DemoTerminalConnection(
            DemoScreens.Lines("\u001b[35m●\u001b[0m 迁移已经生成并应用，测试全部通过。", "", "> "), Echo);

        var mini = new RemoteHost("mini", "Mac mini", HostLink.Offline, isPaired: true)
        {
            LastSeen = DateTimeOffset.Now.AddHours(-11),
        };
        var linux = new RemoteHost("linux", "Linux 服务器", HostLink.Relay, isPaired: false);

        Hosts = [mac, station, mini, linux];
    }

    public ObservableCollection<RemoteHost> Hosts { get; }

    public Task<RemoteSession> CreateSessionAsync(RemoteProject project, NewSessionRequest request, CancellationToken cancellationToken = default)
    {
        var agent = request.AgentId is null ? null : project.Host.Agents.FirstOrDefault(a => a.Id == request.AgentId);
        var prompt = string.IsNullOrWhiteSpace(request.Prompt) ? null : request.Prompt.Trim();
        var title = agent is null ? "zsh" : prompt is null ? agent.Name : FirstLine(prompt, 40);
        var detail = agent is null
            ? "zsh"
            : string.Join(" · ", new[] { request.Model ?? agent.Models.FirstOrDefault(), request.Effort }.Where(part => !string.IsNullOrEmpty(part)));
        var session = Add(project, title, agent, detail, agent is not null && prompt is not null ? SessionActivity.Working : SessionActivity.Idle, prompt is null ? null : "正在读代码");
        var folder = project.Name;
        _screens[session.Id] = agent is null
            ? () => new DemoTerminalConnection(DemoScreens.Prompt(folder), input => Shell(folder, input))
            : () => new DemoTerminalConnection(
                prompt is null ? "> " : DemoScreens.Working(folder, prompt, "正在读代码"), Echo);
        return Task.FromResult(session);
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

    private static RemoteProject Add(RemoteHost host, string name, string path)
    {
        var project = new RemoteProject(host, host.Id + "/" + name, name, path);
        host.Projects.Add(project);
        return project;
    }

    private RemoteSession Add(RemoteProject project, string title, AgentOption? agent, string detail, SessionActivity activity, string? preview)
    {
        var session = new RemoteSession(project, "s" + _nextId++, agent is null ? SessionKind.Terminal : SessionKind.Agent, title)
        {
            AgentId = agent?.Id,
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

/// <summary>An account that is signed in from the start; signing in again takes a moment and always works.</summary>
public sealed class DemoAccount : IAccountSession
{
    /// <summary>How long the made-up browser confirmation takes.</summary>
    public TimeSpan SignInDelay { get; init; } = TimeSpan.FromSeconds(3);

    public bool IsSignedIn { get; private set; } = true;
    public string? Name => IsSignedIn ? "Demo" : null;
    public string Server => "octopus.example";
    public bool SignInEnded => false;
    public event EventHandler? Changed;

    public Task<SignInChallenge> BeginSignInAsync(string server, CancellationToken cancellationToken = default)
    {
        var cancel = new CancellationTokenSource();
        return Task.FromResult(new SignInChallenge("WDJB-MJHT", new Uri("https://octopus.example/device?code=WDJB-MJHT"), ConfirmAsync(cancel.Token), cancel.Cancel));
    }

    public void SignOut()
    {
        if (!IsSignedIn) return;
        IsSignedIn = false;
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
