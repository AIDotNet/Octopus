using System.Diagnostics;
using Octopus.Platform;

namespace Octopus.Plugins.Runtime;

/// <summary>What a user command printed.</summary>
/// <param name="StandardError">The end of the error output (what explains a failure), at most 4,000 characters.</param>
public sealed record UserCommandResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut, long ElapsedMilliseconds)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;
}

/// <summary>
/// Runs a command line the user typed into a <c>command</c> setting (see <see cref="Octopus.Core.Plugins.ConfigurationType.Command"/>):
/// through the user's own shell, with the login environment (minus Octopus's variables), in the home directory, for at
/// most 30 seconds. Pipes, <c>curl</c>, <c>jq</c> and scripts work as they do in a terminal. Never throws for a failing
/// command.
/// </summary>
public sealed class UserCommandRunner(IProcessRunner processes, IShellResolver shells, ILoginEnvironment login)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    public const int MaxOutputCharacters = 1_000_000;
    public const int MaxErrorCharacters = 4_000;

    public async Task<UserCommandResult> RunAsync(string commandLine, CancellationToken cancellationToken = default)
    {
        var shell = shells.GetDefaultShell();
        // Not GetCommandArguments: that starts an interactive shell, whose rc files may print into the output that
        // must be JSON. The login environment (PATH included) is passed in instead.
        IReadOnlyList<string> arguments = shell.Kind switch
        {
            ShellKind.PowerShell => ["-NoProfile", "-NonInteractive", "-Command", commandLine],
            ShellKind.Cmd => ["/d", "/s", "/c", commandLine],
            _ => ["-c", commandLine],
        };
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in await login.GetAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!key.StartsWith("OCTOPUS_", StringComparison.Ordinal)) environment[key] = value;
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var clock = Stopwatch.StartNew();
        var result = await processes.RunAsync(shell.Path, arguments, environment, home, Timeout, cancellationToken).ConfigureAwait(false);
        var output = result.StandardOutput.Length > MaxOutputCharacters ? result.StandardOutput[..MaxOutputCharacters] : result.StandardOutput;
        var error = result.StandardError.Length > MaxErrorCharacters ? result.StandardError[^MaxErrorCharacters..] : result.StandardError;
        return new UserCommandResult(result.ExitCode, output, error, result.TimedOut, clock.ElapsedMilliseconds);
    }
}
