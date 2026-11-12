using Octopus.Platform.Unix;

namespace Octopus.Platform.Linux;

/// <summary>$SHELL, then the passwd entry, then bash, then sh.</summary>
public sealed class LinuxShellResolver(IProcessRunner runner) : UnixShellResolverBase
{
    protected override string LastResortPath => "/bin/sh";

    protected override IEnumerable<string?> EnumerateCandidates()
    {
        yield return ShellFromEnvironment();
        yield return ReadUserShellFromPasswd();
        yield return "/bin/bash";
    }

    private string? ReadUserShellFromPasswd()
    {
        // Blocking is acceptable: the result is cached by the base class.
        var result = runner.RunAsync("getent", ["passwd", Environment.UserName],
            timeout: TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        return result.Succeeded ? ParsePasswdLine(result.StandardOutput) : null;
    }

    /// <summary>The 7th field of <c>name:x:uid:gid:gecos:home:shell</c>.</summary>
    public static string? ParsePasswdLine(string line)
    {
        var fields = line.Trim().Split(':');
        return fields.Length >= 7 && fields[6].Length > 0 ? fields[6] : null;
    }
}
