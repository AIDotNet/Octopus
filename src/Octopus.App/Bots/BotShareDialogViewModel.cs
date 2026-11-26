using System.Text;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Localization;
using Octopus.App.Services;
using Octopus.Core.Bots;
using TKCode.Arc.Icons.Lucide;

namespace Octopus.App.Bots;

/// <summary>ViewModel for the Share / Export Bot modal (matches Orca's share dialog: Soul, Skills, Automations, Tools, Include memory, Setup notes, Security check).</summary>
public sealed partial class BotShareDialogViewModel : ObservableObject
{
    private readonly BotDefinition _bot;
    private readonly IBotService _bots;

    public BotShareDialogViewModel(BotDefinition bot, IBotService bots, IProjectIconStore? icons = null)
    {
        _bot = bot;
        _bots = bots;
        Name = bot.Name;
        Subtitle = bots.Home.ReadSoulSummary(bot);
        HasSubtitle = !string.IsNullOrWhiteSpace(Subtitle);
        Initial = bot.Name.Length > 0 ? bot.Name[..1].ToUpperInvariant() : "B";
        AvatarBrush = SolidColorBrush.Parse(bot.Color);
        AvatarImage = icons?.Load(bot.Image);
        HasAvatarImage = AvatarImage is not null;
        HasAvatarIcon = !HasAvatarImage && !string.IsNullOrWhiteSpace(bot.Icon);
        ShowInitial = !HasAvatarImage && !HasAvatarIcon;
        AvatarIcon = NewBotDialogViewModel.ParseIcon(bot.Icon, LucideIconKind.Bot);

        var soulLines = bots.Home.CountSoulLines(bot);
        SoulSummaryText = soulLines > 0
            ? AppLangResources.Format(AppLangResourceKind.BotShareLinesCount, soulLines)
            : AppLangResources.Get(AppLangResourceKind.BotSettingsNone);

        var skillCount = bots.Home.CountSkillEntries(bot);
        SkillsSummaryText = skillCount > 0 ? skillCount.ToString() : AppLangResources.Get(AppLangResourceKind.BotSettingsNone);

        var autoCount = (bot.LookAroundEnabled ? 1 : 0) + (bot.TidyUpMemoryEnabled ? 1 : 0);
        AutomationsSummaryText = autoCount > 0 ? autoCount.ToString() : AppLangResources.Get(AppLangResourceKind.BotSettingsNone);

        var toolCount = bot.Connectors.Count(c => c.Enabled);
        ToolsSummaryText = toolCount > 0 ? toolCount.ToString() : AppLangResources.Get(AppLangResourceKind.BotSettingsNone);

        SetupNotes = bot.SetupNotes ?? "";
        RefreshSecurityCheck();
    }

    public string Name { get; }
    public string Subtitle { get; }
    public bool HasSubtitle { get; }
    public string Initial { get; }
    public IBrush AvatarBrush { get; }
    public Bitmap? AvatarImage { get; }
    public bool HasAvatarImage { get; }
    public bool HasAvatarIcon { get; }
    public bool ShowInitial { get; }
    public LucideIconKind AvatarIcon { get; }

    public string SoulSummaryText { get; }
    public string SkillsSummaryText { get; }
    public string AutomationsSummaryText { get; }
    public string ToolsSummaryText { get; }

    [ObservableProperty] public partial bool IncludeMemory { get; set; }
    [ObservableProperty] public partial bool IsEditingSetupNotes { get; set; }
    [ObservableProperty] public partial string SetupNotes { get; set; } = "";
    [ObservableProperty] public partial bool SecurityCheckClean { get; private set; } = true;
    [ObservableProperty] public partial string SecurityCheckText { get; private set; } = "";
    [ObservableProperty] public partial bool Copied { get; private set; }

    public string SetupNotesActionText => string.IsNullOrWhiteSpace(SetupNotes)
        ? AppLangResources.Get(AppLangResourceKind.BotShareSetupNotesAdd)
        : SetupNotes.Trim();

    partial void OnIncludeMemoryChanged(bool value) => RefreshSecurityCheck();

    partial void OnSetupNotesChanged(string value)
    {
        OnPropertyChanged(nameof(SetupNotesActionText));
        _bots.UpdateBotSettings(_bot.Id, b => b.SetupNotes = string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    private void RefreshSecurityCheck()
    {
        var findings = _bots.Home.RunSecurityCheck(_bot, IncludeMemory);
        SecurityCheckClean = findings.Count == 0;
        SecurityCheckText = SecurityCheckClean
            ? AppLangResources.Get(AppLangResourceKind.BotShareSecurityClean)
            : AppLangResources.Format(AppLangResourceKind.BotShareSecurityWarning, findings.Count);
    }

    [RelayCommand]
    private void ToggleSetupNotes() => IsEditingSetupNotes = !IsEditingSetupNotes;

    [RelayCommand]
    private void OpenSoul()
    {
        _bots.Home.EnsureHome(_bot);
        _bots.OpenFile(_bot.Id, _bots.Home.GetSoulPath(_bot));
    }

    public string BuildShareLink() =>
        BotShareCodec.Encode(BotShareCodec.BuildPayload(_bot, _bots.Home, IncludeMemory));

    public string BuildShareBundle()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Bot: {_bot.Name}");
        sb.AppendLine($"- Agent: {_bot.AgentId}");
        sb.AppendLine($"- Permissions: {_bot.PermissionMode}");
        sb.AppendLine($"- LookAround: {(_bot.LookAroundEnabled ? $"Every {_bot.LookAroundIntervalHours}h" : "Off")}");
        sb.AppendLine($"- MemoryMaintenance: {(_bot.TidyUpMemoryEnabled ? "Nightly" : "Off")}");
        if (!string.IsNullOrWhiteSpace(SetupNotes))
        {
            sb.AppendLine();
            sb.AppendLine("## Setup Notes");
            sb.AppendLine(SetupNotes.Trim());
        }
        var soul = _bots.Home.ReadSoul(_bot);
        if (!string.IsNullOrWhiteSpace(soul))
        {
            sb.AppendLine();
            sb.AppendLine("## SOUL.md");
            sb.AppendLine(soul);
        }
        if (IncludeMemory)
        {
            var memLines = _bots.Home.ReadMemoryLines(_bot, maxLines: 200);
            if (memLines.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## MEMORY.md");
                foreach (var line in memLines) sb.Append("- ").AppendLine(line);
            }
        }
        return sb.ToString();
    }

    public void MarkCopied() => Copied = true;
}
