namespace Octopus.Remote.Protocol;

/// <summary>Snapshot of one Bot on a host, including its chats (active &amp; closed), connectors, and work items.</summary>
public sealed class BotMessage
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "bot";
    public string AgentId { get; set; } = "claude";
    public string Color { get; set; } = "#5b5bd6";
    public string? Icon { get; set; }
    public string? Image { get; set; }
    public string? SoulSummary { get; set; }
    public string HomePath { get; set; } = "";
    public bool IsPaused { get; set; }
    public bool LookAroundEnabled { get; set; } = true;
    public int LookAroundIntervalHours { get; set; } = 4;
    public bool TidyUpMemoryEnabled { get; set; } = true;
    public int StaleAfterDays { get; set; } = 5;
    public string? HomeChatId { get; set; }
    public string? ShareLink { get; set; }
    public List<BotChatMessage> Chats { get; set; } = [];
    public List<BotConnectorMessage> Connectors { get; set; } = [];
    public List<BotWorkItemMessage> Items { get; set; } = [];
}

public sealed class BotListMessage
{
    public List<BotMessage> Bots { get; set; } = [];
}

public sealed class BotChatMessage
{
    public string Id { get; set; } = "";
    public string BotId { get; set; } = "";
    /// <summary><c>home</c>, <c>chat</c>, <c>automation</c>, or <c>connector</c>.</summary>
    public string Kind { get; set; } = "chat";
    public string Title { get; set; } = "";
    public string? AgentId { get; set; }
    public string? Subtitle { get; set; }
    /// <summary><c>idle</c>, <c>working</c>, <c>waiting</c>, or <c>error</c>.</summary>
    public string Activity { get; set; } = ItemActivities.Idle;
    /// <summary><c>gui</c> or <c>terminal</c>.</summary>
    public string Surface { get; set; } = ItemSurfaces.Gui;
    public bool IsOpen { get; set; } = true;
    public DateTimeOffset LastActivityAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ClosedAt { get; set; }
}

public sealed class BotConnectorMessage
{
    public string Id { get; set; } = "";
    public string AppId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Url { get; set; }
    public string Transport { get; set; } = "http";
    public string AuthMode { get; set; } = "none";
    public bool HasSecret { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class BotWorkItemMessage
{
    public string Id { get; set; } = "";
    public int Number { get; set; }
    public string Title { get; set; } = "";
    /// <summary><c>planned</c>, <c>working</c>, <c>needsYou</c>, <c>done</c>, or <c>canceled</c>.</summary>
    public string Status { get; set; } = "planned";
    public string? Question { get; set; }
    public List<string> Options { get; set; } = [];
    public string? ChatId { get; set; }
    public string? Meta { get; set; }
}

public sealed class CreateBotMessage
{
    public string Name { get; set; } = "";
    public string? AgentId { get; set; }
    public string? Color { get; set; }
    public string? TemplateId { get; set; }
    public string? Soul { get; set; }
}

public sealed class ImportBotMessage
{
    public string ShareLink { get; set; } = "";
}

public sealed class BotRefMessage
{
    public string BotId { get; set; } = "";
}

public sealed class UpdateBotMessage
{
    public string BotId { get; set; } = "";
    public string? Name { get; set; }
    public string? AgentId { get; set; }
    public bool? IsPaused { get; set; }
    public bool? LookAroundEnabled { get; set; }
    public bool? TidyUpMemoryEnabled { get; set; }
}

public sealed class CreateBotChatMessage
{
    public string BotId { get; set; } = "";
    public string? AgentId { get; set; }
    public string? Title { get; set; }
}

public sealed class BotChatRefMessage
{
    public string BotId { get; set; } = "";
    public string ChatId { get; set; } = "";
}

public sealed class RenameBotChatMessage
{
    public string BotId { get; set; } = "";
    public string ChatId { get; set; } = "";
    public string? Title { get; set; }
}

public sealed class ResolveBotItemMessage
{
    public string BotId { get; set; } = "";
    public string ItemId { get; set; } = "";
    public string? ChosenOption { get; set; }
}

public sealed class ToggleBotConnectorMessage
{
    public string BotId { get; set; } = "";
    public string ConnectorId { get; set; } = "";
    public bool Enabled { get; set; }
}
