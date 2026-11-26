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
    public RemoteSettings Remote { get; set; } = new();
    public WorkspaceSettings Workspaces { get; set; } = new();
    public WorkspaceCleanupSettings WorkspaceCleanup { get; set; } = new();
    public ExperimentalSettings Experiments { get; set; } = new();
    public StarPromptSettings StarPrompt { get; set; } = new();
}

/// <summary>Local-only state for the non-intrusive GitHub Star reminder pill (no telemetry).</summary>
public sealed class StarPromptSettings
{
    public const int DefaultMinInstallDays = 3;
    public const int DefaultMinActiveDays = 3;
    public const int DefaultMinCompletedTurns = 5;
    public const int DefaultMinSessionStarts = 8;
    public const int DefaultFirstSnoozeDays = 14;
    public const int DefaultSecondSnoozeDays = 30;
    public const int DefaultMaxPromptCount = 3;

    /// <summary>When Octopus first recorded local usage for this user (UTC).</summary>
    public DateTimeOffset? FirstSeenUtc { get; set; }
    /// <summary>Last local calendar date (<c>yyyy-MM-dd</c>) when the app was used.</summary>
    public string? LastActiveDate { get; set; }
    /// <summary>Number of distinct calendar days the app has been used.</summary>
    public int ActiveDaysCount { get; set; }
    /// <summary>Cumulative number of agent turns completed without error.</summary>
    public int CompletedAgentTurns { get; set; }
    /// <summary>Cumulative number of non-restored terminal or agent tabs started by the user.</summary>
    public int SessionStartCount { get; set; }
    /// <summary>Number of times the reminder has been shown and snoozed/dismissed.</summary>
    public int PromptCount { get; set; }
    /// <summary>Earliest UTC timestamp when the reminder may appear again after a snooze.</summary>
    public DateTimeOffset? SnoozeUntilUtc { get; set; }
    /// <summary>True once the user clicked Star on GitHub; permanently silences the reminder.</summary>
    public bool Starred { get; set; }
    /// <summary>True once the user dismissed the reminder forever or reached the prompt cap.</summary>
    public bool DismissedForever { get; set; }
}

/// <summary>Opt-in / preview capabilities controlled from the Experiments settings page.</summary>
public sealed class ExperimentalSettings
{
    /// <summary>Use Arc's Monaco-based <c>CodeEditor</c> for code tabs and Markdown source mode (off: lightweight read-only viewer).</summary>
    public bool CodeEditor { get; set; } = true;

    /// <summary>Use Arc's Monaco-based <c>DiffEditor</c> for git diffs (off: read-only unified diff view).</summary>
    public bool DiffEditor { get; set; } = true;

    /// <summary>
    /// Start installed local language servers (LSP) on demand for code tabs to provide completion, hover docs,
    /// diagnostics and go-to-definition. Requires <see cref="CodeEditor"/>.
    /// </summary>
    public bool LanguageServer { get; set; }
}

/// <summary>Global preferences for isolated branch workspaces (git worktrees).</summary>
public sealed class WorkspaceSettings
{
    /// <summary>
    /// Custom root folder where new branch workspaces are created (<c>&lt;root&gt;/&lt;project&gt;/&lt;branch&gt;</c>).
    /// Null or empty uses the default <c>~/.octopus/workspaces</c>. Supports a leading <c>~</c>.
    /// </summary>
    public string? RootDirectory { get; set; }
}

/// <summary>Automatic branch workspace (git worktree) cleanup rules.</summary>
public sealed class WorkspaceCleanupSettings
{
    public const int DefaultInactiveDays = 30;
    public const int MinInactiveDays = 1;
    public const int MaxInactiveDays = 365;
    public const int DefaultCheckIntervalMinutes = 60;
    public const int MinCheckIntervalMinutes = 15;
    public const int MaxCheckIntervalMinutes = 1440;
    public const int DefaultUnchangedGraceHours = 1;
    public const int MaxUnchangedGraceHours = 168;

    /// <summary>Remove workspaces whose pull request or branch is merged into the default branch and contains all local content.</summary>
    public bool DeleteMerged { get; set; }
    /// <summary>Remove workspaces with no commits ahead of the default branch and no local edits.</summary>
    public bool DeleteUnchanged { get; set; }
    /// <summary>Remove workspaces with no activity for at least <see cref="InactiveDays"/> days.</summary>
    public bool DeleteInactive { get; set; }
    /// <summary>Days without activity before a workspace qualifies for the inactive rule.</summary>
    public int InactiveDays { get; set; } = DefaultInactiveDays;
    /// <summary>Delete the local branch together with the worktree when the branch is merged; unmerged branches are always kept.</summary>
    public bool DeleteMergedBranch { get; set; } = true;
    /// <summary>How often scheduled automatic cleanup checks run, in minutes.</summary>
    public int CheckIntervalMinutes { get; set; } = DefaultCheckIntervalMinutes;
    /// <summary>Minimum age (in hours) before an unchanged workspace is automatically removed.</summary>
    public int UnchangedGraceHours { get; set; } = DefaultUnchangedGraceHours;
    /// <summary>Run non-interactive <c>git fetch --all --prune</c> before checking merge status.</summary>
    public bool FetchBeforeCheck { get; set; }

    public bool HasAutoRulesEnabled() => DeleteMerged || DeleteUnchanged || DeleteInactive;
}

