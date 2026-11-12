using Octopus.Platform.Unix;

namespace Octopus.Platform.MacOS;

/// <summary>
/// A Dock-launched app has no LANG, which breaks CJK width and git/ls output. Derive one from the system locale.
/// </summary>
public sealed class MacTerminalEnvironment : UnixTerminalEnvironmentBase
{
    private const string Fallback = "en_US.UTF-8";
    private readonly IProcessRunner _runner;
    private string? _lang;

    public MacTerminalEnvironment(IProcessRunner runner, IReadOnlyDictionary<string, string>? snapshot = null, string? appVersion = null)
        : base(snapshot, appVersion) => _runner = runner;

    protected override bool NeedsLang(string? currentLang) => !IsUtf8(currentLang);

    protected override string GetDefaultLang() => _lang ??= DetectLang();

    private string DetectLang()
    {
        var result = _runner.RunAsync("/usr/bin/defaults", ["read", "-g", "AppleLocale"],
            timeout: TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        if (!result.Succeeded) return Fallback;
        return LangFromAppleLocale(result.StandardOutput, name => Directory.Exists("/usr/share/locale/" + name));
    }

    /// <summary>Turns <c>zh_CN</c> (or <c>en_US@rg=cnzzzz</c>) into <c>zh_CN.UTF-8</c> when that locale is installed.</summary>
    public static string LangFromAppleLocale(string appleLocale, Func<string, bool> localeExists)
    {
        var locale = appleLocale.Trim().Split('@')[0];
        if (locale.Length == 0) return Fallback;
        var candidate = locale + ".UTF-8";
        return localeExists(candidate) ? candidate : Fallback;
    }
}
