using System.Collections.ObjectModel;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Services;
using Octopus.Core;
using Octopus.Core.Plugins;
using Octopus.Plugins;
using Octopus.Plugins.Catalog;
using Octopus.Plugins.Marketplace;
using Octopus.Plugins.Packages;
using Octopus.Plugins.Runtime;
using Octopus.Plugins.Skills;
using Octopus.Storage;
using TKCode.Arc;

namespace Octopus.App.Plugins.Settings;

/// <summary>
/// Settings → Plugins (design/plugins/10-分期与风险.md P1): built-in and installed plugins with their state, what
/// they provide and what they may do (each permission can be switched off), their log, enable / reload / uninstall,
/// installing a package file, and the color themes plugins offer.
/// </summary>
public sealed partial class PluginsSettingsViewModel : ObservableObject, IDisposable
{
    private readonly IPluginCatalog _catalog;
    private readonly IPluginStateStore _state;
    private readonly PluginManager _manager;
    private readonly PluginInstaller _installer;
    private readonly PluginPaths _paths;
    private readonly IWindowAccessor _window;
    private readonly PluginThemes _themes;
    private readonly PluginMarketplaceClient _marketplace;
    private readonly PluginSkillInstaller _skillInstaller;
    private readonly ISettingsStore _settings;
    private readonly IProjectService _projects;
    private bool _loadingThemes;
    private bool _marketLoadedOnce;

    public PluginsSettingsViewModel(
        IPluginCatalog catalog,
        IPluginStateStore state,
        PluginManager manager,
        PluginInstaller installer,
        PluginPaths paths,
        IWindowAccessor window,
        PluginThemes themes,
        Octopus.Plugins.Runtime.Modules.PluginConfigStore config,
        PluginMarketplaceClient marketplace,
        PluginSkillInstaller skillInstaller,
        ISettingsStore settings,
        IProjectService projects)
    {
        Config = config;
        _catalog = catalog;
        _state = state;
        _manager = manager;
        _installer = installer;
        _paths = paths;
        _window = window;
        _themes = themes;
        _marketplace = marketplace;
        _skillInstaller = skillInstaller;
        _settings = settings;
        _projects = projects;
        _catalog.Changed += OnCatalogChanged;
        _state.Changed += OnStateChanged;
        _manager.HostChanged += OnHostChanged;
        _themes.Changed += OnThemesChanged;
        InitMarketCategories();
        Refresh();
        RefreshSkillTargets();
    }

