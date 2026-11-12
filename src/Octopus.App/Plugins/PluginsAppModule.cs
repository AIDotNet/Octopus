using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Octopus.App.Services;
using Octopus.Core.Plugins;
using Octopus.Core.Projects;
using Octopus.Plugins;
using Octopus.Plugins.Catalog;
using Octopus.Plugins.Jaunt;
using Octopus.Plugins.Runtime;
using Octopus.Plugins.Runtime.Modules;
using Octopus.Plugins.Usage;
using Octopus.Storage;
using TKCode.Arc;

namespace Octopus.App.Plugins;

/// <summary>Registers the plugin system: catalog, runtime, host modules and the app's own plugin services.</summary>
internal static class PluginsAppModule
{
    /// <summary>Directories listed here (separated like PATH) load as development plugins (design/plugins/03 §5).</summary>
    public const string DevPluginsVariable = "OCTOPUS_DEV_PLUGINS";

    /// <summary>
    /// Next to the executable; in a macOS app bundle they sit in Contents/Resources instead, because codesign treats
    /// everything under Contents/MacOS as code that must be signed (tools/package-macos.sh moves them there).
    /// </summary>
    private static string[] BuiltinRoots()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "BuiltinPlugins");
        var bundle = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "BuiltinPlugins"));
        return OperatingSystem.IsMacOS() && Directory.Exists(bundle) ? [bundle] : [beside];
    }

    public static void Register(IServiceCollection services)
    {
        services.AddOctopusPlugins(AppVersion(), new PluginCatalogOptions
        {
            BuiltinRoots = BuiltinRoots(),
            DevDirectories = () => (Environment.GetEnvironmentVariable(DevPluginsVariable) ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        });
        services.AddSingleton<IPluginRuntimeFactory, JauntPluginRuntimeFactory>();
        services.AddSingleton<UsageRegistry>();
        services.AddSingleton<PluginConfigStore>();
        services.AddSingleton<Octopus.Agents.IAgentDefinitionSource, PluginAgentSource>();
        services.AddSingleton<AppProjectContext>();
        services.AddSingleton<IPluginProjectContext>(p => p.GetRequiredService<AppProjectContext>());
        services.AddSingleton<IPluginConsent, AppPluginConsent>();

        // Host modules: what plugins can call (design/plugins/09-API参考.md).
        services.AddSingleton<IPluginApiModule, EnvModule>();
        services.AddSingleton<IPluginApiModule, StorageModule>();
        services.AddSingleton<IPluginApiModule, SecretsModule>();
        services.AddSingleton<IPluginApiModule, ConfigModule>();
        services.AddSingleton<IPluginApiModule, UsageModule>();
        services.AddSingleton<IPluginApiModule, ProjectsModule>();
        services.AddSingleton<IPluginApiModule, GitModule>();
        services.AddSingleton<IPluginApiModule, AgentsListModule>();
        services.AddSingleton<IPluginApiModule, ProcessModule>();
        services.AddSingleton<IPluginApiModule, NetModule>();
        services.AddSingleton<IPluginApiModule, FsModule>();
        services.AddSingleton<IPluginApiModule, AgentSignalsModule>();
        services.AddSingleton<IPickedPathGrants, PickedPathGrants>();
        services.AddSingleton<WorkbenchModule>();
        services.AddSingleton<IPluginApiModule>(p => p.GetRequiredService<WorkbenchModule>());

        services.AddSingleton(p => new PluginManager(
            p.GetRequiredService<IPluginCatalog>(),
            p.GetRequiredService<IPluginStateStore>(),
            p.GetRequiredService<IPluginRuntimeFactory>(),
            p.GetServices<IPluginApiModule>(),
            p.GetRequiredService<PluginPaths>(),
            consent: p.GetRequiredService<IPluginConsent>()));
        services.AddSingleton<PluginWorkbench>();
        services.AddSingleton<PluginIndicators>();
        services.AddSingleton<PluginThemes>();
    }

    private static SemanticVersion AppVersion()
    {
        var text = typeof(PluginsAppModule).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return SemanticVersion.TryParse(text, out var version) ? version : new SemanticVersion(0, 0, 0);
    }
}

