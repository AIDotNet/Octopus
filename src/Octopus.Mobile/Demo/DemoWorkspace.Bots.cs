using Octopus.Mobile.Workspace;

namespace Octopus.Mobile.Demo;

public sealed partial class DemoWorkspace
{
    public bool SupportsBots(RemoteHost host) => host.IsPaired;

    private void SeedDemoBots(RemoteHost mac, RemoteHost station)
    {
        var now = DateTimeOffset.Now;

        // Bot 1 on MacBook Pro: 西瓜 (has pending work item + 3 active chats + 1 closed history chat + connectors)
        var xigua = new RemoteBot(mac, "bot-xigua", "西瓜", "~/.octopus/bots/xigua")
        {
            Slug = "xigua",
            AgentId = "claude",
            Color = "#3B82D6",
            SoulSummary = "盯住 Octopus 仓库新开的 PR 和失败 CI，给出简洁审查意见；合并或修改前先向我确认。",
            LookAroundEnabled = true,
            LookAroundIntervalHours = 4,
            TidyUpMemoryEnabled = true,
            HomeChatId = "bchat-xigua-home",
            ShareLink = "octopus://bot/import?data=demo-xigua",
        };
        xigua.Chats.Add(new RemoteBotChat(xigua, "bchat-xigua-home", "home", "Home Chat")
        {
            AgentId = "claude",
            Subtitle = "PR #131 CI 已全部通过，等你拍板合并",
            Activity = SessionActivity.Waiting,
            IsGui = true,
            IsOpen = true,
            LastActivityAt = now.AddMinutes(-2),
        });
        xigua.Chats.Add(new RemoteBotChat(xigua, "bchat-xigua-mcp", "chat", "审查 MCP 凭据存储")
        {
            AgentId = "claude",
            Subtitle = "正在核对钥匙串写入与 .mcp.json 隔离",
            Activity = SessionActivity.Working,
            IsGui = true,
            IsOpen = true,
            LastActivityAt = now.AddMinutes(-13),
        });
        xigua.Chats.Add(new RemoteBotChat(xigua, "bchat-xigua-codex", "chat", "Codex 性能压测")
        {
            AgentId = "codex",
            Subtitle = "Base64Url 编解码平均耗时 0.8ms",
            Activity = SessionActivity.Idle,
            IsGui = true,
            IsOpen = true,
            LastActivityAt = now.AddMinutes(-35),
        });
        xigua.Chats.Add(new RemoteBotChat(xigua, "bchat-xigua-closed", "chat", "Claude Chat")
        {
            AgentId = "claude",
            Subtitle = "完成了桌面端 Bot 首页布局对齐",
            Activity = SessionActivity.Idle,
            IsGui = true,
            IsOpen = false,
            LastActivityAt = now.AddDays(-1),
            ClosedAt = now.AddDays(-1),
        });
        xigua.Items.Add(new RemoteBotWorkItem(xigua, "bitem-131", 131, "PR #131 · Bot 远程 MCP 支持")
        {
            Status = "needsYou",
            Question = "CI 全部绿灯（97/97），是否现在合并到 main？",
            Options = ["立即合并", "先暂缓"],
            ChatId = "bchat-xigua-home",
        });
        xigua.Connectors.Add(new RemoteBotConnector(xigua, "bconn-gh", "github", "GitHub")
        {
            Url = "https://api.githubcopilot.com/mcp/",
            Transport = "http",
            AuthMode = "bearer",
            HasSecret = true,
            Enabled = true,
        });
        xigua.Connectors.Add(new RemoteBotConnector(xigua, "bconn-c7", "context7", "Context7")
        {
            Url = "https://mcp.context7.com/mcp",
            Transport = "http",
            AuthMode = "none",
            Enabled = true,
        });
        mac.Bots.Add(xigua);

        // Bot 2 on 工作站: 巡检值班
        var watcher = new RemoteBot(station, "bot-watcher", "巡检值班", "~/.octopus/bots/watcher")
        {
            Slug = "watcher",
            AgentId = "codex",
            Color = "#D97757",
            SoulSummary = "定期检查构建、Sentry 异常与 Relay 延迟，仅在异常升级时提醒。",
            LookAroundEnabled = true,
            LookAroundIntervalHours = 2,
            TidyUpMemoryEnabled = true,
            HomeChatId = "bchat-watcher-home",
            ShareLink = "octopus://bot/import?data=demo-watcher",
        };
        watcher.Chats.Add(new RemoteBotChat(watcher, "bchat-watcher-home", "home", "Home Chat")
        {
            AgentId = "codex",
            Subtitle = "正在分析 Sentry #4821 堆栈…",
            Activity = SessionActivity.Working,
            IsGui = true,
            IsOpen = true,
            LastActivityAt = now.AddMinutes(-1),
        });
        watcher.Connectors.Add(new RemoteBotConnector(watcher, "bconn-sentry", "sentry", "Sentry")
        {
            Url = "https://mcp.sentry.dev/mcp",
            Transport = "http",
            AuthMode = "oauth",
            Enabled = true,
        });
        station.Bots.Add(watcher);
    }

