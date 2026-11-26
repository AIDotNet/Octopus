using Avalonia.Media;
using Octopus.App.Localization;
using Octopus.Core.Bots;
using TKCode.Arc.Icons.Lucide;

namespace Octopus.App.Bots;

/// <summary>ViewModel for the Bot Import confirmation dialog opened from an <c>octopus://bot/import</c> link.</summary>
public sealed class BotImportDialogViewModel
{
    public BotImportDialogViewModel(BotSharePayload payload)
    {
        Payload = payload;
        Name = string.IsNullOrWhiteSpace(payload.Name)
            ? AppLangResources.Get(AppLangResourceKind.BotDefaultName)
            : payload.Name.Trim();
        Initial = Name.Length > 0 ? Name[..1].ToUpperInvariant() : "B";
        AvatarBrush = SolidColorBrush.Parse(string.IsNullOrWhiteSpace(payload.Color) ? "#5b5bd6" : payload.Color);
        HasAvatarIcon = !string.IsNullOrWhiteSpace(payload.Icon);
        ShowInitial = !HasAvatarIcon;
        AvatarIcon = NewBotDialogViewModel.ParseIcon(payload.Icon, LucideIconKind.Bot);
        AgentId = string.IsNullOrWhiteSpace(payload.AgentId) ? "claude" : payload.AgentId;

        var soulLines = string.IsNullOrWhiteSpace(payload.SoulMarkdown)
            ? 0
            : payload.SoulMarkdown.Split('\n').Count(l => !string.IsNullOrWhiteSpace(l));
        SoulSummaryText = soulLines > 0
            ? AppLangResources.Format(AppLangResourceKind.BotShareLinesCount, soulLines)
            : AppLangResources.Get(AppLangResourceKind.BotSettingsNone);

        var autoCount = (payload.LookAroundEnabled ? 1 : 0) + (payload.TidyUpMemoryEnabled ? 1 : 0);
        AutomationsSummaryText = autoCount > 0 ? autoCount.ToString() : AppLangResources.Get(AppLangResourceKind.BotSettingsNone);

        ConnectorsSummaryText = payload.Connectors.Count > 0
            ? string.Join(", ", payload.Connectors.Select(c => string.IsNullOrWhiteSpace(c.DisplayName) ? c.AppId : c.DisplayName))
            : AppLangResources.Get(AppLangResourceKind.BotSettingsNone);

        HasMemory = !string.IsNullOrWhiteSpace(payload.MemoryMarkdown);
        SetupNotes = payload.SetupNotes?.Trim() ?? "";
        HasSetupNotes = SetupNotes.Length > 0;

        var findings = BotShareCodec.ScanPayloadForSecrets(payload);
        SecurityCheckClean = findings.Count == 0;
        SecurityCheckText = SecurityCheckClean
            ? AppLangResources.Get(AppLangResourceKind.BotShareSecurityClean)
            : AppLangResources.Format(AppLangResourceKind.BotShareSecurityWarning, findings.Count);
    }

    public BotSharePayload Payload { get; }
    public string Name { get; }
    public string Initial { get; }
    public IBrush AvatarBrush { get; }
    public bool HasAvatarIcon { get; }
    public bool ShowInitial { get; }
    public LucideIconKind AvatarIcon { get; }
    public string AgentId { get; }
    public string SoulSummaryText { get; }
    public string AutomationsSummaryText { get; }
    public string ConnectorsSummaryText { get; }
    public bool HasMemory { get; }
    public string SetupNotes { get; }
    public bool HasSetupNotes { get; }
    public bool SecurityCheckClean { get; }
    public string SecurityCheckText { get; }
}
