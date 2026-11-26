using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Octopus.Mobile.Workspace;

/// <summary>A Bot hosted on a paired <see cref="RemoteHost"/>, with its multi-chat sessions, connectors, and work items.</summary>
public sealed partial class RemoteBot : ObservableObject
{
    public RemoteBot(RemoteHost host, string id, string name, string homePath)
    {
        Host = host;
        Id = id;
        Name = name;
        HomePath = homePath;
        BackingProject = new RemoteProject(host, "bot:" + id, name, homePath);
    }

    public RemoteHost Host { get; }
    public string Id { get; }
    /// <summary>Synthetic backing project pointing at the Bot's home directory so chats open in <c>ConversationPage</c>/<c>TerminalPage</c> and files open in <c>FilesPage</c>.</summary>
    public RemoteProject BackingProject { get; }

    [ObservableProperty] public partial string Name { get; set; }
    [ObservableProperty] public partial string Slug { get; set; } = "bot";
    [ObservableProperty] public partial string AgentId { get; set; } = "claude";
    [ObservableProperty] public partial string Color { get; set; } = "#5b5bd6";
    [ObservableProperty] public partial string? Icon { get; set; }
    [ObservableProperty] public partial string? SoulSummary { get; set; }
    [ObservableProperty] public partial string HomePath { get; set; }
    [ObservableProperty] public partial bool IsPaused { get; set; }
    [ObservableProperty] public partial bool LookAroundEnabled { get; set; } = true;
    [ObservableProperty] public partial int LookAroundIntervalHours { get; set; } = 4;
    [ObservableProperty] public partial bool TidyUpMemoryEnabled { get; set; } = true;
    [ObservableProperty] public partial int StaleAfterDays { get; set; } = 5;
    [ObservableProperty] public partial string? HomeChatId { get; set; }
    [ObservableProperty] public partial string? ShareLink { get; set; }

    public ObservableCollection<RemoteBotChat> Chats { get; } = [];
    public ObservableCollection<RemoteBotConnector> Connectors { get; } = [];
    public ObservableCollection<RemoteBotWorkItem> Items { get; } = [];

    public int NeedsAttentionCount =>
        Items.Count(i => string.Equals(i.Status, "needsYou", StringComparison.OrdinalIgnoreCase))
        + Chats.Count(c => c.IsOpen && c.Activity == SessionActivity.Waiting);

    public bool IsWorking =>
        Items.Any(i => string.Equals(i.Status, "working", StringComparison.OrdinalIgnoreCase))
        || Chats.Any(c => c.IsOpen && c.Activity == SessionActivity.Working);

    partial void OnColorChanged(string value) => BackingProject.Color = value;
}

/// <summary>One chat/session belonging to a <see cref="RemoteBot"/> (Home Chat or a parallel sub-chat, active or closed).</summary>
public sealed partial class RemoteBotChat : ObservableObject
{
    public RemoteBotChat(RemoteBot bot, string id, string kind, string title)
    {
        Bot = bot;
        Id = id;
        Kind = kind;
        Title = title;
    }

    public RemoteBot Bot { get; }
    public string Id { get; }
    /// <summary><c>home</c>, <c>chat</c>, <c>automation</c>, or <c>connector</c>.</summary>
    public string Kind { get; }
    public bool IsHome => string.Equals(Kind, "home", StringComparison.OrdinalIgnoreCase);

    [ObservableProperty] public partial string Title { get; set; }
    [ObservableProperty] public partial string? AgentId { get; set; }
    [ObservableProperty] public partial string? Subtitle { get; set; }
    [ObservableProperty] public partial SessionActivity Activity { get; set; }
    [ObservableProperty] public partial bool IsGui { get; set; } = true;
    [ObservableProperty] public partial bool IsOpen { get; set; } = true;
    [ObservableProperty] public partial DateTimeOffset LastActivityAt { get; set; } = DateTimeOffset.UtcNow;
    [ObservableProperty] public partial DateTimeOffset? ClosedAt { get; set; }

    /// <summary>Wraps this Bot chat as a <see cref="RemoteSession"/> so existing <c>ConversationPage</c> / <c>TerminalPage</c> / <c>SessionWorkspaceOverlay</c> work seamlessly.</summary>
    public RemoteSession ToRemoteSession()
    {
        var existing = Bot.BackingProject.Sessions.FirstOrDefault(s => s.Id == Id);
        if (existing is not null && existing.IsGui == IsGui)
        {
            existing.Title = Title;
            existing.Activity = Activity;
            existing.Preview = Subtitle;
            return existing;
        }
        if (existing is not null) Bot.BackingProject.Sessions.Remove(existing);
        var session = new RemoteSession(Bot.BackingProject, Id, SessionKind.Agent, Title)
        {
            AgentId = AgentId ?? Bot.AgentId,
            Detail = Bot.Name,
            IsGui = IsGui,
            Activity = Activity,
            Preview = Subtitle,
        };
        Bot.BackingProject.Sessions.Add(session);
        return session;
    }
}

/// <summary>An external Remote MCP or custom URL connector on a <see cref="RemoteBot"/>.</summary>
public sealed partial class RemoteBotConnector : ObservableObject
{
    public RemoteBotConnector(RemoteBot bot, string id, string appId, string displayName)
    {
        Bot = bot;
        Id = id;
        AppId = appId;
        DisplayName = displayName;
    }

    public RemoteBot Bot { get; }
    public string Id { get; }
    public string AppId { get; }
    public string DisplayName { get; }
    [ObservableProperty] public partial string? Url { get; set; }
    [ObservableProperty] public partial string Transport { get; set; } = "http";
    [ObservableProperty] public partial string AuthMode { get; set; } = "none";
    [ObservableProperty] public partial bool HasSecret { get; set; }
    [ObservableProperty] public partial bool Enabled { get; set; } = true;
}

/// <summary>A tracked work item owned by a <see cref="RemoteBot"/>, supporting 1-tap approval from mobile.</summary>
public sealed partial class RemoteBotWorkItem : ObservableObject
{
    public RemoteBotWorkItem(RemoteBot bot, string id, int number, string title)
    {
        Bot = bot;
        Id = id;
        Number = number;
        Title = title;
    }

    public RemoteBot Bot { get; }
    public string Id { get; }
    public int Number { get; }
    [ObservableProperty] public partial string Title { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "planned";
    [ObservableProperty] public partial string? Question { get; set; }
    [ObservableProperty] public partial IReadOnlyList<string> Options { get; set; } = [];
    [ObservableProperty] public partial string? ChatId { get; set; }
    [ObservableProperty] public partial string? Meta { get; set; }

    public bool IsNeedsYou => string.Equals(Status, "needsYou", StringComparison.OrdinalIgnoreCase);
    public bool IsWorking => string.Equals(Status, "working", StringComparison.OrdinalIgnoreCase);
}

public sealed record CreateRemoteBotRequest(
    string Name,
    string? AgentId = null,
    string? Color = null,
    string? TemplateId = null,
    string? Soul = null);

public sealed record CreateRemoteBotChatRequest(
    string? AgentId = null,
    string? Title = null);
