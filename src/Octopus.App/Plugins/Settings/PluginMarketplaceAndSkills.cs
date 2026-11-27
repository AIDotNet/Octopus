using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Plugins.Marketplace;
using Octopus.Plugins.Skills;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace Octopus.App.Plugins.Settings;

/// <summary>One clickable category pill in the Marketplace filter bar.</summary>
public sealed partial class MarketplaceCategoryChipViewModel : ObservableObject
{
    private readonly Action<string> _select;

    public MarketplaceCategoryChipViewModel(string value, string label, Action<string> select)
    {
        Value = value;
        Label = label;
        _select = select;
    }

    public string Value { get; }
    public string Label { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCount))]
    public partial int Count { get; set; }

    public bool HasCount => Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonVariant))]
    public partial bool IsSelected { get; set; }

    public ButtonVariant ButtonVariant => IsSelected ? ButtonVariant.Primary : ButtonVariant.Secondary;

    [RelayCommand]
    private void Select() => _select(Value);
}

/// <summary>One plugin card fetched from the Octopus Plugin Marketplace.</summary>
public sealed partial class MarketplacePluginRowViewModel : ObservableObject
{
    private readonly PluginsSettingsViewModel _owner;

    public MarketplacePluginRowViewModel(
        MarketplaceCatalogEntry entry,
        string? installedVersion,
        Bitmap? localIconBitmap,
        PluginsSettingsViewModel owner)
    {
        Entry = entry;
        _owner = owner;
        var isZh = PluginText.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        Name = (isZh && !string.IsNullOrWhiteSpace(entry.Plugin.LocalizedNameZh) ? entry.Plugin.LocalizedNameZh : entry.Plugin.Name)!;
        Description = (isZh && !string.IsNullOrWhiteSpace(entry.Plugin.LocalizedDescriptionZh) ? entry.Plugin.LocalizedDescriptionZh : entry.Plugin.Description)!;
        PublisherSlug = entry.Plugin.PublisherSlug;
        PublisherVerified = entry.Plugin.PublisherVerified;
        PublisherLabel = entry.Plugin.PublisherVerified ? $"{entry.Plugin.PublisherSlug} ✓" : entry.Plugin.PublisherSlug;
        Category = string.IsNullOrWhiteSpace(entry.Plugin.Category) ? "utility" : entry.Plugin.Category;
        CategoryLabel = FormatCategory(Category);
        FeaturedKicker = PluginText.Get($"推荐插件 · {CategoryLabel}", $"Featured · {CategoryLabel}");
        VersionLabel = $"v{entry.Latest.Version}";
        DownloadsLabel = $"↓ {FormatDownloads(entry.Plugin.Downloads)}";
        Icon = Rendering.UiIcons.Parse(entry.Plugin.Icon) ?? DefaultIconForCategory(Category);
        IconBitmap = localIconBitmap;

        var riskLevel = entry.Latest.RiskLevel?.ToLowerInvariant() ?? "low";
        IsHighRisk = riskLevel == "high";
        IsMediumRisk = riskLevel == "medium";
        IsLowRisk = !IsHighRisk && !IsMediumRisk;

        RiskLabel = riskLevel switch
        {
            "high" => PluginText.Get("高风险", "High risk"),
            "medium" => PluginText.Get("中风险", "Medium risk"),
            _ => PluginText.Get("低风险", "Low risk"),
        };
        RiskShortLabel = entry.Latest.Permissions.Count == 0
            ? PluginText.Get("零权限", "0 perms")
            : RiskLabel;
        RiskTone = riskLevel switch
        {
            "high" => BadgeTone.Danger,
            "medium" => BadgeTone.Warning,
            _ => BadgeTone.Success,
        };
        Permissions = entry.Latest.Permissions;
        PermissionsSummary = entry.Latest.Permissions.Count == 0
            ? PluginText.Get("零权限", "0 permissions")
            : PluginText.Get($"{entry.Latest.Permissions.Count} 项权限", $"{entry.Latest.Permissions.Count} permissions");
        PermissionsHeading = PluginText.Get($"所需权限（{entry.Latest.Permissions.Count}）", $"Permissions ({entry.Latest.Permissions.Count})");
        StatsSummary = $"{DownloadsLabel} · {FormatSize(entry.Latest.PackageSize)}";
        MetaSummary = $"{entry.Plugin.UpdatedAt.ToLocalTime():yyyy-MM-dd} · {(string.IsNullOrWhiteSpace(entry.Plugin.License) ? "—" : entry.Plugin.License)}";
        Tags = entry.Plugin.Tags;
        Changelog = string.IsNullOrWhiteSpace(entry.Latest.Changelog) ? null : entry.Latest.Changelog.Trim();
        Homepage = string.IsNullOrWhiteSpace(entry.Plugin.Homepage) ? null : entry.Plugin.Homepage.Trim();
        IdentitySummary = $"{entry.Plugin.Id} · {entry.Plugin.PublisherSlug} · v{entry.Latest.Version}";

        UpdateInstalledState(installedVersion);
    }

