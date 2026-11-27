namespace Octopus.App.Plugins.Settings;

/// <summary>
/// The fixed texts of Settings → Plugins, for <c>x:Static</c> in XAML. Read when the page is built (the settings
/// page is created again when it is opened), so a language change shows on the next visit.
/// </summary>
public static class PluginStrings
{
    public static string Title => PluginText.Get("插件", "Plugins");
    public static string Subtitle => PluginText.Get(
        "插件在隔离的 JavaScript 引擎里运行，只能使用你授予的权限。插件系统处于预览阶段，接口可能变化。",
        "Plugins run in an isolated JavaScript engine and can only use what you grant. The plugin system is a preview; its API may change.");
    public static string Install => PluginText.Get("从文件安装…", "Install from file…");
    public static string InstallHint => PluginText.Get("安装 .zip 插件包。只安装你信任的来源的插件。", "Install a .zip package. Only install plugins from sources you trust.");
    public static string Rescan => PluginText.Get("重新扫描", "Rescan");
    public static string OpenFolder => PluginText.Get("打开插件目录", "Open plugin folder");
    public static string Builtin => PluginText.Get("内置", "Built-in");
    public static string Installed => PluginText.Get("已安装", "Installed");
    public static string NoneInstalled => PluginText.Get("还没有安装其他插件。", "No other plugins are installed.");
    public static string Themes => PluginText.Get("颜色主题", "Color themes");
    public static string ThemesHint => PluginText.Get("插件提供的配色，浅色和深色外观各选一套。", "Color schemes from plugins, one for each appearance.");
    public static string NoThemes => PluginText.Get("已启用的插件没有提供主题。", "No enabled plugin provides a theme.");
    public static string LightTheme => PluginText.Get("浅色外观", "Light appearance");
    public static string DarkTheme => PluginText.Get("深色外观", "Dark appearance");
    public static string ThemeRefused => PluginText.Get("主题没有应用", "The theme was not applied");
    public static string Provides => PluginText.Get("提供", "Provides");
    public static string Permissions => PluginText.Get("权限", "Permissions");
    public static string Settings => PluginText.Get("设置", "Settings");
    public static string AddTag => PluginText.Get("输入后按回车添加", "Type and press Enter");
    public static string Test => PluginText.Get("测试", "Test");
    public static string ClaudeConnect => PluginText.Get("Claude 用量接入", "Claude usage connection");
    public static string ClaudeConnectHint => PluginText.Get(
        "接入后，这个配置里所有 Claude Code 会话（包括不在 Octopus 里启动的）都会报告 5 小时和 7 天用量。做法是把状态行换成 Octopus 的转接脚本：它记下 Claude Code 交给状态行的数据，再运行你原来的状态行，看起来不变。不读取登录凭据，随时可以恢复。",
        "Once connected, every Claude Code session of a configuration (also those not started in Octopus) reports its 5-hour and weekly usage. Octopus sets a bridge script as the status line: it keeps the data Claude Code passes to the status line and then runs your own status line, which looks the same. No sign-in credential is read; restore it at any time.");
    public static string ClaudeNone => PluginText.Get("没有找到 Claude Code 的配置目录。", "No Claude Code configuration was found.");
    public static string Connect => PluginText.Get("接入", "Connect");
    public static string ConnectAll => PluginText.Get("全部接入", "Connect all");
    public static string Restore => PluginText.Get("恢复", "Restore");
    public static string Rescan2 => PluginText.Get("重新查找", "Look again");
    public static string CommandPlaceholder => PluginText.Get("例如 ~/bin/claude-usage --json", "e.g. ~/bin/claude-usage --json");
    public static string CommandNote => PluginText.Get(
        "用你的默认 shell 运行，最长 30 秒。插件看不到也改不了这条命令，只能请 Octopus 运行它并拿到输出。",
        "Runs in your default shell for at most 30 seconds. The plugin cannot see or change this command; it can only have Octopus run it and read the output.");
    public static string LogTitle => PluginText.Get("插件日志（最近 300 行）", "Plugin log (last 300 lines)");
    public static string Copy => PluginText.Get("复制", "Copy");
    public static string PermissionsHint => PluginText.Get("关掉的权限立即生效；插件下次用到时会收到拒绝。", "Switching a permission off applies at once; the plugin is refused the next time it asks.");
    public static string NoPermissions => PluginText.Get("这个插件不需要任何权限。", "This plugin needs no permissions.");
    public static string Sensitive => PluginText.Get("类密钥文件", "Secret-looking files");
    public static string SensitiveHint => PluginText.Get("允许读取 .env、*.pem 这类文件。其他程序的登录凭据始终不可读。", "Allow files such as .env and *.pem. Other programs' sign-in credentials are never readable.");
    public static string Picked => PluginText.Get("选过的文件", "Picked files");
    public static string PickedHint => PluginText.Get("你在这个插件的对话框里选过的文件和文件夹，它可以继续访问。", "Files and folders you chose in this plugin's dialogs stay accessible to it.");
    public static string Forget => PluginText.Get("全部收回", "Revoke all");
    public static string Reload => PluginText.Get("重新加载", "Reload");
    public static string ShowLog => PluginText.Get("日志", "Log");
    public static string OpenData => PluginText.Get("数据目录", "Data folder");
    public static string Uninstall => PluginText.Get("卸载", "Uninstall");
    public static string LastError => PluginText.Get("最近的错误", "Last error");
    public static string Issues => PluginText.Get("清单有问题", "Manifest issues");
    public static string Details => PluginText.Get("详情", "Details");
    public static string Id => PluginText.Get("标识", "Id");
    public static string Location => PluginText.Get("位置", "Location");