/// <summary>The icon in the notification area (Windows, Linux) or the menu bar (macOS).</summary>
/// <summary>Remote operation: whether other devices may reach this computer (design/12-远程操作.md §7.1).</summary>
public sealed class RemoteSettings
{
    /// <summary>The port other devices of the local network connect to.</summary>
    public const int DefaultLanPort = 47600;

    /// <summary>The name other devices see; null or empty: the computer's name.</summary>
    public string? DeviceName { get; set; }
    /// <summary>Listen for paired devices on the local network. Off until the person turns it on.</summary>
    public bool LanEnabled { get; set; }
    public int LanPort { get; set; } = DefaultLanPort;

    /// <summary>The relay service this computer uses to reach hosts outside its network, and to be reached; null: none. Its access token is not here but in the keystore.</summary>
    public string? RelayUrl { get; set; }
    /// <summary>Stay connected to the relay so that paired devices can reach this computer through it. Off until the person turns it on.</summary>
    public bool RelayHostEnabled { get; set; }
    /// <summary>
    /// Devices signed in to the same account may come in with this computer's access password (design/16). On unless
    /// the person turns it off; the password itself is in the keystore.
    /// </summary>
    public bool PasswordAccessEnabled { get; set; } = true;
}

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

/// <summary>The layout style (density) of the desktop interface.</summary>
public static class UiDensityValues
{
    /// <summary>Tighter spacing and row heights to display more information at once (system default).</summary>
    public const string Compact = "compact";
    /// <summary>More generous spacing and breathing room.</summary>
    public const string Comfortable = "comfortable";

    public static bool IsKnown(string? value) => value is Compact or Comfortable;
}

public sealed class AppearanceSettings
{
    /// <summary><c>system</c>, <c>light</c> or <c>dark</c>.</summary>
    public string Theme { get; set; } = "system";
    /// <summary>Accent seed color (<c>#AARRGGBB</c>); null is the default accent.</summary>
    public string? Accent { get; set; }
    /// <summary>Interface layout style: <c>compact</c> (default) or <c>comfortable</c>.</summary>
    public string Density { get; set; } = UiDensityValues.Compact;
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
    /// <summary>Agents that were open come back when the app starts. Off: every start begins without them (files stay).</summary>
    public bool RestoreItemsOnStartup { get; set; } = true;
    /// <summary>Restored agents continue their previous session when supported instead of starting fresh.</summary>
    public bool ResumeAgentsOnRestart { get; set; } = true;
    /// <summary>Ask before pasting text with several lines into a program that does not use bracketed paste.</summary>
    public bool ConfirmMultilinePaste { get; set; } = true;
}

public sealed class AgentSettings
{
    public string? DefaultAgentId { get; set; }
    /// <summary>Global default surface for ACP-capable agents (<see cref="AgentSurface.Gui"/> or <see cref="AgentSurface.Terminal"/>). Non-ACP agents always use Terminal.</summary>
    public AgentSurface DefaultSurface { get; set; } = AgentSurface.Terminal;
    /// <summary>How ACP adapters (e.g. Claude/Codex official adapters) are resolved when not installed natively.</summary>
    public AcpAdapterPolicy AcpAdapterPolicy { get; set; } = AcpAdapterPolicy.UseLocalRunners;
    /// <summary>Advertises <c>elicitation.form</c> to ACP agents whose dialect supports structured form questions.</summary>
    public bool AcpEnableFormElicitation { get; set; } = true;
    /// <summary>Advertises <c>elicitation.url</c> so ACP agents can request out-of-band browser/OAuth authorization.</summary>
    public bool AcpEnableUrlElicitation { get; set; } = true;
    /// <summary>Advertises <c>fs.readTextFile</c>/<c>fs.writeTextFile</c> so ACP agents can read/write workspace files through Octopus (default off).</summary>
    public bool AcpEnableClientFileSystem { get; set; }
    /// <summary>Advertises <c>terminal: true</c> so ACP agents can run commands inside Octopus-hosted terminals (default off).</summary>
    public bool AcpEnableClientTerminal { get; set; }
    /// <summary>Global MCP servers forwarded to ACP agents in <c>session/new</c>, <c>session/load</c>, <c>session/resume</c>, and <c>session/fork</c>.</summary>
    public List<AcpMcpServerConfig> AcpMcpServers { get; set; } = [];
    /// <summary>Multi-agent delegation, @-mention routing, and cross-session collaboration settings.</summary>
    public CollabSettings Collab { get; set; } = new();
    /// <summary>The permission level of every agent that has none of its own.</summary>
    public string DefaultPermission { get; set; } = PermissionModes.Default;
    /// <summary>
    /// Agents started here use the operating system's proxy, unless their environment already names one
    /// (<c>HTTP_PROXY</c>, <c>HTTPS_PROXY</c> or <c>ALL_PROXY</c>). Off: agents start with the environment as it is.
    /// </summary>
    public bool UseSystemProxy { get; set; } = true;
    public Dictionary<string, AgentProfile> Profiles { get; set; } = [];

    /// <summary>The last expanded/collapsed choice for each agent on the settings page.</summary>
    public Dictionary<string, bool> ExpansionStates { get; set; } = [];

    /// <summary>The profile of an agent; a new, unsaved one when the user has not configured it.</summary>
    public AgentProfile ProfileOf(string agentId) =>
        Profiles.TryGetValue(agentId, out var profile) ? profile : new AgentProfile();
}
