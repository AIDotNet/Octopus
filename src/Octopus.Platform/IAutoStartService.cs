namespace Octopus.Platform;

/// <summary>
/// Registers or removes Octopus as a per-user login item so the desktop app starts automatically when the user signs
/// in (macOS Login Items / LaunchAgent, Windows HKCU Run key, Linux XDG autostart desktop entry).
/// </summary>
public interface IAutoStartService
{
    /// <summary>
    /// Whether this running instance can manage OS auto-start (false for <c>dotnet</c> host runs, tests, isolated
    /// <c>OCTOPUS_DATA_DIR</c> instances, or macOS app-translocated/unbundled runs).
    /// </summary>
    bool IsSupported { get; }

    /// <summary>Whether auto-start is currently registered with the operating system.</summary>
    bool IsEnabled();

    /// <summary>
    /// Enables or disables auto-start with the operating system. Returns true when the requested state is now active
    /// (or was already active); returns false if unsupported or if the OS rejected the change. Never throws.
    /// </summary>
    bool SetEnabled(bool enabled);
}

/// <summary>No-op stand-in for tests, isolated instances, or environments where auto-start is unsupported.</summary>
public sealed class NoAutoStartService : IAutoStartService
{
    public bool IsSupported => false;
    public bool IsEnabled() => false;
    public bool SetEnabled(bool enabled) => false;
}