    // Marketplace & Multi-Agent Skills Installer
    public static string TabInstalled => PluginText.Get("已安装", "Installed");
    public static string TabMarketplace => PluginText.Get("发现市场", "Marketplace");
    public static string TabSkills => PluginText.Get("开发 Skill", "Authoring Skill");
    public static string SkillButtonTip => PluginText.Get(
        "一键向 Claude Code、Cursor、Codex、.agents 等编程智能体安装/覆盖插件开发 Skill",
        "Install or overwrite the plugin authoring Skill into Claude Code, Cursor, Codex, .agents, and more");
    public static string MarketSearchPlaceholder => PluginText.Get("搜索插件市场（名称、ID、发布者、标签）…", "Search marketplace (name, id, publisher, tags)…");
    public static string MarketRefresh => PluginText.Get("刷新市场", "Refresh");
    public static string MarketOpenWeb => PluginText.Get("在网页打开市场 / 上传插件", "Open web marketplace / upload");
    public static string MarketEmpty => PluginText.Get("没有找到匹配的插件。", "No matching plugins found in the marketplace.");
    public static string MarketEmptyHint => PluginText.Get("试试更换关键词、清空筛选条件，或刷新插件市场。", "Try another keyword, reset filters, or refresh the marketplace.");
    public static string MarketResetFilters => PluginText.Get("重置筛选条件", "Reset filters");
    public static string MarketFeaturedHeading => PluginText.Get("编辑精选", "Featured");
    public static string MarketFeaturedHint => PluginText.Get("高频装机推荐", "Recommended picks");
    public static string MarketAllHeading => PluginText.Get("全部插件", "All plugins");
    public static string MarketInstall => PluginText.Get("安装", "Install");
    public static string MarketUpgrade => PluginText.Get("升级", "Upgrade");
    public static string MarketInstalledBadge => PluginText.Get("已安装", "Installed");
    public static string MarketDetailRisk => PluginText.Get("风险等级", "Risk level");
    public static string MarketDetailStats => PluginText.Get("下载量 / 体积", "Downloads / Size");
    public static string MarketDetailMeta => PluginText.Get("更新 / 协议", "Updated / License");
    public static string MarketDetailPermissions => PluginText.Get("所需权限", "Permissions");
    public static string MarketDetailPermissionsHint => PluginText.Get("安装后可在「已安装」随时开关", "Can be toggled individually after install");
    public static string MarketDetailNoPermissions => PluginText.Get("该插件不需要任何敏感权限，运行于纯沙盒模式。", "This plugin requires no sensitive permissions and runs purely sandboxed.");
    public static string MarketDetailChangelog => PluginText.Get("更新说明", "Changelog");
    public static string MarketDetailTags => PluginText.Get("标签", "Tags");
    public static string MarketDetailHomepage => PluginText.Get("打开主页", "Open homepage");
    public static string MarketDetailShaNote => PluginText.Get("下载时自动执行 SHA-256 完整性校验", "SHA-256 checksum verified automatically on download");
    public static string Close => PluginText.Get("关闭", "Close");
    public static string SkillHeading => PluginText.Get("安装插件开发 Skill", "Install plugin authoring Skill");
    public static string SkillSubtitle => PluginText.Get(
        "将官方插件开发规范、API 参考与脚手架模板写入所选 AI 智能体目录；每次安装都会强制覆盖旧版目录，杜绝过时文档残留。",
        "Writes the official plugin authoring guide, API reference, and templates into the selected AI coding agent directories. Every install strictly overwrites any previous version.");
    public static string SkillScopeGlobal => PluginText.Get("用户全局 (~)", "User global (~)");
    public static string SkillScopeProject => PluginText.Get("当前项目 (./)", "Current project (./)");
    public static string SkillCustomPlaceholder => PluginText.Get("添加自定义 Skills 目录（例如 ~/.my-agent/skills）", "Add custom skills folder (e.g. ~/.my-agent/skills)");
    public static string SkillAddCustom => PluginText.Get("添加目录", "Add folder");
    public static string SkillInstallNow => PluginText.Get("覆盖安装到所选", "Overwrite & install");
    public static string SkillResetDefaults => PluginText.Get("恢复默认勾选", "Reset defaults");
    public static string SkillSourceNote => PluginText.Get("优先拉取服务器最新校验包，离线自动回退内置版本", "Fetches latest verified bundle from server; falls back to built-in offline");
}
