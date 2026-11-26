using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Common.Mentions;
using Octopus.Mobile.Common;
using Octopus.Mobile.Localization;
using Octopus.Mobile.Shell;
using Octopus.Mobile.Terminals;
using Octopus.Mobile.Voice;
using Octopus.Mobile.Workspace;
using Octopus.Remote.Protocol;
using Octopus.Terminal.Avalonia;
using TKCode.Arc;

namespace Octopus.Mobile.Conversations;

/// <summary>One choice in a composer option sheet (model, permission mode, or effort).</summary>
public sealed record ConversationOptionChoice(ConversationOptionKind Kind, string Value);

/// <summary>One uploaded image or file staged in the mobile composer before sending.</summary>
public sealed record ConversationAttachmentItem(string Name, string RemotePath, bool IsImage)
{
    public Geometry Icon => IsImage ? ArcIcons.Image : ArcIcons.FileText;
}

/// <summary>
/// One GUI conversation on a host (design/ui/移动端功能扩展方案.html, G-A): attaches to the host's timeline, renders its
/// entries, sends prompts and answers back, and shares the session's "⋯" actions with the other session pages.
/// </summary>
public sealed partial class ConversationViewModel : ObservableObject, IDisposable
{
    private readonly IRemoteWorkspace _workspace;
    private readonly INavigator _navigator;
    private readonly Action<Uri> _openBrowser;
    private Func<Task<PickedImage?>> _pickImage = () => MobileHost.PickImage(null);
    private Func<Task<PickedImage?>> _pickFile = () => MobileHost.PickFile(null);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<ConversationWorkflowTask> _watchedTasks = [];
    private RemoteConversation? _conversation;
    private DispatcherTimer? _statusPhraseTimer;
    private string? _openError;
    private bool _isOpening;
    private bool _disposed;
    // Inline @mentions of the draft (the same rules as the desktop composer: Octopus.App/Common/Mentions).
    private readonly List<MentionToken> _draftMentions = [];
    private IReadOnlyList<MentionSpan> _mentionSpans = [];
    private IReadOnlyList<ConversationMentionTarget> _mentionTargets = [];
    private DateTime _mentionTargetsAt;
    private Task? _mentionFetch;
    private int _mentionCaret;
    private int _lastPromptLength;
    private static readonly TimeSpan MentionTargetsLifetime = TimeSpan.FromSeconds(20);

    public ConversationViewModel(RemoteSession session, IRemoteWorkspace workspace, INavigator navigator, Action<Uri>? openBrowser = null)
    {
        Session = session;
        _workspace = workspace;
        _navigator = navigator;
        _openBrowser = openBrowser ?? MobileHost.OpenBrowser;
        // Closed from its own menu: the page leaves (the session on the host has ended).
        Actions = new SessionActionsViewModel(workspace, navigator, session, _ => navigator.Back());
        WorkspaceMenu = new SessionWorkspaceViewModel(session, workspace, navigator, Actions, BackCommand)
        {
            PrimaryActionLabel = Text(MobileLangResourceKind.ConversationReconnect),
            PrimaryActionCommand = ReloadCommand,
        };
        Voice = new VoiceComposerViewModel(MobileHost.Voice, InsertDictation, SendDictationAsync, navigator.ShowVoiceSettings, Actions.ShowNotice, CollectHotwords);
        PendingAttachments.CollectionChanged += OnPendingAttachmentsChanged;
        session.PropertyChanged += OnSessionChanged;
        MobileHost.Backgrounded += OnAppBackgrounded;
        MobileHost.Foregrounded += OnAppForegrounded;
        _ = OpenAsync();
    }

    public RemoteSession Session { get; }

    /// <summary>The rename sheet, close confirmation, and toast notices.</summary>
    public SessionActionsViewModel Actions { get; }

    /// <summary>The Scheme B capsule top bar and Scheme C top-right popover menu (files, changes, branches, sessions, actions).</summary>
    public SessionWorkspaceViewModel WorkspaceMenu { get; }

    /// <summary>The live conversation once attached; null while opening or if opening failed.</summary>
    public RemoteConversation? Conversation => _conversation;

    /// <summary>"Hold to talk" of the composer: the microphone switch, the bar, and the overlay.</summary>
    public VoiceComposerViewModel Voice { get; }

    /// <summary>The timeline entries in chronological order.</summary>
    [ObservableProperty] public partial ObservableCollection<ConversationEntry> Entries { get; private set; } = [];

    /// <summary>The session-level workflow task list shown in the docked tray above the composer.</summary>
    [ObservableProperty] public partial ObservableCollection<ConversationWorkflowTask> Tasks { get; private set; } = [];

