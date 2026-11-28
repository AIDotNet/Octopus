using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Collab;

namespace Octopus.App.AgentGui;

/// <summary>One compact capsule in Scheme C's docked top collaboration topology ribbon.</summary>
public sealed partial class CollabRibbonNodeViewModel : ObservableObject
{
    private readonly Action _activate;

    public CollabRibbonNodeViewModel(
        string id,
        string kind,
        string agentId,
        string label,
        string status,
        string? meta,
        bool isCurrent,
        Action activate,
        string? peerItemId = null)
    {
        Id = id;
        Kind = kind;
        AgentId = agentId;
        Label = label;
        Status = status;
        Meta = meta;
        IsCurrent = isCurrent;
        PeerItemId = peerItemId;
        _activate = activate;
    }

    public string Id { get; }
    /// <summary><c>parent</c>, <c>self</c>, <c>child</c>, or <c>peer</c>.</summary>
    public string Kind { get; }
    public string AgentId { get; }
    [ObservableProperty]
    public partial string Label { get; set; } = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorking))]
    [NotifyPropertyChangedFor(nameof(IsWaiting))]
    [NotifyPropertyChangedFor(nameof(IsDone))]
    public partial string Status { get; set; } = "";
    public string? Meta { get; }
    public bool IsCurrent { get; }
    public string? PeerItemId { get; }

    public bool MatchesItem(string itemId) =>
        Kind == "peer"
            ? string.Equals(PeerItemId, itemId, StringComparison.Ordinal)
            : string.Equals(Id, itemId, StringComparison.Ordinal);

    public bool IsParent => Kind == "parent";
    public bool IsChild => Kind == "child";
    public bool IsPeer => Kind == "peer";
    public bool IsWorking => Status == "working";
    public bool IsWaiting => Status == "waiting_approval";
    public bool IsDone => Status == "done";
    public bool HasMeta => !string.IsNullOrWhiteSpace(Meta);
    public string PrefixGlyph => Kind switch
    {
        "child" => "↳",
        "peer" => "⇄",
        "parent" => "↑",
        _ => "",
    };
    public bool HasPrefixGlyph => PrefixGlyph.Length > 0;

    [RelayCommand]
    private void Activate() => _activate();
}

/// <summary>Permission approval bubbled up from a spawned child agent to its parent session.</summary>
public sealed partial class CollabBubbledApprovalViewModel : ObservableObject
{
    private readonly CollabBubbledApproval _approval;
    private readonly Action<string> _focusChild;

    public CollabBubbledApprovalViewModel(CollabBubbledApproval approval, Action<string> focusChild)
    {
        _approval = approval;
        _focusChild = focusChild;
    }

    public string ApprovalId => _approval.ApprovalId;
    public string ChildItemId => _approval.ChildItemId;
    public string ChildAgentId => _approval.ChildAgentId;
    public string ChildTitle => _approval.ChildTitle;
    public string ToolTitle => _approval.ToolTitle;
    public string Detail => string.IsNullOrWhiteSpace(_approval.Detail) ? _approval.ToolTitle : _approval.Detail!;
    public bool HasWarning => !string.IsNullOrWhiteSpace(_approval.WarningMessage);
    public string? WarningMessage => _approval.WarningMessage;

    [RelayCommand]
    private void AllowOnce() => _approval.TryRespond(allow: true);

    [RelayCommand]
    private void Deny() => _approval.TryRespond(allow: false);

    [RelayCommand]
    private void InspectChild() => _focusChild(ChildItemId);
}

/// <summary>One message inside the collapsible negotiation thread strip.</summary>
public sealed class CollabThreadMessageViewModel(CollabMessageRecord message)
{
    public string MessageId { get; } = message.MessageId;
    public string FromLabel { get; } = message.FromLabel;
    public string? FromAgentId { get; } = message.FromAgentId;
    public bool HasAgentIcon { get; } = !message.IsUserInterjection && !string.IsNullOrWhiteSpace(message.FromAgentId);
    public bool IsUserInterjection { get; } = message.IsUserInterjection;
    public string RoundBadge { get; } = message.IsUserInterjection ? "User" : $"R{message.Round}";
    public string Text { get; } = message.Text;
    public string TimeText { get; } = message.Timestamp.ToLocalTime().ToString("HH:mm");
}

/// <summary>Active negotiation thread view state shown inline under Scheme C's ribbon when toggled.</summary>
public sealed partial class CollabThreadPanelViewModel : ObservableObject
{
    private readonly Func<string, string, bool, Task> _interject;
    private readonly Action _close;

    public CollabThreadPanelViewModel(CollabThreadRecord thread, Func<string, string, bool, Task> interject, Action close)
    {
        ThreadId = thread.ThreadId;
        _interject = interject;
        _close = close;
        UpdateFrom(thread);
    }

    public string ThreadId { get; }
    [ObservableProperty] public partial string Title { get; private set; } = "";
    [ObservableProperty] public partial string HopsText { get; private set; } = "";
    [ObservableProperty] public partial string Status { get; private set; } = "";
    [ObservableProperty] public partial bool IsBudgetPaused { get; private set; }
    public ObservableCollection<CollabThreadMessageViewModel> Messages { get; } = [];

    [ObservableProperty] public partial string InterjectText { get; set; } = "";

    public void UpdateFrom(CollabThreadRecord thread)
    {
        Title = thread.Title;
        HopsText = $"{thread.Hops}/{thread.MaxHops}";
        Status = thread.StatusKey;
        IsBudgetPaused = thread.Status == CollabThreadStatus.PausedBudget;

        var incoming = thread.Messages;
        var samePrefix = Messages.Count <= incoming.Count;
        if (samePrefix)
        {
            for (var i = 0; i < Messages.Count; i++)
            {
                if (!string.Equals(Messages[i].MessageId, incoming[i].MessageId, StringComparison.Ordinal)
                    || !string.Equals(Messages[i].Text, incoming[i].Text, StringComparison.Ordinal))
                {
                    samePrefix = false;
                    break;
                }
            }
        }

        if (samePrefix)
        {
            for (var i = Messages.Count; i < incoming.Count; i++)
            {
                Messages.Add(new CollabThreadMessageViewModel(incoming[i]));
            }
        }
        else
        {
            Messages.Clear();
            foreach (var msg in incoming)
            {
                Messages.Add(new CollabThreadMessageViewModel(msg));
            }
        }
    }

    [RelayCommand]
    private async Task SendInterjectAsync()
    {
        var text = InterjectText.Trim();
        if (text.Length == 0) return;
        InterjectText = "";
        await _interject(ThreadId, text, IsBudgetPaused);
    }

    [RelayCommand]
    private Task ExtendBudgetAsync() => _interject(ThreadId, "Continue (+4 rounds granted by user).", true);

    [RelayCommand]
    private void Close() => _close();
}
