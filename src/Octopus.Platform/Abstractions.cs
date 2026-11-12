namespace Octopus.Platform;

/// <summary>Where Octopus keeps its files on this platform.</summary>
public interface IAppPaths
{
    /// <summary>projects.json, settings.json, agents/. Created on first use.</summary>
    string DataDirectory { get; }
    /// <summary>Files that can be deleted at any time (agent-scan.json).</summary>
    string CacheDirectory { get; }
}

/// <summary>The dialect a command line must be quoted for. Chosen by shell, not by operating system.</summary>
public enum ShellKind
{
    /// <summary>sh, bash, zsh, dash, ksh.</summary>
    Posix,
    Fish,
    PowerShell,
    Cmd
}

/// <param name="Path">Absolute path of the shell executable.</param>
/// <param name="Name">File name without extension, lower case: <c>zsh</c>, <c>pwsh</c>.</param>
public sealed record ShellInfo(string Path, string Name, ShellKind Kind);

/// <summary>Finds the user's shell and knows how to start it.</summary>
public interface IShellResolver
{
    /// <summary>The user's default shell. Never throws: falls back to a shell that always exists on the platform.</summary>
    ShellInfo GetDefaultShell();

    /// <summary>Describes a shell the user chose in settings; null when the file does not exist.</summary>
    ShellInfo? Describe(string path);

    /// <summary>Arguments that start <paramref name="shell"/> as an interactive (and, where it applies, login) shell.</summary>
    IReadOnlyList<string> GetInteractiveArguments(ShellInfo shell);

    /// <summary>
    /// Arguments that make <paramref name="shell"/> load the user's full environment (login + interactive where it
    /// applies), run <paramref name="commandLine"/> and exit with its exit code.
    /// </summary>
    IReadOnlyList<string> GetCommandArguments(ShellInfo shell, string commandLine);
}

/// <summary>Turns an argument array into a command line that a shell runs without reinterpreting any argument.</summary>
public interface IShellCommandBuilder
{
    string Quote(string argument, ShellKind kind);

    /// <summary>The executable and its arguments, each quoted, joined into one command line.</summary>
    string Build(string executable, IEnumerable<string> arguments, ShellKind kind);

    /// <summary>Splits a user-typed command such as <c>rec --profile work</c> into arguments (POSIX-like quoting rules).</summary>
    IReadOnlyList<string> Split(string commandLine);
}

/// <summary>
/// The environment the user gets in their own terminal. A GUI app started from the Dock or a launcher has a short PATH;
/// this asks the user's login shell once and caches the answer.
/// </summary>
public interface ILoginEnvironment
{
    /// <summary>The login environment; the process environment when it cannot be read. Cached after the first call.</summary>
    Task<IReadOnlyDictionary<string, string>> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>Builds the environment of a terminal's child process.</summary>
public interface ITerminalEnvironment
{
    /// <summary>
    /// The process environment at start-up, minus variables that belong to the host or to another terminal, plus
    /// TERM, COLORTERM, TERM_PROGRAM and a UTF-8 LANG when missing, plus <paramref name="extra"/> (which wins).
    /// </summary>
    IReadOnlyDictionary<string, string> Build(IReadOnlyDictionary<string, string>? extra = null);
}

/// <summary>Finds executables on a PATH.</summary>
public interface IExecutableLocator
{
    /// <summary>
    /// Every file named <paramref name="name"/> that can be executed, in PATH order, without duplicates. All of them,
    /// not only the first: the first one may be a broken link. <paramref name="name"/> that contains a directory
    /// separator is checked as a path.
    /// </summary>
    IReadOnlyList<string> FindAll(string name, string? pathVariable);
}

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;
}

/// <summary>Runs a short command and collects its output. Never throws for a failing or missing program.</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Hands a file or folder to the operating system.</summary>
public interface IFileOpener
{
    /// <summary>Opens with the default application.</summary>
    void Open(string path);
    /// <summary>Shows the file or folder in Finder, Explorer or the file manager.</summary>
    void Reveal(string path);
    /// <summary>
    /// Opens an http or https address in the default browser. Anything else (file:, javascript:, custom schemes)
    /// is refused and false is returned.
    /// </summary>
    bool OpenUrl(string url);
}
