using System.Collections.ObjectModel;
using Octopus.Remote.Protocol;

namespace Octopus.Mobile.Workspace;

/// <summary>What became of an update given to <see cref="ConversationTimeline.Apply(AcpUpdateMessage)"/>.</summary>
public enum ConversationUpdateResult
{
    Applied,
    /// <summary>One that was applied already (or belongs to an older snapshot): nothing changed.</summary>
    Ignored,
    /// <summary>Arrived before the snapshot it follows: kept until the snapshot is there.</summary>
    Queued,
    /// <summary>
    /// Updates were missed (the phone was away): nothing changed, and the conversation waits for a fresh snapshot.
    /// The caller attaches again (AcpMessages.cs: a device that sees a gap in Sequence attaches again).
    /// </summary>
    Gap,
}

/// <summary>
/// Applies a host's GUI conversation to a <see cref="RemoteConversation"/>: the snapshot an attach returns, then each
/// update in order of its sequence number. Entries keep their objects while the host keeps their ids (a page showing
/// one keeps what was expanded or typed into it); a new one goes after the entry it names, or at the end. Pure: no
/// threads, no network, so it is tested on its own and used by the demo as well as the real host.
/// </summary>
public sealed class ConversationTimeline
{
    private readonly RemoteConversation _target;
    // Updates that came before the snapshot they follow (the event and the attach's answer reach the UI thread in either order).
    private readonly List<AcpUpdateMessage> _early = [];

    internal ConversationTimeline(RemoteConversation target) => _target = target;

    /// <summary>The number of the last update applied (or of the snapshot).</summary>
    public long Sequence { get; private set; }

    /// <summary>A snapshot was applied and no gap was seen since: updates apply.</summary>
    public bool HasSnapshot { get; private set; }

