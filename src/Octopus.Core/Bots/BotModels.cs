using Octopus.Core.Agents;

namespace Octopus.Core.Bots;

/// <summary>A long-lived autonomous or conversational agent with its own home folder, chats, connectors, and memory.</summary>
public sealed class BotDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>Folder name under <c>~/.octopus/bots/&lt;slug&gt;</c>.</summary>
    public string Slug { get; set; } = "bot";
    /// <summary>Default agent definition id used for new chats (e.g. <c>claude</c>, <c>codex</c>).</summary>
    public string AgentId { get; set; } = "claude";
    /// <summary>Device/host key where this bot runs (e.g. <c>local</c>, <c>remote:&lt;id&gt;</c>, <c>ssh:&lt;alias&gt;</c>). Independent of projects.</summary>
    public string HostDeviceKey { get; set; } = "local";
    /// <summary>Avatar background hex color.</summary>
    public string Color { get; set; } = "#5b5bd6";
    /// <summary>Optional Lucide icon name shown on the avatar; null shows the bot's initial.</summary>
    public string? Icon { get; set; }
    /// <summary>Optional custom thumbnail image file name stored in <c>project-icons</c>.</summary>
    public string? Image { get; set; }
    /// <summary>Short role summary written to <c>SOUL.md</c> when created.</summary>
    public string? Soul { get; set; }
    /// <summary>Starter template id used to seed the bot (e.g. <c>blank</c>, <c>pr</c>, <c>ci</c>, <c>daily</c>).</summary>
    public string? TemplateId { get; set; }
    public bool IsPaused { get; set; }
    /// <summary>Manual unread marker toggled from the Bot's right-click menu.</summary>
    public bool IsUnread { get; set; }
    /// <summary>Permission policy: <c>same_as_chat</c>, <c>ask</c>, <c>read_only</c>, <c>auto</c>.</summary>
    public string PermissionMode { get; set; } = "same_as_chat";
    /// <summary>Whether periodic background look-around ("巡视") is enabled.</summary>
    public bool LookAroundEnabled { get; set; } = true;
    /// <summary>Interval in hours between look-around runs.</summary>
    public int LookAroundIntervalHours { get; set; } = 4;
    /// <summary>Whether nightly memory maintenance ("记忆维护") is enabled.</summary>
    public bool TidyUpMemoryEnabled { get; set; } = true;
    /// <summary>Days of inactivity before a work item is marked stale.</summary>
    public int StaleAfterDays { get; set; } = 5;
    /// <summary>Access control scope: <c>only_you</c>, <c>members</c>, <c>anyone</c>.</summary>
    public string WhoCanAsk { get; set; } = "only_you";
    /// <summary>Notification toggles.</summary>
    public bool NotifyNeedsPermission { get; set; } = true;
    public bool NotifyTaskCompleted { get; set; } = true;
    public bool NotifyUnableToComplete { get; set; } = true;
    /// <summary>Optional setup notes attached when sharing or exporting the bot.</summary>
    public string? SetupNotes { get; set; }
    /// <summary>Whether the bot's chat tree is expanded in the left Bot panel (Scheme A).</summary>
    public bool IsExpanded { get; set; } = true;
    /// <summary>Id of the pinned Home Chat.</summary>
    public string? HomeChatId { get; set; }
    /// <summary>Last active chat or file item id for this bot.</summary>
    public string? ActiveChatId { get; set; }
    /// <summary>Saved center dock layout JSON for this bot.</summary>
    public string? DockLayout { get; set; }
    public List<BotChat> Chats { get; set; } = [];
    public List<BotConnectorBinding> Connectors { get; set; } = [];
    public List<BotWorkItem> Items { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum BotChatKind
{
    Home,
    Chat,
    Automation,
    Connector
}

public enum BotChatStatus
{
    Idle,
    Working,
    NeedsYou,
    Done,
    Closed
}

/// <summary>One conversation belonging to a <see cref="BotDefinition"/>.</summary>
public sealed class BotChat
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public BotChatKind Kind { get; set; } = BotChatKind.Chat;
    public string Title { get; set; } = "";
    public string? CustomTitle { get; set; }
    /// <summary>Agent definition id for this chat; null falls back to the parent bot's default agent.</summary>
    public string? AgentId { get; set; }
    /// <summary>Short preview or status line shown under the chat title.</summary>
    public string? Subtitle { get; set; }
    public string? SessionId { get; set; }
    public string? AcpSessionId { get; set; }
    public AgentSurface? Surface { get; set; }
    public BotChatStatus Status { get; set; } = BotChatStatus.Idle;
    /// <summary>True while open as a tab in the bot's center dock; closed chats remain in history and can be reopened.</summary>
    public bool IsOpen { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastActivityAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ClosedAt { get; set; }

    public string DisplayTitle => string.IsNullOrWhiteSpace(CustomTitle) ? Title : CustomTitle!;
}

/// <summary>An external app or custom URL connector bound to a bot.</summary>
public sealed class BotConnectorBinding
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AppId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Category { get; set; } = "coding";
    public string? Description { get; set; }
    public string? Url { get; set; }
    /// <summary>MCP transport: <c>http</c> (Streamable HTTP, default) or <c>sse</c>.</summary>
    public string Transport { get; set; } = "http";
    /// <summary>Authentication mode: <c>none</c>, <c>oauth</c>, or <c>bearer</c>.</summary>
    public string AuthMode { get; set; } = "none";
    /// <summary>True when a Bearer token or credential is saved in <c>ISecretStore</c> for this connector.</summary>
    public bool HasSecret { get; set; }
    /// <summary>Optional template variable value (e.g. GitLab host or Datadog site).</summary>
    public string? VariableValue { get; set; }
    public bool Enabled { get; set; } = true;
    public string? StatusText { get; set; }
    public DateTimeOffset ConnectedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum BotWorkItemStatus
{
    Planned,
    Working,
    NeedsYou,
    Done,
    Canceled
}

/// <summary>A tracked work item owned by a bot (shown on the Bot right panel's Home tab).</summary>
public sealed class BotWorkItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public BotWorkItemStatus Status { get; set; } = BotWorkItemStatus.Planned;
    public string? Question { get; set; }
    public List<string> Options { get; set; } = [];
    public string? ChatId { get; set; }
    public string? Meta { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>The persisted content of <c>bots.json</c>.</summary>
public sealed class BotsDocument
{
    public int Version { get; set; } = 1;
    public bool IsBotMode { get; set; }
    public string? ActiveBotId { get; set; }
    public string? ActiveChatId { get; set; }
    public List<BotDefinition> Bots { get; set; } = [];
}

/// <summary>Mirror written to <c>~/.octopus/bots/&lt;slug&gt;/bot.json</c> for inspection by agents and tools.</summary>
public sealed class BotHomeManifest
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string AgentId { get; set; } = "";
    public string HostDeviceKey { get; set; } = "local";
    public string Color { get; set; } = "";
    public bool IsPaused { get; set; }
    public string? HomeChatId { get; set; }
    public List<string> Connectors { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Connector metadata included in a shareable <c>octopus://bot/import</c> payload (never includes secrets).</summary>
public sealed class BotShareConnectorDto
{
    public string AppId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Url { get; set; }
    public string Transport { get; set; } = "http";
    public string AuthMode { get; set; } = "none";
    public string? VariableValue { get; set; }
}

/// <summary>Portable Bot snapshot encoded into an <c>octopus://bot/import?data=...</c> deep link.</summary>
public sealed class BotSharePayload
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "";
    public string AgentId { get; set; } = "claude";
    public string Color { get; set; } = "#5b5bd6";
    public string? Icon { get; set; }
    public string? TemplateId { get; set; }
    public string PermissionMode { get; set; } = "same_as_chat";
    public bool LookAroundEnabled { get; set; } = true;
    public int LookAroundIntervalHours { get; set; } = 4;
    public bool TidyUpMemoryEnabled { get; set; } = true;
    public int StaleAfterDays { get; set; } = 5;
    public string? SetupNotes { get; set; }
    public string? SoulMarkdown { get; set; }
    public string? MemoryMarkdown { get; set; }
    public List<BotShareConnectorDto> Connectors { get; set; } = [];
}

/// <summary>Single server entry written to <c>~/.octopus/bots/&lt;slug&gt;/.mcp.json</c> for CLI agents.</summary>
public sealed class BotMcpServerEntry
{
    public string Type { get; set; } = "http";
    public string Url { get; set; } = "";
    public Dictionary<string, string>? Headers { get; set; }
}

/// <summary>Standard <c>.mcp.json</c> document synchronized inside a Bot's home directory.</summary>
public sealed class BotMcpFileDocument
{
    public Dictionary<string, BotMcpServerEntry> McpServers { get; set; } = new(StringComparer.Ordinal);
}