    public MarketplaceCatalogEntry Entry { get; }
    public string Id => Entry.Plugin.Id;
    public string Name { get; }
    public string Description { get; }
    public string PublisherSlug { get; }
    public bool PublisherVerified { get; }
    public string PublisherLabel { get; }
    public string Category { get; }
    public string CategoryLabel { get; }
    public string FeaturedKicker { get; }
    public string VersionLabel { get; }
    public string DownloadsLabel { get; }
    public LucideIconKind Icon { get; }
    public Bitmap? IconBitmap { get; }
    public bool HasIconBitmap => IconBitmap is not null;
    public bool IsHighRisk { get; }
    public bool IsMediumRisk { get; }
    public bool IsLowRisk { get; }
    public string RiskLabel { get; }
    public string RiskShortLabel { get; }
    public BadgeTone RiskTone { get; }
    public IReadOnlyList<string> Permissions { get; }
    public bool HasPermissions => Permissions.Count > 0;
    public bool HasNoPermissions => Permissions.Count == 0;
    public string PermissionsSummary { get; }
    public string PermissionsHeading { get; }
    public string StatsSummary { get; }
    public string MetaSummary { get; }
    public IReadOnlyList<string> Tags { get; }
    public bool HasTags => Tags.Count > 0;
    public string? Changelog { get; }
    public bool HasChangelog => !string.IsNullOrWhiteSpace(Changelog);
    public string? Homepage { get; }
    public bool HasHomepage => !string.IsNullOrWhiteSpace(Homepage);
    public string IdentitySummary { get; }
    public int RiskOrder => IsHighRisk ? 2 : IsMediumRisk ? 1 : 0;

    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsInstalled { get; private set; }
    [ObservableProperty] public partial bool CanUpgrade { get; private set; }
    [ObservableProperty] public partial string ActionLabel { get; private set; } = "";
    [ObservableProperty] public partial bool ShowActionButton { get; private set; } = true;

    public void UpdateInstalledState(string? installedVersion)
    {
        IsInstalled = installedVersion is not null;
        CanUpgrade = installedVersion is not null
            && !string.Equals(installedVersion, Entry.Latest.Version, StringComparison.OrdinalIgnoreCase);
        ShowActionButton = !IsInstalled || CanUpgrade;
        ActionLabel = CanUpgrade
            ? $"{PluginStrings.MarketUpgrade} v{Entry.Latest.Version}"
            : PluginStrings.MarketInstall;
    }

    [RelayCommand]
    private Task InstallOrUpgradeAsync() => _owner.InstallFromMarketplaceAsync(this);

    [RelayCommand]
    private Task OpenDetailsAsync() => _owner.OpenMarketplaceDetailAsync(this);

    [RelayCommand]
    private Task OpenHomepageAsync() => _owner.OpenExternalUrlAsync(Homepage);

    public static string FormatCategory(string category) => category.ToLowerInvariant() switch
    {
        "agent" => PluginText.Get("智能体", "Agents"),
        "editor" => PluginText.Get("查看器 / 编辑器", "Editors & Views"),
        "theme" => PluginText.Get("主题", "Themes"),
        "terminal" => PluginText.Get("终端", "Terminal"),
        "workflow" => PluginText.Get("工作流", "Workflow"),
        _ => PluginText.Get("工具", "Utilities"),
    };

    private static LucideIconKind DefaultIconForCategory(string category) => category.ToLowerInvariant() switch
    {
        "agent" => LucideIconKind.Bot,
        "editor" => LucideIconKind.FileCode,
        "theme" => LucideIconKind.Palette,
        "terminal" => LucideIconKind.Terminal,
        "workflow" => LucideIconKind.Workflow,
        _ => LucideIconKind.Puzzle,
    };

    private static string FormatDownloads(long downloads) =>
        downloads >= 1000 ? $"{downloads / 1000.0:0.#}k" : downloads.ToString();

    private static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => "—",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB",
    };
}

/// <summary>One Agent client target card inside the multi-agent Skill overwrite installer modal.</summary>
public sealed partial class SkillTargetRowViewModel : ObservableObject
{
    private readonly Action? _onSelectionChanged;

    public SkillTargetRowViewModel(SkillTargetStatus status, bool selected, Action? onSelectionChanged = null)
    {
        _onSelectionChanged = onSelectionChanged;
        TargetId = status.Spec.Id;
        var isZh = PluginText.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        DisplayName = isZh ? status.Spec.DisplayNameZh : status.Spec.DisplayName;
        ClientsNote = isZh ? status.Spec.ClientsNoteZh : status.Spec.ClientsNote;
        ResolvedPath = status.ResolvedPath;
        IsCustom = false;
        IsSelected = selected;
        RefreshStatus(status.AgentRootDetected, status.InstalledVersion);
    }

    public SkillTargetRowViewModel(string customPath, string? installedVersion, Action? onSelectionChanged = null)
    {
        _onSelectionChanged = onSelectionChanged;
        TargetId = customPath;
        DisplayName = PluginText.Get("自定义 Agent 目录", "Custom Agent Folder");
        ClientsNote = PluginText.Get("用户手动添加的 Skills 目录", "User-added custom skills path");
        ResolvedPath = customPath;
        IsCustom = true;
        IsSelected = true;
        RefreshStatus(Directory.Exists(customPath), installedVersion);
    }

    public string TargetId { get; }
    public string DisplayName { get; }
    public string ClientsNote { get; }
    public bool IsCustom { get; }

    [ObservableProperty] public partial string ResolvedPath { get; set; }
    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial string StatusLabel { get; private set; } = "";
    [ObservableProperty] public partial BadgeTone StatusTone { get; private set; } = BadgeTone.Neutral;

    partial void OnIsSelectedChanged(bool value) => _onSelectionChanged?.Invoke();

    [RelayCommand]
    private void ToggleSelected() => IsSelected = !IsSelected;

    public void RefreshStatus(bool detected, string? installedVersion)
    {
        if (!string.IsNullOrWhiteSpace(installedVersion))
        {
            StatusLabel = PluginText.Get($"v{installedVersion} · 将覆盖", $"v{installedVersion} · overwrite");
            StatusTone = BadgeTone.Warning;
        }
        else if (detected)
        {
            StatusLabel = PluginText.Get("已探测", "Detected");
            StatusTone = BadgeTone.Info;
        }
        else
        {
            StatusLabel = PluginText.Get("新建目录", "New folder");
            StatusTone = BadgeTone.Neutral;
        }
    }
}
