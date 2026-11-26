using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Threading;
using Octopus.App.AgentGui;
using Octopus.App.Common.Mentions;
using Octopus.App.Localization;
using Octopus.App.Workspace;
using Octopus.Core.Projects;
using Octopus.Remote.Host;
using Octopus.Remote.Protocol;

namespace Octopus.App.Remote;

/// <summary>
/// The GUI conversations of this computer as devices see them (design/ui/移动端功能扩展方案.html ④, G-A). The
/// conversation stays in its tab here: a device gets a flattened copy of the tab's timeline and sends prompts, answers
/// and choices back through the same paths the tab's own controls use, so the computer and the device share one
/// conversation. Everything here runs on the UI thread, where the tab's view model lives.
/// </summary>
internal sealed class HostConversations(ITabContentRegistry tabs)
{
    /// <summary>How long changes collect before the devices are told once.</summary>
    internal static readonly TimeSpan CoalesceDelay = TimeSpan.FromMilliseconds(100);

    // One wire message is at most a megabyte (SecureChannel.MaxMessageLength); what a conversation sends stays well
    // below it, whatever the agent printed.
    private const int MessageBudget = 768 * 1024;
    private const int EntryBudget = 256 * 1024;

    // Ids for the life of a timeline item on this computer, the same for every device and every attachment.
    private static readonly ConditionalWeakTable<AgentTimelineItem, string> Ids = new();
    private static long s_lastId;

    private readonly Dictionary<string, Conversation> _conversations = new(StringComparer.Ordinal);

    public IAcpAttachment Attach(Project project, ProjectItem item, Action<AcpUpdateMessage> updated)
    {
        var model = ModelOf(project, item);
        if (_conversations.TryGetValue(item.Id, out var conversation) && !ReferenceEquals(conversation.Model, model))
        {
            // The tab was closed and built again: the old view model says nothing any more.
            conversation.Stop();
            conversation = null;
        }
        if (conversation is null)
        {
            conversation = new Conversation(this, item.Id, model);
            _conversations[item.Id] = conversation;
        }
        return conversation.Add(updated);
    }

    public async Task PromptAsync(Project project, ProjectItem item, string text, IReadOnlyList<string> attachments,
        IReadOnlyList<AcpMentionMessage>? mentions = null)
    {
        var model = ModelOf(project, item);
        // Only sessions and agents, and only tokens the text really contains: the rest of the prompt path trusts them.
        var tokens = (mentions ?? [])
            .Where(m => m.Kind is "session" or "agent" && !string.IsNullOrWhiteSpace(m.TargetId)
                && m.Text.StartsWith('@') && text.Contains(m.Text, StringComparison.Ordinal))
            .Select(m => new MentionToken(m.Kind, m.TargetId, m.Text, m.AgentId))
            .ToList();
        if (!await model.SendExternalPromptAsync(text, attachments, tokens))
        {
            throw new RemoteException(RemoteMethods.NotAllowed, "The agent is not connected on the host yet.");
        }
    }

    public AcpMentionTargetsMessage ListMentionTargets(Project project, ProjectItem item, string query)
    {
        var model = ModelOf(project, item);
        var result = new AcpMentionTargetsMessage();
        foreach (var suggestion in model.ListCollabMentionSuggestions(query))
        {
            result.Targets.Add(new AcpMentionTargetMessage
            {
                Kind = suggestion.Kind,
                TargetId = suggestion.TargetId,
                Name = suggestion.IsSession ? model.MentionAgentName(suggestion) : suggestion.RelativePath,
                Detail = suggestion.IsSession ? suggestion.RelativePath : null,
                AgentId = suggestion.AgentId,
                Subtitle = suggestion.Subtitle,
            });
        }
        return result;
    }

    public Task CancelAsync(Project project, ProjectItem item)
    {
        var model = ModelOf(project, item);
        // Like the stop button: the turn settles in its own time; the device sees it in the updates.
        _ = model.CancelTurnAsync();
        return Task.CompletedTask;
    }

