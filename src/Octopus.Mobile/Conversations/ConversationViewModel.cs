using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Mobile.Common;
using Octopus.Mobile.Localization;
using Octopus.Mobile.Shell;
using Octopus.Mobile.Workspace;

namespace Octopus.Mobile.Conversations;

/// <summary>One choice in a composer option sheet (model, permission mode, or effort).</summary>
public sealed record ConversationOptionChoice(ConversationOptionKind Kind, string Value);

/// <summary>
/// One GUI conversation on a host (design/ui/移动端功能扩展方案.html, G-A): attaches to the host's timeline, renders its
/// entries, sends prompts and answers back, and shares the session's "⋯" actions with the other session pages.
/// </summary>
public sealed partial class ConversationViewModel : ObservableObject, IDisposable
{
    private readonly IRemoteWorkspace _workspace;
    private readonly INavigator _navigator;
    private readonly CancellationTokenSource _lifetime = new();
    private RemoteConversation? _conversation;
    private string? _openError;
    private bool _isOpening;
    private bool _disposed;

    public ConversationViewModel(RemoteSession session, IRemoteWorkspace workspace, INavigator navigator)
    {
        Session = session;
        _workspace = workspace;
        _navigator = navigator;
        // Closed from its own menu: the page leaves (the session on the host has ended).
        Actions = new SessionActionsViewModel(workspace, navigator, session, _ => navigator.Back());
        session.PropertyChanged += OnSessionChanged;
        _ = OpenAsync();
    }

    public RemoteSession Session { get; }

    /// <summary>The "⋯" menu, rename sheet, close confirmation, and toast notices.</summary>
    public SessionActionsViewModel Actions { get; }

    /// <summary>The live conversation once attached; null while opening or if opening failed.</summary>
    public RemoteConversation? Conversation => _conversation;

    /// <summary>The timeline entries in chronological order.</summary>
    [ObservableProperty] public partial ObservableCollection<ConversationEntry> Entries { get; private set; } = [];

    /// <summary>The model, mode, and effort choices offered by the host.</summary>
    [ObservableProperty] public partial ConversationComposer Composer { get; private set; } = new();

    /// <summary>What the person is typing in the composer.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanSend))]
    public partial string PromptText { get; set; } = "";

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanSend))]
    public partial bool IsSending { get; private set; }

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
    public bool IsEmpty => IsLoaded && Entries.Count == 0 && !IsConnecting && !HasError;
    public int QueuedCount => _conversation?.QueuedCount ?? 0;
    public bool HasQueued => QueuedCount > 0;
    public string QueuedText => HasQueued ? string.Format(Text(MobileLangResourceKind.ConversationQueued), QueuedCount) : "";
    public bool ShowComposerTopRow => Composer.HasChoices || HasQueued;
    public bool CanStop => IsLoaded && (IsWorking || IsWaiting);
    public bool CanSend => IsLoaded && !IsSending && !string.IsNullOrWhiteSpace(PromptText);

    /// <summary>Raised when the person taps a model, mode, or effort chip; the page shows an <c>ActionSheet</c>.</summary>
    public event Action<ConversationOptionKind, string, IReadOnlyList<ConversationOption>, string?>? OptionPickerRequested;

    /// <summary>Raised when entries change so the page can stay pinned to the bottom.</summary>
    public event Action? TimelineChanged;

    [RelayCommand]
    private void Back() => _navigator.Back();

    /// <summary>Opens the "⋯" menu: reload conversation, project files/changes when supported, then standard session actions.</summary>
    [RelayCommand]
    private void OpenMenu()
    {
        var leading = new List<SessionMenuEntry>
        {
            new(Text(MobileLangResourceKind.ConversationReconnect), ReloadCommand),
        };
        if (_workspace.SupportsFiles(Session.Project.Host))
        {
            leading.Add(new(Text(MobileLangResourceKind.FilesBrowse), new RelayCommand(() =>
                _navigator.ShowFiles(Session.Project, RemoteFolders.WorkspacePathOf(Session)))));
        }
        if (_workspace.SupportsGit(Session.Project.Host))
        {
            leading.Add(new(Text(MobileLangResourceKind.ChangesView), new RelayCommand(() =>
                _navigator.ShowChanges(Session.Project, RemoteFolders.WorkspacePathOf(Session)))));
        }
        Actions.ShowMenu(Session, leading);
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
    private async Task SendAsync()
    {
        if (_conversation is null || IsSending) return;
        var text = PromptText.Trim();
        if (text.Length == 0) return;
        IsSending = true;
        PromptText = "";
        try
        {
            await _conversation.SendAsync(text, cancellationToken: _lifetime.Token);
            TimelineChanged?.Invoke();
        }
        catch (IOException e)
        {
            // Keep what the person typed when sending failed so they do not lose it.
            if (string.IsNullOrEmpty(PromptText)) PromptText = text;
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
        if (choice is not null) choice.IsSelected = !choice.IsSelected;
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
    private static void ToggleThought(ConversationAssistantEntry? entry)
    {
        if (entry is not null) entry.IsThoughtExpanded = !entry.IsThoughtExpanded;
    }

    [RelayCommand]
    private static void ToggleTool(ConversationTool? tool)
    {
        if (tool is { HasDetails: true }) tool.IsExpanded = !tool.IsExpanded;
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
        _lifetime.Cancel();
        _lifetime.Dispose();
        Session.PropertyChanged -= OnSessionChanged;
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
        Composer = conversation.Composer;
        conversation.PropertyChanged += OnConversationChanged;
        conversation.Entries.CollectionChanged += OnEntriesChanged;
        conversation.Composer.PropertyChanged += OnComposerChanged;
        OnPropertyChanged(nameof(Conversation));
        NotifyState();
        TimelineChanged?.Invoke();
    }

    private void DetachConversation()
    {
        if (_conversation is null) return;
        _conversation.PropertyChanged -= OnConversationChanged;
        _conversation.Entries.CollectionChanged -= OnEntriesChanged;
        _conversation.Composer.PropertyChanged -= OnComposerChanged;
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
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(QueuedCount));
        OnPropertyChanged(nameof(HasQueued));
        OnPropertyChanged(nameof(QueuedText));
        OnPropertyChanged(nameof(ShowComposerTopRow));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanSend));
    }

    private static string LinkName(HostLink link) => Text(link switch
    {
        HostLink.Lan => MobileLangResourceKind.LinkLan,
        HostLink.Relay => MobileLangResourceKind.LinkRelay,
        _ => MobileLangResourceKind.LinkOffline,
    });

    private static string Text(MobileLangResourceKind kind) => MobileLangResources.Get(kind);
}
