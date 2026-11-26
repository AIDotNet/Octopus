namespace Octopus.Core.Bots;

/// <summary>A built-in starter template offered when creating a new Bot.</summary>
public sealed record BotTemplateDefinition(
    string Id,
    string DefaultName,
    string EnglishName,
    string DefaultSoulZh,
    string DefaultSoulEn,
    string Color,
    string IconName);

/// <summary>A category in the Bot Connector Catalog dialog ("添加到 &lt;Bot&gt;").</summary>
public sealed record ConnectorCategoryDefinition(
    string Id,
    string NameZh,
    string NameEn,
    string IconName);

/// <summary>An external app or integration available in the Bot Connector Catalog.</summary>
public sealed record ConnectorAppDefinition(
    string Id,
    string Name,
    string CategoryId,
    string DescriptionZh,
    string DescriptionEn,
    string AccentColor,
    string BadgeText,
    string IconName,
    bool ViaComposio = false);

/// <summary>Built-in Bot templates, avatar palette, and connector catalog.</summary>
public static class BotCatalog
{
    public static IReadOnlyList<string> AvatarPalette { get; } =
    [
        "#5b5bd6",
        "#2f9e63",
        "#3b82d6",
        "#c98a12",
        "#d97757",
        "#8e4ec6",
        "#d14343",
        "#0e9aa7",
    ];

    public static IReadOnlyList<BotTemplateDefinition> Templates { get; } =
    [
        new("blank", "", "", "", "", "#5b5bd6", "Sparkles"),
        new(
            "pr",
            "PR 审查员",
            "PR Reviewer",
            "盯住仓库里新开的 PR 和失败的 CI，给出简洁、可执行的审查意见，需要合并或改动前先向我确认。",
            "Watch new pull requests and failing CI runs, post concise actionable reviews, and ask before merging or pushing changes.",
            "#3b82d6",
            "GitPullRequest"),
        new(
            "ci",
            "巡检值班",
            "On-call Watcher",
            "定期检查构建、告警和关键服务健康状态，只在我需要介入时打扰我，平时保持静默。",
            "Monitor builds, alerts, and service health on a schedule; stay quiet when healthy and escalate only when action is needed.",
            "#d97757",
            "Activity"),
        new(
            "daily",
            "日报助手",
            "Daily Digest",
            "汇总昨天合并的提交、还在等我审的 PR 和待处理问题，每天早上给我一份简短清单。",
            "Summarize merged commits, PRs waiting on me, and open blockers into a concise morning digest.",
            "#2f9e63",
            "CalendarClock"),
    ];

    public static IReadOnlyList<ConnectorCategoryDefinition> ConnectorCategories { get; } =
    [
        new("all", "所有应用", "All apps", "LayoutGrid"),
        new("coding", "编程", "Coding", "Code"),
        new("chat", "聊天", "Chat", "MessageSquare"),
        new("monitoring", "监控", "Monitoring", "Activity"),
        new("docs", "文档和项目", "Docs & Projects", "FileText"),
        new("mail", "邮件和会议", "Mail & Meetings", "Mail"),
        new("support", "销售和支持", "Sales & Support", "Store"),
        new("composio", "通过 Composio", "Via Composio", "Plug"),
    ];

    public static IReadOnlyList<ConnectorAppDefinition> ConnectorApps { get; } =
    [
        // Coding (8) — matches reference screenshot
        new("github", "GitHub", "coding", "议题和 PR 中的提及与事件", "Mentions and events in issues and PRs", "#24292e", "GH", "GitBranch"),
        new("vercel", "Vercel", "coding", "查看部署状态并读取构建日志", "Check deployments and read their logs", "#111111", "▲", "Triangle"),
        new("cloudflare", "Cloudflare", "coding", "管理 Workers、DNS 和账户资源", "Manage Workers, DNS and the rest of an account", "#f38020", "CF", "Cloud"),
        new("supabase", "Supabase", "coding", "查询数据库并管理项目", "Query databases and manage projects", "#3ecf8e", "SB", "Database"),
        new("neon", "Neon", "coding", "运行 SQL 并管理 Postgres 分支", "Run SQL and manage Postgres branches", "#00e599", "NE", "Server"),
        new("netlify", "Netlify", "coding", "部署站点并检查构建结果", "Deploy sites and check their builds", "#00c7b7", "NL", "Globe"),
        new("gitlab", "GitLab", "coding", "读取并更新议题与合并请求", "Read and update issues and merge requests", "#fc6d26", "GL", "GitMerge"),
        new("context7", "Context7", "coding", "查询最新文档与代码库示例", "Current docs and examples for code libraries", "#22c55e", "C7", "BookOpen"),

        // Chat (1)
        new("slack", "Slack", "chat", "在频道和私信中接收提及并回复", "Receive mentions and reply in channels and DMs", "#4a154b", "SL", "Hash"),

        // Monitoring (3)
        new("sentry", "Sentry", "monitoring", "跟踪异常告警并分析堆栈", "Track error alerts and inspect stack traces", "#362d59", "SE", "Bug"),
        new("datadog", "Datadog", "monitoring", "查询指标、日志和监控告警", "Query metrics, logs and monitors", "#632ca6", "DD", "BarChart3"),
        new("pagerduty", "PagerDuty", "monitoring", "处理值班告警与事件升级", "Handle on-call incidents and escalations", "#06ac38", "PD", "Bell"),

        // Docs & Projects (3)
        new("linear", "Linear", "docs", "同步议题分配、状态和评论", "Sync assigned issues, statuses and comments", "#5e6ad2", "LN", "CheckSquare"),
        new("notion", "Notion", "docs", "检索与更新知识库和文档页面", "Search and update wiki pages and databases", "#1f1f1f", "NO", "FileText"),
        new("jira", "Jira", "docs", "跟踪工单状态与冲刺任务", "Track tickets and sprint tasks", "#0052cc", "JR", "Kanban"),

        // Mail & Meetings (1)
        new("google", "Google Workspace", "mail", "读取邮件、日历安排与文档", "Work across Gmail, Calendar and Drive", "#4285f4", "G", "Mail"),

        // Sales & Support (1)
        new("intercom", "Intercom", "support", "跟进客户对话并整理工单", "Follow customer conversations and triage tickets", "#286efa", "IC", "Headphones"),
    ];

    public static BotTemplateDefinition FindTemplate(string? id) =>
        Templates.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Templates[0];

    public static ConnectorAppDefinition? FindConnectorApp(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : ConnectorApps.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
}