    public Task<RemoteBot> CreateBotAsync(RemoteHost host, CreateRemoteBotRequest request, CancellationToken cancellationToken = default)
    {
        var name = string.IsNullOrWhiteSpace(request.Name) ? "新 Bot" : request.Name.Trim();
        var id = "bot-" + _nextId++;
        var slug = name.ToLowerInvariant().Replace(' ', '-');
        var bot = new RemoteBot(host, id, name, $"~/.octopus/bots/{slug}")
        {
            Slug = slug,
            AgentId = string.IsNullOrWhiteSpace(request.AgentId) ? (host.Agents.FirstOrDefault()?.Id ?? "claude") : request.AgentId,
            Color = string.IsNullOrWhiteSpace(request.Color) ? "#5B5BD6" : request.Color,
            SoulSummary = string.IsNullOrWhiteSpace(request.Soul) ? $"由移动端创建的 {name}" : request.Soul.Trim(),
            HomeChatId = "bchat-" + _nextId,
            ShareLink = $"octopus://bot/import?data=demo-{id}",
        };
        bot.Chats.Add(new RemoteBotChat(bot, bot.HomeChatId, "home", "Home Chat")
        {
            AgentId = bot.AgentId,
            Subtitle = $"向 {name} 打个招呼",
            Activity = SessionActivity.Idle,
            IsGui = true,
            IsOpen = true,
            LastActivityAt = DateTimeOffset.Now,
        });
        host.Bots.Insert(0, bot);
        return Task.FromResult(bot);
    }

    public Task<RemoteBot> ImportBotAsync(RemoteHost host, string shareLink, CancellationToken cancellationToken = default)
    {
        var trimmed = shareLink?.Trim() ?? "";
        if (!trimmed.StartsWith("octopus://bot/import", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("请输入有效的 octopus://bot/import 分享链接。");
        }
        return CreateBotAsync(host, new CreateRemoteBotRequest("导入的 Bot", host.Agents.FirstOrDefault()?.Id ?? "claude", "#0D9488", null, "通过分享链接导入"), cancellationToken);
    }

    public Task<RemoteBot> UpdateBotAsync(
        RemoteBot bot,
        bool? isPaused = null,
        bool? lookAroundEnabled = null,
        bool? tidyUpMemoryEnabled = null,
        string? agentId = null,
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(name)) bot.Name = name.Trim();
        if (isPaused.HasValue) bot.IsPaused = isPaused.Value;
        if (lookAroundEnabled.HasValue) bot.LookAroundEnabled = lookAroundEnabled.Value;
        if (tidyUpMemoryEnabled.HasValue) bot.TidyUpMemoryEnabled = tidyUpMemoryEnabled.Value;
        if (!string.IsNullOrWhiteSpace(agentId)) bot.AgentId = agentId.Trim();
        return Task.FromResult(bot);
    }

    public Task DeleteBotAsync(RemoteBot bot, CancellationToken cancellationToken = default)
    {
        bot.Host.Bots.Remove(bot);
        return Task.CompletedTask;
    }

    public Task<RemoteBotChat> CreateBotChatAsync(RemoteBot bot, CreateRemoteBotChatRequest request, CancellationToken cancellationToken = default)
    {
        var agentId = string.IsNullOrWhiteSpace(request.AgentId) ? bot.AgentId : request.AgentId;
        var agentName = bot.Host.Agents.FirstOrDefault(a => a.Id == agentId)?.Name ?? agentId;
        var title = string.IsNullOrWhiteSpace(request.Title) ? $"{agentName} Chat" : request.Title.Trim();
        var chat = new RemoteBotChat(bot, "bchat-" + _nextId++, "chat", title)
        {
            AgentId = agentId,
            Subtitle = "新会话已就绪",
            Activity = SessionActivity.Idle,
            IsGui = true,
            IsOpen = true,
            LastActivityAt = DateTimeOffset.Now,
        };
        var idx = bot.Chats.ToList().FindIndex(c => !c.IsHome);
        if (idx < 0) bot.Chats.Add(chat);
        else bot.Chats.Insert(idx, chat);
        return Task.FromResult(chat);
    }

    public Task<RemoteBotChat> OpenBotChatAsync(RemoteBotChat chat, CancellationToken cancellationToken = default)
    {
        chat.IsOpen = true;
        chat.ClosedAt = null;
        chat.LastActivityAt = DateTimeOffset.Now;
        return Task.FromResult(chat);
    }

    public Task CloseBotChatAsync(RemoteBotChat chat, CancellationToken cancellationToken = default)
    {
        chat.IsOpen = false;
        chat.Activity = SessionActivity.Idle;
        chat.ClosedAt = DateTimeOffset.Now;
        return Task.CompletedTask;
    }

    public Task RenameBotChatAsync(RemoteBotChat chat, string? title, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(title)) chat.Title = title.Trim();
        return Task.CompletedTask;
    }

    public Task DeleteBotChatAsync(RemoteBotChat chat, CancellationToken cancellationToken = default)
    {
        if (chat.IsHome) throw new IOException("Home Chat 不能删除。");
        chat.Bot.Chats.Remove(chat);
        return Task.CompletedTask;
    }

    public Task ResolveBotWorkItemAsync(RemoteBot bot, RemoteBotWorkItem item, string? chosenOption = null, CancellationToken cancellationToken = default)
    {
        item.Status = "done";
        if (!string.IsNullOrWhiteSpace(chosenOption)) item.Meta = chosenOption;
        if (bot.Chats.FirstOrDefault(c => c.Id == item.ChatId) is { } chat && chat.Activity == SessionActivity.Waiting)
        {
            chat.Activity = SessionActivity.Idle;
            chat.Subtitle = string.IsNullOrWhiteSpace(chosenOption) ? "已处理" : $"已确认「{chosenOption}」";
        }
        return Task.CompletedTask;
    }

    public Task ToggleBotConnectorAsync(RemoteBot bot, RemoteBotConnector connector, bool enabled, CancellationToken cancellationToken = default)
    {
        connector.Enabled = enabled;
        return Task.CompletedTask;
    }
}