/// <summary>The projects as plugins see them: a snapshot taken on the UI thread, read from the plugins' threads.</summary>
public sealed class AppProjectContext : IPluginProjectContext
{
    private readonly IProjectService _projects;
    private volatile Snapshot _snapshot = new(null, []);

    public AppProjectContext(IProjectService projects)
    {
        _projects = projects;
        projects.Changed += (_, e) =>
        {
            if (e.Kind is ProjectChangeKind.Projects or ProjectChangeKind.CurrentProject) Refresh();
        };
        Refresh();
    }

    private sealed record Snapshot(PluginProjectInfo? Current, IReadOnlyList<PluginProjectInfo> All);

    public PluginProjectInfo? Current => _snapshot.Current;
    public IReadOnlyList<PluginProjectInfo> All => _snapshot.All;
    public event EventHandler? CurrentChanged;

    private void Refresh()
    {
        var all = _projects.Projects.Select(Info).ToList();
        var current = _projects.Current is { } p ? Info(p) : null;
        var changed = current?.Id != _snapshot.Current?.Id;
        _snapshot = new Snapshot(current, all);
        if (changed) CurrentChanged?.Invoke(this, EventArgs.Empty);
    }

    private static PluginProjectInfo Info(Project project) => new(project.Id, project.Name, project.Path, project.Color);
}

/// <summary>Asks the user, in a dialog that names the plugin and the risk, for a permission requested at run time.</summary>
public sealed class AppPluginConsent(IWindowAccessor window) : IPluginConsent
{
    public async Task<bool> RequestPermissionAsync(PluginHost plugin, PermissionRequest request, string? reason, CancellationToken cancellationToken)
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (window.Window is not { } owner) return false;
            var name = PluginWorkbench.NameOf(plugin);
            var what = PermissionText.Describe(request);
            var risk = request.Risk switch
            {
                PermissionRisk.High => PluginText.Get("高风险", "High risk"),
                PermissionRisk.Medium => PluginText.Get("中风险", "Medium risk"),
                _ => PluginText.Get("低风险", "Low risk"),
            };
            var description = PluginText.Get(
                $"{name} 请求：{what}（{risk}）",
                $"{name} asks for: {what} ({risk})");
            if (!string.IsNullOrWhiteSpace(reason)) description += "\n\n" + PluginText.Get("理由（由插件提供）：", "Reason (given by the plugin): ") + reason;
            return await Dialog.ConfirmAsync(owner, PluginText.Get("插件权限请求", "Plugin permission"), description,
                PluginText.Get("允许", "Allow"), PluginText.Get("拒绝", "Deny"), request.Risk == PermissionRisk.High);
        });
    }
}

/// <summary>Plain-language names of permissions for consent dialogs and the plugin settings page.</summary>
internal static class PermissionText
{
    public static string Describe(PermissionRequest request)
    {
        var key = request.Key;
        var details = request.Details;
        string List(IEnumerable<string> items) => string.Join(PluginText.Get("、", ", "), items);
        if (key.StartsWith("fs.agentData.", StringComparison.Ordinal))
        {
            var agent = AgentName(key["fs.agentData.".Length..]);
            return PluginText.Get($"读取 {agent} 的本地记录", $"Local records of {agent}");
        }
        if (key.StartsWith("agents.signals.", StringComparison.Ordinal) && details.Count == 2)
        {
            return PluginText.Get($"接收 {AgentName(details[0])} 的{Channel(details[1])}", $"{Channel(details[1])} from {AgentName(details[0])}");
        }
        if (key.StartsWith("agents.probes.", StringComparison.Ordinal) && details.Count == 2)
        {
            return PluginText.Get($"向 {AgentName(details[0])} 查询{Probe(details[1])}", $"Ask {AgentName(details[0])} for {Probe(details[1])}");
        }
        return key switch
        {
            "fs.project" when details.FirstOrDefault() == "readwrite" => PluginText.Get("读写当前项目的文件", "Read and change files of the open project"),
            "fs.project" => PluginText.Get("读取当前项目的文件", "Read files of the open project"),
            "fs.external" => PluginText.Get($"访问 {details.FirstOrDefault()}", $"Access {details.FirstOrDefault()}"),
            "git" => PluginText.Get("读取 Git 状态与历史", "Read Git status and history"),
            "projects" => PluginText.Get("查看项目和标签列表", "See projects and tabs"),
            "workspace" => PluginText.Get("打开、切换标签", "Open and switch tabs"),
            "settings" => PluginText.Get("读取外观与语言设置", "Read appearance and language settings"),
            "notifications" => PluginText.Get("显示通知", "Show notifications"),
            "theme" => PluginText.Get("更改配色", "Change the colors"),
            "secrets" => PluginText.Get("保存自己的密钥", "Keep its own secrets"),
            "clipboard.read" => PluginText.Get("读取剪贴板", "Read the clipboard"),
            "clipboard.write" => PluginText.Get("写入剪贴板", "Write to the clipboard"),
            "terminal.read" => PluginText.Get("读取终端内容", "Read terminal output"),
            "terminal.write" => PluginText.Get("向终端输入", "Type into terminals"),
            "terminal.create" => PluginText.Get("新建终端", "Create terminals"),
            "net" => PluginText.Get($"访问网络：{List(details)}", $"Network: {List(details)}"),
            "shell.open" => PluginText.Get($"在浏览器中打开 {List(details)}", $"Open {List(details)} in the browser"),
            "process.run" => PluginText.Get($"运行程序：{List(details)}", $"Run programs: {List(details)}"),
            "agents.launch" => PluginText.Get("启动智能体", "Launch agents"),
            "unsafeEval" => PluginText.Get("运行动态生成的代码", "Run generated code"),
            _ => key,
        };
    }

