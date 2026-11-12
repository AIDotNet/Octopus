using System.Text;

namespace Octopus.Platform.Unix;

/// <summary>
/// Asks the user's login shell for its environment (<c>env -0</c>) so that a Dock-launched app sees Homebrew, nvm
/// and friends. A random marker is printed first because rc files may write banners to stdout.
/// </summary>
public sealed class UnixLoginEnvironment : ILoginEnvironment
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly IShellResolver _shells;
    private readonly IProcessRunner _runner;
    private readonly Lazy<Task<IReadOnlyDictionary<string, string>>> _cached;

    public UnixLoginEnvironment(IShellResolver shells, IProcessRunner runner)
    {
        _shells = shells;
        _runner = runner;
        _cached = new Lazy<Task<IReadOnlyDictionary<string, string>>>(LoadAsync);
    }

    public Task<IReadOnlyDictionary<string, string>> GetAsync(CancellationToken cancellationToken = default) =>
        _cached.Value.WaitAsync(cancellationToken);

    private async Task<IReadOnlyDictionary<string, string>> LoadAsync()
    {
        try
        {
            var shell = _shells.GetDefaultShell();
            var marker = "__OCTOPUS_ENV_" + Guid.NewGuid().ToString("N") + "__";
            // printf and env are external programs, so the same line is valid in sh, fish and pwsh.
            var command = $"printf '%s' '{marker}'; env -0";
            var result = await _runner.RunAsync(
                shell.Path, _shells.GetCommandArguments(shell, command), timeout: Timeout).ConfigureAwait(false);
            var parsed = result.TimedOut ? null : Parse(result.StandardOutput, marker);
            if (parsed is { Count: > 0 } && parsed.ContainsKey("PATH"))
                return parsed;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Fall through to the process environment: a broken rc file must not break the app.
        }
        return HostEnvironmentFilter.SnapshotProcessEnvironment(StringComparer.Ordinal);
    }

    /// <summary>Parses the NUL separated block that follows <paramref name="marker"/>; null when the marker is missing.</summary>
    public static Dictionary<string, string>? Parse(string output, string marker)
    {
        var start = output.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in output.AsSpan(start + marker.Length).ToString().Split('\0'))
        {
            var eq = entry.IndexOf('=');
            if (eq > 0)
                result[entry[..eq]] = entry[(eq + 1)..];
        }
        return result;
    }
}
