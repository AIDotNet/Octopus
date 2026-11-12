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
}