    public ObservableCollection<PluginRowViewModel> Builtin { get; } = [];
    public ObservableCollection<PluginRowViewModel> Installed { get; } = [];
    public ObservableCollection<MarketplacePluginRowViewModel> MarketItems { get; } = [];
    public ObservableCollection<SelectOption> MarketCategories { get; } = [];
    public ObservableCollection<SkillTargetRowViewModel> SkillTargets { get; } = [];
    public bool HasBuiltin => Builtin.Count > 0;
    public bool HasInstalled => Installed.Count > 0;
    public bool HasMarketItems => MarketItems.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstalledTab))]
    [NotifyPropertyChangedFor(nameof(IsMarketplaceTab))]
    [NotifyPropertyChangedFor(nameof(IsSkillsTab))]
    public partial string ActiveTab { get; set; } = "installed";

    public bool IsInstalledTab => ActiveTab == "installed";
    public bool IsMarketplaceTab => ActiveTab == "marketplace";
    public bool IsSkillsTab => ActiveTab == "skills";

    [ObservableProperty] public partial string MarketQuery { get; set; } = "";
    [ObservableProperty] public partial string MarketCategory { get; set; } = "all";
    [ObservableProperty] public partial bool IsMarketLoading { get; private set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMarketError))]
    public partial string? MarketError { get; private set; }
    public bool HasMarketError => !string.IsNullOrWhiteSpace(MarketError);

    [ObservableProperty] public partial string SkillScopeKey { get; set; } = "global";
    [ObservableProperty] public partial string CustomSkillPath { get; set; } = "";
    [ObservableProperty] public partial bool IsSkillInstalling { get; private set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSkillResult))]
    public partial string? SkillInstallSummary { get; private set; }
    public bool HasSkillResult => !string.IsNullOrWhiteSpace(SkillInstallSummary);
    public bool HasCurrentProject => _projects.Current is not null;

    partial void OnActiveTabChanged(string value)
    {
        if (value == "marketplace" && !_marketLoadedOnce)
        {
            _marketLoadedOnce = true;
            _ = RefreshMarketplaceAsync();
        }
        else if (value == "skills")
        {
            RefreshSkillTargets();
        }
    }

    partial void OnMarketCategoryChanged(string value)
    {
        if (IsMarketplaceTab) _ = RefreshMarketplaceAsync();
    }

    partial void OnSkillScopeKeyChanged(string value) => RefreshSkillTargets();

    private string ActiveServerUrl()
    {
        var configured = _settings.Current.Remote.RelayUrl?.Trim().TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        return ServiceEndpoints.OfficialServiceUrl ?? ServiceEndpoints.DefaultOfficialServiceUrl;
    }

    private void InitMarketCategories()
    {
        MarketCategories.Add(new SelectOption("all", PluginText.Get("全部分类", "All categories")));
        MarketCategories.Add(new SelectOption("agent", PluginText.Get("智能体", "Agents")));
        MarketCategories.Add(new SelectOption("editor", PluginText.Get("查看器 / 编辑器", "Editors & Views")));
        MarketCategories.Add(new SelectOption("theme", PluginText.Get("主题", "Themes")));
        MarketCategories.Add(new SelectOption("terminal", PluginText.Get("终端", "Terminal")));
        MarketCategories.Add(new SelectOption("workflow", PluginText.Get("工作流", "Workflow")));
        MarketCategories.Add(new SelectOption("utility", PluginText.Get("工具", "Utilities")));
    }

    [RelayCommand]
    private async Task RefreshMarketplaceAsync()
    {
        IsMarketLoading = true;
        MarketError = null;
        try
        {
            var page = await _marketplace.SearchAsync(
                ActiveServerUrl(),
                query: MarketQuery,
                category: MarketCategory,
                apiVersion: PluginApi.CurrentVersion);
            var installedMap = _catalog.Packages
                .Where(p => p.Status != PluginStatus.Shadowed && p.Manifest is not null)
                .ToDictionary(p => p.Id, p => p.Manifest!.Version, StringComparer.OrdinalIgnoreCase);

            MarketItems.Clear();
            foreach (var entry in page.Items)
            {
                installedMap.TryGetValue(entry.Plugin.Id, out var installedVer);
                MarketItems.Add(new MarketplacePluginRowViewModel(entry, installedVer, this));
            }
            OnPropertyChanged(nameof(HasMarketItems));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            MarketError = PluginText.Get($"无法连接插件市场（{ActiveServerUrl()}）：{ex.Message}", $"Could not reach marketplace ({ActiveServerUrl()}): {ex.Message}");
        }
        finally
        {
            IsMarketLoading = false;
        }
    }

    [RelayCommand]
    private async Task OpenWebMarketplaceAsync()
    {
        if (_window.Window is { } owner)
        {
            await owner.Launcher.LaunchUriAsync(new Uri($"{ActiveServerUrl()}/plugins"));
        }
    }

    internal async Task InstallFromMarketplaceAsync(MarketplacePluginRowViewModel row)
    {
        if (_window.Window is not { } owner) return;
        row.IsBusy = true;
        string? tempFile = null;
        try
        {
            tempFile = await _marketplace.DownloadVerifiedAsync(ActiveServerUrl(), row.Entry.Latest);
            var installed = await StageConfirmAndInstallAsync(owner, tempFile);
            if (installed)
            {
                row.UpdateInstalledState(row.Entry.Latest.Version);
            }
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or PluginPackageException or UnauthorizedAccessException)
        {
            Say(PluginText.Get($"从市场安装失败：{e.Message}", $"Marketplace install failed: {e.Message}"), AlertTone.Danger);
        }
        finally
        {
            row.IsBusy = false;
            if (tempFile is not null)
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            }
        }
    }

    private SkillInstallScope CurrentSkillScope() =>
        SkillScopeKey == "project" && _projects.Current is not null ? SkillInstallScope.ProjectLocal : SkillInstallScope.UserGlobal;

    private void RefreshSkillTargets()
    {
        var scope = CurrentSkillScope();
        var projectRoot = _projects.Current?.Path;
        var statuses = _skillInstaller.InspectTargets(scope, projectRoot);
        var prevSelection = SkillTargets.ToDictionary(r => r.TargetId, r => r.IsSelected, StringComparer.OrdinalIgnoreCase);
        var customRows = SkillTargets.Where(r => r.IsCustom).ToList();

        SkillTargets.Clear();
        foreach (var status in statuses)
        {
            var selected = prevSelection.TryGetValue(status.Spec.Id, out var wasSelected) ? wasSelected : status.Spec.DefaultSelected;
            SkillTargets.Add(new SkillTargetRowViewModel(status, selected));
        }
        foreach (var custom in customRows)
        {
            var resolved = _skillInstaller.ResolveTargetPath(custom.TargetId, scope, projectRoot);
            custom.ResolvedPath = resolved;
            custom.RefreshStatus(Directory.Exists(resolved), PluginSkillInstaller.ReadInstalledVersion(resolved));
            SkillTargets.Add(custom);
        }
    }

    [RelayCommand]
    private void ResetSkillDefaults()
    {
        var scope = CurrentSkillScope();
        var projectRoot = _projects.Current?.Path;
        var statuses = _skillInstaller.InspectTargets(scope, projectRoot);
        SkillTargets.Clear();
        foreach (var status in statuses)
        {
            SkillTargets.Add(new SkillTargetRowViewModel(status, status.Spec.DefaultSelected));
        }
    }

    [RelayCommand]
    private void AddCustomSkillTarget()
    {
        var raw = CustomSkillPath.Trim();
        if (string.IsNullOrWhiteSpace(raw)) return;
        var resolved = _skillInstaller.ResolveTargetPath(raw, CurrentSkillScope(), _projects.Current?.Path);
        if (SkillTargets.Any(r => r.ResolvedPath.Equals(resolved, StringComparison.OrdinalIgnoreCase)))
        {
            CustomSkillPath = "";
            return;
        }
        SkillTargets.Add(new SkillTargetRowViewModel(resolved, PluginSkillInstaller.ReadInstalledVersion(resolved)));
        CustomSkillPath = "";
    }

    [RelayCommand]
    private async Task InstallSkillAsync()
    {
        var selected = SkillTargets.Where(t => t.IsSelected).Select(t => t.TargetId).ToList();
        if (selected.Count == 0)
        {
            Say(PluginText.Get("请至少勾选一个目标 Agent 目录。", "Select at least one target Agent directory."), AlertTone.Warning);
            return;
        }

        IsSkillInstalling = true;
        try
        {
            var result = await _skillInstaller.InstallOverwriteAsync(
                selected,
                CurrentSkillScope(),
                projectRoot: _projects.Current?.Path,
                serverBaseUrl: ActiveServerUrl());

            var lines = result.Results.Select(r =>
            {
                if (!r.Succeeded) return $"✕ {r.TargetPath}: {r.Error}";
                var verb = r.PreviousVersion is null
                    ? PluginText.Get("已安装", "installed")
                    : PluginText.Get($"已覆盖旧版 v{r.PreviousVersion}", $"overwrote v{r.PreviousVersion}");
                return $"✓ {r.TargetPath} ({verb} → v{r.InstalledVersion})";
            });
            SkillInstallSummary = string.Join("\n", lines);
            RefreshSkillTargets();
            var srcLabel = result.Source == "server" ? PluginText.Get("在线最新", "server latest") : PluginText.Get("本地内置", "built-in");
            Say(PluginText.Get(
                $"已覆盖安装 octopus-plugin Skill v{result.SkillVersion}（{srcLabel}）至 {result.SucceededCount}/{result.Results.Count} 个 Agent 目录。",
                $"Overwrote octopus-plugin Skill v{result.SkillVersion} ({srcLabel}) in {result.SucceededCount}/{result.Results.Count} agent directories."),
                result.FailedCount == 0 ? AlertTone.Success : AlertTone.Warning);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Say(PluginText.Get($"安装 Skill 失败：{ex.Message}", $"Skill install failed: {ex.Message}"), AlertTone.Danger);
        }
        finally
        {
            IsSkillInstalling = false;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; private set; }

    [ObservableProperty] public partial AlertTone MessageTone { get; private set; }
    public bool HasMessage => Message is not null;

    // ---------------------------------------------------------------- themes

    /// <summary>The theme choices for one appearance; the empty value is Octopus's own colors.</summary>
    public ObservableCollection<SelectOption> LightThemes { get; } = [];
    public ObservableCollection<SelectOption> DarkThemes { get; } = [];
    public bool HasThemes => LightThemes.Count > 1 || DarkThemes.Count > 1;

    [ObservableProperty] public partial string LightTheme { get; set; } = "";
    [ObservableProperty] public partial string DarkTheme { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThemeProblem))]
    public partial string? ThemeProblem { get; private set; }
    public bool HasThemeProblem => ThemeProblem is not null;

    private void RefreshThemes()
    {
        _loadingThemes = true;
        try
        {
            void Fill(ObservableCollection<SelectOption> target, ThemeAppearance appearance)
            {
                target.Clear();
                target.Add(new SelectOption("", PluginText.Get("Octopus 默认", "Octopus default")));
                foreach (var option in _themes.Available.Where(o => o.Appearance == appearance)) target.Add(new SelectOption(option.Key, option.Label));
            }
            Fill(LightThemes, ThemeAppearance.Light);
            Fill(DarkThemes, ThemeAppearance.Dark);
            LightTheme = _themes.Choice.Light ?? "";
            DarkTheme = _themes.Choice.Dark ?? "";
            ThemeProblem = _themes.Problem;
            OnPropertyChanged(nameof(HasThemes));
        }
        finally
        {
            _loadingThemes = false;
        }
    }

    partial void OnLightThemeChanged(string value)
    {
        if (!_loadingThemes) _themes.Choose(ThemeAppearance.Light, value.Length == 0 ? null : value);
    }

    partial void OnDarkThemeChanged(string value)
    {
        if (!_loadingThemes) _themes.Choose(ThemeAppearance.Dark, value.Length == 0 ? null : value);
    }

    private void OnThemesChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => ThemeProblem = _themes.Problem);

    // ---------------------------------------------------------------- the list

    private void OnCatalogChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Refresh);

    /// <summary>Grants and switches changed: rows update in place, so nothing jumps while the user toggles.</summary>
    private void OnStateChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        foreach (var row in Builtin.Concat(Installed)) row.RefreshState();
    });

    private void OnHostChanged(object? sender, PluginHost host) => Dispatcher.UIThread.Post(() =>
    {
        foreach (var row in Builtin.Concat(Installed).Where(r => r.Id == host.Id)) row.RefreshState();
    });

    public void Refresh()
    {
        RefreshThemes();
        // A copy shadowed by another of the same id is not what runs: only the winner is listed.
        var packages = _catalog.Packages.Where(p => p.Status != PluginStatus.Shadowed).ToList();
        Sync(Builtin, packages.Where(p => p.Source == PluginSource.Builtin));
        Sync(Installed, packages.Where(p => p.Source != PluginSource.Builtin));
        OnPropertyChanged(nameof(HasBuiltin));
        OnPropertyChanged(nameof(HasInstalled));
    }

    private void Sync(ObservableCollection<PluginRowViewModel> rows, IEnumerable<PluginPackage> packages)
    {
        var wanted = packages.Select(p =>
        {
            var existing = rows.FirstOrDefault(r => r.Id == p.Id);
            if (existing is not null && ReferenceEquals(existing.Package, p))
            {
                existing.RefreshState();
                return existing;
            }
            existing?.Detach();
            return new PluginRowViewModel(p, this) { IsExpanded = existing?.IsExpanded ?? false };
        }).OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (rows.SequenceEqual(wanted)) return;
        foreach (var removed in rows.Except(wanted)) removed.Detach();
        rows.Clear();
        foreach (var row in wanted) rows.Add(row);
    }

    internal Octopus.Plugins.Runtime.Modules.PluginConfigStore Config { get; }

    internal PluginHost? HostOf(string id) => _manager.Find(id);

    internal PluginRecord? RecordOf(string id) => _state.Current.Find(id);

    internal string FilesDirectoryOf(string id) => _paths.FilesDirectoryOf(id);

    // ---------------------------------------------------------------- what rows ask for

    internal async Task SetEnabledAsync(PluginRowViewModel row, bool enabled)
    {
        await _manager.SetEnabledAsync(row.Id, enabled);
        row.RefreshState();
    }

    internal async Task ReloadAsync(PluginRowViewModel row)
    {
        row.IsBusy = true;
        try
        {
            await _manager.ReloadAsync(row.Id);
        }
        finally
        {
            row.IsBusy = false;
            row.RefreshState();
        }
    }

    internal void SetPermission(PluginRowViewModel row, PermissionRequest request, bool granted) => _state.Update(d =>
    {
        var record = d.Find(row.Id) ?? (d.Plugins[row.Id] = new PluginRecord { Enabled = row.IsEnabled, Source = row.Package.Source, Version = row.Package.Manifest?.Version });
        if (granted)
        {
            record.Denied.Remove(request.Key);
            record.Grants[request.Key] = request.Details.FirstOrDefault() ?? "granted";
        }
        else
        {
            record.Grants.Remove(request.Key);
            if (!record.Denied.Contains(request.Key)) record.Denied.Add(request.Key);
        }
    });

    internal void SetIncludeSensitive(PluginRowViewModel row, bool include) => _state.Update(d =>
    {
        var record = d.Find(row.Id) ?? (d.Plugins[row.Id] = new PluginRecord { Enabled = row.IsEnabled, Source = row.Package.Source, Version = row.Package.Manifest?.Version });
        record.IncludeSensitiveFiles = include;
    });

    internal void ForgetPickedPaths(PluginRowViewModel row) => _state.Update(d => d.Find(row.Id)?.PathGrants.RemoveAll(g => g.Scope == "picked"));

    internal async Task OpenDirectoryAsync(string path)
    {
        Directory.CreateDirectory(path);
        if (_window.Window is { } owner) await owner.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
    }

    internal async Task UninstallAsync(PluginRowViewModel row)
    {
        if (_window.Window is not { } owner) return;
        var confirmed = await Dialog.ConfirmAsync(owner,
            PluginText.Get($"卸载 {row.Name}？", $"Uninstall {row.Name}?"),
            PluginText.Get("插件和它保存的数据都会删除，它打开的标签会显示为不可用。", "The plugin and the data it saved are removed; its open tabs show as unavailable."),
            PluginText.Get("卸载", "Uninstall"), PluginText.Get("取消", "Cancel"), true);
        if (!confirmed) return;
        await _manager.SetEnabledAsync(row.Id, false);
        _installer.Uninstall(row.Id, removeData: true);
        _state.Update(d => d.Plugins.Remove(row.Id));
        _catalog.Refresh();
        _manager.SyncHosts();
        Say(PluginText.Get($"已卸载 {row.Name}。", $"{row.Name} was uninstalled."));
    }

    private void Say(string message, AlertTone tone = AlertTone.Success)
    {
        MessageTone = tone;
        Message = message;
    }

    [RelayCommand]
    private void DismissMessage() => Message = null;

    [RelayCommand]
    private void Rescan()
    {
        _catalog.Refresh();
        _manager.SyncHosts();
        Say(PluginText.Get($"已重新扫描，共 {Builtin.Count + Installed.Count} 个插件。", $"Scanned again: {Builtin.Count + Installed.Count} plugins."), AlertTone.Info);
    }

    [RelayCommand]
    private Task OpenFolderAsync() => OpenDirectoryAsync(_paths.PluginsDirectory);

    /// <summary>Installs a <c>.octoplugin</c> or <c>.zip</c> package after showing what it asks for; it starts enabled with those permissions.</summary>
    [RelayCommand]
    private async Task InstallAsync()
    {
        if (_window.Window is not { } owner) return;
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = PluginText.Get("选择插件包", "Choose a plugin package"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(PluginText.Get("插件包", "Plugin package")) { Patterns = ["*.octoplugin", "*.zip", "*.octopus-plugin"] }],
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        await StageConfirmAndInstallAsync(owner, path);
    }

    private async Task<bool> StageConfirmAndInstallAsync(Avalonia.Controls.TopLevel owner, string path)
    {
        StagedPlugin staged;
        try
        {
            staged = await Task.Run(() => _installer.Stage(path));
        }
        catch (Exception e) when (e is PluginPackageException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Say(PluginText.Get($"无法读取插件包：{e.Message}", $"The package cannot be read: {e.Message}"), AlertTone.Danger);
            return false;
        }
        if (!staged.CanInstall || staged.Manifest is not { } manifest)
        {
            _installer.Discard(staged);
            Say(PluginText.Get("插件包无效：", "The package is not valid: ") + string.Join("; ", staged.Result.Errors.Select(i => i.ToString())), AlertTone.Danger);
            return false;
        }
        var required = manifest.Permissions.Requests.Where(r => !r.Optional).ToList();
        var lines = required.Count == 0
            ? PluginText.Get("它不需要任何权限。", "It needs no permissions.")
            : PluginText.Get("它需要：\n", "It needs:\n") + string.Join("\n", required.Select(r => $"• {PermissionText.Describe(r)}{(r.Risk == PermissionRisk.High ? PluginText.Get("（高风险）", " (high risk)") : "")}"));
        var verb = staged.IsUpgrade ? PluginText.Get("升级", "Upgrade") : PluginText.Get("安装", "Install");
        var accepted = await Dialog.ConfirmAsync(owner,
            $"{verb} {manifest.Name} {manifest.Version}？",
            $"{manifest.Description}\n\n{lines}\n\n{PluginText.Get("只安装你信任的来源的插件。", "Only install plugins from sources you trust.")}",
            verb, PluginText.Get("取消", "Cancel"), required.Any(r => r.Risk == PermissionRisk.High));
        if (!accepted)
        {
            _installer.Discard(staged);
            return false;
        }
        await _manager.SetEnabledAsync(manifest.Id, false);
        await Task.Run(() => _installer.Install(staged));
        _state.Update(d =>
        {
            var record = d.Find(manifest.Id) ?? (d.Plugins[manifest.Id] = new PluginRecord { InstalledAt = DateTimeOffset.Now });
            record.Source = PluginSource.File;
            record.Version = manifest.Version;
            foreach (var request in required) record.Grants[request.Key] = request.Details.FirstOrDefault() ?? "granted";
        });
        await _manager.SetEnabledAsync(manifest.Id, true);
        Say(PluginText.Get($"已{verb} {manifest.Name}。", $"{manifest.Name}: {verb.ToLowerInvariant()} done."));
        return true;
    }

    public void Dispose()
    {
        _catalog.Changed -= OnCatalogChanged;
        _state.Changed -= OnStateChanged;
        _manager.HostChanged -= OnHostChanged;
        _themes.Changed -= OnThemesChanged;
        foreach (var row in Builtin.Concat(Installed)) row.Detach();
    }
}

