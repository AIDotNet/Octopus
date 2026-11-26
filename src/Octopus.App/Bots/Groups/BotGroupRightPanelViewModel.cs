using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Agents;
using Octopus.App.Common;
using Octopus.App.Localization;
using Octopus.App.Services;
using Octopus.Core.Bots;
using TKCode.Arc;
using TKCode.Arc.Localization;

namespace Octopus.App.Bots.Groups;

public enum BotGroupRightTab
{
    Overview,
    Items
}

/// <summary>One member bot row in the Bot Group right panel.</summary>
public sealed partial class BotGroupRightMemberViewModel(
    BotDefinition bot,
    BotGroupAvatarTile avatar,
    string agentName,
    bool canRemove,
    Action<string> jumpToBot,
    Func<string, Task> removeMember) : ObservableObject
{
    public string BotId { get; } = bot.Id;
    public string Name { get; } = bot.Name;
    public BotGroupAvatarTile Avatar { get; } = avatar;
    public string AgentName { get; } = agentName;
    public IBrush AgentAccent { get; } = NewBotDialogViewModel.AgentAccent(bot.AgentId);
    public bool IsPaused { get; } = bot.IsPaused;
    public bool CanRemove { get; } = canRemove;

    [RelayCommand]
    private void JumpToBot() => jumpToBot(BotId);

    [RelayCommand]
    private Task RemoveAsync() => removeMember(BotId);
}

/// <summary>One aggregated <see cref="BotWorkItem"/> card tagged with its owning member bot.</summary>
public sealed class BotGroupWorkItemCardViewModel
{
    public BotGroupWorkItemCardViewModel(
        BotDefinition bot,
        BotWorkItem item,
        Action<string, string, string?> resolve)
    {
        BotId = bot.Id;
        BotName = bot.Name;
        BotBrush = BotGroupUiHelpers.ParseBrush(bot.Color);
        ItemId = item.Id;
        NumberText = $"#{item.Number}";
        Title = item.Title;
        IsNeedsYou = item.Status == BotWorkItemStatus.NeedsYou;
        Question = item.Question;
        HasQuestion = !string.IsNullOrWhiteSpace(item.Question);
        StatusLabel = item.Status switch
        {
            BotWorkItemStatus.NeedsYou => AppLangResources.Get(AppLangResourceKind.BotStatusWorking),
            BotWorkItemStatus.Working => AppLangResources.Get(AppLangResourceKind.BotStatusWorking),
            _ => item.Status.ToString(),
        };
        Options = [.. item.Options.Select(o => new BotWorkItemOptionViewModel(o, opt => resolve(bot.Id, item.Id, opt)))];
    }

    public string BotId { get; }
    public string BotName { get; }
    public IBrush BotBrush { get; }
    public string ItemId { get; }
    public string NumberText { get; }
    public string Title { get; }
    public bool IsNeedsYou { get; }
    public string? Question { get; }
    public bool HasQuestion { get; }
    public string StatusLabel { get; }
    public IReadOnlyList<BotWorkItemOptionViewModel> Options { get; }
}

/// <summary>
/// Right panel shown in Bot mode when a Bot Group is active:
/// 2 tabs: Overview &amp; Members | Aggregated Work Items.
/// </summary>
public sealed partial class BotGroupRightPanelViewModel : ObservableObject
{
    private static readonly int[] DefaultHopPresets = [2, 4, 6, 8, 12, 16, 20];

    private readonly IBotGroupService _groups;
    private readonly IBotService _bots;
    private readonly IDialogService _dialogs;
    private readonly IAgentCatalog _catalog;
    private readonly IProjectIconStore? _icons;
    private bool _hydrating;

    public BotGroupRightPanelViewModel(
        IBotGroupService groups,
        IBotService bots,
        IDialogService dialogs,
        IAgentCatalog catalog,
        IProjectIconStore? icons = null)
    {
        _groups = groups;
        _bots = bots;
        _dialogs = dialogs;
        _catalog = catalog;
        _icons = icons;

        _groups.Changed += (_, _) => UiContext.Run(Refresh);
        _bots.Changed += (_, _) => UiContext.Run(Refresh);
        ArcLanguage.Changed += (_, _) => UiContext.Run(() =>
        {
            BuildStaticOptions();
            Refresh();
        });

        BuildStaticOptions();
        Refresh();
    }

