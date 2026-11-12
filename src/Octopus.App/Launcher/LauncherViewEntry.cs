using CommunityToolkit.Mvvm.Input;
using TKCode.Arc.Icons.Lucide;

namespace Octopus.App.Launcher;

/// <summary>A plugin view that asks to be in the launcher (<c>"launcher": true</c>, design/plugins/06-标签与渲染.md §3).</summary>
public sealed partial class LauncherViewEntry(string id, string title, string pluginName, LucideIconKind icon, Action<LauncherViewEntry> open)
{
    public string Id { get; } = id;
    public string Title { get; } = title;
    /// <summary>Shown as the tooltip: which plugin the view comes from.</summary>
    public string PluginName { get; } = pluginName;
    public LucideIconKind Icon { get; } = icon;

    [RelayCommand]
    private void Open() => open(this);
}