/// <summary>A line of "what this plugin adds": a kind and the names.</summary>
public sealed record ContributionLine(string Kind, string Names);

/// <summary>One plugin on the page.</summary>
public sealed partial class PluginRowViewModel : ObservableObject
{
    private readonly PluginsSettingsViewModel _owner;
    private PluginHost? _listening;
    private bool _refreshing;

    public PluginRowViewModel(PluginPackage package, PluginsSettingsViewModel owner)
    {
        Package = package;
        _owner = owner;
        var manifest = package.Manifest;
        Name = manifest is null ? package.Id : manifest.Localized.Pick(PluginText.Language, "name") ?? manifest.Name;
        Description = manifest is null ? null : manifest.Localized.Pick(PluginText.Language, "description") ?? manifest.Description;
        Icon = Rendering.UiIcons.Parse(manifest?.Icon) ?? TKCode.Arc.Icons.Lucide.LucideIconKind.Puzzle;
        Permissions = manifest?.Permissions.Requests.Select(r => new PermissionRowViewModel(this, r)).ToList() ?? [];
        Contributions = manifest is null ? [] : Describe(manifest.Contributes);
        Settings = manifest is null ? [] : [.. manifest.Contributes.Configuration.Select(p => PluginConfigField.Create(owner.Config, manifest, p)).OfType<PluginConfigField>()];
        ReadsFiles = manifest?.Permissions.Requests.Any(r => r.Key.StartsWith("fs.", StringComparison.Ordinal)) ?? false;
        RefreshState();
    }

