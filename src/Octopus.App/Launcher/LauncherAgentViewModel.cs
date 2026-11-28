using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Octopus.App.Launcher;

/// <summary>One installed agent in the first level of the launcher.</summary>
public sealed partial class LauncherAgentViewModel(
    string id,
    string name,
    bool isDefault,
    Action<LauncherAgentViewModel> launch,
    Action<LauncherAgentViewModel> expand,
    bool supportsAcp = false,
    string? sourceBadge = null,
    bool isTunnelSource = false) : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public bool IsDefault { get; } = isDefault;
    public bool SupportsAcp { get; } = supportsAcp;
    public string? SourceBadge { get; } = sourceBadge;
    public bool HasSourceBadge => !string.IsNullOrWhiteSpace(SourceBadge);
    public bool IsTunnelSource { get; } = isTunnelSource;

    [ObservableProperty] public partial bool IsExpanded { get; set; }

    /// <summary>Starts the agent with its default configuration.</summary>
    [RelayCommand]
    private void Launch() => launch(this);

    /// <summary>Shows the second panel (the arrow at the end of the row).</summary>
    [RelayCommand]
    private void Expand() => expand(this);
}

/// <summary>One pinned quick-launch preset (Agent + Provider + Model + Effort) in the first level of the launcher.</summary>
public sealed partial class LauncherPinnedPresetViewModel(
    string id,
    string agentId,
    string agentName,
    string title,
    string subtitle,
    bool isTunnel,
    Action<LauncherPinnedPresetViewModel> launch,
    Action<LauncherPinnedPresetViewModel> unpin) : ObservableObject
{
    public string Id { get; } = id;
    public string AgentId { get; } = agentId;
    public string AgentName { get; } = agentName;
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    public bool HasSubtitle => !string.IsNullOrWhiteSpace(Subtitle);
    public bool IsTunnel { get; } = isTunnel;

    [RelayCommand]
    private void Launch() => launch(this);

    [RelayCommand]
    private void Unpin() => unpin(this);
}
