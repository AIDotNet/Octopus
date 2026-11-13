using Octopus.Core.Agents;

namespace Octopus.Core.Settings;

/// <summary>The content of settings.json.</summary>
public sealed class AppSettingsModel
{
    public int Version { get; set; } = 1;
    public AppearanceSettings Appearance { get; set; } = new();
    public WindowSettings? Window { get; set; }
    public SidebarSettings Sidebar { get; set; } = new();
    public RightPanelSettings RightPanel { get; set; } = new();
    public TerminalSettings Terminal { get; set; } = new();
    public AgentSettings Agents { get; set; } = new();
    public UpdateSettings Updates { get; set; } = new();
    public TraySettings Tray { get; set; } = new();
}

/// <summary>The icon in the notification area (Windows, Linux) or the menu bar (macOS).</summary>
public sealed class TraySettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>The window's close button hides the window to the tray instead of quitting; Quit in the tray menu (or the app menu) quits.</summary>
    public bool CloseToTray { get; set; }
}

/// <summary>Automatic updates (design/10-自动更新.md §9).</summary>
public sealed class UpdateSettings
{
    /// <summary><c>auto</c> (download, install on quit), <c>notify</c> (tell only) or <c>off</c> (check only when asked).</summary>
    public string Mode { get; set; } = Octopus.Core.Updates.UpdateSettingValues.Auto;
    /// <summary><c>stable</c> or <c>preview</c>; null follows the running version (a preview receives previews).</summary>
    public string? Channel { get; set; }
    /// <summary>The version the person chose to skip; only a newer one is offered.</summary>
    public string? SkippedVersion { get; set; }
}

public sealed class AppearanceSettings
{
    /// <summary><c>system</c>, <c>light</c> or <c>dark</c>.</summary>
    public string Theme { get; set; } = "system";
    /// <summary>Accent seed color (<c>#AARRGGBB</c>); null is the default accent.</summary>
    public string? Accent { get; set; }
    /// <summary>BCP 47 tag; null is the language the app starts with.</summary>
    public string? Language { get; set; }
}

public sealed class WindowSettings
{
    public int X { get; set; }
    public int Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool IsMaximized { get; set; }
}

public sealed class SidebarSettings
{
    public bool IsCollapsed { get; set; }
    public double Width { get; set; } = 260;
}

/// <summary>The panel on the right: files of the project folder and, in a git repository, source control.</summary>
public sealed class RightPanelSettings
{
    public bool IsOpen { get; set; }
    public double Width { get; set; } = 300;
    /// <summary>The tab shown last: <c>files</c>, <c>git</c> or <c>tunnels</c>.</summary>
    public string Tab { get; set; } = "files";
}

public sealed class TerminalSettings
{
    /// <summary>Null is the platform's default monospace stack.</summary>
    public string? FontFamily { get; set; }
    public double FontSize { get; set; } = 13;
    public int ScrollbackLines { get; set; } = 10_000;
    /// <summary>Absolute path of the shell; null detects the user's shell.</summary>
    public string? Shell { get; set; }
    /// <summary>Terminals and agents that were open come back when the app starts. Off: every start begins without them (files stay).</summary>
    public bool RestoreItemsOnStartup { get; set; } = true;
    public bool ResumeAgentsOnRestart { get; set; } = true;
    /// <summary>Ask before pasting text with several lines into a program that does not use bracketed paste.</summary>
    public bool ConfirmMultilinePaste { get; set; } = true;
}

public sealed class AgentSettings
{
    public string? DefaultAgentId { get; set; }
    /// <summary>The permission level of every agent that has none of its own.</summary>
    public string DefaultPermission { get; set; } = PermissionModes.Default;
    public Dictionary<string, AgentProfile> Profiles { get; set; } = [];

    /// <summary>The profile of an agent; a new, unsaved one when the user has not configured it.</summary>
    public AgentProfile ProfileOf(string agentId) =>
        Profiles.TryGetValue(agentId, out var profile) ? profile : new AgentProfile();
}
