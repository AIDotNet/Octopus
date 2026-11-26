using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Agents;
using Octopus.App.Localization;
using Octopus.App.Services;
using Octopus.Core.Bots;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace Octopus.App.Bots.Groups;

/// <summary>Validated output of the Create / Manage Bot Group dialog.</summary>
public sealed record BotGroupDialogResult(
    string Name,
    IReadOnlyList<string> BotIds,
    BotGroupReplyMode ReplyMode,
    int MaxHops);

/// <summary>Selectable bot row inside <see cref="BotGroupDialogViewModel"/>.</summary>
public sealed partial class BotGroupMemberOptionViewModel : ObservableObject
{
    private readonly Action _onChanged;

    public BotGroupMemberOptionViewModel(
        BotDefinition bot,
        string agentName,
        bool isSelected,
        Bitmap? avatarImage,
        Action onChanged)
    {
        BotId = bot.Id;
        Name = bot.Name;
        AgentName = agentName;
        Initial = bot.Name.Length > 0 ? bot.Name[..1].ToUpperInvariant() : "B";
        AvatarBrush = ParseBrush(bot.Color);
        AvatarImage = avatarImage;
        HasAvatarImage = avatarImage is not null;
        HasAvatarIcon = !HasAvatarImage && !string.IsNullOrWhiteSpace(bot.Icon);
        ShowInitial = !HasAvatarImage && !HasAvatarIcon;
        AvatarIcon = NewBotDialogViewModel.ParseIcon(bot.Icon, LucideIconKind.Bot);
        IsPaused = bot.IsPaused;
        IsSelected = isSelected;
        _onChanged = onChanged;
    }

    public string BotId { get; }
    public string Name { get; }
    public string AgentName { get; }
    public string Initial { get; }
    public IBrush AvatarBrush { get; }
    public Bitmap? AvatarImage { get; }
    public bool HasAvatarImage { get; }
    public bool HasAvatarIcon { get; }
    public bool ShowInitial { get; }
    public LucideIconKind AvatarIcon { get; }
    public bool IsPaused { get; }

    [ObservableProperty] public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value) => _onChanged();

    [RelayCommand]
    private void Toggle() => IsSelected = !IsSelected;

    private static IBrush ParseBrush(string? hex)
    {
        if (!string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex, out var color))
        {
            return new SolidColorBrush(color);
        }
        return SolidColorBrush.Parse("#5b5bd6");
    }
}

/// <summary>ViewModel for both creating a new Bot Group and editing an existing group's members and settings.</summary>
public sealed partial class BotGroupDialogViewModel : ObservableObject
{
    private static readonly int[] DefaultHopPresets = [2, 4, 6, 8, 12, 16, 20];

    public BotGroupDialogViewModel(
        IReadOnlyList<BotDefinition> availableBots,
        IAgentCatalog? catalog = null,
        IProjectIconStore? icons = null,
        BotGroupDefinition? existingGroup = null)
    {
        IsEditing = existingGroup is not null;
        Name = existingGroup?.Name ?? "";

        var preselected = existingGroup is not null
            ? existingGroup.Members.Select(m => m.BotId).ToHashSet(StringComparer.Ordinal)
            : availableBots.Take(Math.Min(availableBots.Count, 2)).Select(b => b.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var bot in availableBots)
        {
            var agentName = catalog?.Find(bot.AgentId)?.Name ?? bot.AgentId;
            Members.Add(new BotGroupMemberOptionViewModel(
                bot,
                agentName,
                preselected.Contains(bot.Id),
                icons?.Load(bot.Image),
                OnMemberSelectionChanged));
        }

        ReplyModeOptions =
        [
            new SelectOption("mentions", AppLangResources.Get(AppLangResourceKind.BotGroupReplyModeMentions)),
            new SelectOption("all", AppLangResources.Get(AppLangResourceKind.BotGroupReplyModeAll)),
            new SelectOption("bot", AppLangResources.Get(AppLangResourceKind.BotGroupReplyModeBot)),
        ];

        var initialHops = existingGroup is not null
            ? Math.Clamp(existingGroup.MaxHops, 1, 30)
            : BotGroupDefinition.DefaultMaxHops;
        var hopValues = DefaultHopPresets.ToList();
        if (!hopValues.Contains(initialHops))
        {
            hopValues.Add(initialHops);
            hopValues.Sort();
        }
        foreach (var hops in hopValues)
        {
            MaxHopsOptions.Add(new SelectOption(hops.ToString(), AppLangResources.Format(AppLangResourceKind.BotGroupHopsOption, hops)));
        }
        SelectedMaxHopsKey = initialHops.ToString();

        SelectedReplyModeKind = existingGroup?.ReplyMode.Kind switch
        {
            BotGroupReplyModeKind.All => "all",
            BotGroupReplyModeKind.Bot => "bot",
            _ => "mentions",
        };
        SelectedDefaultBotId = existingGroup?.ReplyMode.BotId ?? "";

        SyncMemberDerivedState();
    }

