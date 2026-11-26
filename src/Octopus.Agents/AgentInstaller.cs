using System.Collections.Concurrent;
using Octopus.Core.Agents;
using Octopus.Platform;

namespace Octopus.Agents;

/// <summary>Whether running an install command is expected to need administrator rights (sudo, UAC).</summary>
public enum InstallElevation
{
    /// <summary>Installs into the user's own folders (brew, uv, pipx, cargo, go, a user-owned npm prefix).</summary>
    None,
    /// <summary>Might ask: an install script may call sudo itself, winget may show a UAC prompt.</summary>
    Possible,
    /// <summary>Will fail without it: the command uses sudo, or npm's global folder is not writable by the user.</summary>
    Required,
}

/// <summary>
/// How Octopus would install one tool: the hint it picked, and the shell command line that runs it.
/// <see cref="Tool"/> is the program the hint needs (npm, uv, brew, curl, …), already found on this machine.
/// </summary>
public sealed record AgentInstallPlan(string AgentId, AgentInstallHint Hint, string Tool, string Shell, IReadOnlyList<string> ShellArguments)
{
    public string CommandLine => Hint.Command;

    public InstallElevation Elevation { get; init; }

    /// <summary>The resolved path of <see cref="Tool"/>; sudo resets PATH, so the elevated command names it in full.</summary>
    public string? ToolPath { get; init; }

    /// <summary>A <c>curl … | bash</c> or <c>irm … | iex</c> installer, rather than a package manager command.</summary>
    public bool IsScript { get; init; }
}

/// <summary>
/// Outcome of running an <see cref="AgentInstallPlan"/>; <see cref="Output"/> is stdout then stderr.
/// <see cref="NeedsAdmin"/>: it failed because of permissions, so the elevated route may succeed.
/// </summary>
public sealed record AgentInstallResult(bool Succeeded, string Output, bool NeedsAdmin = false);

/// <summary>
/// Runs install hints (<see cref="AgentDetect.InstallHints"/>, or a language server's recipes) for the user. A plain
/// install runs in the background; an install that needs administrator rights goes through the system prompt (UAC on
/// Windows) or through a terminal the user can type the sudo password into (<see cref="BuildTerminalCommand"/>).
/// </summary>
public interface IAgentInstaller
{
    /// <summary>
    /// The first install hint of <paramref name="definition"/> that fits this OS and whose tool is on the login PATH
    /// (or a usual install directory); null when none does (for example only a download page is known).
    /// </summary>
    Task<AgentInstallPlan?> PlanAsync(AgentDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>Like the other overload, for any list of hints (language servers use it).</summary>
    Task<AgentInstallPlan?> PlanAsync(string id, IReadOnlyList<AgentInstallHint> hints, CancellationToken cancellationToken = default);

    /// <summary>Runs the plan in the background, without elevation. Never throws for a failing installer.</summary>
    Task<AgentInstallResult> InstallAsync(AgentInstallPlan plan, CancellationToken cancellationToken = default);

    /// <summary>True where <see cref="InstallElevatedAsync"/> works (Windows: UAC). Elsewhere use a terminal and sudo.</summary>
    bool CanElevateInBackground { get; }

    /// <summary>Windows: runs the plan as administrator, after the UAC prompt. The output is not captured.</summary>
    Task<AgentInstallResult> InstallElevatedAsync(AgentInstallPlan plan, CancellationToken cancellationToken = default);

    /// <summary>
    /// The command to run in an interactive terminal: as is, or with <c>sudo</c> in front when <paramref name="elevated"/>
    /// (an install script and brew are never prefixed: a script asks for sudo itself, brew refuses to run as root).
    /// </summary>
    AgentCommand BuildTerminalCommand(AgentInstallPlan plan, bool elevated);
}

/// <summary>
/// Production <see cref="IAgentInstaller"/>: hints are shell one-liners (<c>npm i -g …</c>, <c>curl … | bash</c>), so
/// they run through <c>bash -c</c> on macOS/Linux and through <c>cmd</c> or PowerShell on Windows, with stdin closed
/// so that an installer that asks a question (or sudo asking for a password) fails at once instead of hanging.
/// </summary>
public sealed class AgentInstaller : IAgentInstaller
{
    /// <summary>Upper bound for one install (downloads can be slow).</summary>
    public static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Output fragments of a failure caused by missing rights, from npm, pip, sudo, macOS and Windows.</summary>
    private static readonly string[] PermissionMarkers =
    [
        "EACCES",
        "EPERM",
        "Permission denied",
        "permission denied",
        "Operation not permitted",
        "a terminal is required",
        "a password is required",
        "no tty present",
        "Access is denied",
        "requires elevation",
        "Run as administrator",
        "administrator privileges",
        "elevated privileges",
    ];