    public PluginPackage Package { get; }
    public string Id => Package.Id;
    public string Name { get; }
    public string? Description { get; }
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public string Version => Package.Manifest?.Version is { } v ? $"v{v}" : "";
    public TKCode.Arc.Icons.Lucide.LucideIconKind Icon { get; }
    public string Location => Package.Directory;
    public bool CanUninstall => Package.Source == PluginSource.File;
    public bool IsDevelopment => Package.Source == PluginSource.Dev;
    public bool CanToggle => Package.Status is PluginStatus.Enabled or PluginStatus.Disabled;
    public string Issues => string.Join("\n", Package.Issues.Select(i => i.ToString()));
    public bool HasIssues => Package.Issues.Count > 0;
    public IReadOnlyList<ContributionLine> Contributions { get; }
    public bool HasContributions => Contributions.Count > 0;
    /// <summary>The plugin's own settings (<c>contributes.configuration</c>), editable here.</summary>
    public IReadOnlyList<PluginConfigField> Settings { get; }
    public bool HasSettings => Settings.Count > 0;
    public IReadOnlyList<PermissionRowViewModel> Permissions { get; }
    public bool HasPermissions => Permissions.Count > 0;
    public bool HasNoPermissions => Permissions.Count == 0;
    /// <summary>Declares file access, so the "secret-looking files" switch means something.</summary>
    public bool ReadsFiles { get; }

