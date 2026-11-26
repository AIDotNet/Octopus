using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Agents;
using Octopus.App.Common;
using Octopus.App.Localization;
using Octopus.Core.Bots;
using Octopus.Storage;
using TKCode.Arc.Icons.Lucide;
using TKCode.Arc.Localization;

namespace Octopus.App.Bots;

public sealed partial class BotColorOptionViewModel(string hex, bool isSelected, Action<string> select) : ObservableObject
{
    public string Hex { get; } = hex;
    public IBrush Brush { get; } = SolidColorBrush.Parse(hex);
    [ObservableProperty] public partial bool IsSelected { get; set; } = isSelected;

    [RelayCommand]
    private void Pick() => select(Hex);
}

public sealed partial class BotAgentOptionViewModel(string id, string name, IBrush accent, bool isSelected, Action<string> select) : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public IBrush Accent { get; } = accent;
    [ObservableProperty] public partial bool IsSelected { get; set; } = isSelected;

    [RelayCommand]
    private void Pick() => select(Id);
}

public sealed partial class BotTemplateOptionViewModel(
    BotTemplateDefinition template,
    string label,
    LucideIconKind icon,
    bool isSelected,
    Action<BotTemplateDefinition> select) : ObservableObject
{
    public BotTemplateDefinition Template { get; } = template;
    public string Id => Template.Id;
    public string Label { get; } = label;
    public LucideIconKind Icon { get; } = icon;
    [ObservableProperty] public partial bool IsSelected { get; set; } = isSelected;

    [RelayCommand]
    private void Pick() => select(Template);
}

/// <summary>ViewModel for the "New Bot" creation dialog: name & avatar, agent, starter template, and SOUL.md seed.</summary>
public sealed partial class NewBotDialogViewModel : ObservableObject
{
    private BotTemplateDefinition _selectedTemplate;

    public NewBotDialogViewModel(
        IAgentCatalog catalog,
        IAgentScanner scanner,
        ISettingsStore settings,
        string? initialTemplateId = null)
    {
        _selectedTemplate = BotCatalog.FindTemplate(initialTemplateId);
        SelectedColor = _selectedTemplate.Color;
        var isZh = IsChinese();
        Name = _selectedTemplate.Id == "blank" ? "" : (isZh ? _selectedTemplate.DefaultName : _selectedTemplate.EnglishName);
        Soul = isZh ? _selectedTemplate.DefaultSoulZh : _selectedTemplate.DefaultSoulEn;

        foreach (var hex in BotCatalog.AvatarPalette)
        {
            Colors.Add(new BotColorOptionViewModel(hex, string.Equals(hex, SelectedColor, StringComparison.OrdinalIgnoreCase), SelectColor));
        }

        // Prefer installed & enabled agents, then fall back to built-in catalog agents.
        var agentSettings = settings.Current.Agents;
        var orderedIds = new List<string>();
        foreach (var inst in scanner.Current.Installations)
        {
            if (catalog.Find(inst.AgentId) is not null && agentSettings.ProfileOf(inst.AgentId).Enabled && !orderedIds.Contains(inst.AgentId, StringComparer.OrdinalIgnoreCase))
            {
                orderedIds.Add(inst.AgentId);
            }
        }
        foreach (var def in catalog.Definitions)
        {
            if (agentSettings.ProfileOf(def.Id).Enabled && !orderedIds.Contains(def.Id, StringComparer.OrdinalIgnoreCase))
            {
                orderedIds.Add(def.Id);
            }
        }
        if (orderedIds.Count == 0 && catalog.Definitions.Count > 0)
        {
            orderedIds.Add(catalog.Definitions[0].Id);
        }

        SelectedAgentId = !string.IsNullOrWhiteSpace(agentSettings.DefaultAgentId)
            && orderedIds.Contains(agentSettings.DefaultAgentId, StringComparer.OrdinalIgnoreCase)
                ? agentSettings.DefaultAgentId
                : orderedIds.FirstOrDefault() ?? "claude";

        foreach (var id in orderedIds.Take(6))
        {
            var def = catalog.Find(id);
            var displayName = def?.Name ?? id;
            Agents.Add(new BotAgentOptionViewModel(id, displayName, AgentAccent(id), string.Equals(id, SelectedAgentId, StringComparison.OrdinalIgnoreCase), SelectAgent));
        }

        foreach (var tpl in BotCatalog.Templates)
        {
            Templates.Add(new BotTemplateOptionViewModel(
                tpl,
                TemplateLabel(tpl.Id),
                ParseIcon(tpl.IconName, LucideIconKind.Sparkles),
                tpl.Id == _selectedTemplate.Id,
                SelectTemplate));
        }
    }

