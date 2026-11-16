namespace Octopus.Platform.Windows;

/// <summary>
/// Same recipe as on Unix minus LANG (Windows has no locale variable convention) and minus TERM: ConPTY programs
/// detect the terminal by capability, and a stray TERM confuses native Windows tools. Names are case-insensitive.
/// </summary>
public sealed class WindowsTerminalEnvironment : ITerminalEnvironment
{
    private readonly IReadOnlyDictionary<string, string>? _snapshot;
    private readonly string _appVersion;

    public WindowsTerminalEnvironment(IReadOnlyDictionary<string, string>? snapshot = null, string? appVersion = null)
    {
        _snapshot = snapshot;
        _appVersion = appVersion ?? HostEnvironmentFilter.DefaultAppVersion();
    }

    public IReadOnlyDictionary<string, string> Build(IReadOnlyDictionary<string, string>? extra = null)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in _snapshot ?? HostEnvironmentFilter.SnapshotProcessEnvironment(StringComparer.OrdinalIgnoreCase))
        {
            if (!HostEnvironmentFilter.ShouldRemove(key))
                env[key] = value;
        }
        // Match discovery's fresh PATH, including installers run after Explorer or Octopus started.
        if (_snapshot is null)
            env["PATH"] = WindowsLoginEnvironment.MergePath(env.GetValueOrDefault("PATH"));
        env["COLORTERM"] = "truecolor";
        env["TERM_PROGRAM"] = "Octopus";
        env["TERM_PROGRAM_VERSION"] = _appVersion;
        if (extra is not null)
        {
            foreach (var (key, value) in extra)
                env[key] = value;
        }
        return env;
    }
}
