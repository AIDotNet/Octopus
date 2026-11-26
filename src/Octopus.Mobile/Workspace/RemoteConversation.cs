using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Octopus.App.Common.Mentions;

namespace Octopus.Mobile.Workspace;

/// <summary>
/// A GUI conversation of a host as the phone shows it (design/ui/移动端功能扩展方案.html, G-A): the host keeps the
/// conversation, this is a flattened copy of its timeline kept in step by <see cref="ConversationTimeline"/>, and what
/// the person does goes back to the host. The computer and the phone look at the same conversation, so a card answered
/// on one closes on the other. Everything here changes on the UI thread. Disposing it stops following the host.
/// </summary>
public abstract partial class RemoteConversation : ObservableObject, IDisposable
{
    protected RemoteConversation(RemoteSession session)
    {
        Session = session;
        Timeline = new ConversationTimeline(this);
    }

    public RemoteSession Session { get; }

    /// <summary>What applies the host's snapshots and updates to this conversation.</summary>
    public ConversationTimeline Timeline { get; }

    /// <summary>The latest entries in order, oldest first.</summary>
    public ObservableCollection<ConversationEntry> Entries { get; } = [];

    [ObservableProperty] public partial ConversationState State { get; internal set; }
    /// <summary>A line about what it is doing (starting the agent, installing an adapter…); null when there is nothing to say.</summary>
    [ObservableProperty] public partial string? StatusDetail { get; internal set; }
    /// <summary>Why it failed, while <see cref="State"/> is <see cref="ConversationState.Error"/>.</summary>
    [ObservableProperty] public partial string? ErrorText { get; internal set; }
    /// <summary>The agent's name.</summary>
    [ObservableProperty] public partial string AgentName { get; internal set; } = "";
    /// <summary>Older entries were left out: they are on the computer.</summary>
    [ObservableProperty] public partial bool HasEarlier { get; internal set; }
    /// <summary>Prompts queued on the host while a turn runs.</summary>
    [ObservableProperty] public partial int QueuedCount { get; internal set; }

    public ConversationComposer Composer { get; } = new();

    /// <summary>Sends a prompt; while a turn runs the host queues it.</summary>
    public abstract Task SendAsync(string text, IReadOnlyList<string>? attachments = null, CancellationToken cancellationToken = default);

    /// <summary>True when the host takes inline @session / @agent mentions.</summary>
    public virtual bool SupportsMentions => false;

    /// <summary>Sessions and agents the prompt may @-mention; everything when <paramref name="query"/> is empty.</summary>
    public virtual Task<IReadOnlyList<ConversationMentionTarget>> ListMentionTargetsAsync(string query, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ConversationMentionTarget>>([]);

    /// <summary>Sends a prompt whose text @-mentions <paramref name="mentions"/> (in order of appearance).</summary>
    public virtual Task SendAsync(string text, IReadOnlyList<string>? attachments, IReadOnlyList<MentionToken> mentions,
        CancellationToken cancellationToken = default) =>
        SendAsync(text, attachments, cancellationToken);

    /// <summary>Stops the turn that runs.</summary>
    public abstract Task CancelAsync(CancellationToken cancellationToken = default);

    /// <summary>Answers a permission card with one of its options.</summary>
    public abstract Task RespondAsync(ConversationPermissionEntry card, string optionId, CancellationToken cancellationToken = default);

    /// <summary>Answers a question card: the chosen choice ids and the typed text, by field id.</summary>
    public abstract Task AnswerAsync(ConversationQuestionEntry card, IReadOnlyDictionary<string, IReadOnlyList<string>> answers,
        IReadOnlyDictionary<string, string> freeText, CancellationToken cancellationToken = default);

    /// <summary>Chooses another model, mode or effort.</summary>
    public abstract Task SetOptionAsync(ConversationOptionKind kind, string value, CancellationToken cancellationToken = default);

    /// <summary>Reads the conversation from the host again (a fresh snapshot).</summary>
    public virtual Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public virtual void Dispose() => GC.SuppressFinalize(this);
}