    /// <summary>
    /// Shows the conversation as a fresh attach returned it, then the updates that came early. Returns
    /// <see cref="ConversationUpdateResult.Gap"/> when one of those shows that more were missed.
    /// </summary>
    public ConversationUpdateResult ApplySnapshot(AcpSnapshotMessage snapshot)
    {
        var target = _target;
        Sequence = snapshot.Sequence;
        HasSnapshot = true;
        target.State = StateOf(snapshot.State);
        target.StatusDetail = NullIfEmpty(snapshot.StatusDetail);
        target.ErrorText = NullIfEmpty(snapshot.ErrorText);
        target.AgentName = snapshot.AgentName;
        target.HasEarlier = snapshot.HasEarlier;
        target.QueuedCount = snapshot.QueuedCount;
        ApplyComposer(target.Composer, snapshot.Composer);
        var known = target.Entries.GroupBy(entry => entry.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        Sync(target.Entries, [.. snapshot.Entries.Select(message => Upsert(known.GetValueOrDefault(message.Id), message))]);

        var early = _early.Where(update => update.Sequence > Sequence).OrderBy(update => update.Sequence).ToList();
        _early.Clear();
        foreach (var update in early)
        {
            if (Apply(update) == ConversationUpdateResult.Gap) return ConversationUpdateResult.Gap;
        }
        return ConversationUpdateResult.Applied;
    }

    /// <summary>Applies one update when it is the next one; see <see cref="ConversationUpdateResult"/> for the rest.</summary>
    public ConversationUpdateResult Apply(AcpUpdateMessage update)
    {
        if (!HasSnapshot)
        {
            _early.Add(update);
            return ConversationUpdateResult.Queued;
        }
        if (update.Sequence <= Sequence) return ConversationUpdateResult.Ignored;
        if (update.Sequence > Sequence + 1)
        {
            Reset();
            return ConversationUpdateResult.Gap;
        }
        Sequence = update.Sequence;
        var target = _target;
        var entries = target.Entries;
        foreach (var id in update.Removed)
        {
            for (var i = entries.Count - 1; i >= 0; i--)
            {
                if (entries[i].Id == id) entries.RemoveAt(i);
            }
        }
        foreach (var message in update.Upserts)
        {
            var index = IndexOf(entries, message.Id);
            if (index >= 0)
            {
                var entry = Upsert(entries[index], message);
                // The kind of an id changed (it should not): the new object takes the old one's place.
                if (!ReferenceEquals(entry, entries[index])) entries[index] = entry;
                continue;
            }
            var created = Upsert(null, message);
            var after = message.AfterId is { } afterId ? IndexOf(entries, afterId) : -1;
            if (after >= 0) entries.Insert(after + 1, created);
            else entries.Add(created);
        }
        if (update.State is { } state)
        {
            var next = StateOf(state);
            // A new state says what is going on now: a line or a failure of the old one does not carry over unless repeated.
            if (update.StatusDetail is null) target.StatusDetail = null;
            if (update.ErrorText is null && next != ConversationState.Error) target.ErrorText = null;
            target.State = next;
        }
        if (update.StatusDetail is not null) target.StatusDetail = NullIfEmpty(update.StatusDetail);
        if (update.ErrorText is not null) target.ErrorText = NullIfEmpty(update.ErrorText);
        if (update.QueuedCount is { } queued) target.QueuedCount = queued;
        if (update.Composer is { } composer) ApplyComposer(target.Composer, composer);
        return ConversationUpdateResult.Applied;
    }

    /// <summary>The connection to the host went away: until a fresh snapshot, updates are kept rather than applied.</summary>
    public void Reset()
    {
        HasSnapshot = false;
        _early.Clear();
    }

    public static ConversationState StateOf(string? state) => state switch
    {
        AcpStates.Ready => ConversationState.Ready,
        AcpStates.Working => ConversationState.Working,
        AcpStates.Waiting => ConversationState.Waiting,
        AcpStates.Error => ConversationState.Error,
        _ => ConversationState.Connecting,
    };

    public static string KindOf(ConversationOptionKind kind) => kind switch
    {
        ConversationOptionKind.Model => AcpOptionKinds.Model,
        ConversationOptionKind.Mode => AcpOptionKinds.Mode,
        _ => AcpOptionKinds.Effort,
    };

    // ---- entries ----

    // The entry with the message's content: the same object when it is of the message's kind, a new one otherwise.
    private static ConversationEntry Upsert(ConversationEntry? existing, AcpEntryMessage message)
    {
        switch (message.Kind)
        {
            case AcpEntryKinds.User:
            {
                var entry = existing as ConversationUserEntry ?? new ConversationUserEntry(message.Id);
                entry.Text = message.Text ?? "";
                if (!entry.Attachments.SequenceEqual(message.Attachments ?? [])) entry.Attachments = [.. message.Attachments ?? []];
                return entry;
            }
            case AcpEntryKinds.Permission:
            {
                var entry = existing as ConversationPermissionEntry ?? new ConversationPermissionEntry(message.Id);
                var card = message.Permission ?? new AcpPermissionMessage();
                entry.Title = card.Title;
                entry.Detail = NullIfEmpty(card.Detail);
                entry.Warning = NullIfEmpty(card.Warning);
                if (!entry.Options.Select(option => (option.Id, option.Label, option.Kind)).SequenceEqual(card.Options.Select(option => (option.Id, option.Label, option.Detail))))
                {
                    entry.Options = [.. card.Options.Select(option => new ConversationPermissionOption(entry, option.Id, option.Label, option.Detail))];
                }
                entry.ResolvedSummary = NullIfEmpty(card.ResolvedSummary);
                entry.IsResolved = card.IsResolved;
                if (card.IsResolved) entry.IsAnswering = false;
                return entry;
            }
            case AcpEntryKinds.Question:
            {
                var entry = existing as ConversationQuestionEntry ?? new ConversationQuestionEntry(message.Id);
                var card = message.Question ?? new AcpQuestionMessage();
                entry.Title = card.Title;
                var fields = card.Fields.Select(field => new ConversationQuestionField(field.Id, field.Prompt, field.MultiSelect, field.AllowFreeText,
                    field.Choices.Select(choice => new ConversationOption(choice.Id, choice.Label, NullIfEmpty(choice.Detail))))).ToList();
                // The same questions again (only the state changed): what was picked so far stays.
                if (fields.Count != entry.Fields.Count || !fields.Zip(entry.Fields).All(pair => pair.Second.IsSameAs(pair.First))) entry.Fields = fields;
                entry.ResolvedSummary = NullIfEmpty(card.ResolvedSummary);
                entry.IsResolved = card.IsResolved;
                if (card.IsResolved) entry.IsAnswering = false;
                return entry;
            }
            default:
            {
                // An assistant turn; a kind this phone does not know yet is shown as one too (its text, if any).
                var entry = existing as ConversationAssistantEntry ?? new ConversationAssistantEntry(message.Id);
                entry.Text = message.Text ?? "";
                entry.Thought = NullIfEmpty(message.Thought);
                entry.IsStreaming = message.IsStreaming;
                entry.ErrorText = NullIfEmpty(message.ErrorText);
                entry.Duration = NullIfEmpty(message.Duration);
                var plan = (message.Plan ?? []).Select(item => new ConversationPlanItem(item.Content, item.Status)).ToList();
                if (!entry.Plan.SequenceEqual(plan)) entry.Plan = plan;
                var tools = entry.Tools.ToDictionary(tool => tool.Id, StringComparer.Ordinal);
                Sync(entry.Tools, [.. (message.Tools ?? []).DistinctBy(tool => tool.Id).Select(tool => Fill(tools.GetValueOrDefault(tool.Id) ?? new ConversationTool(tool.Id), tool))]);
                entry.SyncWorkArea();
                return entry;
            }
        }
    }

    private static ConversationTool Fill(ConversationTool tool, AcpToolMessage message)
    {
        tool.Title = message.Title;
        tool.Kind = message.Kind;
        tool.Status = message.Status;
        tool.Command = NullIfEmpty(message.Command);
        tool.Path = NullIfEmpty(message.Path);
        tool.Added = message.Added;
        tool.Removed = message.Removed;
        tool.ExitCode = message.ExitCode;
        tool.Output = NullIfEmpty(message.Output);
        var diff = (message.Diff ?? []).Select(line => new ConversationDiffLine(line)).ToList();
        if (!tool.Diff.SequenceEqual(diff)) tool.Diff = diff;
        return tool;
    }

    private static void ApplyComposer(ConversationComposer composer, AcpComposerMessage message)
    {
        static IReadOnlyList<ConversationOption> Options(List<AcpOptionMessage> options) =>
            [.. options.Select(option => new ConversationOption(option.Id, option.Label, NullIfEmpty(option.Detail)))];

        var models = Options(message.Models);
        if (!composer.Models.SequenceEqual(models)) composer.Models = models;
        composer.Model = message.Model;
        var modes = Options(message.Modes);
        if (!composer.Modes.SequenceEqual(modes)) composer.Modes = modes;
        composer.Mode = message.Mode;
        var efforts = Options(message.Efforts);
        if (!composer.Efforts.SequenceEqual(efforts)) composer.Efforts = efforts;
        composer.Effort = message.Effort;
    }

    // ---- helpers ----

    private static int IndexOf(ObservableCollection<ConversationEntry> entries, string id)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Id == id) return i;
        }
        return -1;
    }

    /// <summary>Makes a collection hold the target's objects in the target's order with as few changes as it takes (a page keeps its rows).</summary>
    internal static void Sync<T>(ObservableCollection<T> collection, IReadOnlyList<T> target) where T : class
    {
        for (var i = 0; i < target.Count; i++)
        {
            if (i < collection.Count && ReferenceEquals(collection[i], target[i])) continue;
            var at = -1;
            for (var j = i + 1; j < collection.Count; j++)
            {
                if (ReferenceEquals(collection[j], target[i]))
                {
                    at = j;
                    break;
                }
            }
            if (at >= 0) collection.Move(at, i);
            else collection.Insert(i, target[i]);
        }
        while (collection.Count > target.Count) collection.RemoveAt(collection.Count - 1);
    }

    private static string? NullIfEmpty(string? text) => string.IsNullOrEmpty(text) ? null : text;
}