    public ObservableCollection<BotColorOptionViewModel> Colors { get; } = [];
    public ObservableCollection<BotAgentOptionViewModel> Agents { get; } = [];
    public ObservableCollection<BotTemplateOptionViewModel> Templates { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvatarInitial))]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvatarBrush))]
    public partial string SelectedColor { get; set; } = "#5b5bd6";

    [ObservableProperty] public partial string SelectedAgentId { get; set; } = "claude";
    [ObservableProperty] public partial string Soul { get; set; } = "";

    public IBrush AvatarBrush => SolidColorBrush.Parse(SelectedColor);

    public string AvatarInitial
    {
        get
        {
            var source = string.IsNullOrWhiteSpace(Name)
                ? AppLangResources.Get(AppLangResourceKind.BotDefaultName)
                : Name.Trim();
            return source.Length > 0 ? source[..1].ToUpperInvariant() : "B";
        }
    }

    public NewBotRequest BuildRequest() => new(
        string.IsNullOrWhiteSpace(Name) ? AppLangResources.Get(AppLangResourceKind.BotDefaultName) : Name.Trim(),
        SelectedAgentId,
        SelectedColor,
        _selectedTemplate.Id,
        string.IsNullOrWhiteSpace(Soul) ? null : Soul.Trim());

    private void SelectColor(string hex)
    {
        SelectedColor = hex;
        foreach (var c in Colors) c.IsSelected = string.Equals(c.Hex, hex, StringComparison.OrdinalIgnoreCase);
    }

    private void SelectAgent(string id)
    {
        SelectedAgentId = id;
        foreach (var a in Agents) a.IsSelected = string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase);
    }

    private void SelectTemplate(BotTemplateDefinition next)
    {
        var isZh = IsChinese();
        var prevName = _selectedTemplate.Id == "blank" ? "" : (isZh ? _selectedTemplate.DefaultName : _selectedTemplate.EnglishName);
        var prevSoul = isZh ? _selectedTemplate.DefaultSoulZh : _selectedTemplate.DefaultSoulEn;
        var nextName = next.Id == "blank" ? "" : (isZh ? next.DefaultName : next.EnglishName);
        var nextSoul = isZh ? next.DefaultSoulZh : next.DefaultSoulEn;

        if (string.IsNullOrWhiteSpace(Name) || string.Equals(Name.Trim(), prevName, StringComparison.Ordinal))
        {
            Name = nextName;
        }
        if (string.IsNullOrWhiteSpace(Soul) || string.Equals(Soul.Trim(), prevSoul, StringComparison.Ordinal))
        {
            Soul = nextSoul;
        }
        SelectColor(next.Color);
        _selectedTemplate = next;
        foreach (var t in Templates) t.IsSelected = t.Id == next.Id;
    }

    private static bool IsChinese() =>
        ArcLanguage.Current.ToString().StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    public static string TemplateLabel(string templateId) => templateId switch
    {
        "pr" => AppLangResources.Get(AppLangResourceKind.BotTemplatePr),
        "ci" => AppLangResources.Get(AppLangResourceKind.BotTemplateCi),
        "daily" => AppLangResources.Get(AppLangResourceKind.BotTemplateDaily),
        _ => AppLangResources.Get(AppLangResourceKind.BotTemplateBlank),
    };

    public static IBrush AgentAccent(string? agentId) => (agentId ?? "").ToLowerInvariant() switch
    {
        "claude" => SolidColorBrush.Parse("#d97757"),
        "codex" => SolidColorBrush.Parse("#1f9d74"),
        "gemini" or "antigravity" or "agy" => SolidColorBrush.Parse("#3b7de9"),
        "opencode" => SolidColorBrush.Parse("#8e4ec6"),
        _ => SolidColorBrush.Parse("#5b5bd6"),
    };

    public static LucideIconKind ParseIcon(string? name, LucideIconKind fallback) =>
        !string.IsNullOrWhiteSpace(name) && Enum.TryParse<LucideIconKind>(name, ignoreCase: true, out var kind)
            ? kind
            : fallback;
}
