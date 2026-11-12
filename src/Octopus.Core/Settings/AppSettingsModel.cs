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
    /// <summary>The tab shown last: <c>files</c> or <c>git</c>.</summary>
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
