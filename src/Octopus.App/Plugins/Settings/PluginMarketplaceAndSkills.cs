using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Plugins.Marketplace;
using Octopus.Plugins.Skills;
using TKCode.Arc;

namespace Octopus.App.Plugins.Settings;

/// <summary>One plugin entry fetched from the Octopus Plugin Marketplace.</summary>
public sealed partial class MarketplacePluginRowViewModel : ObservableObject
{
    private readonly PluginsSettingsViewModel _owner;

    public MarketplacePluginRowViewModel(
        MarketplaceCatalogEntry entry,
        string? installedVersion,
        PluginsSettingsViewModel owner)
    {
        Entry = entry;
        _owner = owner;
        var isZh = PluginText.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        Name = (isZh && !string.IsNullOrWhiteSpace(entry.Plugin.LocalizedNameZh) ? entry.Plugin.LocalizedNameZh : entry.Plugin.Name)!;
        Description = (isZh && !string.IsNullOrWhiteSpace(entry.Plugin.LocalizedDescriptionZh) ? entry.Plugin.LocalizedDescriptionZh : entry.Plugin.Description)!;
        PublisherLabel = entry.Plugin.PublisherVerified ? $"{entry.Plugin.PublisherSlug} ✓" : entry.Plugin.PublisherSlug;
        VersionLabel = $"v{entry.Latest.Version}";
        DownloadsLabel = PluginText.Get($"↓ {entry.Plugin.Downloads}", $"↓ {entry.Plugin.Downloads}");
        RiskLabel = entry.Latest.RiskLevel switch
        {
            "high" => PluginText.Get("高风险", "High risk"),
            "medium" => PluginText.Get("中风险", "Medium risk"),
            _ => PluginText.Get("低风险", "Low risk"),
        };
        RiskTone = entry.Latest.RiskLevel switch
        {
            "high" => BadgeTone.Danger,
            "medium" => BadgeTone.Warning,
            _ => BadgeTone.Success,
        };
        PermissionsSummary = entry.Latest.Permissions.Count == 0
            ? PluginText.Get("零权限", "0 permissions")
            : PluginText.Get($"{entry.Latest.Permissions.Count} 项权限", $"{entry.Latest.Permissions.Count} permissions");
        UpdateInstalledState(installedVersion);
    }

    public MarketplaceCatalogEntry Entry { get; }
    public string Id => Entry.Plugin.Id;
    public string Name { get; }
    public string Description { get; }
    public string PublisherLabel { get; }
    public string VersionLabel { get; }
    public string DownloadsLabel { get; }
    public string RiskLabel { get; }
    public BadgeTone RiskTone { get; }
    public string PermissionsSummary { get; }

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
        ActionLabel = CanUpgrade ? PluginStrings.MarketUpgrade : PluginStrings.MarketInstall;
    }

    [RelayCommand]
    private Task InstallOrUpgradeAsync() => _owner.InstallFromMarketplaceAsync(this);
}

/// <summary>One Agent client target row inside the multi-agent Skill overwrite installer.</summary>
public sealed partial class SkillTargetRowViewModel : ObservableObject
{
    public SkillTargetRowViewModel(SkillTargetStatus status, bool selected)
    {
        TargetId = status.Spec.Id;
        var isZh = PluginText.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        DisplayName = isZh ? status.Spec.DisplayNameZh : status.Spec.DisplayName;
        ClientsNote = isZh ? status.Spec.ClientsNoteZh : status.Spec.ClientsNote;
        ResolvedPath = status.ResolvedPath;
        IsCustom = false;
        IsSelected = selected;
        RefreshStatus(status.AgentRootDetected, status.InstalledVersion);
    }

    public SkillTargetRowViewModel(string customPath, string? installedVersion)
    {
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

    public void RefreshStatus(bool detected, string? installedVersion)
    {
        if (!string.IsNullOrWhiteSpace(installedVersion))
        {
            StatusLabel = PluginText.Get($"已装 v{installedVersion}（点击将覆盖）", $"v{installedVersion} (will overwrite)");
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