    public bool IsEditing { get; }

    public ObservableCollection<BotGroupMemberOptionViewModel> Members { get; } = [];
    public IReadOnlyList<SelectOption> ReplyModeOptions { get; }
    public ObservableCollection<SelectOption> DefaultBotOptions { get; } = [];
    public ObservableCollection<SelectOption> MaxHopsOptions { get; } = [];

    public event EventHandler? ValidationChanged;

    [ObservableProperty] public partial string Name { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDefaultBotPicker))]
    public partial string SelectedReplyModeKind { get; set; } = "mentions";

    [ObservableProperty] public partial string SelectedDefaultBotId { get; set; } = "";
    [ObservableProperty] public partial string SelectedMaxHopsKey { get; set; } = BotGroupDefinition.DefaultMaxHops.ToString();
    [ObservableProperty] public partial int SelectedCount { get; private set; }
    [ObservableProperty] public partial string MembersHeading { get; private set; } = "";
    [ObservableProperty] public partial bool CanConfirm { get; private set; }
    [ObservableProperty] public partial bool HasValidationError { get; private set; }

    public bool ShowDefaultBotPicker => string.Equals(SelectedReplyModeKind, "bot", StringComparison.Ordinal) && DefaultBotOptions.Count > 0;

    partial void OnSelectedReplyModeKindChanged(string value)
    {
        if (string.Equals(value, "bot", StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(SelectedDefaultBotId)
            && DefaultBotOptions.Count > 0)
        {
            SelectedDefaultBotId = DefaultBotOptions[0].Value?.ToString() ?? "";
        }
        OnPropertyChanged(nameof(ShowDefaultBotPicker));
    }

    public BotGroupDialogResult? BuildResult()
    {
        var selectedIds = Members.Where(m => m.IsSelected).Select(m => m.BotId).ToList();
        if (selectedIds.Count == 0)
        {
            return null;
        }

        var trimmedName = string.IsNullOrWhiteSpace(Name)
            ? AppLangResources.Get(AppLangResourceKind.BotGroupDialogDefaultName)
            : Name.Trim();

        var mode = SelectedReplyModeKind switch
        {
            "all" => new BotGroupReplyMode { Kind = BotGroupReplyModeKind.All },
            "bot" => new BotGroupReplyMode
            {
                Kind = BotGroupReplyModeKind.Bot,
                BotId = selectedIds.Contains(SelectedDefaultBotId, StringComparer.Ordinal)
                    ? SelectedDefaultBotId
                    : selectedIds[0],
            },
            _ => new BotGroupReplyMode { Kind = BotGroupReplyModeKind.Mentions },
        };

        var maxHops = int.TryParse(SelectedMaxHopsKey, out var parsed)
            ? Math.Clamp(parsed, 1, 30)
            : BotGroupDefinition.DefaultMaxHops;

        return new BotGroupDialogResult(trimmedName, selectedIds, mode, maxHops);
    }

    private void OnMemberSelectionChanged()
    {
        SyncMemberDerivedState();
        ValidationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SyncMemberDerivedState()
    {
        var selected = Members.Where(m => m.IsSelected).ToList();
        SelectedCount = selected.Count;
        MembersHeading = AppLangResources.Format(AppLangResourceKind.BotGroupDialogMembersLabel, SelectedCount);
        CanConfirm = SelectedCount > 0;
        HasValidationError = SelectedCount == 0;

        DefaultBotOptions.Clear();
        foreach (var member in selected)
        {
            DefaultBotOptions.Add(new SelectOption(member.BotId, member.Name));
        }
        if (selected.Count > 0 && !selected.Any(m => string.Equals(m.BotId, SelectedDefaultBotId, StringComparison.Ordinal)))
        {
            SelectedDefaultBotId = selected[0].BotId;
        }
        else if (selected.Count == 0)
        {
            SelectedDefaultBotId = "";
        }
        OnPropertyChanged(nameof(ShowDefaultBotPicker));
    }
}