    [ObservableProperty] public partial bool IsExpanded { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }

    /// <summary>The usage plugin's row also manages the Claude status line bridge (a host feature it reads from).</summary>
    public bool HasClaudeConnections => Id == "octopus.agent-usage";

    [ObservableProperty] public partial ClaudeConnectionsViewModel? ClaudeConnections { get; private set; }

    partial void OnIsExpandedChanged(bool value)
    {
        // Created when first shown: looking for configurations scans the home folder (two seconds at most).
        if (value && HasClaudeConnections && ClaudeConnections is null)
        {
            ClaudeConnections = Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<ClaudeConnectionsViewModel>(Composition.AppHost.Services);
        }
    }
    [ObservableProperty] public partial bool IsLogVisible { get; set; }
    [ObservableProperty] public partial string LogText { get; private set; } = "";

    /// <summary>"on" or "off", for the same segmented switch as the agents page.</summary>
    [ObservableProperty] public partial string EnabledKey { get; set; } = "off";
    public bool IsEnabled => EnabledKey == "on";

    [ObservableProperty] public partial string State { get; private set; } = "";
    [ObservableProperty] public partial BadgeTone StateTone { get; private set; }
    [ObservableProperty] public partial bool ShowState { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? LastError { get; private set; }
    public bool HasError => !string.IsNullOrWhiteSpace(LastError);

    [ObservableProperty] public partial bool IncludeSensitiveFiles { get; set; }
    [ObservableProperty] public partial string? PickedText { get; private set; }
    [ObservableProperty] public partial bool HasPickedPaths { get; private set; }

    public void RefreshState()
    {
        _refreshing = true;
        try
        {
            var record = _owner.RecordOf(Id);
            EnabledKey = Package.Status == PluginStatus.Enabled && (record?.Enabled ?? true) ? "on" : "off";
            OnPropertyChanged(nameof(IsEnabled));
            IncludeSensitiveFiles = record?.IncludeSensitiveFiles ?? false;
            var picked = record?.PathGrants.Count(g => g.Scope == "picked") ?? 0;
            HasPickedPaths = picked > 0;
            PickedText = PluginText.Get($"{picked} 个", $"{picked}");
            var host = _owner.HostOf(Id);
            // Only what needs attention gets a badge: a running or idle plugin is the normal case.
            (State, StateTone, ShowState) = (Package.Status, host?.State) switch
            {
                (PluginStatus.Invalid, _) => (PluginText.Get("清单有误", "Invalid"), BadgeTone.Danger, true),
                (PluginStatus.Incompatible, _) => (PluginText.Get("版本不兼容", "Incompatible"), BadgeTone.Warning, true),
                (PluginStatus.Disabled, _) => ("", BadgeTone.Neutral, false),
                (_, PluginHostState.Running) => (PluginText.Get("运行中", "Running"), BadgeTone.Success, true),
                (_, PluginHostState.Activating) => (PluginText.Get("启动中", "Starting"), BadgeTone.Info, true),
                (_, PluginHostState.Crashed) => (PluginText.Get("崩溃，正在重启", "Crashed, restarting"), BadgeTone.Warning, true),
                (_, PluginHostState.Stopped) => (PluginText.Get("已停止", "Stopped"), BadgeTone.Danger, true),
                (_, PluginHostState.Failed) => (PluginText.Get("启动失败", "Failed to start"), BadgeTone.Danger, true),
                _ => (PluginText.Get("待命", "Idle"), BadgeTone.Neutral, true),
            };
            if (IsDevelopment && !ShowState) (State, StateTone, ShowState) = (PluginText.Get("开发", "Development"), BadgeTone.Info, true);
            LastError = host?.State is PluginHostState.Running ? null : host?.LastError;
            // From the saved state, not the running host: a disabled plugin shows its grants too.
            var grants = Package.Manifest is { } manifest ? new PluginGrants(manifest, record, Package.Source == PluginSource.Builtin) : null;
            foreach (var permission in Permissions) permission.Refresh(grants);
            if (IsLogVisible) RefreshLog();
        }
        finally
        {
            _refreshing = false;
        }
    }

    partial void OnEnabledKeyChanged(string value)
    {
        if (!_refreshing) _ = _owner.SetEnabledAsync(this, value == "on");
    }

    partial void OnIncludeSensitiveFilesChanged(bool value)
    {
        if (!_refreshing) _owner.SetIncludeSensitive(this, value);
    }

    partial void OnIsLogVisibleChanged(bool value)
    {
        if (value) RefreshLog();
        Listen(value ? _owner.HostOf(Id) : null);
    }

    internal void SetPermission(PermissionRequest request, bool granted) => _owner.SetPermission(this, request, granted);

    private void RefreshLog()
    {
        var host = _owner.HostOf(Id);
        LogText = host is null || host.Log.Count == 0
            ? PluginText.Get("还没有日志。", "Nothing logged yet.")
            : string.Join("\n", host.Log.TakeLast(300).Select(l => $"{l.Time:HH:mm:ss} {l.Level.ToString().ToUpperInvariant(),-5} {l.Text}"));
    }

    /// <summary>While the log is open it follows new lines.</summary>
    private void Listen(PluginHost? host)
    {
        if (_listening is not null) _listening.Logged -= OnLogged;
        _listening = host;
        if (host is not null) host.Logged += OnLogged;
    }

    private void OnLogged(object? sender, PluginLogEntry e) => Dispatcher.UIThread.Post(RefreshLog);

    internal void Detach() => Listen(null);

    private static string AgentName(string id)
    {
        try { return Composition.AppHost.Get<Octopus.Agents.IAgentCatalog>().Find(id)?.Name ?? id; }
        catch (InvalidOperationException) { return id; }
    }

    private static List<ContributionLine> Describe(PluginContributions c)
    {
        var lines = new List<ContributionLine>();
        void Add(string zh, string en, IEnumerable<string> names)
        {
            var list = names.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            if (list.Count == 0) return;
            var shown = string.Join("、", list.Take(4)) + (list.Count > 4 ? PluginText.Get($" 等 {list.Count} 项", $" and {list.Count - 4} more") : "");
            lines.Add(new ContributionLine(PluginText.Get(zh, en), shown));
        }
        var language = PluginText.Language;
        Add("标签", "Tabs", c.Views.Select(v => v.Localized.Pick(language, "title") ?? v.Title));
        Add("打开方式", "Opens files", c.Editors.Select(e => e.Localized.Pick(language, "title") ?? e.Title));
        Add("指示器", "Indicators", c.Indicators.Select(i => i.Localized.Pick(language, "title") ?? i.Title ?? i.Id));
        Add("用量", "Usage", c.UsageProviders.Select(u => AgentName(u.Agent)));
        Add("命令", "Commands", c.Commands.Select(m => m.Localized.Pick(language, "title") ?? m.Title));
        Add("主题", "Themes", c.Themes.Select(t => t.Localized.Pick(language, "label") ?? t.Label));
        Add("面板", "Panels", c.Panels.Select(p => p.Localized.Pick(language, "title") ?? p.Title ?? p.Id));
        return lines;
    }

    [RelayCommand] private void ToggleExpanded() => IsExpanded = !IsExpanded;
    [RelayCommand] private void ToggleLog() => IsLogVisible = !IsLogVisible;
    [RelayCommand] private Task ReloadAsync() => _owner.ReloadAsync(this);
    [RelayCommand] private Task UninstallAsync() => _owner.UninstallAsync(this);
    [RelayCommand] private void ForgetPickedPaths() => _owner.ForgetPickedPaths(this);
    [RelayCommand] private Task OpenDataAsync() => _owner.OpenDirectoryAsync(_owner.FilesDirectoryOf(Id));
}

/// <summary>One permission of a plugin, with a switch.</summary>
public sealed partial class PermissionRowViewModel : ObservableObject
{
    private readonly PluginRowViewModel _row;
    private readonly PermissionRequest _request;
    private bool _refreshing;

