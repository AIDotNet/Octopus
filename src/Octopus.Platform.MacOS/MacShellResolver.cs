using Octopus.Platform.Unix;

namespace Octopus.Platform.MacOS;

/// <summary>
/// $SHELL first (a Dock launch usually still has it), then the account record from Directory Services, then zsh.
/// </summary>
public sealed class MacShellResolver(IProcessRunner runner) : UnixShellResolverBase
{
    protected override string LastResortPath => "/bin/zsh";

    protected override IEnumerable<string?> EnumerateCandidates()
    {
        yield return ShellFromEnvironment();
        yield return ReadUserShellFromDscl();
    }

    private string? ReadUserShellFromDscl()
    {
        var user = Environment.UserName;
        // Blocking is acceptable: the result is cached by the base class and the call takes ~20 ms.
        var result = runner.RunAsync("/usr/bin/dscl", [".", "-read", "/Users/" + user, "UserShell"],
            timeout: TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        return result.Succeeded ? ParseDsclOutput(result.StandardOutput) : null;
    }

    /// <summary>Parses <c>UserShell: /bin/zsh</c>.</summary>
    public static string? ParseDsclOutput(string output)
    {
        const string key = "UserShell:";
        var index = output.IndexOf(key, StringComparison.Ordinal);
        if (index < 0) return null;
        var value = output[(index + key.Length)..].Split('\n')[0].Trim();
        return value.Length == 0 ? null : value;
    }
}