    public void Respond(Project project, ProjectItem item, AcpRespondMessage answer)
    {
        var model = ModelOf(project, item);
        var card = model.Timeline.FirstOrDefault(entry => Ids.TryGetValue(entry, out var id) && id == answer.EntryId)
            ?? throw new RemoteException(RemoteMethods.NotFound, "That card is no longer in the conversation.");
        switch (card)
        {
            case PermissionCardItem permission:
                // Answered here or on another device already: the update that closes the card is on its way.
                if (permission.IsResolved) return;
                var action = permission.Actions.FirstOrDefault(a => a.OptionId == answer.OptionId)
                    ?? throw new RemoteException(RemoteProtocol.InvalidParams, "The card has no such option.");
                action.ChooseCommand.Execute(null);
                break;
            case PlanApprovalCardItem plan:
                if (plan.IsResolved) return;
                if (answer.OptionId is { } optionId)
                {
                    var chosen = plan.Actions.FirstOrDefault(a => a.OptionId == optionId)
                        ?? throw new RemoteException(RemoteProtocol.InvalidParams, "The card has no such option.");
                    chosen.ChooseCommand.Execute(null);
                }
                else if (answer.FreeText?.Values.FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) is { } feedback)
                {
                    // The plan goes back with what should change, as "revise" does here.
                    plan.FeedbackText = feedback;
                    plan.SubmitFeedbackCommand.Execute(null);
                }
                else
                {
                    throw new RemoteException(RemoteProtocol.InvalidParams, "An option or feedback is needed.");
                }
                break;
            case QuestionCardItem question:
                if (question.IsResolved) return;
                var parsed = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                foreach (var field in question.Fields)
                {
                    if (answer.Answers?.TryGetValue(field.Id, out var ids) == true)
                    {
                        var wanted = ids.ToHashSet(StringComparer.Ordinal);
                        if (wanted.Any(id => field.Choices.All(c => c.Id != id))) throw new RemoteException(RemoteProtocol.InvalidParams, $"The question '{field.Id}' has no such choice.");
                        if (!field.MultiSelect && wanted.Count > 1) throw new RemoteException(RemoteProtocol.InvalidParams, $"The question '{field.Id}' takes one choice.");
                        parsed[field.Id] = wanted;
                    }
                }
                foreach (var field in question.Fields)
                {
                    var hasFree = field.AllowFreeText && answer.FreeText?.TryGetValue(field.Id, out var freeText) == true && !string.IsNullOrWhiteSpace(freeText);
                    if (parsed.TryGetValue(field.Id, out var wanted))
                    {
                        foreach (var choice in field.Choices) choice.IsSelected = wanted.Contains(choice.Id);
                    }
                    else if (answer.Answers is not null || hasFree)
                    {
                        // Avoid keeping the constructor's default first choice selected when the device answered with free text only.
                        foreach (var choice in field.Choices) choice.IsSelected = false;
                    }
                    if (field.AllowFreeText && answer.FreeText?.TryGetValue(field.Id, out var text) == true)
                    {
                        field.FreeText = text;
                    }
                }
                question.SubmitCommand.Execute(null);
                break;
            default:
                throw new RemoteException(RemoteProtocol.InvalidParams, "That entry is not a card.");
        }
    }

    public void SetOption(Project project, ProjectItem item, AcpSetOptionMessage option)
    {
        var composer = ModelOf(project, item).Composer;
        var value = option.Value;
        // The same properties the pickers above the input set: the tab hands the choice on to the agent.
        switch (option.Kind)
        {
            case AcpOptionKinds.Model when composer.ShowModelSelector && composer.ModelChoices.Any(c => c.Id == value):
                composer.SelectedModelId = value;
                break;
            case AcpOptionKinds.Mode when composer.ShowModeSelector && composer.ModeChoices.Any(c => c.Id == value):
                composer.SelectedModeId = value;
                break;
            case AcpOptionKinds.Mode when !composer.ShowModeSelector && composer.ShowPermissionSelector && composer.PermissionChoices.Any(c => c.Id == value):
                composer.SelectedPermissionId = value;
                break;
            case AcpOptionKinds.Effort when composer.ShowEffortSelector && composer.EffortChoices.Any(c => c.Id == value):
                composer.SelectedEffortId = value;
                break;
            default:
                throw new RemoteException(RemoteProtocol.InvalidParams, $"'{value}' is not a {option.Kind} the agent offers.");
        }
    }

    private AgentGuiTabViewModel ModelOf(Project project, ProjectItem item)
    {
        var tab = tabs.GetOrCreate(project, item);
        if (tab.DataContext is not AgentGuiTabViewModel model)
        {
            throw new RemoteException(RemoteMethods.NotAllowed, "This agent cannot run as a GUI conversation on the host.");
        }
        // The view defers ConnectAsync until attached to the visual tree; a remote device may attach before the tab is shown.
        if (tab is AgentGuiTabView view) view.EnsureStarted();
        return model;
    }

    public void Dispose()
    {
        if (Dispatcher.UIThread.CheckAccess()) StopAll();
        else Dispatcher.UIThread.Post(StopAll);
    }

    private void StopAll()
    {
        foreach (var conversation in _conversations.Values.ToList()) conversation.Stop();
        _conversations.Clear();
    }

    private void Forget(Conversation conversation)
    {
        if (_conversations.TryGetValue(conversation.ItemId, out var current) && ReferenceEquals(current, conversation)) _conversations.Remove(conversation.ItemId);
    }

    private static string IdOf(AgentTimelineItem item) =>
        Ids.GetValue(item, static _ => "e" + Interlocked.Increment(ref s_lastId).ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// One tab's conversation while at least one device looks at it: watches the timeline, its items and the composer,
    /// and every <see cref="CoalesceDelay"/> sends what changed since the last time to every device.
    /// </summary>
    private sealed class Conversation
    {
        // Properties that move on their own while a turn runs (a clock, live ticker) or only fold the view here.
        private static readonly HashSet<string> Ignored = new(StringComparer.Ordinal)
        {
            nameof(AssistantTurnItem.LiveElapsedText), nameof(AssistantTurnItem.HasLiveElapsed),
            nameof(AssistantTurnItem.IsWorkExpanded), nameof(AssistantTurnItem.ExpandedWorkSteps),
            nameof(AssistantTurnItem.ShowLiveTicker), nameof(AssistantTurnItem.LiveTicker),
            nameof(AssistantTurnItem.WorkHeaderTitle), nameof(AssistantTurnItem.WorkHeaderCounts),
            nameof(AssistantTurnItem.HasWorkHeaderCounts), nameof(AssistantTurnItem.WorkHeaderFailedText),
            nameof(AssistantTurnItem.HasWorkFailures), nameof(AssistantTurnItem.WorkHeaderDurationText),
            nameof(AssistantTurnItem.HasWorkHeaderDuration), nameof(AssistantTurnItem.ShowWaitingBadge),
            nameof(AssistantTurnItem.HasWorkArea), nameof(AssistantTurnItem.CanExpandWork),
            nameof(AssistantTurnItem.WorkStepCount),
            nameof(PlanApprovalCardItem.IsFeedbackOpen), nameof(PlanApprovalCardItem.FeedbackText),
        };

        private static readonly HashSet<string> Header = new(StringComparer.Ordinal)
        {
            nameof(AgentGuiTabViewModel.State), nameof(AgentGuiTabViewModel.StatusDetail), nameof(AgentGuiTabViewModel.ErrorBanner),
            nameof(AgentGuiTabViewModel.IsAuthRequired), nameof(AgentGuiTabViewModel.QueuedCount),
        };

        private readonly HostConversations _owner;
        private readonly List<Attachment> _attachments = [];
        private readonly Dictionary<AgentTimelineItem, ItemWatcher> _watched = [];
        private readonly HashSet<AgentTimelineItem> _dirty = [];

        // What the devices were last told: the ids in order and each entry's byte size + content hash
        // (avoids retaining a duplicate serialized JSON string per timeline turn in memory).
        private List<string> _sentOrder = [];
        private readonly Dictionary<string, (AcpEntryMessage Entry, int ByteLength, ulong Hash)> _sent = new(StringComparer.Ordinal);
        private (string State, string? StatusDetail, string? ErrorText, int QueuedCount) _sentHeader;
        private ulong _sentComposerHash;
        private int _sentComposerLength;
        private bool _structureChanged;
        private bool _scheduled;
        private int _generation;

        public Conversation(HostConversations owner, string itemId, AgentGuiTabViewModel model)
        {
            _owner = owner;
            ItemId = itemId;
            Model = model;
        }

        public string ItemId { get; }
        public AgentGuiTabViewModel Model { get; }

        public IAcpAttachment Add(Action<AcpUpdateMessage> updated)
        {
            if (_attachments.Count == 0) Start();
            // What changed since the last update goes to the devices that have it, so the snapshot and every view agree.
            else Flush();
            var attachment = new Attachment(this, updated, BuildSnapshot());
            _attachments.Add(attachment);
            return attachment;
        }

        private void Remove(Attachment attachment)
        {
            if (_attachments.Remove(attachment) && _attachments.Count == 0) Stop();
        }

        public void Stop()
        {
            Model.Timeline.CollectionChanged -= OnTimelineChanged;
            Model.PropertyChanged -= OnModelChanged;
            Model.Composer.PropertyChanged -= OnComposerChanged;
            Model.Composer.ModelChoices.CollectionChanged -= OnComposerChoicesChanged;
            Model.Composer.ModeChoices.CollectionChanged -= OnComposerChoicesChanged;
            Model.Composer.PermissionChoices.CollectionChanged -= OnComposerChoicesChanged;
            Model.Composer.EffortChoices.CollectionChanged -= OnComposerChoicesChanged;
            foreach (var watcher in _watched.Values) watcher.Dispose();
            _watched.Clear();
            _dirty.Clear();
            _sent.Clear();
            _attachments.Clear();
            _generation++;
            _scheduled = false;
            _owner.Forget(this);
        }

        private void Start()
        {
            Model.Timeline.CollectionChanged += OnTimelineChanged;
            Model.PropertyChanged += OnModelChanged;
            Model.Composer.PropertyChanged += OnComposerChanged;
            Model.Composer.ModelChoices.CollectionChanged += OnComposerChoicesChanged;
            Model.Composer.ModeChoices.CollectionChanged += OnComposerChoicesChanged;
            Model.Composer.PermissionChoices.CollectionChanged += OnComposerChoicesChanged;
            Model.Composer.EffortChoices.CollectionChanged += OnComposerChoicesChanged;
            WatchItems();
            // What is there now is what the first snapshot shows; updates are measured against it.
            _sent.Clear();
            var order = new List<string>();
            foreach (var item in Model.Timeline)
            {
                if (ToEntry(item) is not { } entry) continue;
                order.Add(entry.Id);
                var (len, hash) = Fingerprint(entry);
                _sent[entry.Id] = (entry, len, hash);
            }
            _sentOrder = order;
            _sentHeader = HeaderNow();
            (_sentComposerLength, _sentComposerHash) = Fingerprint(ComposerNow());
            _dirty.Clear();
            _structureChanged = false;
        }

        private AcpSnapshotMessage BuildSnapshot()
        {
            var (state, statusDetail, errorText, queued) = _sentHeader;
            var snapshot = new AcpSnapshotMessage
            {
                ItemId = ItemId,
                State = state,
                StatusDetail = statusDetail,
                ErrorText = errorText,
                AgentName = Model.AgentName,
                QueuedCount = queued,
                Composer = ComposerNow(),
            };
            // The latest entries, as many as the limit and one message allow; older ones are only counted.
            var budget = MessageBudget;
            var taken = new List<AcpEntryMessage>();
            for (var i = _sentOrder.Count - 1; i >= 0 && taken.Count < AcpLimits.SnapshotEntries; i--)
            {
                var (entry, byteLength, _) = _sent[_sentOrder[i]];
                budget -= byteLength;
                if (budget < 0 && taken.Count > 0) break;
                taken.Add(entry);
            }
            taken.Reverse();
            snapshot.Entries = taken;
            snapshot.HasEarlier = taken.Count < _sentOrder.Count;
            return snapshot;
        }

        private void OnTimelineChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            _structureChanged = true;
            WatchItems();
            Schedule();
        }

        // Follows the items that are in the timeline now (including nested tool/plan/attachment collections); a new one counts as changed.
        private void WatchItems()
        {
            var now = Model.Timeline.ToHashSet();
            foreach (var gone in _watched.Keys.Where(item => !now.Contains(item)).ToList())
            {
                _watched[gone].Dispose();
                _watched.Remove(gone);
                _dirty.Remove(gone);
            }
            foreach (var item in now)
            {
                if (_watched.ContainsKey(item)) continue;
                _watched[item] = new ItemWatcher(item, MarkDirty);
                _dirty.Add(item);
            }
        }

        private void MarkDirty(AgentTimelineItem item)
        {
            _dirty.Add(item);
            Schedule();
        }

        private sealed class ItemWatcher : IDisposable
        {
            private readonly AgentTimelineItem _item;
            private readonly Action<AgentTimelineItem> _dirty;
            private readonly HashSet<ToolCallItemViewModel> _tools = [];

            public ItemWatcher(AgentTimelineItem item, Action<AgentTimelineItem> dirty)
            {
                _item = item;
                _dirty = dirty;
                item.PropertyChanged += OnItemPropertyChanged;
                switch (item)
                {
                    case AssistantTurnItem assistant:
                        assistant.ToolCalls.CollectionChanged += OnToolCallsChanged;
                        assistant.PlanEntries.CollectionChanged += OnNestedCollectionChanged;
                        SyncTools(assistant);
                        break;
                    case UserTurnItem user:
                        user.Attachments.CollectionChanged += OnNestedCollectionChanged;
                        break;
                }
            }

            public void Dispose()
            {
                _item.PropertyChanged -= OnItemPropertyChanged;
                switch (_item)
                {
                    case AssistantTurnItem assistant:
                        assistant.ToolCalls.CollectionChanged -= OnToolCallsChanged;
                        assistant.PlanEntries.CollectionChanged -= OnNestedCollectionChanged;
                        foreach (var tool in _tools) UnwatchTool(tool);
                        _tools.Clear();
                        break;
                    case UserTurnItem user:
                        user.Attachments.CollectionChanged -= OnNestedCollectionChanged;
                        break;
                }
            }

            private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
            {
                if (e.PropertyName is { } name && Ignored.Contains(name)) return;
                _dirty(_item);
            }

            private void OnNestedCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => _dirty(_item);

            private void OnToolCallsChanged(object? sender, NotifyCollectionChangedEventArgs e)
            {
                if (_item is AssistantTurnItem assistant) SyncTools(assistant);
                _dirty(_item);
            }

            private void SyncTools(AssistantTurnItem assistant)
            {
                var current = assistant.ToolCalls.ToHashSet();
                foreach (var gone in _tools.Where(t => !current.Contains(t)).ToList())
                {
                    UnwatchTool(gone);
                    _tools.Remove(gone);
                }
                foreach (var tool in current)
                {
                    if (!_tools.Add(tool)) continue;
                    tool.PropertyChanged += OnToolPropertyChanged;
                    tool.DiffLines.CollectionChanged += OnNestedCollectionChanged;
                }
            }

            private void UnwatchTool(ToolCallItemViewModel tool)
            {
                tool.PropertyChanged -= OnToolPropertyChanged;
                tool.DiffLines.CollectionChanged -= OnNestedCollectionChanged;
            }

            private void OnToolPropertyChanged(object? sender, PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(ToolCallItemViewModel.IsExpanded)) return;
                _dirty(_item);
            }
        }

        private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is null || Header.Contains(e.PropertyName)) Schedule();
        }

        private void OnComposerChanged(object? sender, PropertyChangedEventArgs e) => Schedule();

        private void OnComposerChoicesChanged(object? sender, NotifyCollectionChangedEventArgs e) => Schedule();

        private void Schedule()
        {
            if (_scheduled || _attachments.Count == 0) return;
            _scheduled = true;
            var generation = _generation;
            _ = Task.Delay(CoalesceDelay).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
            {
                if (generation == _generation) Flush();
            }), TaskScheduler.Default);
        }

        /// <summary>Tells every device what changed since the last update, if anything did.</summary>
        private void Flush()
        {
            _scheduled = false;
            _generation++;
            if (_attachments.Count == 0) return;

            var update = new AcpUpdateMessage { ItemId = ItemId };
            var changed = false;

            if (_structureChanged || _dirty.Count > 0)
            {
                var order = new List<string>();
                string? previous = null;
                foreach (var item in Model.Timeline)
                {
                    var id = IdOf(item);
                    var known = _sent.TryGetValue(id, out var prevSent);
                    if (!known || _dirty.Contains(item))
                    {
                        if (ToEntry(item) is not { } entry) continue;
                        var (len, hash) = Fingerprint(entry);
                        if (!known || prevSent.ByteLength != len || prevSent.Hash != hash)
                        {
                            _sent[id] = (entry, len, hash);
                            // A device that has the entry ignores this; one that lacks it (it was older than its
                            // snapshot) puts it in its place.
                            update.Upserts.Add(Positioned(entry, previous));
                        }
                    }
                    order.Add(id);
                    previous = id;
                }
                var present = order.ToHashSet(StringComparer.Ordinal);
                foreach (var id in _sentOrder.Where(id => !present.Contains(id)))
                {
                    update.Removed.Add(id);
                    _sent.Remove(id);
                }
                _sentOrder = order;
                _dirty.Clear();
                _structureChanged = false;
                changed = update.Upserts.Count > 0 || update.Removed.Count > 0;
            }

            var header = HeaderNow();
            if (header.State != _sentHeader.State) (update.State, changed) = (header.State, true);
            // Null in an update means "unchanged": a line that went away is sent as an empty one.
            if (header.StatusDetail != _sentHeader.StatusDetail) (update.StatusDetail, changed) = (header.StatusDetail ?? "", true);
            if (header.ErrorText != _sentHeader.ErrorText) (update.ErrorText, changed) = (header.ErrorText ?? "", true);
            if (header.QueuedCount != _sentHeader.QueuedCount) (update.QueuedCount, changed) = (header.QueuedCount, true);
            _sentHeader = header;

            var composer = ComposerNow();
            var (composerLen, composerHash) = Fingerprint(composer);
            if (composerLen != _sentComposerLength || composerHash != _sentComposerHash)
            {
                update.Composer = composer;
                changed = true;
                _sentComposerLength = composerLen;
                _sentComposerHash = composerHash;
            }

            if (!changed) return;
            foreach (var part in Split(update))
            {
                foreach (var attachment in _attachments.ToList()) attachment.Send(part);
            }
        }

        // The entry as kept for snapshots has no position; the one in an update says where it goes.
        private static AcpEntryMessage Positioned(AcpEntryMessage entry, string? afterId) => afterId is null ? entry : new AcpEntryMessage
        {
            Id = entry.Id,
            Kind = entry.Kind,
            AfterId = afterId,
            Text = entry.Text,
            Thought = entry.Thought,
            IsStreaming = entry.IsStreaming,
            ErrorText = entry.ErrorText,
            Duration = entry.Duration,
            Attachments = entry.Attachments,
            Tools = entry.Tools,
            Notes = entry.Notes,
            Plan = entry.Plan,
            Permission = entry.Permission,
            Question = entry.Question,
        };

        // An update that would not fit one message goes as several, each with its own number.
        private static IEnumerable<AcpUpdateMessage> Split(AcpUpdateMessage update)
        {
            if (Serialize(update) <= MessageBudget)
            {
                yield return update;
                yield break;
            }
            var first = true;
            var part = new AcpUpdateMessage { ItemId = update.ItemId };
            var size = 0;
            foreach (var entry in update.Upserts)
            {
                var length = SerializeLength(entry);
                if (part.Upserts.Count > 0 && size + length > MessageBudget)
                {
                    yield return first ? WithHeader(part, update) : part;
                    first = false;
                    part = new AcpUpdateMessage { ItemId = update.ItemId };
                    size = 0;
                }
                part.Upserts.Add(entry);
                size += length;
            }
            yield return first ? WithHeader(part, update) : part;
        }

        private static AcpUpdateMessage WithHeader(AcpUpdateMessage part, AcpUpdateMessage whole)
        {
            part.Removed = whole.Removed;
            part.State = whole.State;
            part.StatusDetail = whole.StatusDetail;
            part.ErrorText = whole.ErrorText;
            part.QueuedCount = whole.QueuedCount;
            part.Composer = whole.Composer;
            return part;
        }

        private (string State, string? StatusDetail, string? ErrorText, int QueuedCount) HeaderNow()
        {
            // A card that waits for an answer is what the device most needs to know, whatever the turn does meanwhile.
            var open = Model.Timeline.Any(item => item is PermissionCardItem { IsResolved: false } or QuestionCardItem { IsResolved: false } or PlanApprovalCardItem { IsResolved: false });
            var state = Model.State switch
            {
                AgentGuiState.Connecting => AcpStates.Connecting,
                AgentGuiState.Disconnected or AgentGuiState.Error => AcpStates.Error,
                _ when open => AcpStates.Waiting,
                AgentGuiState.Working => AcpStates.Working,
                AgentGuiState.Waiting => AcpStates.Waiting,
                _ => AcpStates.Ready,
            };
            string? error = null;
            if (state == AcpStates.Error)
            {
                error = Blank(Model.ErrorBanner)
                    ?? AppLangResources.Get(Model.IsAuthRequired ? AppLangResourceKind.AgentGuiAuthRequiredTitle : AppLangResourceKind.AgentGuiStatusDisconnected);
            }
            return (state, Blank(Model.StatusDetail), error, Model.QueuedCount);
        }

        private AcpComposerMessage ComposerNow()
        {
            var composer = Model.Composer;
            var message = new AcpComposerMessage
            {
                Model = composer.SelectedModelId,
                Effort = composer.ShowEffortSelector ? composer.SelectedEffortId : null,
            };
            if (composer.ShowModelSelector) message.Models = [.. composer.ModelChoices.Select(Option)];
            if (composer.ShowEffortSelector) message.Efforts = [.. composer.EffortChoices.Select(Option)];
            // One list of modes: the agent's own modes when it has them, otherwise its permission levels.
            if (composer.ShowModeSelector)
            {
                message.Modes = [.. composer.ModeChoices.Select(Option)];
                message.Mode = composer.SelectedModeId;
            }
            else if (composer.ShowPermissionSelector)
            {
                message.Modes = [.. composer.PermissionChoices.Select(Option)];
                message.Mode = composer.SelectedPermissionId;
            }
            return message;
        }

        private static AcpOptionMessage Option(ComposerChoiceItem choice) => new() { Id = choice.Id, Label = choice.Label, Detail = Blank(choice.Description) };

        private sealed class Attachment(Conversation owner, Action<AcpUpdateMessage> updated, AcpSnapshotMessage snapshot) : IAcpAttachment
        {
            private int _disposed;

            public AcpSnapshotMessage Snapshot { get; } = snapshot;

            public void Send(AcpUpdateMessage update)
            {
                if (Volatile.Read(ref _disposed) == 0) updated(update);
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                // From the connection's thread: the conversation itself is only touched on the UI thread.
                if (Dispatcher.UIThread.CheckAccess()) owner.Remove(this);
                else Dispatcher.UIThread.Post(() => owner.Remove(this));
            }
        }
    }

    // ---- the timeline as entries ----

    private static (int ByteLength, ulong Hash) Fingerprint(AcpEntryMessage entry)
    {
        var utf8 = JsonSerializer.SerializeToUtf8Bytes(entry, RemoteJsonContext.Default.AcpEntryMessage);
        return (utf8.Length, HashFnv1a64(utf8));
    }

    private static (int ByteLength, ulong Hash) Fingerprint(AcpComposerMessage composer)
    {
        var utf8 = JsonSerializer.SerializeToUtf8Bytes(composer, RemoteJsonContext.Default.AcpComposerMessage);
        return (utf8.Length, HashFnv1a64(utf8));
    }

    private static int SerializeLength(AcpEntryMessage entry) => JsonSerializer.SerializeToUtf8Bytes(entry, RemoteJsonContext.Default.AcpEntryMessage).Length;

    private static int Serialize(AcpUpdateMessage update) => JsonSerializer.SerializeToUtf8Bytes(update, RemoteJsonContext.Default.AcpUpdateMessage).Length;

    private static ulong HashFnv1a64(ReadOnlySpan<byte> data)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offsetBasis;
        for (var i = 0; i < data.Length; i++)
        {
            hash ^= data[i];
            hash *= prime;
        }
        return hash;
    }

    /// <summary>The entry a timeline item shows as on a device; null for what only this computer's window shows.</summary>
    internal static AcpEntryMessage? ToEntry(AgentTimelineItem item)
    {
        var entry = item switch
        {
            UserTurnItem user => new AcpEntryMessage
            {
                Kind = AcpEntryKinds.User,
                Text = Tail(user.Text, AcpLimits.TextChars),
                Attachments = user.Attachments.Count > 0 ? [.. user.Attachments.Select(a => a.Label)] : null,
            },
            AssistantTurnItem turn => new AcpEntryMessage
            {
                Kind = AcpEntryKinds.Assistant,
                Text = Tail(Blank(turn.MarkdownText), AcpLimits.TextChars),
                Thought = Tail(Blank(turn.ThoughtText), AcpLimits.TextChars),
                IsStreaming = turn.IsStreaming,
                ErrorText = Blank(turn.ErrorText),
                Duration = Blank(turn.DurationText),
                Tools = turn.ToolCalls.Count > 0 ? [.. turn.ToolCalls.Select(ToTool)] : null,
                Notes = ExtractWorkNotes(turn),
                Plan = turn.PlanEntries.Count > 0 ? [.. turn.PlanEntries.Select(p => new AcpPlanEntryMessage { Content = p.Content, Status = p.Status })] : null,
            },
            PermissionCardItem card => new AcpEntryMessage
            {
                Kind = AcpEntryKinds.Permission,
                Permission = new AcpPermissionMessage
                {
                    Title = card.Title,
                    Detail = Tail(Blank(card.ActionDetail), AcpLimits.TextChars),
                    Warning = Blank(card.WarningMessage),
                    Options = [.. card.Actions.Select(ToOption)],
                    IsResolved = card.IsResolved,
                    ResolvedSummary = Blank(card.ResolvedSummary),
                },
            },
            // The plan gate is a permission with the plan as what it wants to do; feedback comes back as free text.
            PlanApprovalCardItem plan => new AcpEntryMessage
            {
                Kind = AcpEntryKinds.Permission,
                Permission = new AcpPermissionMessage
                {
                    Title = plan.Title,
                    Detail = Tail(Blank(plan.PlanMarkdown), AcpLimits.TextChars),
                    Options = [.. plan.Actions.Select(ToOption)],
                    IsResolved = plan.IsResolved,
                    ResolvedSummary = Blank(plan.ResolvedSummary),
                },
            },
            QuestionCardItem question => new AcpEntryMessage
            {
                Kind = AcpEntryKinds.Question,
                Question = new AcpQuestionMessage
                {
                    Title = question.Title,
                    Fields =
                    [
                        .. question.Fields.Select(field => new AcpQuestionFieldMessage
                        {
                            Id = field.Id,
                            Prompt = field.Prompt,
                            MultiSelect = field.MultiSelect,
                            AllowFreeText = field.AllowFreeText,
                            Choices = [.. field.Choices.Select(c => new AcpOptionMessage { Id = c.Id, Label = c.Label, Detail = Blank(c.Description) })],
                        }),
                    ],
                    IsResolved = question.IsResolved,
                    ResolvedSummary = Blank(question.ResolvedSummary),
                },
            },
            // The swimlane of sub-agents and background shells is a view of this window's side panels.
            _ => null,
        };
        if (entry is null) return null;
        entry.Id = IdOf(item);
        return Fit(entry);
    }

    private static AcpOptionMessage ToOption(PermissionActionViewModel action) => new() { Id = action.OptionId, Label = action.Label, Detail = action.Kind };

    private static AcpToolMessage ToTool(ToolCallItemViewModel tool)
    {
        var output = string.Join("\n", new[] { tool.Stdout, tool.Stderr, tool.OutputText }.Where(part => !string.IsNullOrWhiteSpace(part)));
        return new AcpToolMessage
        {
            Id = tool.ToolCallId,
            Title = tool.DisplayTitle,
            Kind = tool.Kind,
            Status = tool.Status,
            Command = Blank(tool.CommandText),
            Path = Blank(tool.PrimaryPath),
            Added = tool.AddedLines,
            Removed = tool.RemovedLines,
            ExitCode = tool.ExitCode,
            Output = Tail(Blank(output), AcpLimits.ToolOutputChars),
            Diff = tool.DiffLines.Count > 0 ? [.. tool.DiffLines.Take(AcpLimits.DiffLines).Select(line => line.Prefix + line.Text)] : null,
        };
    }

    private static List<AcpWorkNoteMessage>? ExtractWorkNotes(AssistantTurnItem turn)
    {
        List<AcpWorkNoteMessage>? notes = null;
        var toolIndex = 0;
        var steps = turn.WorkSteps;
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step.StepKind == WorkStepKind.Tool)
            {
                toolIndex++;
            }
            else if (step.StepKind == WorkStepKind.Text && !string.IsNullOrWhiteSpace(step.NoteText))
            {
                notes ??= [];
                notes.Add(new AcpWorkNoteMessage
                {
                    Text = Tail(step.NoteText, AcpLimits.TextChars)!,
                    BeforeToolIndex = toolIndex,
                });
            }
        }
        return notes;
    }

    /// <summary>
    /// Makes an entry that is still too large for a message smaller, least useful first: long outputs and diffs of
    /// its tools, then what it thought, then the start of its answer.
    /// </summary>
    private static AcpEntryMessage Fit(AcpEntryMessage entry)
    {
        if (SerializeLength(entry) <= EntryBudget) return entry;
        foreach (var tool in entry.Tools ?? [])
        {
            tool.Output = Tail(tool.Output, 512);
            if (tool.Diff is { Count: > 40 } diff) tool.Diff = diff[..40];
        }
        if (SerializeLength(entry) <= EntryBudget) return entry;
        entry.Thought = Tail(entry.Thought, 2 * 1024);
        foreach (var note in entry.Notes ?? [])
        {
            note.Text = Tail(note.Text, 1024) ?? "";
        }
        foreach (var tool in entry.Tools ?? [])
        {
            tool.Output = null;
            tool.Diff = null;
            tool.Command = Tail(tool.Command, 256);
        }
        if (SerializeLength(entry) <= EntryBudget) return entry;
        entry.Text = Tail(entry.Text, 8 * 1024);
        entry.Thought = null;
        entry.Notes = null;
        if (entry.Tools is { Count: > 100 } tools) entry.Tools = tools[^100..];
        return entry;
    }

    /// <summary>The end of a long text, marked as cut: what was said last is what a device needs to see.</summary>
    internal static string? Tail(string? text, int max)
    {
        if (text is null || text.Length <= max) return text;
        var start = text.Length - (max - 1);
        // Never half of a character that takes two.
        if (char.IsLowSurrogate(text[start])) start++;
        return "…" + text[start..];
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