    private readonly IProcessRunner _runner;
    private readonly IExecutableLocator _locator;
    private readonly ILoginEnvironment _loginEnvironment;
    private readonly Func<IReadOnlyList<string>> _searchDirectories;
    private readonly bool _isWindows;
    private readonly Func<string, CancellationToken, Task<bool>> _isWritable;
    /// <summary>Whether npm's global folder is writable, per npm executable; asked once.</summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<bool>>> _npmWritable = new(StringComparer.Ordinal);

    public AgentInstaller(IProcessRunner runner, IExecutableLocator locator, ILoginEnvironment loginEnvironment)
        : this(runner, locator, loginEnvironment, AgentSearchPaths.ForThisMachine, OperatingSystem.IsWindows(), isWritable: null)
    {
    }

    internal AgentInstaller(
        IProcessRunner runner,
        IExecutableLocator locator,
        ILoginEnvironment loginEnvironment,
        Func<IReadOnlyList<string>> searchDirectories,
        bool isWindows,
        Func<string, CancellationToken, Task<bool>>? isWritable = null)
    {
        _runner = runner;
        _locator = locator;
        _loginEnvironment = loginEnvironment;
        _searchDirectories = searchDirectories;
        _isWindows = isWindows;
        _isWritable = isWritable ?? IsWritableAsync;
    }

    public bool CanElevateInBackground => _isWindows;

    public Task<AgentInstallPlan?> PlanAsync(AgentDefinition definition, CancellationToken cancellationToken = default) =>
        PlanAsync(definition.Id, definition.Detect.InstallHints, cancellationToken);

    public async Task<AgentInstallPlan?> PlanAsync(string id, IReadOnlyList<AgentInstallHint> hints, CancellationToken cancellationToken = default)
    {
        if (hints.Count == 0) return null;
        var path = await SearchPathAsync(cancellationToken).ConfigureAwait(false);
        foreach (var hint in hints)
        {
            if (ShellFor(hint) is not { } shell) continue;
            var words = Words(hint.Command);
            var usesSudo = words.Count > 0 && words[0] == "sudo";
            if (usesSudo) words = words.Skip(1).ToList();
            // irm/iex are PowerShell cmdlets, not files: PowerShell itself is the tool then.
            var tool = IsPowerShell(hint.Command) ? shell.Program : words.FirstOrDefault() ?? "";
            if (tool.Length == 0) continue;
            var toolPath = _locator.FindAll(tool, path).FirstOrDefault();
            if (toolPath is null) continue;
            if (_locator.FindAll(shell.Program, path).FirstOrDefault() is not { } shellPath) continue;

            var isScript = IsPowerShell(hint.Command) || PipesToUnixShell(hint.Command);
            var elevation = usesSudo ? InstallElevation.Required
                : isScript ? InstallElevation.Possible
                : tool.Equals("winget", StringComparison.OrdinalIgnoreCase) ? InstallElevation.Possible
                : InstallElevation.None;
            if (elevation == InstallElevation.None && IsNpmGlobalInstall(words)
                && !await NpmGlobalWritableAsync(toolPath, path, cancellationToken).ConfigureAwait(false))
            {
                elevation = InstallElevation.Required;
            }
            return new AgentInstallPlan(id, hint, tool, shellPath, [.. shell.Arguments, hint.Command])
            {
                Elevation = elevation,
                IsScript = isScript,
                ToolPath = toolPath,
            };
        }
        return null;
    }

    public async Task<AgentInstallResult> InstallAsync(AgentInstallPlan plan, CancellationToken cancellationToken = default)
    {
        var env = await InstallEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = await _runner.RunAsync(
            plan.Shell,
            plan.ShellArguments,
            env,
            Directory.Exists(home) ? home : null,
            InstallTimeout,
            cancellationToken).ConfigureAwait(false);
        var output = string.Join('\n', new[] { result.StandardOutput, result.StandardError }
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim()));
        if (result.TimedOut && output.Length == 0) output = "timed out";
        return new AgentInstallResult(result.Succeeded, output, !result.Succeeded && LooksLikePermissionError(output));
    }

