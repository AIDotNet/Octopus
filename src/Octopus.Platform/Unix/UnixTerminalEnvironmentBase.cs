namespace Octopus.Platform.Unix;

/// <summary>
/// Environment for a terminal child on Unix: startup snapshot, minus host variables, plus a UTF-8 LANG, TERM and
/// friends, plus caller extras. The snapshot is taken at construction so later changes by this process do not leak.
/// </summary>
public abstract class UnixTerminalEnvironmentBase : ITerminalEnvironment
{
    private readonly IReadOnlyDictionary<string, string> _snapshot;
    private readonly string _appVersion;

    /// <param name="snapshot">Test seam; defaults to the current process environment.</param>
    /// <param name="appVersion">Reported as TERM_PROGRAM_VERSION.</param>
    protected UnixTerminalEnvironmentBase(IReadOnlyDictionary<string, string>? snapshot = null, string? appVersion = null)
    {
        _snapshot = snapshot ?? HostEnvironmentFilter.SnapshotProcessEnvironment(StringComparer.Ordinal);
        _appVersion = appVersion ?? HostEnvironmentFilter.DefaultAppVersion();
    }

    /// <summary>True when LANG must be replaced. Linux only fills a missing one; macOS also fixes non-UTF-8 values.</summary>
    protected virtual bool NeedsLang(string? currentLang) => string.IsNullOrEmpty(currentLang);

    /// <summary>The UTF-8 locale to use when <see cref="NeedsLang"/> says so.</summary>
    protected abstract string GetDefaultLang();

    protected static bool IsUtf8(string? lang) =>
        lang is not null && (lang.Contains("UTF-8", StringComparison.OrdinalIgnoreCase)
                             || lang.Contains("UTF8", StringComparison.OrdinalIgnoreCase));

    public IReadOnlyDictionary<string, string> Build(IReadOnlyDictionary<string, string>? extra = null)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in _snapshot)
        {
            if (!HostEnvironmentFilter.ShouldRemove(key))
                env[key] = value;
        }

        env.TryGetValue("LANG", out var lang);
        if (NeedsLang(lang))
            env["LANG"] = GetDefaultLang();

        env["TERM"] = "xterm-256color";
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
