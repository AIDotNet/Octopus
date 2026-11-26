using System.Text;

namespace Octopus.Platform.Linux;

/// <summary>
/// Manages per-user Linux desktop auto-start via an XDG autostart desktop entry
/// (<c>${XDG_CONFIG_HOME:-$HOME/.config}/autostart/octopus.desktop</c>).
/// </summary>
public sealed class LinuxAutoStartService : IAutoStartService
{
    public const string DesktopEntryFileName = "octopus.desktop";

    private readonly string? _executablePath;
    private readonly string _desktopEntryPath;

    public LinuxAutoStartService()
        : this(executablePath: null, home: null, xdgConfigHome: null)
    {
    }

    internal LinuxAutoStartService(string? executablePath = null, string? home = null, string? xdgConfigHome = null)
    {
        var candidate = executablePath ?? Environment.ProcessPath;
        _executablePath = IsEligibleExecutable(candidate) ? Path.GetFullPath(candidate!) : null;

        home ??= Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        xdgConfigHome ??= Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var configRoot = Absolute(xdgConfigHome) ?? Path.Combine(home, ".config");
        _desktopEntryPath = Path.Combine(configRoot, "autostart", DesktopEntryFileName);
    }

    public bool IsSupported => _executablePath is not null;

    internal string? ExecutablePath => _executablePath;

    internal string DesktopEntryPath => _desktopEntryPath;

    public bool IsEnabled()
    {
        if (!IsSupported || !File.Exists(_desktopEntryPath))
        {
            return false;
        }
        try
        {
            foreach (var raw in File.ReadLines(_desktopEntryPath, Encoding.UTF8))
            {
                var line = raw.Trim();
                if (line.Equals("Hidden=true", StringComparison.OrdinalIgnoreCase)
                    || line.Equals("X-GNOME-Autostart-enabled=false", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool SetEnabled(bool enabled)
    {
        if (!IsSupported || _executablePath is null)
        {
            return false;
        }
        return enabled ? TryWriteEntry(_executablePath) : TryDeleteEntry();
    }

    internal static string BuildDesktopEntry(string executablePath)
    {
        var exec = EscapeExecArgument(executablePath);
        var dir = Path.GetDirectoryName(executablePath);
        var iconCandidate = string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "octopus.png");
        var icon = iconCandidate is not null && File.Exists(iconCandidate) ? iconCandidate : "octopus";
        return $"""
            [Desktop Entry]
            Type=Application
            Name=Octopus
            Comment=Terminal and project manager
            Exec="{exec}"
            Icon={icon}
            Terminal=false
            Categories=Development;
            StartupWMClass=Octopus
            X-GNOME-Autostart-enabled=true

            """;
    }

    internal static string EscapeExecArgument(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\':
                case '"':
                case '`':
                case '$':
                    sb.Append('\\').Append(ch);
                    break;
                case '%':
                    sb.Append("%%");
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }

    private bool TryWriteEntry(string executablePath)
    {
        try
        {
            var expected = BuildDesktopEntry(executablePath);
            if (File.Exists(_desktopEntryPath) && string.Equals(File.ReadAllText(_desktopEntryPath, Encoding.UTF8), expected, StringComparison.Ordinal))
            {
                return true;
            }
            var dir = Path.GetDirectoryName(_desktopEntryPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var temp = _desktopEntryPath + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(temp, expected, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, _desktopEntryPath, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool TryDeleteEntry()
    {
        try
        {
            if (File.Exists(_desktopEntryPath))
            {
                File.Delete(_desktopEntryPath);
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsEligibleExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
        {
            return false;
        }
        var stem = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(stem))
        {
            return false;
        }
        return !stem.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            && !stem.Equals("testhost", StringComparison.OrdinalIgnoreCase)
            && !stem.Equals("vstest.console", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Absolute(string? path) =>
        !string.IsNullOrEmpty(path) && Path.IsPathRooted(path) ? path : null;
}