    public async Task<AgentInstallResult> InstallElevatedAsync(AgentInstallPlan plan, CancellationToken cancellationToken = default)
    {
        if (!_isWindows) return new AgentInstallResult(false, "elevation needs a terminal on this system", NeedsAdmin: true);
        var path = await SearchPathAsync(cancellationToken).ConfigureAwait(false);
        var powerShell = _locator.FindAll("powershell", path).FirstOrDefault() ?? "powershell";
        var result = await _runner.RunAsync(
            powerShell,
            ["-NoProfile", "-NonInteractive", "-Command", ElevatedScript(plan)],
            await InstallEnvironmentAsync(cancellationToken).ConfigureAwait(false),
            null,
            InstallTimeout,
            cancellationToken).ConfigureAwait(false);
        var output = string.Join('\n', new[] { result.StandardOutput, result.StandardError }
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim()));
        return new AgentInstallResult(result.Succeeded, output);
    }

    /// <summary>
    /// PowerShell that starts the plan's shell as administrator (the UAC prompt), waits, and exits with its code. A
    /// declined prompt makes Start-Process throw, which exits non-zero with "canceled by the user".
    /// </summary>
    internal static string ElevatedScript(AgentInstallPlan plan)
    {
        static string Single(string s) => "'" + s.Replace("'", "''") + "'";
        var arguments = string.Join(' ', plan.ShellArguments.Take(plan.ShellArguments.Count - 1))
            + " \"" + plan.CommandLine.Replace("\"", "\\\"") + "\"";
        return $"$ErrorActionPreference = 'Stop'; $p = Start-Process -FilePath {Single(plan.Shell)} -ArgumentList {Single(arguments)} -Verb RunAs -Wait -PassThru; exit $p.ExitCode";
    }

    public AgentCommand BuildTerminalCommand(AgentInstallPlan plan, bool elevated)
    {
        var command = plan.CommandLine.Trim();
        if (elevated && !_isWindows && !plan.IsScript
            && !command.StartsWith("sudo ", StringComparison.Ordinal)
            && !plan.Tool.Equals("brew", StringComparison.Ordinal))
        {
            // sudo replaces PATH with its secure_path, so the tool is named by its full path.
            var words = Words(command);
            if (plan.ToolPath is { } toolPath && words.Count > 0 && words[0] == plan.Tool) words[0] = ShellQuote(toolPath);
            command = "sudo " + string.Join(' ', words);
        }
        var env = new Dictionary<string, string>(QuietEnvironment);
        return new AgentCommand(plan.Shell, [.. plan.ShellArguments.Take(plan.ShellArguments.Count - 1), command], env);
    }

    private static string ShellQuote(string value) =>
        value.All(c => char.IsLetterOrDigit(c) || "-_./+@:".Contains(c)) ? value : "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>True when installer output says the failure was about missing rights.</summary>
    public static bool LooksLikePermissionError(string output) =>
        PermissionMarkers.Any(m => output.Contains(m, StringComparison.Ordinal));

    private static readonly IReadOnlyDictionary<string, string> QuietEnvironment = new Dictionary<string, string>
    {
        ["HOMEBREW_NO_AUTO_UPDATE"] = "1",
        ["HOMEBREW_NO_INSTALL_CLEANUP"] = "1",
        ["NPM_CONFIG_FUND"] = "false",
        ["NPM_CONFIG_AUDIT"] = "false",
    };

    private async Task<Dictionary<string, string>> InstallEnvironmentAsync(CancellationToken cancellationToken)
    {
        var login = await _loginEnvironment.GetAsync(cancellationToken).ConfigureAwait(false);
        var env = new Dictionary<string, string>(login, _isWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var pathKey = env.Keys.FirstOrDefault(k => string.Equals(k, "PATH", StringComparison.OrdinalIgnoreCase)) ?? "PATH";
        if (await SearchPathAsync(cancellationToken).ConfigureAwait(false) is { Length: > 0 } path) env[pathKey] = path;
        foreach (var (k, v) in QuietEnvironment) env[k] = v;
        env["CI"] = "1";
        return env;
    }

    private static bool IsNpmGlobalInstall(IReadOnlyList<string> words) =>
        words.Count >= 2
        && words[0] is "npm" or "npm.cmd"
        && words[1] is "i" or "install" or "add"
        && words.Any(w => w is "-g" or "--global");

    private Task<bool> NpmGlobalWritableAsync(string npm, string? path, CancellationToken cancellationToken) =>
        _npmWritable.GetOrAdd(npm, _ => new Lazy<Task<bool>>(() => ProbeNpmWritableAsync(npm, path))).Value.WaitAsync(cancellationToken);

    /// <summary>Asks npm for its global prefix and checks that the folder packages go to can be written.</summary>
    private async Task<bool> ProbeNpmWritableAsync(string npm, string? path)
    {
        var env = new Dictionary<string, string>();
        if (path is not null) env["PATH"] = path;
        var result = await _runner.RunAsync(npm, ["prefix", "-g"], env, null, ProbeTimeout).ConfigureAwait(false);
        var prefix = result.StandardOutput.Trim().Split('\n').LastOrDefault()?.Trim();
        // Unknown: assume writable; a permission failure is still caught from the output and offered elevated.
        if (!result.Succeeded || string.IsNullOrEmpty(prefix)) return true;
        var target = _isWindows
            ? Path.Combine(prefix, "node_modules")
            : Path.Combine(prefix, "lib", "node_modules");
        // The folder may not exist yet: the closest existing parent decides.
        var probe = target;
        while (!Directory.Exists(probe))
        {
            var parent = Path.GetDirectoryName(probe);
            if (string.IsNullOrEmpty(parent) || parent == probe) return true;
            probe = parent;
        }
        return await _isWritable(probe, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<bool> IsWritableAsync(string directory, CancellationToken cancellationToken)
    {
        if (!_isWindows)
        {
            // access(2) via test -w: right for ACLs and group rights, and creates nothing.
            var test = await _runner.RunAsync("/bin/sh", ["-c", "test -w \"$1\"", "sh", directory], null, null, ProbeTimeout, cancellationToken)
                .ConfigureAwait(false);
            return test.Succeeded;
        }
        try
        {
            var file = Path.Combine(directory, ".octopus-write-test-" + Guid.NewGuid().ToString("N"));
            using (File.Create(file, 1, FileOptions.DeleteOnClose))
            {
            }
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// The shell that can run <paramref name="hint"/> here, or null: a download page (<c>native</c>) is not a command,
    /// <c>curl … | bash</c> needs a Unix shell, and <c>irm … | iex</c> needs Windows PowerShell.
    /// </summary>
    internal (string Program, IReadOnlyList<string> Arguments)? ShellFor(AgentInstallHint hint)
    {
        var command = hint.Command.Trim();
        if (command.Length == 0 || command.Contains('\n') || command.Contains('\r')) return null;
        if (command.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var powerShell = IsPowerShell(command);
        if (_isWindows)
        {
            if (powerShell) return ("powershell", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command"]);
            if (PipesToUnixShell(command) || command.StartsWith("sudo ", StringComparison.Ordinal)) return null;
            return ("cmd", ["/d", "/s", "/c"]);
        }
        if (powerShell) return null;
        return ("bash", ["-c"]);
    }

    private static bool IsPowerShell(string command)
    {
        var first = FirstWord(command);
        return first.Equals("irm", StringComparison.OrdinalIgnoreCase)
            || first.Equals("iwr", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Invoke-RestMethod", StringComparison.OrdinalIgnoreCase)
            || command.Contains("| iex", StringComparison.OrdinalIgnoreCase);
    }

    private static bool PipesToUnixShell(string command) =>
        command.Contains("| bash", StringComparison.Ordinal)
        || command.Contains("| sh", StringComparison.Ordinal)
        || command.Contains("|bash", StringComparison.Ordinal)
        || command.Contains("|sh", StringComparison.Ordinal);

    internal static string FirstWord(string command) => Words(command).FirstOrDefault() ?? "";

    private static List<string> Words(string command) =>
        [.. command.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)];

    private async Task<string?> SearchPathAsync(CancellationToken cancellationToken)
    {
        var env = await _loginEnvironment.GetAsync(cancellationToken).ConfigureAwait(false);
        string? path = null;
        foreach (var (k, v) in env)
        {
            if (string.Equals(k, "PATH", StringComparison.OrdinalIgnoreCase)) path = v;
        }
        path ??= Environment.GetEnvironmentVariable("PATH");
        var comparer = _isWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var entries = (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        var known = new HashSet<string>(entries.Select(e => e.TrimEnd('/', '\\')), comparer);
        IReadOnlyList<string> extra;
        try
        {
            extra = _searchDirectories();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            extra = [];
        }
        foreach (var directory in extra)
        {
            if (known.Add(directory.TrimEnd('/', '\\'))) entries.Add(directory);
        }
        return entries.Count == 0 ? null : string.Join(Path.PathSeparator, entries);
    }
}