    public ObservableCollection<BotGroupAvatarTile> GroupAvatars { get; } = [];
    public ObservableCollection<BotGroupRightMemberViewModel> Members { get; } = [];
    public ObservableCollection<BotGroupWorkItemCardViewModel> WorkItems { get; } = [];
    public ObservableCollection<SelectOption> ReplyModeOptions { get; } = [];
    public ObservableCollection<SelectOption> DefaultBotOptions { get; } = [];
    public ObservableCollection<SelectOption> MaxHopsOptions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabKey), nameof(IsOverviewTab), nameof(IsItemsTab))]
    public partial BotGroupRightTab Tab { get; set; } = BotGroupRightTab.Overview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoGroup), nameof(IsOverviewTab), nameof(IsItemsTab))]
    public partial bool HasGroup { get; private set; }

    [ObservableProperty] public partial string GroupName { get; private set; } = "";
    [ObservableProperty] public partial string MemberCountText { get; private set; } = "";
    [ObservableProperty] public partial bool HasSingleAvatar { get; private set; }
    [ObservableProperty] public partial bool HasMultiAvatar { get; private set; }
    [ObservableProperty] public partial BotGroupAvatarTile? SingleAvatar { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDefaultBotPicker))]
    public partial string SelectedReplyModeKind { get; set; } = "mentions";

    [ObservableProperty] public partial string SelectedDefaultBotId { get; set; } = "";
    [ObservableProperty] public partial string SelectedMaxHopsKey { get; set; } = BotGroupDefinition.DefaultMaxHops.ToString();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoWorkItems))]
    public partial bool HasWorkItems { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNeedsCount), nameof(NeedsCountText))]
    public partial int NeedsCount { get; private set; }

    public bool HasNoGroup => !HasGroup;
    public bool IsOverviewTab => HasGroup && Tab == BotGroupRightTab.Overview;
    public bool IsItemsTab => HasGroup && Tab == BotGroupRightTab.Items;
    public bool HasNoWorkItems => !HasWorkItems;
    public bool HasNeedsCount => NeedsCount > 0;
    public string NeedsCountText => NeedsCount > 99 ? "99+" : NeedsCount.ToString();
    public bool ShowDefaultBotPicker => string.Equals(SelectedReplyModeKind, "bot", StringComparison.Ordinal) && DefaultBotOptions.Count > 0;

    public string TabKey
    {
        get => Tab == BotGroupRightTab.Items ? "items" : "overview";
        set => Tab = string.Equals(value, "items", StringComparison.Ordinal) ? BotGroupRightTab.Items : BotGroupRightTab.Overview;
    }

    partial void OnSelectedReplyModeKindChanged(string value)
    {
        if (_hydrating || _groups.CurrentGroup is not { } group)
        {
            return;
        }
        var mode = value switch
        {
            "all" => new BotGroupReplyMode { Kind = BotGroupReplyModeKind.All },
            "bot" => new BotGroupReplyMode
            {
                Kind = BotGroupReplyModeKind.Bot,
                BotId = !string.IsNullOrWhiteSpace(SelectedDefaultBotId)
                    ? SelectedDefaultBotId
                    : group.Members.FirstOrDefault()?.BotId,
            },
            _ => new BotGroupReplyMode { Kind = BotGroupReplyModeKind.Mentions },
        };
        _ = _groups.UpdateGroupAsync(new UpdateBotGroupRequest(group.Id, group.Revision, ReplyMode: mode));
    }

    partial void OnSelectedDefaultBotIdChanged(string value)
    {
        if (_hydrating || _groups.CurrentGroup is not { } group || string.IsNullOrWhiteSpace(value))
        {
            return;
        }
        if (group.ReplyMode.Kind != BotGroupReplyModeKind.Bot
            || string.Equals(group.ReplyMode.BotId, value, StringComparison.Ordinal))
        {
            return;
        }
        _ = _groups.UpdateGroupAsync(new UpdateBotGroupRequest(
            group.Id,
            group.Revision,
            ReplyMode: new BotGroupReplyMode { Kind = BotGroupReplyModeKind.Bot, BotId = value }));
    }

    partial void OnSelectedMaxHopsKeyChanged(string value)
    {
        if (_hydrating || _groups.CurrentGroup is not { } group)
        {
            return;
        }
        if (!int.TryParse(value, out var hops) || hops <= 0 || hops == group.MaxHops)
        {
            return;
        }
        _ = _groups.UpdateGroupAsync(new UpdateBotGroupRequest(group.Id, group.Revision, MaxHops: Math.Clamp(hops, 1, 30)));
    }

    private void BuildStaticOptions()
    {
        ReplyModeOptions.Clear();
        ReplyModeOptions.Add(new SelectOption("mentions", AppLangResources.Get(AppLangResourceKind.BotGroupReplyModeMentions)));
        ReplyModeOptions.Add(new SelectOption("all", AppLangResources.Get(AppLangResourceKind.BotGroupReplyModeAll)));
        ReplyModeOptions.Add(new SelectOption("bot", AppLangResources.Get(AppLangResourceKind.BotGroupReplyModeBot)));

        RebuildMaxHopsOptions(_groups.CurrentGroup?.MaxHops ?? BotGroupDefinition.DefaultMaxHops);
    }

    private void RebuildMaxHopsOptions(int currentHops)
    {
        var clamped = Math.Clamp(currentHops, 1, 30);
        var values = DefaultHopPresets.ToList();
        if (!values.Contains(clamped))
        {
            values.Add(clamped);
            values.Sort();
        }
        MaxHopsOptions.Clear();
        foreach (var h in values)
        {
            MaxHopsOptions.Add(new SelectOption(h.ToString(), AppLangResources.Format(AppLangResourceKind.BotGroupHopsOption, h)));
        }
    }

    public void Refresh()
    {
        var group = _groups.CurrentGroup;
        if (group is null)
        {
            HasGroup = false;
            GroupName = "";
            MemberCountText = "";
            GroupAvatars.Clear();
            Members.Clear();
            WorkItems.Clear();
            HasWorkItems = false;
            NeedsCount = 0;
            return;
        }

        _hydrating = true;
        try
        {
            HasGroup = true;
            GroupName = group.Name;
            MemberCountText = AppLangResources.Format(AppLangResourceKind.BotGroupMemberCount, group.Members.Count);

            var avatars = BotGroupUiHelpers.BuildAvatarCluster(group, _bots, _icons, 4);
            GroupAvatars.Clear();
            foreach (var tile in avatars)
            {
                GroupAvatars.Add(tile);
            }
            HasSingleAvatar = avatars.Count == 1;
            HasMultiAvatar = avatars.Count > 1;
            SingleAvatar = avatars.Count > 0 ? avatars[0] : null;

            Members.Clear();
            DefaultBotOptions.Clear();
            var memberBots = new List<BotDefinition>(group.Members.Count);
            var canRemove = group.Members.Count > 1;
            foreach (var member in group.Members)
            {
                var bot = _bots.Bots.FirstOrDefault(b => string.Equals(b.Id, member.BotId, StringComparison.Ordinal));
                if (bot is null)
                {
                    continue;
                }
                memberBots.Add(bot);
                var avatar = BotGroupUiHelpers.BuildBotAvatar(bot.Id, bot.Name, bot.Color, bot.Icon, bot.Image, _icons);
                var agentName = _catalog.Find(bot.AgentId)?.Name ?? bot.AgentId;
                Members.Add(new BotGroupRightMemberViewModel(
                    bot,
                    avatar,
                    agentName,
                    canRemove,
                    JumpToBot,
                    RemoveMemberAsync));
                DefaultBotOptions.Add(new SelectOption(bot.Id, bot.Name));
            }

            RebuildMaxHopsOptions(group.MaxHops);
            SelectedReplyModeKind = group.ReplyMode.Kind switch
            {
                BotGroupReplyModeKind.All => "all",
                BotGroupReplyModeKind.Bot => "bot",
                _ => "mentions",
            };
            SelectedDefaultBotId = !string.IsNullOrWhiteSpace(group.ReplyMode.BotId)
                && memberBots.Any(b => string.Equals(b.Id, group.ReplyMode.BotId, StringComparison.Ordinal))
                    ? group.ReplyMode.BotId!
                    : (memberBots.FirstOrDefault()?.Id ?? "");
            SelectedMaxHopsKey = Math.Clamp(group.MaxHops, 1, 30).ToString();

            WorkItems.Clear();
            var aggregated = memberBots
                .SelectMany(b => b.Items
                    .Where(i => i.Status != BotWorkItemStatus.Canceled)
                    .Select(i => (Bot: b, Item: i)))
                .OrderBy(pair => pair.Item.Status == BotWorkItemStatus.NeedsYou ? 0 : 1)
                .ThenByDescending(pair => pair.Item.Number)
                .ToList();

            var needs = 0;
            foreach (var (bot, item) in aggregated)
            {
                if (item.Status == BotWorkItemStatus.NeedsYou)
                {
                    needs++;
                }
                WorkItems.Add(new BotGroupWorkItemCardViewModel(
                    bot,
                    item,
                    (botId, itemId, chosen) => _bots.ResolveWorkItem(botId, itemId, chosen)));
            }
            HasWorkItems = WorkItems.Count > 0;
            NeedsCount = needs;
            OnPropertyChanged(nameof(ShowDefaultBotPicker));
        }
        finally
        {
            _hydrating = false;
        }
    }

    private void JumpToBot(string botId) => _bots.SelectBot(botId);

    private async Task RemoveMemberAsync(string botId)
    {
        var group = _groups.CurrentGroup;
        if (group is null || group.Members.Count <= 1)
        {
            return;
        }
        var nextBotIds = group.Members
            .Select(m => m.BotId)
            .Where(id => !string.Equals(id, botId, StringComparison.Ordinal))
            .ToList();
        if (nextBotIds.Count == 0)
        {
            return;
        }
        await _groups.UpdateGroupAsync(new UpdateBotGroupRequest(group.Id, group.Revision, BotIds: nextBotIds));
    }

    [RelayCommand]
    private async Task ManageMembersAsync()
    {
        var group = _groups.CurrentGroup;
        if (group is null)
        {
            return;
        }
        var result = await _dialogs.AskEditBotGroupAsync(group, _bots.Bots);
        if (result is null)
        {
            return;
        }
        await _groups.UpdateGroupAsync(new UpdateBotGroupRequest(
            group.Id,
            group.Revision,
            result.Name,
            result.BotIds,
            result.ReplyMode,
            result.MaxHops));
    }

    [RelayCommand]
    private async Task CreateThreadAsync()
    {
        if (_groups.CurrentGroup is not { } group)
        {
            return;
        }
        await _groups.CreateThreadAsync(group.Id);
    }

    [RelayCommand]
    private async Task RenameGroupAsync()
    {
        if (_groups.CurrentGroup is not { } group)
        {
            return;
        }
        var name = await _dialogs.AskNameAsync(
            AppLangResources.Get(AppLangResourceKind.BotGroupRenameTitle),
            AppLangResources.Get(AppLangResourceKind.BotGroupDialogNameLabel),
            group.Name,
            AppLangResources.Get(AppLangResourceKind.Save));
        if (!string.IsNullOrWhiteSpace(name))
        {
            await _groups.UpdateGroupAsync(new UpdateBotGroupRequest(group.Id, group.Revision, Name: name));
        }
    }

    [RelayCommand]
    private async Task DeleteGroupAsync()
    {
        if (_groups.CurrentGroup is not { } group)
        {
            return;
        }
        var ok = await _dialogs.ConfirmAsync(
            AppLangResources.Format(AppLangResourceKind.BotGroupDeleteConfirmTitle, group.Name),
            AppLangResources.Get(AppLangResourceKind.BotGroupDeleteConfirmBody),
            AppLangResources.Get(AppLangResourceKind.BotGroupMenuDelete),
            isDanger: true);
        if (ok)
        {
            await _groups.DeleteGroupAsync(group.Id);
        }
    }
}
