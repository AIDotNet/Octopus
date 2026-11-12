namespace Octopus.Platform;

/// <summary>
/// Decides which inherited variables must not leak into a terminal's child process: they describe the host app or
/// another terminal and would mislead the programs inside (or leak IPC tokens).
/// </summary>
public static class HostEnvironmentFilter
{
    private static readonly string[] Prefixes =
    [
        "TKCODE_", "OCTOPUS_", "TMUX", "ITERM_", "KITTY_", "GHOSTTY_", "VSCODE_", "ELECTRON_", "XPC_", "COR_",
    ];

    private static readonly HashSet<string> Names = new(StringComparer.Ordinal)
    {
        "TERMCAP", "WT_SESSION", "SHLVL", "__CFBundleIdentifier",
        "DOTNET_LAUNCH_PROFILE", "DOTNET_STARTUP_HOOKS",
    };

    public static bool ShouldRemove(string name)
    {
        if (Names.Contains(name)) return true;
        foreach (var prefix in Prefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Snapshot of the current process environment.</summary>
    public static Dictionary<string, string> SnapshotProcessEnvironment(StringComparer comparer)
    {
        var result = new Dictionary<string, string>(comparer);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key)
                result[key] = entry.Value as string ?? "";
        }
        return result;
    }

    /// <summary>The version reported in TERM_PROGRAM_VERSION.</summary>
    public static string DefaultAppVersion() =>
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0";
}