    /// <summary>The agent's name from the catalog (Claude Code, Codex CLI…); the id when it is not known.</summary>
    private static string AgentName(string id)
    {
        try
        {
            return Composition.AppHost.Get<Octopus.Agents.IAgentCatalog>().Find(id)?.Name ?? id;
        }
        catch (InvalidOperationException)
        {
            return id;
        }
    }

    private static string Channel(string channel) => channel switch
    {
        "statusline" => PluginText.Get("状态行（用量、成本）", "status line (usage, cost)"),
        "session-usage" => PluginText.Get("会话用量", "session usage"),
        "session-start" => PluginText.Get("会话开始", "session start"),
        "session-end" => PluginText.Get("会话结束", "session end"),
        "session-busy" or "session-idle" or "session-error" => PluginText.Get("会话状态", "session state"),
        "prompt-submit" => PluginText.Get("提交的提示词", "submitted prompts"),
        "permission-request" => PluginText.Get("权限请求", "permission requests"),
        "notification" => PluginText.Get("通知", "notifications"),
        "stop" or "turn-complete" => PluginText.Get("回复完成", "finished replies"),
        "stop-failure" => PluginText.Get("回复失败", "failed replies"),
        _ => channel,
    };

    private static string Probe(string probe) => probe switch
    {
        "rateLimits" => PluginText.Get("额度", "its quota"),
        "account" => PluginText.Get("订阅档位", "its plan"),
        "sessionUsage" => PluginText.Get("会话用量", "session usage"),
        _ => probe,
    };
}

/// <summary>A path the user picked in a dialog a plugin opened becomes part of that plugin's grants (design 04 §3.5).</summary>
public sealed class PickedPathGrants(IPluginStateStore state) : IPickedPathGrants
{
    public const int MaxPicked = 50;

    public void Grant(PluginHost plugin, string path, bool folder, bool writable)
    {
        var access = writable ? FileAccessKind.ReadWrite : FileAccessKind.Read;
        state.Update(d =>
        {
            var record = d.Find(plugin.Id) ?? (d.Plugins[plugin.Id] = new PluginRecord { Enabled = true, Source = plugin.Package.Source, Version = plugin.Manifest.Version });
            record.PathGrants.RemoveAll(g => g.Scope == "picked" && g.Path == path);
            record.PathGrants.Add(new PluginPathGrant("picked", path, access));
            // The oldest picks go first: a plugin cannot collect a growing share of the disk one dialog at a time.
            var picked = record.PathGrants.Where(g => g.Scope == "picked").ToList();
            foreach (var old in picked.Take(Math.Max(0, picked.Count - MaxPicked))) record.PathGrants.Remove(old);
        });
    }
}