    public PermissionRowViewModel(PluginRowViewModel row, PermissionRequest request)
    {
        _row = row;
        _request = request;
        Title = PermissionText.Describe(request);
        Risk = request.Risk switch
        {
            PermissionRisk.High => PluginText.Get("高风险", "High risk"),
            PermissionRisk.Medium => PluginText.Get("中风险", "Medium risk"),
            _ => PluginText.Get("低风险", "Low risk"),
        };
        RiskTone = request.Risk switch { PermissionRisk.High => BadgeTone.Danger, PermissionRisk.Medium => BadgeTone.Warning, _ => BadgeTone.Neutral };
        Reason = request.Localized?.Pick(PluginText.Language, "reason") ?? request.Reason;
        IsOptional = request.Optional;
    }

    public string Title { get; }
    public string Risk { get; }
    public BadgeTone RiskTone { get; }
    /// <summary>Why the plugin asks, in its own words (shown as the plugin wrote it).</summary>
    public string? Reason { get; }
    public bool HasReason => !string.IsNullOrWhiteSpace(Reason);
    public bool IsOptional { get; }
    public string Key => _request.Key;

    [ObservableProperty] public partial bool IsGranted { get; set; }

    internal void Refresh(PluginGrants? grants)
    {
        _refreshing = true;
        try { IsGranted = grants?.Has(_request.Key) ?? false; }
        finally { _refreshing = false; }
    }

    partial void OnIsGrantedChanged(bool value)
    {
        if (!_refreshing) _row.SetPermission(_request, value);
    }
}