    /// <summary>Whether the docked task list tray above the composer is expanded (collapsed by default on mobile).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TaskTrayHeadline))]
    public partial bool IsTaskListExpanded { get; set; }

    /// <summary>The model, mode, and effort choices offered by the host.</summary>
    [ObservableProperty] public partial ConversationComposer Composer { get; private set; } = new();

    /// <summary>What the person is typing in the composer.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasComposerInput))]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyPropertyChangedFor(nameof(ShowComposerStop))]
    [NotifyPropertyChangedFor(nameof(ShowComposerSend))]
    [NotifyPropertyChangedFor(nameof(CanSendNow))]
    public partial string PromptText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyPropertyChangedFor(nameof(CanSendNow))]
    public partial bool IsSending { get; private set; }

    /// <summary>Images and files already uploaded to the host, staged to go out with the next prompt.</summary>
    public ObservableCollection<ConversationAttachmentItem> PendingAttachments { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyPropertyChangedFor(nameof(CanSendNow))]
    public partial bool IsUploading { get; private set; }

    [ObservableProperty] public partial string UploadText { get; private set; } = "";

    /// <summary>The prompt field's caret, two-way bound: an <c>@</c> query is the word before it.</summary>
    [ObservableProperty] public partial int PromptCaretIndex { get; set; }

    /// <summary>Session / agent tokens of the draft, bound to the prompt field's <c>MentionEditing.Tokens</c>.</summary>
    public IReadOnlyList<MentionToken> MentionTokens { get; private set; } = [];

    /// <summary>What the suggestion strip above the keyboard offers for the <c>@</c> query being typed.</summary>
    public ObservableCollection<ConversationMentionTarget> MentionSuggestions { get; } = [];

    /// <summary>The suggestion strip shows: an <c>@</c> query is being typed and something matches it.</summary>
    [ObservableProperty] public partial bool IsMentionStripOpen { get; private set; }

    /// <summary>The host takes @mentions: the <c>@</c> button shows.</summary>
    public bool SupportsMentions => _conversation?.SupportsMentions == true;

    public string Title => Session.Title;

    /// <summary>Project (with branch when in a worktree), host, and how the host is reached.</summary>
    public string Location => string.Join(" · ",
        Session.WorkspaceBranch is { Length: > 0 } branch
            ? $"{Session.Project.Name} ({branch})"
            : Session.Project.Name,
        Session.Project.Host.Name,
        LinkName(Session.Project.Host.Link));

    public bool IsLoaded => _conversation is not null;
    public ConversationState State => _conversation?.State ?? (_openError is not null ? ConversationState.Error : ConversationState.Connecting);
    public bool IsConnecting => State == ConversationState.Connecting;
    public bool IsWorking => State == ConversationState.Working;
    public bool IsWaiting => State == ConversationState.Waiting;
    public bool IsError => State == ConversationState.Error;
    public bool ShowSpinner => IsConnecting || IsWorking;
    public string? StatusDetail => _conversation?.StatusDetail;
    public bool HasStatusPill => State != ConversationState.Ready || !string.IsNullOrEmpty(StatusDetail);

    public string StatusText => State switch
    {
        ConversationState.Connecting => StatusDetail ?? Text(MobileLangResourceKind.ConversationConnecting),
        ConversationState.Working => StatusDetail ?? Text(MobileLangResourceKind.ConversationWorking),
        ConversationState.Waiting => StatusDetail ?? Text(MobileLangResourceKind.ConversationWaiting),
        ConversationState.Error => Text(MobileLangResourceKind.ConversationFailed),
        _ => StatusDetail ?? "",
    };

    public string? ErrorText => _conversation?.ErrorText ?? _openError;
    public bool HasError => !string.IsNullOrEmpty(ErrorText);
    public bool HasEarlier => _conversation?.HasEarlier == true;
    public bool ShowInitialLoading => !IsLoaded && !HasError;
    public bool IsEmpty => IsLoaded && Entries.Count == 0 && !IsConnecting && !HasError;
    public int QueuedCount => _conversation?.QueuedCount ?? 0;
    public bool HasQueued => QueuedCount > 0;
    public string QueuedText => HasQueued ? string.Format(Text(MobileLangResourceKind.ConversationQueued), QueuedCount) : "";
    public bool ShowComposerTopRow => Composer.HasChoices || HasQueued;
    public bool HasPendingAttachments => PendingAttachments.Count > 0;
    public bool HasComposerInput => !string.IsNullOrWhiteSpace(PromptText) || HasPendingAttachments;
    public bool CanAttach => IsLoaded && _workspace.SupportsUpload(Session.Project.Host);
    public bool CanStop => IsLoaded && (IsWorking || IsWaiting);
    public bool CanSend => IsLoaded && !IsSending && !IsUploading && HasComposerInput;
    /// <summary>Merged right-side primary action: shows Stop only when active and the composer has no draft text or attachments.</summary>
    public bool ShowComposerStop => CanStop && !HasComposerInput;
    /// <summary>Merged right-side primary action: shows Send whenever Stop is not shown.</summary>
    public bool ShowComposerSend => !ShowComposerStop;
    /// <summary>Shown alongside Send while a turn is active and the composer has input, to interrupt and send immediately.</summary>
    public bool CanSendNow => CanStop && CanSend;
    public bool CanOpenFiles => _workspace.SupportsFiles(Session.Project.Host);
    public string WorkingDirectory => RemoteFolders.WorkspacePathOf(Session) ?? Session.Project.Path;

    public bool HasTasks => Tasks.Count > 0;
    public bool HasRunningTasks => Tasks.Any(t => t.IsInProgress);
    public bool HasFailedTasks => !HasRunningTasks && Tasks.Any(t => t.IsFailed);
    public bool IsAllTasksCompleted => Tasks.Count > 0 && Tasks.All(t => t.IsCompleted);
    public bool IsTasksIdlePending => HasTasks && !HasRunningTasks && !HasFailedTasks && !IsAllTasksCompleted;
    public double TaskProgressPercent => Tasks.Count == 0 ? 0d : Tasks.Count(t => t.IsCompleted) * 100d / Tasks.Count;
    public string TaskRatioText => $"{Tasks.Count(t => t.IsCompleted)}/{Tasks.Count}";

    public string TaskTrayHeadline
    {
        get
        {
            if (IsTaskListExpanded || Tasks.Count == 0) return Text(MobileLangResourceKind.ConversationTaskList);
            if (Tasks.FirstOrDefault(t => t.IsInProgress) is { } active)
            {
                var label = string.IsNullOrWhiteSpace(active.ActiveForm) ? active.Title : active.ActiveForm!;
                return $"{active.DisplayId} {label}";
            }
            if (IsAllTasksCompleted) return Text(MobileLangResourceKind.ConversationTaskCompletedAll);
            if (Tasks.FirstOrDefault(t => t.IsFailed) is { } failed) return $"{failed.DisplayId} {failed.Title}";
            if (Tasks.FirstOrDefault(t => !t.IsCompleted) is { } next) return $"{next.DisplayId} {next.Title}";
            return Text(MobileLangResourceKind.ConversationTaskList);
        }
    }

    /// <summary>Raised when the person taps a model, mode, or effort chip; the page shows an <c>ActionSheet</c>.</summary>
    public event Action<ConversationOptionKind, string, IReadOnlyList<ConversationOption>, string?>? OptionPickerRequested;

    /// <summary>Raised when the person taps the attach button; the page shows a photo/file picker sheet.</summary>
    public event Action? AttachPickerRequested;

    /// <summary>Raised when entries change so the page can stay pinned to the bottom.</summary>
    public event Action? TimelineChanged;

    /// <summary>Raised when the view should force-pin and scroll to the very bottom (initial attach or sending a prompt).</summary>
    public event Action? ScrollToBottomRequested;

    /// <summary>Supplies the page-scoped photo and file pickers (using the page's <c>TopLevel</c>).</summary>
    internal void SetPickers(Func<Task<PickedImage?>> pickImage, Func<Task<PickedImage?>> pickFile)
    {
        _pickImage = pickImage;
        _pickFile = pickFile;
    }

    [RelayCommand]
    private void Back() => _navigator.Back();

    /// <summary>Opens the top-right workspace &amp; session popover menu.</summary>
    [RelayCommand]
    private void OpenMenu() => WorkspaceMenu.OpenMenuCommand.Execute(null);

    /// <summary>Opens a clicked Markdown link or image: web URLs in the system browser, project/file paths in the in-app preview.</summary>
    [RelayCommand]
    private void OpenLink(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        if (MobileFileLinks.IsWebUrl(target) && Uri.TryCreate(target.Trim(), UriKind.Absolute, out var uri))
        {
            _openBrowser(uri);
            return;
        }
        OpenFile(target);
    }

    /// <summary>Opens a file or image path (from Markdown, tool steps, or attachments) in the mobile file/image preview page.</summary>
    [RelayCommand]
    private void OpenFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (MobileFileLinks.ResolveRemotePath(path, WorkingDirectory) is not { } resolved) return;
        _navigator.ShowFile(Session.Project, resolved);
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (_conversation is null)
        {
            await OpenAsync();
            return;
        }
        try
        {
            await _conversation.ReloadAsync(_lifetime.Token);
        }
        catch (IOException e)
        {
            Actions.ShowNotice(e.Message);
        }
        catch (OperationCanceledException)
        {
        }
    }

    [RelayCommand]
    private Task SendAsync() => SendInternalAsync(immediate: false);

    [RelayCommand]
    private Task SendNowAsync() => SendInternalAsync(immediate: true);

    private async Task SendInternalAsync(bool immediate)
    {
        if (_conversation is null || IsSending || IsUploading) return;
        var text = PromptText.Trim();
        var staged = PendingAttachments.ToList();
        if (text.Length == 0 && staged.Count == 0) return;
        var attachments = staged.Count > 0 ? staged.Select(a => a.RemotePath).ToList() : null;
        var mentions = MentionText.DistinctInOrder(MentionText.Scan(text, _draftMentions));
        IsSending = true;
        PromptText = "";
        PendingAttachments.Clear();
        try
        {
            if (immediate || mentions.Count > 0) await _conversation.SendAsync(text, attachments, mentions, immediate, _lifetime.Token);
            else await _conversation.SendAsync(text, attachments, _lifetime.Token);
            // Kept until the prompt is out, so a failed send gives the text back with its tokens.
            if (PromptText.Length == 0) ClearMentions();
            ScrollToBottomRequested?.Invoke();
        }
        catch (IOException e)
        {
            // Keep what the person typed and staged when sending failed so they do not lose it.
            if (string.IsNullOrEmpty(PromptText)) PromptText = text;
            if (PendingAttachments.Count == 0 && staged.Count > 0)
            {
                foreach (var item in staged) PendingAttachments.Add(item);
            }
            Actions.ShowNotice(e.Message);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsSending = false;
        }
    }

    [RelayCommand]
    private void OpenAttachPicker()
    {
        if (!CanAttach || IsUploading) return;
        AttachPickerRequested?.Invoke();
    }

    [RelayCommand]
    private Task AttachImageAsync() => PickAndUploadAttachmentAsync(_pickImage, forceImagePrepare: true);

    [RelayCommand]
    private Task AttachFileAsync() => PickAndUploadAttachmentAsync(_pickFile, forceImagePrepare: false);

    [RelayCommand]
    private void RemoveAttachment(ConversationAttachmentItem? item)
    {
        if (item is not null) PendingAttachments.Remove(item);
    }

    private async Task PickAndUploadAttachmentAsync(Func<Task<PickedImage?>> pick, bool forceImagePrepare)
    {
        if (!CanAttach || IsUploading) return;
        try
        {
            var picked = await pick();
            if (picked is null) return;
            IsUploading = true;
            ShowUploadProgress(0);
            var isImage = forceImagePrepare || MobileFileLinks.IsImageExtension(picked.Name) || ClipboardImages.ImageExtension(picked.Name) is not null;
            PickedImage toUpload;
            if (isImage)
            {
                toUpload = await Task.Run(() => UploadImages.Prepare(picked), _lifetime.Token);
            }
            else
            {
                if (picked.Data.Length > RemoteMethods.UploadMaxBytes) throw new ImageTooLargeException();
                toUpload = picked;
            }
            var remotePath = await _workspace.UploadImageAsync(Session, toUpload.Name, toUpload.Data, new Progress<double>(ShowUploadProgress), _lifetime.Token);
            if (_disposed) return;
            var displayName = Path.GetFileName(picked.Name);
            if (string.IsNullOrWhiteSpace(displayName)) displayName = Path.GetFileName(remotePath);
            PendingAttachments.Add(new ConversationAttachmentItem(displayName, remotePath, isImage));
        }
        catch (ImageTooLargeException)
        {
            Actions.ShowNotice(string.Format(Text(MobileLangResourceKind.UploadTooLarge), RemoteMethods.UploadMaxBytes / (1024 * 1024)));
        }
        catch (InvalidDataException)
        {
            Actions.ShowNotice(Text(MobileLangResourceKind.UploadUnreadable));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Actions.ShowNotice(string.Format(Text(MobileLangResourceKind.UploadFailed), e.Message));
        }
        finally
        {
            IsUploading = false;
        }
    }

    private void ShowUploadProgress(double share) =>
        UploadText = string.Format(Text(MobileLangResourceKind.UploadProgress), (int)Math.Round(share * 100));

    private void OnPendingAttachmentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasPendingAttachments));
        OnPropertyChanged(nameof(HasComposerInput));
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(ShowComposerStop));
        OnPropertyChanged(nameof(ShowComposerSend));
        OnPropertyChanged(nameof(CanSendNow));
    }

    /// <summary>
    /// What this conversation talks about, for the recognizer to expect: the project, who can be @-mentioned, the files
    /// the agent touched lately, and the code-like words of the last messages.
    /// </summary>
    internal IReadOnlyList<string> CollectHotwords()
    {
        var recent = Entries.Reverse().Take(30).ToList();
        return VoiceHotwords.Collect(
            [Session.Project.Name, .. _mentionTargets.Select(target => target.Name)],
            recent.OfType<ConversationAssistantEntry>().SelectMany(entry => entry.Tools.Reverse()).Select(tool => tool.Path),
            recent.Select(entry => entry switch
            {
                ConversationUserEntry user => user.Text,
                ConversationAssistantEntry assistant => assistant.Text,
                _ => null,
            }));
    }

    /// <summary>Dictated text into the prompt field at the caret, to be checked before it is sent.</summary>
    internal void InsertDictation(string text)
    {
        var current = PromptText;
        var caret = Math.Clamp(PromptCaretIndex, 0, current.Length);
        // Only words in Latin letters need a space between them; Chinese runs straight on.
        var lead = caret > 0 && IsWordChar(current[caret - 1]) && IsWordChar(text[0]) ? " " : "";
        var tail = caret < current.Length && IsWordChar(current[caret]) && IsWordChar(text[^1]) ? " " : "";
        var inserted = lead + text + tail;
        PromptText = string.Concat(current.AsSpan(0, caret), inserted, current.AsSpan(caret));
        PromptCaretIndex = caret + inserted.Length;

        static bool IsWordChar(char c) => char.IsAsciiLetterOrDigit(c);
    }

    /// <summary>Dictated text released on "send": goes out as it is, the draft in the field stays.</summary>
    private async Task SendDictationAsync(string text)
    {
        if (_conversation is null || IsSending)
        {
            InsertDictation(text);
            return;
        }
        IsSending = true;
        try
        {
            await _conversation.SendAsync(text, cancellationToken: _lifetime.Token);
            ScrollToBottomRequested?.Invoke();
        }
        catch (IOException e)
        {
            // Not lost: it waits in the field.
            InsertDictation(text);
            Actions.ShowNotice(e.Message);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsSending = false;
        }
    }

    partial void OnPromptTextChanged(string value)
    {
        // The field moves its caret right after changing the text; until then assume the edit happened at the caret.
        var delta = value.Length - _lastPromptLength;
        _lastPromptLength = value.Length;
        _mentionCaret = Math.Clamp(PromptCaretIndex + delta, 0, value.Length);
        _mentionSpans = MentionText.Scan(value, _draftMentions);
        UpdateMentionStrip();
    }

    partial void OnPromptCaretIndexChanged(int value)
    {
        _mentionCaret = Math.Clamp(value, 0, PromptText.Length);
        UpdateMentionStrip();
    }

    /// <summary>The <c>@</c> button: types an <c>@</c> at the caret (typing one on a phone keyboard takes a layer switch).</summary>
    [RelayCommand]
    private void StartMention()
    {
        if (!SupportsMentions) return;
        var text = PromptText;
        var caret = Math.Clamp(_mentionCaret, 0, text.Length);
        var lead = caret > 0 && !char.IsWhiteSpace(text[caret - 1]) ? " " : "";
        PromptText = string.Concat(text.AsSpan(0, caret), lead + "@", text.AsSpan(caret));
        PromptCaretIndex = caret + lead.Length + 1;
        UpdateMentionStrip();
    }

    /// <summary>Writes the chosen target's token in place of the <c>@</c> query.</summary>
    [RelayCommand]
    private void InsertMention(ConversationMentionTarget? target)
    {
        if (target is null) return;
        var text = PromptText;
        var caret = Math.Clamp(_mentionCaret, 0, text.Length);
        var range = MentionText.FindQuery(text, caret, _mentionSpans) is { } query ? (query.Start, query.End) : (caret, caret);
        var label = MentionText.BuildLabel(target.Name, target.IsSession ? target.Detail : null, target.Kind, target.TargetId, _draftMentions);
        if (!_draftMentions.Any(m => m.Kind == target.Kind && m.TargetId == target.TargetId))
        {
            _draftMentions.Add(new MentionToken(target.Kind, target.TargetId, label, target.AgentId));
            PublishMentions();
        }
        var inserted = MentionText.InsertToken(text, range.Item1, range.Item2, label);
        CloseMentionStrip();
        PromptText = inserted.Text;
        PromptCaretIndex = inserted.Caret;
    }

    private void UpdateMentionStrip()
    {
        if (!SupportsMentions || MentionText.FindQuery(PromptText, _mentionCaret, _mentionSpans) is not { } query)
        {
            CloseMentionStrip();
            return;
        }
        if (_mentionTargets.Count == 0 || DateTime.UtcNow - _mentionTargetsAt > MentionTargetsLifetime)
        {
            // One list per few seconds, filtered here while typing: no round trip per keystroke.
            _mentionFetch ??= FetchMentionTargetsAsync();
            if (_mentionTargets.Count == 0) return;
        }
        FilterMentionSuggestions(query.Query);
    }

    private async Task FetchMentionTargetsAsync()
    {
        try
        {
            if (_conversation is not { } conversation) return;
            _mentionTargets = await conversation.ListMentionTargetsAsync("", _lifetime.Token);
            _mentionTargetsAt = DateTime.UtcNow;
        }
        catch (Exception e) when (e is IOException or OperationCanceledException)
        {
            // No list this time: the strip stays closed and plain text still sends.
        }
        finally
        {
            _mentionFetch = null;
        }
        if (!_disposed && _mentionTargets.Count > 0 && MentionText.FindQuery(PromptText, _mentionCaret, _mentionSpans) is { } query)
        {
            FilterMentionSuggestions(query.Query);
        }
    }

    private void FilterMentionSuggestions(string query)
    {
        MentionSuggestions.Clear();
        foreach (var target in _mentionTargets)
        {
            if (query.Length == 0
                || target.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (target.Detail?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || target.TargetId.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (target.AgentId?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                MentionSuggestions.Add(target);
                if (MentionSuggestions.Count >= 12) break;
            }
        }
        IsMentionStripOpen = MentionSuggestions.Count > 0;
    }

    private void CloseMentionStrip()
    {
        IsMentionStripOpen = false;
        if (MentionSuggestions.Count > 0) MentionSuggestions.Clear();
    }

    private void PublishMentions()
    {
        MentionTokens = _draftMentions.ToArray();
        OnPropertyChanged(nameof(MentionTokens));
        _mentionSpans = MentionText.Scan(PromptText, _draftMentions);
    }

    private void ClearMentions()
    {
        if (_draftMentions.Count == 0) return;
        _draftMentions.Clear();
        PublishMentions();
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        if (_conversation is null) return;
        try
        {
            await _conversation.CancelAsync(_lifetime.Token);
        }
        catch (IOException e)
        {
            Actions.ShowNotice(e.Message);
        }
        catch (OperationCanceledException)
        {
        }
    }

    [RelayCommand]
    private async Task RespondAsync(ConversationPermissionOption? option)
    {
        if (_conversation is null || option is null || !option.Card.IsOpen) return;
        var card = option.Card;
        card.IsAnswering = true;
        try
        {
            await _conversation.RespondAsync(card, option.Id, _lifetime.Token);
        }
        catch (IOException e)
        {
            card.IsAnswering = false;
            Actions.ShowNotice(e.Message);
        }
        catch (OperationCanceledException)
        {
            card.IsAnswering = false;
        }
    }

    [RelayCommand]
    private static void ToggleChoice(ConversationChoice? choice)
    {
        choice?.IsSelected = !choice.IsSelected;
    }

    [RelayCommand]
    private async Task SubmitQuestionAsync(ConversationQuestionEntry? card)
    {
        if (_conversation is null || card is null || !card.IsOpen) return;
        var answers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var freeText = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in card.Fields)
        {
            IReadOnlyList<string> picked = [.. field.Choices.Where(choice => choice.IsSelected).Select(choice => choice.Id)];
            if (picked.Count > 0) answers[field.Id] = picked;
            if (field.AllowFreeText && !string.IsNullOrWhiteSpace(field.FreeText)) freeText[field.Id] = field.FreeText.Trim();
        }
        card.IsAnswering = true;
        try
        {
            await _conversation.AnswerAsync(card, answers, freeText, _lifetime.Token);
        }
        catch (IOException e)
        {
            card.IsAnswering = false;
            Actions.ShowNotice(e.Message);
        }
        catch (OperationCanceledException)
        {
            card.IsAnswering = false;
        }
    }

    [RelayCommand]
    private static void ToggleWork(ConversationAssistantEntry? entry)
    {
        if (entry is { CanExpandWork: true }) entry.IsWorkExpanded = !entry.IsWorkExpanded;
    }

    [RelayCommand]
    private static void ToggleWorkStep(ConversationWorkStep? step)
    {
        if (step is { CanExpand: true }) step.IsExpanded = !step.IsExpanded;
    }

    [RelayCommand]
    private static void ToggleThought(ConversationAssistantEntry? entry)
    {
        entry?.IsThoughtExpanded = !entry.IsThoughtExpanded;
    }

    [RelayCommand]
    private static void ToggleTool(ConversationTool? tool)
    {
        if (tool is { HasDetails: true }) tool.IsExpanded = !tool.IsExpanded;
    }

    [RelayCommand]
    private void ToggleTaskList()
    {
        if (HasTasks) IsTaskListExpanded = !IsTaskListExpanded;
    }

    internal void CollapseTaskList()
    {
        if (IsTaskListExpanded) IsTaskListExpanded = false;
    }

    [RelayCommand]
    private void OpenModelPicker() => RequestPicker(ConversationOptionKind.Model, MobileLangResourceKind.ConversationModel);

    [RelayCommand]
    private void OpenModePicker() => RequestPicker(ConversationOptionKind.Mode, MobileLangResourceKind.ConversationMode);

    [RelayCommand]
    private void OpenEffortPicker() => RequestPicker(ConversationOptionKind.Effort, MobileLangResourceKind.ConversationEffort);

    [RelayCommand]
    private async Task SelectOptionAsync(ConversationOptionChoice? choice)
    {
        if (_conversation is null || choice is null) return;
        try
        {
            await _conversation.SetOptionAsync(choice.Kind, choice.Value, _lifetime.Token);
        }
        catch (IOException e)
        {
            Actions.ShowNotice(e.Message);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        MobileHost.Backgrounded -= OnAppBackgrounded;
        MobileHost.Foregrounded -= OnAppForegrounded;
        Voice.Dispose();
        StopStatusPhraseTimer();
        _lifetime.Cancel();
        _lifetime.Dispose();
        PendingAttachments.CollectionChanged -= OnPendingAttachmentsChanged;
        Session.PropertyChanged -= OnSessionChanged;
        WorkspaceMenu.Dispose();
        DetachConversation();
    }

    private async Task OpenAsync()
    {
        if (_isOpening || _disposed) return;
        _isOpening = true;
        _openError = null;
        NotifyState();
        try
        {
            var opened = await _workspace.OpenConversationAsync(Session, _lifetime.Token);
            if (_disposed)
            {
                opened.Dispose();
                return;
            }
            AttachConversation(opened);
        }
        catch (IOException e)
        {
            if (!_disposed)
            {
                _openError = e.Message;
                Actions.ShowNotice(e.Message);
                NotifyState();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _isOpening = false;
        }
    }

    private void AttachConversation(RemoteConversation conversation)
    {
        DetachConversation();
        _conversation = conversation;
        _openError = null;
        Entries = conversation.Entries;
        Tasks = conversation.Tasks;
        Composer = conversation.Composer;
        conversation.PropertyChanged += OnConversationChanged;
        conversation.Entries.CollectionChanged += OnEntriesChanged;
        conversation.Tasks.CollectionChanged += OnTasksChanged;
        conversation.Composer.PropertyChanged += OnComposerChanged;
        WatchTasks();
        OnPropertyChanged(nameof(Conversation));
        NotifyState();
        NotifyTaskState();
        ScrollToBottomRequested?.Invoke();
    }

    private void DetachConversation()
    {
        if (_conversation is null) return;
        _conversation.PropertyChanged -= OnConversationChanged;
        _conversation.Entries.CollectionChanged -= OnEntriesChanged;
        _conversation.Tasks.CollectionChanged -= OnTasksChanged;
        _conversation.Composer.PropertyChanged -= OnComposerChanged;
        ClearWatchedTasks();
        _conversation.Dispose();
        _conversation = null;
    }

    private void RequestPicker(ConversationOptionKind kind, MobileLangResourceKind title)
    {
        var (options, current) = Composer.Of(kind);
        if (options.Count == 0) return;
        OptionPickerRequested?.Invoke(kind, Text(title), options, current);
    }

    private void OnConversationChanged(object? sender, PropertyChangedEventArgs e)
    {
        NotifyState();
        if (e.PropertyName is nameof(RemoteConversation.State) or nameof(RemoteConversation.ErrorText))
        {
            TimelineChanged?.Invoke();
        }
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsEmpty));
        TimelineChanged?.Invoke();
    }

    private void OnTasksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        WatchTasks();
        if (!HasTasks && IsTaskListExpanded) IsTaskListExpanded = false;
        NotifyTaskState();
    }

    private void OnTaskPropertyChanged(object? sender, PropertyChangedEventArgs e) => NotifyTaskState();

    private void WatchTasks()
    {
        var current = Tasks.ToHashSet();
        foreach (var gone in _watchedTasks.Where(t => !current.Contains(t)).ToList())
        {
            gone.PropertyChanged -= OnTaskPropertyChanged;
            _watchedTasks.Remove(gone);
        }
        foreach (var task in current)
        {
            if (_watchedTasks.Add(task)) task.PropertyChanged += OnTaskPropertyChanged;
        }
    }

    private void ClearWatchedTasks()
    {
        foreach (var task in _watchedTasks) task.PropertyChanged -= OnTaskPropertyChanged;
        _watchedTasks.Clear();
    }

    private void NotifyTaskState()
    {
        OnPropertyChanged(nameof(HasTasks));
        OnPropertyChanged(nameof(HasRunningTasks));
        OnPropertyChanged(nameof(HasFailedTasks));
        OnPropertyChanged(nameof(IsAllTasksCompleted));
        OnPropertyChanged(nameof(IsTasksIdlePending));
        OnPropertyChanged(nameof(TaskProgressPercent));
        OnPropertyChanged(nameof(TaskRatioText));
        OnPropertyChanged(nameof(TaskTrayHeadline));
    }

    private void OnComposerChanged(object? sender, PropertyChangedEventArgs e) =>
        OnPropertyChanged(nameof(ShowComposerTopRow));

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(RemoteSession.Title):
                OnPropertyChanged(nameof(Title));
                break;
            case nameof(RemoteSession.WorkspaceBranch):
                OnPropertyChanged(nameof(Location));
                break;
        }
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(IsLoaded));
        OnPropertyChanged(nameof(SupportsMentions));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsConnecting));
        OnPropertyChanged(nameof(IsWorking));
        OnPropertyChanged(nameof(IsWaiting));
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(ShowSpinner));
        OnPropertyChanged(nameof(StatusDetail));
        OnPropertyChanged(nameof(HasStatusPill));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(HasEarlier));
        OnPropertyChanged(nameof(ShowInitialLoading));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(QueuedCount));
        OnPropertyChanged(nameof(HasQueued));
        OnPropertyChanged(nameof(QueuedText));
        OnPropertyChanged(nameof(ShowComposerTopRow));
        OnPropertyChanged(nameof(CanAttach));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(ShowComposerStop));
        OnPropertyChanged(nameof(ShowComposerSend));
        OnPropertyChanged(nameof(CanSendNow));
        WorkspaceMenu.UpdateLiveStatus(IsWorking, IsWaiting, IsError, ShowSpinner, HasStatusPill ? StatusText : null);
        EnsureStatusPhraseTimer();
    }

    private void OnAppBackgrounded() => Dispatcher.UIThread.Post(() =>
    {
        StopStatusPhraseTimer();
        // A call came in or the app was left mid-sentence: nothing half-heard goes anywhere.
        Voice.Abort();
    });

    private void OnAppForegrounded() => Dispatcher.UIThread.Post(EnsureStatusPhraseTimer);

    private void EnsureStatusPhraseTimer()
    {
        if (_disposed || Application.Current is null || !Dispatcher.UIThread.CheckAccess()) return;
        var need = IsWorking && !MobileHost.IsBackgrounded;
        if (need && _statusPhraseTimer is null)
        {
            _statusPhraseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _statusPhraseTimer.Tick += OnStatusPhraseTick;
            _statusPhraseTimer.Start();
        }
        else if (!need && _statusPhraseTimer is not null)
        {
            StopStatusPhraseTimer();
        }
    }

    private void StopStatusPhraseTimer()
    {
        if (_statusPhraseTimer is null) return;
        _statusPhraseTimer.Stop();
        _statusPhraseTimer.Tick -= OnStatusPhraseTick;
        _statusPhraseTimer = null;
    }

    private void OnStatusPhraseTick(object? sender, EventArgs e)
    {
        if (_disposed || !IsWorking)
        {
            StopStatusPhraseTimer();
            return;
        }
        for (var i = Entries.Count - 1; i >= 0; i--)
        {
            if (Entries[i] is ConversationAssistantEntry { IsStreaming: true } active)
            {
                active.AdvanceLiveStatus();
                break;
            }
        }
    }

    private static string LinkName(HostLink link) => Text(link switch
    {
        HostLink.Lan => MobileLangResourceKind.LinkLan,
        HostLink.Relay => MobileLangResourceKind.LinkRelay,
        _ => MobileLangResourceKind.LinkOffline,
    });

    private static string Text(MobileLangResourceKind kind) => MobileLangResources.Get(kind);
}
