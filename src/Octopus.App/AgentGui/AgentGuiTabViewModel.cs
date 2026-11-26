using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Octopus.Acp.Dialects;
using Octopus.Acp.Process;
using Octopus.Acp.Protocol;
using Octopus.Acp.Transport;
using Octopus.Agents;
using Octopus.Agents.Accounts;
using Octopus.Agents.Subagents;
using Octopus.App.Activity;
using Octopus.App.Bots;
using Octopus.App.Collab;
using Octopus.App.Common;
using Octopus.App.Common.Mentions;
using Octopus.App.Explorer.Subagent;
using Octopus.App.Files;
using Octopus.App.Localization;
using Octopus.App.Services;
using Octopus.App.Sidebar;
using Octopus.App.Workspace;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.Platform;
using Octopus.Storage;
using TKCode.Arc.Icons.Lucide;

namespace Octopus.App.AgentGui;

/// <summary>Connection & turn lifecycle state of an ACP GUI tab.</summary>
public enum AgentGuiState
{
    Connecting,
    Ready,
    Working,
    Waiting,
    Disconnected,
    Error,
}

/// <summary>A queued follow-up prompt waiting for dispatch in the ACP GUI queue tray.</summary>
public sealed partial class QueuedPrompt : ObservableObject
{
    private readonly Func<QueuedPrompt, Task>? _onSendNow;
    private readonly Action<QueuedPrompt>? _onEdit;
    private readonly Action<QueuedPrompt>? _onMoveUp;
    private readonly Action<QueuedPrompt>? _onMoveDown;
    private readonly Action<QueuedPrompt>? _onRemove;

    public QueuedPrompt(
        string text,
        IReadOnlyList<AcpPromptAttachment> attachments,
        Func<QueuedPrompt, Task>? onSendNow = null,
        Action<QueuedPrompt>? onEdit = null,
        Action<QueuedPrompt>? onMoveUp = null,
        Action<QueuedPrompt>? onMoveDown = null,
        Action<QueuedPrompt>? onRemove = null,
        string? collabThreadId = null,
        string? collabTaskId = null)
    {
        Text = text;
        Attachments = attachments;
        CollabThreadId = collabThreadId;
        CollabTaskId = collabTaskId;
        _onSendNow = onSendNow;
        _onEdit = onEdit;
        _onMoveUp = onMoveUp;
        _onMoveDown = onMoveDown;
        _onRemove = onRemove;
    }

    public string Text { get; }
    public IReadOnlyList<AcpPromptAttachment> Attachments { get; }
    public string? CollabThreadId { get; }
    public string? CollabTaskId { get; }

    [ObservableProperty] public partial int Position { get; set; } = 1;
    [ObservableProperty] public partial bool CanMoveUp { get; set; }
    [ObservableProperty] public partial bool CanMoveDown { get; set; }

    public string PositionText => Position.ToString();
    public bool IsNext => Position == 1;
    public int AttachmentCount => Attachments.Count;
    public bool HasAttachments => Attachments.Count > 0;
    public LucideIconKind AttachmentIcon => Attachments.Count > 0 && Attachments.All(a => a.Kind == "image")
        ? LucideIconKind.Image
        : LucideIconKind.FileText;

    public string PreviewText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Text))
            {
                return CollapseWhitespace(Text);
            }
            if (Attachments.Count > 0)
            {
                return string.Join(", ", Attachments.Select(AttachmentName));
            }
            return "";
        }
    }

    public string FullTooltip
    {
        get
        {
            var body = Text.Length > 800 ? Text[..800] + "…" : Text;
            if (Attachments.Count == 0) return body;
            var names = string.Join(", ", Attachments.Select(AttachmentName));
            return string.IsNullOrWhiteSpace(body) ? names : $"{body}\n[{names}]";
        }
    }

    public string AttachmentTooltip =>
        string.Join("\n", Attachments.Select(AttachmentName));

    partial void OnPositionChanged(int value)
    {
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(IsNext));
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task SendNowAsync() => _onSendNow?.Invoke(this) ?? Task.CompletedTask;

    [RelayCommand]
    private void Edit() => _onEdit?.Invoke(this);

    [RelayCommand]
    private void MoveUp() => _onMoveUp?.Invoke(this);

    [RelayCommand]
    private void MoveDown() => _onMoveDown?.Invoke(this);

    [RelayCommand]
    private void Remove() => _onRemove?.Invoke(this);

    private static string AttachmentName(AcpPromptAttachment attachment) =>
        string.IsNullOrWhiteSpace(attachment.DisplayName) ? Path.GetFileName(attachment.Path) : attachment.DisplayName;

    private static string CollapseWhitespace(string value)
    {
        var sb = new StringBuilder(Math.Min(value.Length, 200));
        var prevSpace = false;
        foreach (var ch in value.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!prevSpace)
                {
                    sb.Append(' ');
                    prevSpace = true;
                }
            }
            else
            {
                sb.Append(ch);
                prevSpace = false;
            }
            if (sb.Length >= 200)
            {
                sb.Append('…');
                break;
            }
        }
        return sb.ToString();
    }
}

/// <summary>
/// ViewModel for an ACP Agent GUI tab with Scheme D orchestration (Live Workflow Swimlane, Floating HUD Capsules,
/// Slide-Over SubAgent Drawer, persistent Background Bash tracking) and Bash Style 2 (output-first + script modal).
/// </summary>
public sealed partial class AgentGuiTabViewModel : ObservableObject, IRecipient<FocusSubagentRequested>, ICollabSessionBridge, IDisposable
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp",
    };

    private static readonly ConcurrentDictionary<string, (DateTimeOffset IndexedAt, string[] Files)> SharedWorkspaceFileCache = new(StringComparer.Ordinal);
    private static readonly TimeSpan WorkspaceFileCacheTtl = TimeSpan.FromSeconds(60);
    private const int MaxCachedWorkspaces = 16;
    private const int StreamFlushIntervalMs = 60;

    private readonly Project _project;
    private readonly ProjectItem _item;
    private readonly AgentDefinition _definition;
    private readonly ISettingsStore _settings;
    private readonly IAgentScanner _scanner;
    private readonly IAgentCommandCompiler _compiler;
    private readonly AcpLaunchResolver _launchResolver;
    private readonly IAcpSessionFactory _sessionFactory;
    private readonly AcpDialectRegistry _dialects;
    private readonly IProjectService _projects;
    private readonly IItemOpener _opener;
    private readonly IPickerService? _pickers;
    private readonly IItemActivityService? _activity;
    private readonly IAgentAccountService? _accounts;
    private readonly Func<IWorkspaceCommands?>? _workspaceCommands;
    private readonly ISubagentService? _subagents;
    private readonly IModelDiscovery? _modelDiscovery;
    private readonly IFileOpener? _fileOpener;
    private readonly IBotService? _bots;
    private readonly ICollabCoordinator? _collab;
    private readonly Dictionary<string, UrlElicitationCardItem> _urlElicitations = new(StringComparer.Ordinal);

    private IAcpSession? _session;
    private AssistantTurnItem? _activeTurn;
    private string? _desiredModelId;
    private string? _desiredEffortId;
    private bool? _desiredFast;
    private string? _confirmedModelId;
    private bool _reconnectingForModel;
    private bool _hasSyncedInitialPreferences;
    private readonly Dictionary<string, SubagentRunViewModel> _subagentsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SubagentRunViewModel> _agySubagentsByConvId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<AgySubagentSpawn> _agyUnmatchedSpawns = new();
    private readonly Dictionary<string, AgySubagentMessage> _agyBufferedMessages = new(StringComparer.OrdinalIgnoreCase);
    private string? _agyParentTranscriptPath;
    private long _agyParentTranscriptOffset;
    private bool _agyPolling;
    private bool _agyPollAgain;
    private bool _agyFinalizeOnRepoll;
    private Task? _agyPollTask;
    private string? _agyAppDataDirOverride;
    private readonly Dictionary<string, BackgroundBashViewModel> _shellsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BackgroundBashViewModel> _shellsByTaskId = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherTimer? _liveTickTimer;
    private CancellationTokenSource? _connectCts;
    private CancellationTokenSource? _turnCts;
    private Task? _pumpTask;
    private TaskCompletionSource? _activeTurnSettled;
    private TaskCompletionSource? _cancellingTurn;
    private QueuedPrompt? _urgentPrompt;
    private bool _isTurnActive;
    private bool _manualStopRequested;
    private string[] _workspaceFiles = [];
    private bool _workspaceIndexing;
    private string _lastFileFilterQuery = "";
    private readonly List<MentionToken> _draftMentions = [];
    private IReadOnlyList<MentionSpan> _mentionSpans = [];
    private int _caretForPickers;
    private int _lastInputLength;
    private readonly List<string> _promptHistory = [];
    private int _historyCursor = -1;
    private string _historyDraft = "";
    private bool _applyingHistory;
    private bool _initialPromptSent;
    private bool _titledFromPrompt;
    private bool _hasConnectedOnce;
    private volatile bool _acceptReplay;
    private readonly object _replayGate = new();
    private readonly List<AcpRuntimeEvent> _replayBuffer = [];
    private readonly object _streamGate = new();
    private readonly List<(string Channel, string? MessageId, StringBuilder Buffer)> _pendingDeltas = [];
    private bool _deltaFlushScheduled;
    private bool _disposed;

    public AgentGuiTabViewModel(
        Project project,
        ProjectItem item,
        AgentDefinition definition,
        ISettingsStore settings,
        IAgentScanner scanner,
        IAgentCommandCompiler compiler,
        AcpLaunchResolver launchResolver,
        IAcpSessionFactory sessionFactory,
        AcpDialectRegistry dialects,
        IProjectService projects,
        IItemOpener opener,
        IPickerService? pickers = null,
        IItemActivityService? activity = null,
        IAgentAccountService? accounts = null,
        Func<IWorkspaceCommands?>? workspaceCommands = null,
        ISubagentService? subagents = null,
        IModelDiscovery? modelDiscovery = null,
        IFileOpener? fileOpener = null,
        IBotService? bots = null,
        ICollabCoordinator? collab = null)
    {
        _project = project;
        _item = item;
        _definition = definition;
        _settings = settings;
        _scanner = scanner;
        _compiler = compiler;
        _launchResolver = launchResolver;
        _sessionFactory = sessionFactory;
        _dialects = dialects;
        _projects = projects;
        _opener = opener;
        _pickers = pickers;
        _activity = activity;
        _accounts = accounts;
        _workspaceCommands = workspaceCommands;
        _subagents = subagents;
        _modelDiscovery = modelDiscovery;
        _fileOpener = fileOpener;
        _bots = bots;
        _collab = collab;

        var profile = settings.Current.Agents.ProfileOf(definition.Id);
        var effectivePerm = !string.IsNullOrWhiteSpace(item.Launch?.Permission)
            ? item.Launch!.Permission!
            : (compiler.ResolvePermission(definition, profile, settings.Current.Agents.DefaultPermission) ?? PermissionModes.Default);
        var dialect = dialects.Resolve(definition.Acp?.Dialect);

        _desiredModelId = NormalizeLaunchOption(item.Launch?.Model is not null ? item.Launch.Model : profile.Model);
        _desiredEffortId = NormalizeLaunchOption(item.Launch?.Effort is not null ? item.Launch.Effort : profile.Effort);
        _desiredFast = item.Launch?.Fast ?? profile.Fast;

        Composer = new AgentComposerCapabilities(
            definition,
            dialect,
            effectivePerm,
            _desiredModelId,
            _desiredEffortId,
            _desiredFast,
            onModeChanged: ApplyModeAsync,
            onPermissionChanged: ApplyPermissionAsync,
            onModelChanged: ApplyModelAsync,
            onConfigSelectChanged: ApplyConfigSelectAsync,
            onConfigBoolChanged: ApplyConfigBoolAsync);

        var preferredCwd = !string.IsNullOrWhiteSpace(item.WorkingDirectory) ? item.WorkingDirectory! : project.Path;
        WorkingDirectory = Directory.Exists(preferredCwd) || !Directory.Exists(project.Path) ? preferredCwd : project.Path;
        WorkingDirectoryName = Path.GetFileName(WorkingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(WorkingDirectoryName)) WorkingDirectoryName = WorkingDirectory;

        QuickPrompts =
        [
            AppLangResources.Get(AppLangResourceKind.AgentGuiQuickPromptOverview),
            AppLangResources.Get(AppLangResourceKind.AgentGuiQuickPromptDiff),
            AppLangResources.Get(AppLangResourceKind.AgentGuiQuickPromptTests),
        ];

        WeakReferenceMessenger.Default.Register(this);
        ScriptPreview.PropertyChanged += OnScriptPreviewPropertyChanged;
        if (_collab is not null)
        {
            _collab.RegisterGuiSession(this);
            _collab.Changed += OnCollabChanged;
            RefreshCollabTopology();
        }
    }

    public ProjectItem Item => _item;
    public AgentDefinition Definition => _definition;
    public string AgentId => _definition.Id;
    public string AgentName => _definition.Name;
    public string WorkingDirectory { get; }
    public string WorkingDirectoryName { get; }
    public AgentComposerCapabilities Composer { get; }
    public IReadOnlyList<string> QuickPrompts { get; }

    public ObservableCollection<AgentTimelineItem> Timeline { get; } = [];
    public ObservableCollection<AttachmentChipViewModel> Attachments { get; } = [];
    public ObservableCollection<SlashCommandSuggestion> FilteredCommands { get; } = [];
    public ObservableCollection<FileMentionSuggestion> FilteredFiles { get; } = [];
    public ObservableCollection<QueuedPrompt> QueuedPrompts { get; } = [];
    /// <summary>Claude Code / Antigravity workflow task entries rendered in the Docked Activity Tray.</summary>
    public ObservableCollection<WorkflowTaskViewModel> WorkflowTasks { get; } = [];
    /// <summary>SubAgents dispatched during this session (Docked Activity Tray, Slide-Over Drawer & Sidebar).</summary>
    public ObservableCollection<SubagentRunViewModel> Subagents { get; } = [];
    /// <summary>Persistent background Bash shells running or recently exited across turns.</summary>
    public ObservableCollection<BackgroundBashViewModel> BackgroundShells { get; } = [];
    /// <summary>Advertised ACP auth methods surfaced when authentication is required.</summary>
    public ObservableCollection<AuthMethodActionViewModel> AuthMethods { get; } = [];
    /// <summary>Saved ACP sessions fetched via <c>session/list</c>.</summary>
    public ObservableCollection<AcpSessionListEntryViewModel> RemoteSessions { get; } = [];
    /// <summary>Scheme C top docked collaboration topology capsules (parent, direct spawned children, and peer negotiation threads).</summary>
    public ObservableCollection<CollabRibbonNodeViewModel> CollabRibbonNodes { get; } = [];
    /// <summary>Pending permission approval requests bubbled up from direct child agents spawned by this session.</summary>
    public ObservableCollection<CollabBubbledApprovalViewModel> BubbledApprovals { get; } = [];
    /// <summary>Modal viewer for collapsed multi-line Bash scripts (Bash Style 2).</summary>
    public ScriptPreviewViewModel ScriptPreview { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveCollabThread))]
    public partial CollabThreadPanelViewModel? ActiveCollabThread { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RouteModeLabel))]
    public partial bool IsDirectRelayMode { get; set; }

    public bool HasCollabTopology => CollabRibbonNodes.Count > 0;
    public bool HasBubbledApprovals => BubbledApprovals.Count > 0;
    public bool HasActiveCollabThread => ActiveCollabThread is not null;
    /// <summary>True when the draft @-mentions at least one existing session (the direct-relay toggle applies).</summary>
    public bool HasSessionMention => _mentionSpans.Any(s => s.Token.Kind == "session");

    /// <summary>
    /// Session / agent mention tokens of the current draft, written inline in <see cref="InputText"/> (for example
    /// <c>让 @Codex（重构登录） 和 @Claude Code 一起处理</c>). Bound to the composer's <c>MentionEditing.Tokens</c>.
    /// </summary>
    public IReadOnlyList<MentionToken> MentionTokens { get; private set; } = [];

    /// <summary>The composer's caret, two-way bound so <c>@</c> queries and insertion follow the caret, not the text's end.</summary>
    [ObservableProperty] public partial int ComposerCaretIndex { get; set; }
    public string RouteModeLabel => AppLangResources.Get(
        IsDirectRelayMode ? AppLangResourceKind.CollabRouteDirectRelay : AppLangResourceKind.CollabRouteOrchestrate);

    [RelayCommand]
    private void ToggleRouteMode() => IsDirectRelayMode = !IsDirectRelayMode;

    // ICollabSessionBridge implementation
    string ICollabSessionBridge.ItemId => _item.Id;
    string ICollabSessionBridge.DisplayTitle => _item.DisplayTitle;
    bool ICollabSessionBridge.IsConnected => _session?.IsConnected == true;
    string? ICollabSessionBridge.CurrentStatusDetail => StatusDetail;

    public async Task<bool> DeliverCollabPromptAsync(string promptText, string? threadId = null, string? taskId = null, CancellationToken cancellationToken = default)
    {
        if (_disposed || string.IsNullOrWhiteSpace(promptText)) return false;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetUi(() =>
        {
            if (_disposed)
            {
                tcs.TrySetResult(false);
                return;
            }
            if (_session is null)
            {
                EnqueuePrompt(promptText.Trim(), [], threadId, taskId);
                if (State != AgentGuiState.Connecting)
                {
                    _ = ConnectAsync();
                }
                tcs.TrySetResult(true);
                return;
            }
            if (_isTurnActive || IsTurnActive)
            {
                EnqueuePrompt(promptText.Trim(), [], threadId, taskId);
                tcs.TrySetResult(true);
                return;
            }
            _ = StartPumpAsync(CreateQueuedPrompt(promptText.Trim(), [], threadId, taskId));
            tcs.TrySetResult(true);
        });
        return await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public int CancelQueuedCollabPrompts(string? threadId = null, string? taskId = null)
    {
        if (_disposed || (string.IsNullOrWhiteSpace(threadId) && string.IsNullOrWhiteSpace(taskId))) return 0;

        int RemoveMatching()
        {
            var removed = 0;
            for (var i = QueuedPrompts.Count - 1; i >= 0; i--)
            {
                var q = QueuedPrompts[i];
                var matchesThread = !string.IsNullOrWhiteSpace(threadId)
                    && string.Equals(q.CollabThreadId, threadId, StringComparison.Ordinal);
                var matchesTask = !string.IsNullOrWhiteSpace(taskId)
                    && string.Equals(q.CollabTaskId, taskId, StringComparison.Ordinal);
                if (matchesThread || matchesTask)
                {
                    QueuedPrompts.RemoveAt(i);
                    removed++;
                }
            }
            if (removed > 0)
            {
                RefreshQueueState();
            }
            return removed;
        }

        if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            return RemoveMatching();
        }
        return Dispatcher.UIThread.Invoke(RemoveMatching);
    }

    public string ReadRecentSummary(int maxTurns = 5)
    {
        var sb = new StringBuilder();
        var collected = new List<string>();
        for (var i = Timeline.Count - 1; i >= 0 && collected.Count < maxTurns * 2; i--)
        {
            if (Timeline[i] is UserTurnItem u && !string.IsNullOrWhiteSpace(u.Text))
            {
                var snippet = u.Text.Length > 400 ? u.Text[..400] + "…" : u.Text;
                collected.Add($"User: {snippet}");
            }
            else if (Timeline[i] is AssistantTurnItem a && !string.IsNullOrWhiteSpace(a.MarkdownText))
            {
                var md = a.MarkdownText.Trim();
                var snippet = md.Length > 600 ? md[..600] + "…" : md;
                collected.Add($"Assistant ({AgentName}): {snippet}");
            }
        }
        collected.Reverse();
        foreach (var entry in collected)
        {
            sb.AppendLine(entry);
            sb.AppendLine();
        }
        return sb.Length > 0 ? sb.ToString().Trim() : "(No conversation turns recorded yet.)";
    }

    private void OnCollabChanged(string? _) => SetUi(RefreshCollabTopology);

    private void RefreshCollabTopology()
    {
        if (_disposed || _collab is null) return;

        CollabRibbonNodes.Clear();
        var children = _collab.GetSpawnedChildren(_item.Id);
        var threads = _collab.GetThreadsForItem(_item.Id);
        var parentItem = !string.IsNullOrEmpty(_item.ParentItemId) ? _projects.FindItem(_item.ParentItemId) : null;

        // Only populate Scheme C's ribbon when this session participates in parent/child or peer collaboration.
        if (parentItem is not null || children.Count > 0 || threads.Count > 0)
        {
            if (parentItem is not null)
            {
                CollabRibbonNodes.Add(new CollabRibbonNodeViewModel(
                    parentItem.Id,
                    "parent",
                    parentItem.AgentId ?? "terminal",
                    parentItem.DisplayTitle,
                    DescribeRibbonStatus(parentItem.Id),
                    null,
                    isCurrent: false,
                    activate: () => _projects.ActivateItem(parentItem.Id)));
            }

            CollabRibbonNodes.Add(new CollabRibbonNodeViewModel(
                _item.Id,
                "self",
                AgentId,
                _item.DisplayTitle,
                IsBusy ? "working" : "idle",
                _item.CollabIsolation is "worktree" ? "wt" : _item.CollabIsolation is "read_only" ? "ro" : null,
                isCurrent: true,
                activate: () => { }));

            foreach (var child in children)
            {
                var childId = child.Id;
                var childMeta = child.CollabIsolation switch
                {
                    "worktree" => "wt",
                    "read_only" => "ro",
                    _ => null,
                };
                CollabRibbonNodes.Add(new CollabRibbonNodeViewModel(
                    childId,
                    "child",
                    child.AgentId ?? "terminal",
                    child.DisplayTitle,
                    DescribeRibbonStatus(childId),
                    childMeta,
                    isCurrent: false,
                    activate: () => _projects.ActivateItem(childId)));
            }

            // Peer negotiation threads (involving sessions that are neither our direct parent nor direct child).
            foreach (var thread in threads)
            {
                var otherId = thread.ParticipantItemIds.FirstOrDefault(pid => pid != _item.Id);
                if (otherId is null) continue;
                if (otherId == _item.ParentItemId || children.Any(c => c.Id == otherId)) continue;
                var other = _projects.FindItem(otherId);
                if (other is null) continue;
                var threadId = thread.ThreadId;
                CollabRibbonNodes.Add(new CollabRibbonNodeViewModel(
                    threadId,
                    "peer",
                    other.AgentId ?? "terminal",
                    other.DisplayTitle,
                    thread.Status == "paused_budget" ? "waiting_approval" : DescribeRibbonStatus(otherId),
                    $"{thread.Hops}/{thread.MaxHops}",
                    isCurrent: ActiveCollabThread?.ThreadId == threadId,
                    activate: () => ToggleCollabThread(threadId)));
            }
        }

        BubbledApprovals.Clear();
        foreach (var apr in _collab.GetBubbledApprovalsForParent(_item.Id))
        {
            BubbledApprovals.Add(new CollabBubbledApprovalViewModel(apr, childId => _projects.ActivateItem(childId)));
        }

        if (ActiveCollabThread is { } openThread)
        {
            var refreshed = _collab.FindThread(openThread.ThreadId);
            ActiveCollabThread = refreshed is null ? null : BuildThreadPanel(refreshed);
        }

        OnPropertyChanged(nameof(HasCollabTopology));
        OnPropertyChanged(nameof(HasBubbledApprovals));
    }

    private void ToggleCollabThread(string threadId)
    {
        if (ActiveCollabThread?.ThreadId == threadId)
        {
            ActiveCollabThread = null;
            RefreshCollabTopology();
            return;
        }
        if (_collab?.FindThread(threadId) is { } thread)
        {
            ActiveCollabThread = BuildThreadPanel(thread);
            RefreshCollabTopology();
        }
    }

    private CollabThreadPanelViewModel BuildThreadPanel(CollabThreadRecord thread) =>
        new(
            thread,
            (tid, text, extend) => _collab?.InterjectInThreadAsync(tid, text, extend) ?? Task.CompletedTask,
            () =>
            {
                ActiveCollabThread = null;
                RefreshCollabTopology();
            });

    private string DescribeRibbonStatus(string itemId)
    {
        if (_collab?.GetBubbledApprovalsForParent(_item.Id).Any(a => a.ChildItemId == itemId) == true)
        {
            return "waiting_approval";
        }
        return (_activity?.Of(itemId) ?? ItemActivity.Idle) switch
        {
            ItemActivity.Working => "working",
            ItemActivity.Waiting => "waiting_approval",
            ItemActivity.Done => "done",
            _ => "idle",
        };
    }

    [ObservableProperty] public partial AgentGuiState State { get; private set; } = AgentGuiState.Connecting;
    [ObservableProperty] public partial string? StatusDetail { get; private set; }
    [ObservableProperty] public partial string InputText { get; set; } = "";
    [ObservableProperty] public partial string? ErrorBanner { get; private set; }
    [ObservableProperty] public partial bool IsAuthRequired { get; private set; }
    [ObservableProperty] public partial bool IsSessionHistoryOpen { get; set; }
    [ObservableProperty] public partial bool IsLoadingSessionHistory { get; private set; }
    [ObservableProperty] public partial string SourceLabel { get; private set; } = "";
    [ObservableProperty] public partial long ContextUsed { get; private set; }
    [ObservableProperty] public partial long ContextSize { get; private set; }
    [ObservableProperty] public partial double? ContextCostAmount { get; private set; }
    [ObservableProperty] public partial string? ContextCostCurrency { get; private set; }
    [ObservableProperty] public partial bool IsCommandPopupOpen { get; set; }
    [ObservableProperty] public partial bool IsFilePopupOpen { get; set; }
    [ObservableProperty] public partial int SelectedPickerIndex { get; set; }
    [ObservableProperty] public partial bool ShowDiagnostics { get; set; }
    [ObservableProperty] public partial string DiagnosticsText { get; private set; } = "";
    [ObservableProperty] public partial bool IsPinnedToBottom { get; set; } = true;
    [ObservableProperty] public partial bool IsActivityTrayExpanded { get; set; }
    [ObservableProperty] public partial bool IsWorkflowTasksExpanded { get; set; } = true;
    [ObservableProperty] public partial SubagentRunViewModel? SelectedSubagent { get; set; }
    [ObservableProperty] public partial BackgroundBashViewModel? SelectedBackgroundShell { get; set; }
    [ObservableProperty] public partial WorkflowPhase CurrentPhase { get; private set; } = WorkflowPhase.Idle;
    [ObservableProperty] public partial bool IsQueueExpanded { get; set; } = true;
    [ObservableProperty] public partial bool IsQueuePaused { get; private set; }
    [ObservableProperty] public partial bool IsCancelling { get; private set; }

    /// <summary>Grace period to wait for the ACP agent to reply to <c>session/prompt</c> after <c>session/cancel</c> before force-cancelling locally.</summary>
    internal TimeSpan CancelGraceTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Optional Antigravity CLI app data directory override for deterministic subagent transcript tests.</summary>
    internal string? AgyAppDataDirOverride
    {
        get => _agyAppDataDirOverride;
        set
        {
            _agyAppDataDirOverride = value;
            _agyParentTranscriptPath = null;
            _agyParentTranscriptOffset = 0;
        }
    }

    public ScriptPreviewViewModel? ActiveScriptPreview => ScriptPreview.IsOpen ? ScriptPreview : null;
    public bool HasWorkflowTasks => WorkflowTasks.Count > 0;
    public bool HasSubagents => Subagents.Count > 0;
    public bool HasBackgroundShells => BackgroundShells.Count > 0;
    public bool HasActivityTray => HasWorkflowTasks || HasSubagents || HasBackgroundShells;
    public bool IsSingleBackgroundShell => !HasSubagents && !HasWorkflowTasks && BackgroundShells.Count == 1;
    public bool IsMultiActivityTray => HasActivityTray && !IsSingleBackgroundShell;
    public BackgroundBashViewModel? SingleBackgroundShell => IsSingleBackgroundShell ? BackgroundShells[0] : null;
    public bool ShowCollapsedSingleShellActions => IsSingleBackgroundShell && !IsActivityTrayExpanded;
    public bool ShowCollapsedSingleShellStop => ShowCollapsedSingleShellActions && (SingleBackgroundShell?.IsRunning ?? false);
    public bool HasSelectedSubagent => SelectedSubagent is not null;
    public bool HasSelectedBackgroundShell => SelectedBackgroundShell is not null;
    public bool HasRunningSubagents => Subagents.Any(s => s.IsRunning);
    public bool HasRunningBackgroundShells => BackgroundShells.Any(s => s.IsRunning);
    public bool HasRunningWorkflowTasks => WorkflowTasks.Any(t => t.IsInProgress);
    public bool HasFailedWorkflowTasks => WorkflowTasks.Any(t => t.IsFailed);
    public bool ShowSpinnerInActivityHeader => HasRunningSubagents || HasRunningWorkflowTasks;
    public bool ShowRunningBashDotOnly => HasRunningBackgroundShells && !ShowSpinnerInActivityHeader;
    public bool HasFailedBackgroundShell => BackgroundShells.Any(s => s.IsFailed);
    public bool HasRunningActivity => HasRunningSubagents || HasRunningBackgroundShells || HasRunningWorkflowTasks;
    public bool HasFailedActivity => !HasRunningActivity && (HasFailedBackgroundShell || Subagents.Any(s => s.IsFailed) || HasFailedWorkflowTasks);
    public bool HasCompletedActivity => HasActivityTray && !HasRunningActivity && !HasFailedActivity;
    public bool HasExpandedBackgroundShellLogs => BackgroundShells.Any(s => s.IsLogsExpanded);
    public bool IsOnlyWorkflowTasks => HasWorkflowTasks && !HasSubagents && !HasBackgroundShells;
    public bool ShowWorkflowTasksSectionHeader => HasWorkflowTasks && (HasSubagents || HasBackgroundShells);
    public bool IsWorkflowTasksListVisible => !ShowWorkflowTasksSectionHeader || IsWorkflowTasksExpanded;
    public double WorkflowProgressPercent => WorkflowTasks.Count == 0
        ? 0d
        : WorkflowTasks.Count(t => t.IsCompleted) * 100d / WorkflowTasks.Count;
    public bool IsAllWorkflowTasksCompleted => WorkflowTasks.Count > 0 && WorkflowTasks.All(t => t.IsCompleted);
    public string WorkflowProgressRatioText => $"{WorkflowTasks.Count(t => t.IsCompleted)}/{WorkflowTasks.Count}";
    public string? ActiveWorkflowTaskTitle
    {
        get
        {
            var active = WorkflowTasks.FirstOrDefault(t => t.IsInProgress);
            if (active is null) return null;
            return !string.IsNullOrWhiteSpace(active.ActiveForm) ? active.ActiveForm : active.Title;
        }
    }
    public bool ShowCollapsedActiveTaskPreview => IsOnlyWorkflowTasks && !IsActivityTrayExpanded && !string.IsNullOrWhiteSpace(ActiveWorkflowTaskTitle);
    public bool ShowMultiHeaderSummary => !IsOnlyWorkflowTasks;

    public string ActivityTrayTitle
    {
        get
        {
            if (HasSubagents && (HasBackgroundShells || HasWorkflowTasks))
                return AppLangResources.Get(AppLangResourceKind.AgentGuiActivityTrayTitleMixed);
            if (HasSubagents)
                return AppLangResources.Get(AppLangResourceKind.AgentGuiActivityTrayTitleSubagents);
            if (HasBackgroundShells && HasWorkflowTasks)
                return AppLangResources.Get(AppLangResourceKind.AgentGuiActivityTrayTitleMixed);
            if (HasBackgroundShells)
                return AppLangResources.Get(AppLangResourceKind.AgentGuiActivityTrayTitleShells);
            if (HasWorkflowTasks)
                return AppLangResources.Get(AppLangResourceKind.AgentGuiActivityTrayTitleTasks);
            return "";
        }
    }

    public string ActivityTraySummary
    {
        get
        {
            var parts = new List<string>(3);
            if (Subagents.Count > 0)
            {
                parts.Add(AppLangResources.Format(
                    AppLangResourceKind.AgentGuiActivityTraySubagentsSummary,
                    Subagents.Count(s => s.IsDone),
                    Subagents.Count));
            }
            if (BackgroundShells.Count > 0)
            {
                var running = BackgroundShells.Count(s => s.IsRunning);
                parts.Add(running > 0
                    ? AppLangResources.Format(AppLangResourceKind.AgentGuiActivityTrayShellsRunningSummary, running)
                    : AppLangResources.Format(AppLangResourceKind.AgentGuiActivityTrayShellsCountSummary, BackgroundShells.Count));
            }
            if (WorkflowTasks.Count > 0)
            {
                parts.Add(AppLangResources.Format(
                    AppLangResourceKind.AgentGuiActivityTrayTasksSummary,
                    WorkflowTasks.Count(t => t.IsCompleted),
                    WorkflowTasks.Count));
            }
            return string.Join(" · ", parts);
        }
    }

    public string WorkflowTasksSummaryText => AppLangResources.Format(
        AppLangResourceKind.AgentGuiActivityTrayTasksSummary,
        WorkflowTasks.Count(t => t.IsCompleted),
        WorkflowTasks.Count);

    public string CurrentPhaseLabel => CurrentPhase switch
    {
        WorkflowPhase.Research => AppLangResources.Get(AppLangResourceKind.AgentGuiPhaseResearch),
        WorkflowPhase.PlanGate => AppLangResources.Get(AppLangResourceKind.AgentGuiPhasePlanGate),
        WorkflowPhase.Executing => AppLangResources.Get(AppLangResourceKind.AgentGuiPhaseExecute),
        WorkflowPhase.Verifying => AppLangResources.Get(AppLangResourceKind.AgentGuiPhaseVerify),
        WorkflowPhase.Completed => AppLangResources.Get(AppLangResourceKind.AgentGuiPhasePlan),
        _ => "",
    };

    public bool IsPhasePlanGate => CurrentPhase == WorkflowPhase.PlanGate;
    public bool IsPhaseExecuting => CurrentPhase == WorkflowPhase.Executing;
    public bool IsPhaseCompleted => CurrentPhase == WorkflowPhase.Completed;

    partial void OnIsActivityTrayExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowCollapsedSingleShellActions));
        OnPropertyChanged(nameof(ShowCollapsedSingleShellStop));
        OnPropertyChanged(nameof(ShowCollapsedActiveTaskPreview));
    }

    partial void OnIsWorkflowTasksExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsWorkflowTasksListVisible));
    }

    private void OnScriptPreviewPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScriptPreviewViewModel.IsOpen))
        {
            OnPropertyChanged(nameof(ActiveScriptPreview));
        }
    }

    partial void OnSelectedSubagentChanged(SubagentRunViewModel? value) => OnPropertyChanged(nameof(HasSelectedSubagent));

    partial void OnSelectedBackgroundShellChanged(BackgroundBashViewModel? value)
    {
        foreach (var sh in BackgroundShells)
        {
            sh.IsSelected = ReferenceEquals(sh, value);
        }
        value?.ClearUnread();
        OnPropertyChanged(nameof(HasSelectedBackgroundShell));
    }

    partial void OnCurrentPhaseChanged(WorkflowPhase value)
    {
        OnPropertyChanged(nameof(CurrentPhaseLabel));
        OnPropertyChanged(nameof(IsPhasePlanGate));
        OnPropertyChanged(nameof(IsPhaseExecuting));
        OnPropertyChanged(nameof(IsPhaseCompleted));
    }

    public bool IsConnecting => State == AgentGuiState.Connecting;
    public bool IsBusy => State is AgentGuiState.Connecting or AgentGuiState.Working;
    public bool IsWorking => State == AgentGuiState.Working;
    public bool IsTurnActive => State is AgentGuiState.Working or AgentGuiState.Waiting;
    public bool HasComposerInput => !string.IsNullOrWhiteSpace(InputText) || Attachments.Count > 0;
    public bool CanSend => State is AgentGuiState.Ready or AgentGuiState.Waiting or AgentGuiState.Working
        && HasComposerInput;
    public bool CanSendNow => IsTurnActive && CanSend;
    public bool ShowComposerLoading => IsTurnActive && !HasComposerInput;
    public bool CanStopFromComposerButton => ShowComposerLoading && !IsCancelling;
    public bool CanExecuteComposerAction => CanSend || IsTurnActive;
    public bool ShowEmptyState => Timeline.Count == 0 && !HasErrorBanner && !IsAuthRequired;
    public bool HasTimeline => Timeline.Count > 0;
    public bool HasErrorBanner => !string.IsNullOrWhiteSpace(ErrorBanner);
    public bool HasAttachments => Attachments.Count > 0;
    public int QueuedCount => QueuedPrompts.Count;
    public bool HasQueuedPrompts => QueuedPrompts.Count > 0;
    public bool ShowResumeQueueButton => IsQueuePaused && HasQueuedPrompts && !IsTurnActive && State == AgentGuiState.Ready;
    public string FirstQueuedPreview => QueuedPrompts.Count > 0 ? QueuedPrompts[0].PreviewText : "";
    public bool ShowCollapsedQueuePreview => !IsQueueExpanded && HasQueuedPrompts;
    public string QueuedBadgeText => AppLangResources.Format(AppLangResourceKind.AgentGuiQueuedBadge, QueuedPrompts.Count);
    public string SendNowTooltip => AppLangResources.Format(AppLangResourceKind.AgentGuiSendNowTip, OperatingSystem.IsMacOS() ? "⌘↵" : "Ctrl+↵");
    public string SendButtonTooltip => ShowComposerLoading
        ? StopButtonLabel
        : IsTurnActive
            ? AppLangResources.Get(AppLangResourceKind.AgentGuiQueuePromptTip)
            : AppLangResources.Get(AppLangResourceKind.AgentGuiSend);
    public string StopButtonLabel => IsCancelling
        ? AppLangResources.Get(AppLangResourceKind.AgentGuiStopping)
        : AppLangResources.Get(AppLangResourceKind.AgentGuiStop);
    public bool HasContextUsage => ContextSize > 0 || ContextUsed > 0;
    public bool HasDiagnostics => !string.IsNullOrWhiteSpace(DiagnosticsText);
    public bool ShowReconnect => State is AgentGuiState.Disconnected or AgentGuiState.Error || IsAuthRequired;
    public bool ShowJumpToBottom => !IsPinnedToBottom && Timeline.Count > 0;
    public bool HasAuthMethods => AuthMethods.Count > 0;
    public bool CanLogout => _session?.Initialized?.AgentCapabilities.AuthCapabilities.Logout == true
        && State is AgentGuiState.Ready or AgentGuiState.Working;
    public bool CanForkSession => _session?.Initialized?.AgentCapabilities.SessionCapabilities.SupportsFork == true
        && !string.IsNullOrWhiteSpace(_session.SessionId)
        && !IsConnecting;
    public bool CanListSessions => _session?.Initialized?.AgentCapabilities.SessionCapabilities.SupportsList == true
        && !IsConnecting;
    public bool HasRemoteSessions => RemoteSessions.Count > 0;
    public bool ShowEmptyRemoteSessions => !IsLoadingSessionHistory && RemoteSessions.Count == 0;

    public string EmptyTitle => AppLangResources.Format(AppLangResourceKind.AgentGuiEmptyTitle, AgentName);
    public string ComposerPlaceholder => AppLangResources.Format(AppLangResourceKind.AgentGuiComposerPlaceholder, AgentName);
    public string ContextUsageText
    {
        get
        {
            if (!HasContextUsage) return "";
            var baseText = ContextSize > 0
                ? AppLangResources.Format(AppLangResourceKind.AgentGuiContextUsage, FormatTokens(ContextUsed), FormatTokens(ContextSize))
                : $"{FormatTokens(ContextUsed)} tok";
            if (ContextCostAmount is > 0)
            {
                var symbol = string.Equals(ContextCostCurrency, "USD", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(ContextCostCurrency)
                    ? "$"
                    : $"{ContextCostCurrency} ";
                return $"{baseText} · {symbol}{ContextCostAmount.Value:0.00##}";
            }
            return baseText;
        }
    }
    public double ContextUsagePercent => ContextSize > 0 ? Math.Clamp((double)ContextUsed / ContextSize * 100.0, 0, 100) : 0;

    public string StatusText => State switch
    {
        AgentGuiState.Connecting => !string.IsNullOrWhiteSpace(StatusDetail)
            ? StatusDetail!
            : AppLangResources.Get(AppLangResourceKind.AgentGuiStatusConnecting),
        AgentGuiState.Ready => AppLangResources.Get(AppLangResourceKind.AgentGuiStatusReady),
        AgentGuiState.Working => AppLangResources.Get(AppLangResourceKind.AgentGuiStatusThinking),
        AgentGuiState.Waiting => AppLangResources.Get(AppLangResourceKind.AgentGuiStatusWaiting),
        AgentGuiState.Disconnected => AppLangResources.Get(AppLangResourceKind.AgentGuiStatusDisconnected),
        _ => AppLangResources.Get(AppLangResourceKind.AgentGuiStatusError),
    };

    partial void OnStatusDetailChanged(string? value) => OnPropertyChanged(nameof(StatusText));

    partial void OnIsQueueExpandedChanged(bool value) => OnPropertyChanged(nameof(ShowCollapsedQueuePreview));

    partial void OnIsQueuePausedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowResumeQueueButton));
        ResumeQueueCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsCancellingChanged(bool value)
    {
        OnPropertyChanged(nameof(StopButtonLabel));
        OnPropertyChanged(nameof(CanStopFromComposerButton));
        OnPropertyChanged(nameof(SendButtonTooltip));
    }

    private void NotifyComposerActionStateChanged()
    {
        OnPropertyChanged(nameof(HasComposerInput));
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(CanSendNow));
        OnPropertyChanged(nameof(ShowComposerLoading));
        OnPropertyChanged(nameof(CanStopFromComposerButton));
        OnPropertyChanged(nameof(CanExecuteComposerAction));
        OnPropertyChanged(nameof(SendButtonTooltip));
        SendPromptCommand.NotifyCanExecuteChanged();
        SendPromptNowCommand.NotifyCanExecuteChanged();
        ComposerActionCommand.NotifyCanExecuteChanged();
    }

    public event EventHandler<bool>? ScrollToBottomRequested;
    public event EventHandler? FocusComposerRequested;

    partial void OnIsPinnedToBottomChanged(bool value) => OnPropertyChanged(nameof(ShowJumpToBottom));

    partial void OnStateChanged(AgentGuiState value)
    {
        OnPropertyChanged(nameof(IsConnecting));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsWorking));
        OnPropertyChanged(nameof(IsTurnActive));
        OnPropertyChanged(nameof(ShowResumeQueueButton));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ShowReconnect));
        OnPropertyChanged(nameof(CanLogout));
        OnPropertyChanged(nameof(CanForkSession));
        OnPropertyChanged(nameof(CanListSessions));
        NotifyComposerActionStateChanged();
        CancelTurnCommand.NotifyCanExecuteChanged();
        ResumeQueueCommand.NotifyCanExecuteChanged();
        ForkSessionCommand.NotifyCanExecuteChanged();
        LogoutCommand.NotifyCanExecuteChanged();

        var mapped = value switch
        {
            AgentGuiState.Working => ItemActivity.Working,
            AgentGuiState.Waiting => ItemActivity.Waiting,
            AgentGuiState.Error => ItemActivity.Error,
            _ => ItemActivity.Idle,
        };
        _activity?.ReportStatus(_item.Id, mapped);
        _activeTurn?.SetWaitingForInput(value == AgentGuiState.Waiting);
    }

    partial void OnInputTextChanged(string value)
    {
        if (!_applyingHistory)
        {
            _historyCursor = -1;
        }
        NotifyComposerActionStateChanged();
        // The TextBox moves its caret right after changing the text; until that arrives, assume the edit happened at
        // the caret (true for typing, deleting and IME commits) so the @ picker does not flicker.
        var delta = value.Length - _lastInputLength;
        _lastInputLength = value.Length;
        _caretForPickers = Math.Clamp(ComposerCaretIndex + delta, 0, value.Length);
        RefreshMentionSpans();
        UpdateInlinePickers(value);
    }

    partial void OnComposerCaretIndexChanged(int value)
    {
        _caretForPickers = Math.Clamp(value, 0, InputText.Length);
        UpdateInlinePickers(InputText);
    }

    /// <summary>
    /// True once the session was started (by a view coming on screen or by a device asking for it). Kept here, not on
    /// the view: a tab closed and opened again gets a new view over this model, and must not start the session again.
    /// </summary>
    public bool HasStarted { get; private set; }

    /// <summary>Starts the ACP session the first time it is called; later calls do nothing.</summary>
    public void EnsureStarted()
    {
        if (HasStarted || _disposed) return;
        HasStarted = true;
        _ = ConnectAsync();
    }

    /// <summary>Starts or reconnects the underlying ACP child session.</summary>
    public async Task ConnectAsync()
    {
        if (_disposed) return;
        await DisconnectInternalAsync();

        _connectCts = new CancellationTokenSource();
        var ct = _connectCts.Token;

        SetUi(() =>
        {
            State = AgentGuiState.Connecting;
            StatusDetail = null;
            ErrorBanner = null;
            IsAuthRequired = false;
            OnPropertyChanged(nameof(HasErrorBanner));
            OnPropertyChanged(nameof(ShowEmptyState));
        });

        try
        {
            var agentSettings = _settings.Current.Agents;
            var profile = agentSettings.ProfileOf(_definition.Id);
            IReadOnlyDictionary<string, string>? accountEnv = null;
            IReadOnlyList<string>? unsetEnv = null;
            if (_accounts is not null && _definition.Accounts is not null)
            {
                var resolvedAcc = _accounts.Resolve(_definition, profile, _project, _item.Launch);
                accountEnv = resolvedAcc.Environment;
                unsetEnv = resolvedAcc.UnsetEnvironment;
            }

            var installation = _scanner.Find(_definition.Id);
            var perm = Composer.SelectedPermissionId ?? PermissionModes.Default;
            var plan = await _launchResolver.ResolveAsync(
                _definition,
                installation,
                agentSettings,
                WorkingDirectory,
                perm,
                accountEnv,
                unsetEnv,
                ct,
                progress => SetUi(() => StatusDetail = progress),
                launchEnv: _item.Launch?.Env,
                initialModelId: _desiredModelId,
                initialEffortId: _desiredEffortId).ConfigureAwait(false);

            SetUi(() => StatusDetail = null);

            var additionalDirectories = ResolveAdditionalDirectories();
            IReadOnlyList<AcpMcpServerConfig> mcpServers = agentSettings.AcpMcpServers;
            if (_bots?.FindBotByBackingProjectId(_project.Id) is { } ownerBot)
            {
                var botMcpServers = await _bots.BuildAcpMcpServersAsync(ownerBot.Id, ct).ConfigureAwait(false);
                mcpServers = BotConnectorRuntime.MergeMcpServers(agentSettings.AcpMcpServers, botMcpServers);
            }
            if (_collab?.BuildAcpMcpServer(_item.Id) is { } collabMcp)
            {
                mcpServers = BotConnectorRuntime.MergeMcpServers(mcpServers, [collabMcp]);
            }

            var callbacks = new AcpSessionCallbacks
            {
                EnableFormElicitation = agentSettings.AcpEnableFormElicitation,
                EnableUrlElicitation = agentSettings.AcpEnableUrlElicitation,
                EnableClientFileSystem = agentSettings.AcpEnableClientFileSystem,
                EnableClientTerminal = agentSettings.AcpEnableClientTerminal,
                McpServers = mcpServers,
                AllowedFileSystemRoots = BuildAllowedFileSystemRoots(additionalDirectories),
                GetActivePermissionMode = () => Composer.SelectedPermissionId ?? PermissionModes.Default,
                OnPermissionRequest = HandlePermissionRequestAsync,
                OnQuestionRequest = HandleQuestionRequestAsync,
                OnFormElicitationRequest = HandleFormElicitationRequestAsync,
                OnUrlElicitationRequest = HandleUrlElicitationRequestAsync,
            };

            var session = _sessionFactory.Create(plan, callbacks);
            session.EventReceived += OnSessionEvent;
            _session = session;

            // Set the replay flag before StartAsync: the session emits session/load history synchronously, and a
            // fire-and-forget UI post can run after those events and drop them (empty timeline on reopen).
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                SourceLabel = session.SourceLabel;
                // Rebuild history from session/load replay only when nothing is on screen yet
                // (reopened tab / app restart); a reconnect in a live tab already shows it.
                _acceptReplay = Timeline.Count == 0;
                ClearReplayBuffer();
            });

            var skipRestoredResume = !_hasConnectedOnce
                && _projects.WasRestored(_item.Id)
                && !_settings.Current.Terminal.ResumeAgentsOnRestart;
            var requestedResumeId = skipRestoredResume ? null : (_item.AcpSessionId ?? _item.Launch?.SessionId);
            _hasConnectedOnce = true;

            var started = await session.StartAsync(
                WorkingDirectory,
                additionalDirectories: additionalDirectories,
                resumeSessionId: requestedResumeId,
                cancellationToken: ct).ConfigureAwait(false);

            var desiredPermission = Composer.SelectedPermissionId ?? PermissionModes.Default;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                AuthMethods.Clear();
                OnPropertyChanged(nameof(HasAuthMethods));
                _projects.SetItemSessionId(_item.Id, started.SessionId, isAcp: true);
                InitAgyTranscriptMonitor(started.SessionId);
                if (!string.IsNullOrWhiteSpace(requestedResumeId) && started.Kind == "new" && _acceptReplay)
                {
                    // session/load fell back to session/new -> discard any partial replay buffer without touching Timeline.
                    ClearReplayBuffer();
                    _acceptReplay = false;
                }
                FinishHistoryReplay();
                Composer.UpdateFromSession(
                    session.Initialized,
                    session.Modes ?? started.Modes,
                    session.Models ?? started.Models,
                    session.ConfigOptions.Count > 0 ? session.ConfigOptions : started.ConfigOptions,
                    session.AvailableCommands,
                    preserveConfiguredPermission: true);
                _confirmedModelId = Composer.SelectedModelId;
                State = AgentGuiState.Ready;
                OnPropertyChanged(nameof(CanLogout));
                OnPropertyChanged(nameof(CanForkSession));
                OnPropertyChanged(nameof(CanListSessions));
                ForkSessionCommand.NotifyCanExecuteChanged();
                LogoutCommand.NotifyCanExecuteChanged();
                RefreshDiagnostics();
            });

            // Discover third-party gateway/catalog models in the background so custom API models show up in the GUI picker.
            if (_modelDiscovery is not null
                && _definition.Capabilities.Model?.Discover is { } discover
                && !string.Equals(discover.Kind, "command", StringComparison.OrdinalIgnoreCase))
            {
                _ = DiscoverSupplementalModelsAsync(installation?.Path ?? "", plan.Environment, ct);
            }

            // Apply configured AgentMode permission (unless fullAccessArgs already granted full access at process launch).
            var launchedWithFullArgs = desiredPermission == PermissionModes.Full
                && _definition.Acp?.FullAccessArgs is { Count: > 0 };
            if (_definition.Acp?.PermissionStrategy == AcpPermissionStrategy.AgentMode
                && !launchedWithFullArgs
                && !string.IsNullOrEmpty(desiredPermission))
            {
                await ApplyPermissionAsync(desiredPermission).ConfigureAwait(false);
            }

            // Apply requested launch/profile model, effort, and fast mode when the session started on a different default.
            if (!_hasSyncedInitialPreferences)
            {
                _hasSyncedInitialPreferences = true;
                await ApplyInitialLaunchPreferencesAsync(session, ct).ConfigureAwait(false);
            }

            if (!_initialPromptSent && !string.IsNullOrWhiteSpace(_item.Launch?.Prompt))
            {
                _initialPromptSent = true;
                var firstPrompt = _item.Launch!.Prompt!.Trim();
                Task? pump = null;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_disposed || _session is null) return;
                    if (_isTurnActive || IsTurnActive)
                    {
                        EnqueuePrompt(firstPrompt, []);
                        return;
                    }
                    pump = StartPumpAsync(CreateQueuedPrompt(firstPrompt, []));
                });
                if (pump is not null)
                {
                    await pump.ConfigureAwait(false);
                }
            }
            else
            {
                Task? pump = null;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!_disposed && _session is not null && QueuedPrompts.Count > 0 && !_isTurnActive && !IsTurnActive)
                    {
                        pump = StartPumpAsync();
                    }
                });
                if (pump is not null)
                {
                    await pump.ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelled due to close/reconnect.
        }
        catch (AcpAuthRequiredException authEx)
        {
            SetUi(() =>
            {
                PopulateAuthMethods(authEx.AuthMethods.Count > 0 ? authEx.AuthMethods : _session?.Initialized?.AuthMethods);
                IsAuthRequired = true;
                State = AgentGuiState.Disconnected;
                RefreshDiagnostics();
                OnPropertyChanged(nameof(ShowEmptyState));
            });
        }
        catch (Exception ex)
        {
            SetUi(() =>
            {
                ErrorBanner = ex.Message;
                State = AgentGuiState.Error;
                RefreshDiagnostics();
                OnPropertyChanged(nameof(HasErrorBanner));
                OnPropertyChanged(nameof(ShowEmptyState));
            });
        }
    }

    private QueuedPrompt CreateQueuedPrompt(
        string text,
        IReadOnlyList<AcpPromptAttachment> attachments,
        string? collabThreadId = null,
        string? collabTaskId = null) =>
        new(
            text,
            attachments,
            onSendNow: SendQueuedPromptNowAsync,
            onEdit: EditQueuedPrompt,
            onMoveUp: MoveQueuedPromptUp,
            onMoveDown: MoveQueuedPromptDown,
            onRemove: RemoveQueuedPrompt,
            collabThreadId: collabThreadId,
            collabTaskId: collabTaskId);

    private void EnqueuePrompt(
        string text,
        IReadOnlyList<AcpPromptAttachment> attachments,
        string? collabThreadId = null,
        string? collabTaskId = null)
    {
        QueuedPrompts.Add(CreateQueuedPrompt(text, attachments, collabThreadId, collabTaskId));
        RefreshQueueState();
    }

    private (string Text, List<AcpPromptAttachment> Attachments)? CaptureComposerPrompt()
    {
        var text = InputText.Trim();
        if (text.Length == 0 && Attachments.Count == 0) return null;

        var attached = CurrentDraftAttachments();
        InputText = "";
        ClearDraftMentions();
        Attachments.Clear();
        IsCommandPopupOpen = false;
        IsFilePopupOpen = false;
        OnPropertyChanged(nameof(HasAttachments));
        NotifyComposerActionStateChanged();

        if (text.Length > 0 && (_promptHistory.Count == 0 || _promptHistory[^1] != text))
        {
            _promptHistory.Add(text);
        }
        _historyCursor = -1;
        return (text, attached);
    }

    [RelayCommand(CanExecute = nameof(CanExecuteComposerAction), AllowConcurrentExecutions = true)]
    public Task ComposerActionAsync()
    {
        if (CanSend)
        {
            return SendPromptAsync();
        }
        if (IsTurnActive)
        {
            return CancelTurnAsync();
        }
        return Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanSend), AllowConcurrentExecutions = true)]
    public async Task SendPromptAsync()
    {
        if (CaptureComposerPrompt() is not { } captured) return;

        // Direct relay mode: send straight to every @-mentioned existing session (one shared thread) without burning a
        // turn here.
        var directTargets = captured.Attachments.Where(a => a.Kind == "session").Select(a => a.Path).Distinct(StringComparer.Ordinal).ToList();
        if (IsDirectRelayMode && _collab is not null && directTargets.Count > 0)
        {
            var body = string.IsNullOrWhiteSpace(captured.Text) ? "(ping)" : captured.Text;
            var thread = await _collab.SendUserBroadcastAsync(_item.Id, directTargets, body);
            SetUi(() =>
            {
                IsDirectRelayMode = false;
                if (thread is not null)
                {
                    ActiveCollabThread = BuildThreadPanel(thread);
                    RefreshCollabTopology();
                }
            });
            return;
        }

        if (_session is null)
        {
            EnqueuePrompt(captured.Text, captured.Attachments);
            return;
        }

        // If a turn is currently active (Working or Waiting for approval/question), queue this follow-up.
        if (_isTurnActive || IsTurnActive)
        {
            EnqueuePrompt(captured.Text, captured.Attachments);
            return;
        }

        await StartPumpAsync(CreateQueuedPrompt(captured.Text, captured.Attachments));
    }

    /// <summary>Interrupts any active turn gracefully and immediately sends the composer's message.</summary>
    [RelayCommand(CanExecute = nameof(CanSendNow), AllowConcurrentExecutions = true)]
    public async Task SendPromptNowAsync()
    {
        if (_session is null) return;
        if (CaptureComposerPrompt() is not { } captured) return;

        var prompt = CreateQueuedPrompt(captured.Text, captured.Attachments);
        if (_isTurnActive || IsTurnActive)
        {
            await InterruptAndSendAsync(prompt);
            return;
        }

        await StartPumpAsync(prompt);
    }

    /// <summary>Promotes a queued prompt and sends it immediately (gracefully interrupting any active turn).</summary>
    public async Task SendQueuedPromptNowAsync(QueuedPrompt item)
    {
        if (_disposed || _session is null) return;
        if (!QueuedPrompts.Remove(item)) return;
        RefreshQueueState();

        if (_isTurnActive || IsTurnActive)
        {
            await InterruptAndSendAsync(item);
            return;
        }

        await StartPumpAsync(item);
    }

    /// <summary>Moves a queued prompt back into the composer for editing without losing any existing draft.</summary>
    public void EditQueuedPrompt(QueuedPrompt item)
    {
        var idx = QueuedPrompts.IndexOf(item);
        if (idx < 0) return;
        QueuedPrompts.RemoveAt(idx);

        var existingText = InputText.Trim();
        var existingAttachments = CurrentDraftAttachments();
        if (existingText.Length > 0 || existingAttachments.Count > 0)
        {
            QueuedPrompts.Add(CreateQueuedPrompt(existingText, existingAttachments));
        }

        Attachments.Clear();
        ClearDraftMentions();
        foreach (var att in item.Attachments)
        {
            if (att.Kind is "session" or "agent" && !string.IsNullOrEmpty(att.DisplayName))
            {
                // Mentions travel as attachments once captured; bring them back as inline tokens.
                _draftMentions.Add(new MentionToken(att.Kind, att.Path, att.DisplayName, att.MimeType));
                continue;
            }
            Attachments.Add(AttachmentChipViewModel.Removable(att, RemoveAttachment));
        }
        PublishMentionTokens();
        InputText = item.Text;
        ComposerCaretIndex = item.Text.Length;
        OnPropertyChanged(nameof(HasAttachments));
        NotifyComposerActionStateChanged();
        RefreshQueueState();
        FocusComposerRequested?.Invoke(this, EventArgs.Empty);
    }

    public void MoveQueuedPromptUp(QueuedPrompt item)
    {
        var idx = QueuedPrompts.IndexOf(item);
        if (idx <= 0) return;
        QueuedPrompts.Move(idx, idx - 1);
        RefreshQueueState();
    }

    public void MoveQueuedPromptDown(QueuedPrompt item)
    {
        var idx = QueuedPrompts.IndexOf(item);
        if (idx < 0 || idx >= QueuedPrompts.Count - 1) return;
        QueuedPrompts.Move(idx, idx + 1);
        RefreshQueueState();
    }

    public void RemoveQueuedPrompt(QueuedPrompt item)
    {
        if (QueuedPrompts.Remove(item))
        {
            RefreshQueueState();
        }
    }

    [RelayCommand]
    private void ToggleQueueExpanded() => IsQueueExpanded = !IsQueueExpanded;

    [RelayCommand(CanExecute = nameof(ShowResumeQueueButton), AllowConcurrentExecutions = true)]
    public async Task ResumeQueueAsync()
    {
        if (_disposed || _session is null || QueuedPrompts.Count == 0) return;
        IsQueuePaused = false;
        RefreshQueueState();
        if (_isTurnActive || IsTurnActive) return;
        await StartPumpAsync();
    }

    private async Task InterruptAndSendAsync(QueuedPrompt urgent)
    {
        if (_disposed || _session is null) return;

        // If another urgent prompt was already waiting for cancellation, keep it at the head of the queue.
        if (_urgentPrompt is { } previousUrgent)
        {
            QueuedPrompts.Insert(0, previousUrgent);
            RefreshQueueState();
        }
        _urgentPrompt = urgent;
        _manualStopRequested = false;

        if (_isTurnActive || IsTurnActive)
        {
            await RequestCancelActiveTurnAsync(isManualStop: false);
            if (_pumpTask is { } pump)
            {
                await pump;
            }
        }

        // Guard against the narrow window where the previous pump exited just as _urgentPrompt was set.
        if (!_disposed && ReferenceEquals(_urgentPrompt, urgent))
        {
            _urgentPrompt = null;
            await StartPumpAsync(urgent);
        }
    }

    private Task StartPumpAsync(QueuedPrompt? initialPrompt = null)
    {
        if (_pumpTask is { IsCompleted: false })
        {
            if (initialPrompt is not null)
            {
                QueuedPrompts.Insert(0, initialPrompt);
                RefreshQueueState();
            }
            return _pumpTask;
        }

        _pumpTask = RunTurnPumpAsync(initialPrompt);
        return _pumpTask;
    }

    private async Task RunTurnPumpAsync(QueuedPrompt? initialPrompt)
    {
        var next = initialPrompt;
        while (!_disposed && _session is { } activeSession)
        {
            if (next is null)
            {
                if (_urgentPrompt is { } urgent)
                {
                    _urgentPrompt = null;
                    next = urgent;
                }
                else if (!_manualStopRequested && !IsQueuePaused && State == AgentGuiState.Ready && QueuedPrompts.Count > 0)
                {
                    next = QueuedPrompts[0];
                    QueuedPrompts.RemoveAt(0);
                    RefreshQueueState();
                }
                else
                {
                    break;
                }
            }

            _manualStopRequested = false;
            await ExecutePromptAsync(activeSession, next.Text, next.Attachments);
            next = null;

            // 1. An urgent "Send Now" prompt always dispatches immediately after the cancelled turn settles.
            if (_urgentPrompt is not null && State is not (AgentGuiState.Disconnected or AgentGuiState.Error))
            {
                if (State != AgentGuiState.Ready) SetUi(() => State = AgentGuiState.Ready);
                continue;
            }

            // 2. Manual Stop pauses automatic queue draining so remaining queued prompts stay safe for user review.
            if (_manualStopRequested)
            {
                _manualStopRequested = false;
                if (QueuedPrompts.Count > 0)
                {
                    SetUi(() =>
                    {
                        IsQueuePaused = true;
                        RefreshQueueState();
                    });
                }
                break;
            }

            if (State != AgentGuiState.Ready || IsQueuePaused || QueuedPrompts.Count == 0)
            {
                break;
            }
        }
    }

    private async Task ExecutePromptAsync(IAcpSession session, string text, IReadOnlyList<AcpPromptAttachment> attached)
    {
        var turnSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _activeTurnSettled = turnSettled;
        _isTurnActive = true;

        var userTurn = new UserTurnItem(text, attached, ReusePrompt);
        var assistantTurn = new AssistantTurnItem(AgentId, AgentName, OpenToolFile, _workspaceCommands is null ? null : OpenToolDiff, OpenToolScript, workingDirectory: WorkingDirectory);
        _activeTurn = assistantTurn;
        EnsureLiveTickTimer();
        PruneFinishedForegroundSubagents();
        Timeline.Add(userTurn);
        Timeline.Add(assistantTurn);
        IsPinnedToBottom = true;
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(HasTimeline));
        OnPropertyChanged(nameof(ShowJumpToBottom));
        ScrollToBottomRequested?.Invoke(this, true);

        if (!_titledFromPrompt && text.Length > 0 && !userTurn.IsCollabMessage)
        {
            _titledFromPrompt = true;
            var shortTitle = text.Length > 48 ? text[..48] + "…" : text;
            _projects.SetItemTitle(_item.Id, shortTitle);
        }

        State = AgentGuiState.Working;
        _turnCts?.Dispose();
        _turnCts = new CancellationTokenSource();

        try
        {
            var promptToSend = text;
            var acpAttachments = attached;
            var collabMentions = attached.Where(a => a.Kind is "session" or "agent").ToList();
            if (collabMentions.Count > 0 && _collab is not null)
            {
                var refs = collabMentions.Select(a => new CollabMentionReference(a.Kind, a.Path, a.DisplayName, a.MimeType)).ToList();
                promptToSend = text + _collab.FormatMentionContextBlock(_item.Id, refs);
                acpAttachments = attached.Where(a => a.Kind is not ("session" or "agent")).ToList();
            }

            var promptResult = await session.PromptAsync(promptToSend, acpAttachments, _turnCts.Token);
            var stopNotice = FormatStopReasonNotice(promptResult.StopReason);
            string? finalMarkdown = null;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                FlushPendingTextDeltas();
                if (promptResult.Usage is { TotalTokens: > 0 } turnUsage && ContextSize == 0)
                {
                    ContextUsed = turnUsage.TotalTokens;
                    OnPropertyChanged(nameof(HasContextUsage));
                    OnPropertyChanged(nameof(ContextUsageText));
                    OnPropertyChanged(nameof(ContextUsagePercent));
                }
                assistantTurn.Complete(stopReasonNotice: stopNotice);
                finalMarkdown = assistantTurn.MarkdownText;
                if (State is AgentGuiState.Working or AgentGuiState.Waiting) State = AgentGuiState.Ready;
                RefreshDiagnostics();
                if (Subagents.Any(s => s.IsRunning && s.RunsAsynchronously))
                {
                    PollAgySubagents(finalizeMissingTranscript: true);
                }
            });
            _collab?.NotifyTurnCompleted(_item.Id, finalMarkdown);
        }
        catch (OperationCanceledException)
        {
            SetUi(() =>
            {
                FlushPendingTextDeltas();
                assistantTurn.Complete();
                InterruptRunningAsyncSubagents();
                if (State != AgentGuiState.Disconnected) State = AgentGuiState.Ready;
            });
        }
        catch (AcpRpcException ex) when (ex.Code == -32800)
        {
            SetUi(() =>
            {
                FlushPendingTextDeltas();
                assistantTurn.Complete();
                InterruptRunningAsyncSubagents();
                if (State != AgentGuiState.Disconnected) State = AgentGuiState.Ready;
            });
        }
        catch (AcpAuthRequiredException authEx)
        {
            SetUi(() =>
            {
                FlushPendingTextDeltas();
                PopulateAuthMethods(authEx.AuthMethods.Count > 0 ? authEx.AuthMethods : _session?.Initialized?.AuthMethods);
                assistantTurn.Complete(AppLangResources.Get(AppLangResourceKind.AgentGuiAuthRequiredTitle));
                IsAuthRequired = true;
                State = AgentGuiState.Disconnected;
                RefreshDiagnostics();
            });
        }
        catch (Exception ex)
        {
            SetUi(() =>
            {
                FlushPendingTextDeltas();
                assistantTurn.Complete(ex.Message);
                State = AgentGuiState.Ready;
                RefreshDiagnostics();
            });
        }
        finally
        {
            if (ReferenceEquals(_activeTurn, assistantTurn))
            {
                _activeTurn = null;
            }
            SetUi(EnsureLiveTickTimer);
            _isTurnActive = false;
            if (ReferenceEquals(_activeTurnSettled, turnSettled))
            {
                _activeTurnSettled = null;
            }
            if (ReferenceEquals(_cancellingTurn, turnSettled))
            {
                _cancellingTurn = null;
                SetUi(() => IsCancelling = false);
            }
            turnSettled.TrySetResult();
        }
    }

    private void RefreshQueueState()
    {
        for (var i = 0; i < QueuedPrompts.Count; i++)
        {
            var entry = QueuedPrompts[i];
            entry.Position = i + 1;
            entry.CanMoveUp = i > 0;
            entry.CanMoveDown = i < QueuedPrompts.Count - 1;
        }
        if (QueuedPrompts.Count == 0 && IsQueuePaused)
        {
            IsQueuePaused = false;
        }
        OnPropertyChanged(nameof(QueuedCount));
        OnPropertyChanged(nameof(HasQueuedPrompts));
        OnPropertyChanged(nameof(QueuedBadgeText));
        OnPropertyChanged(nameof(FirstQueuedPreview));
        OnPropertyChanged(nameof(ShowCollapsedQueuePreview));
        OnPropertyChanged(nameof(ShowResumeQueueButton));
        ResumeQueueCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ClearQueuedPrompts()
    {
        QueuedPrompts.Clear();
        IsQueuePaused = false;
        RefreshQueueState();
    }

    [RelayCommand]
    private void ClearTimeline()
    {
        if (IsTurnActive) return;
        DisposeAndClearTimeline();
        IsPinnedToBottom = true;
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(HasTimeline));
        OnPropertyChanged(nameof(ShowJumpToBottom));
    }

    /// <summary>Starts a clean new ACP session in this tab without carrying the previous session id.</summary>
    [RelayCommand]
    public async Task NewSessionAsync()
    {
        if (IsTurnActive || _isTurnActive) await CancelTurnAsync();
        _projects.SetItemSessionId(_item.Id, null, isAcp: true);
        _item.AcpSessionId = null;
        _item.Launch?.SessionId = null;
        _titledFromPrompt = false;
        _urgentPrompt = null;
        _cancellingTurn = null;
        _manualStopRequested = false;
        QueuedPrompts.Clear();
        IsQueuePaused = false;
        IsQueueExpanded = true;
        DisposeAndClearTimeline();
        ResetOrchestrationState();
        ContextUsed = 0;
        ContextSize = 0;
        ContextCostAmount = null;
        ContextCostCurrency = null;
        IsPinnedToBottom = true;
        RefreshQueueState();
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(HasTimeline));
        OnPropertyChanged(nameof(ShowJumpToBottom));
        OnPropertyChanged(nameof(HasContextUsage));
        OnPropertyChanged(nameof(ContextUsageText));
        await ConnectAsync();
    }

    public void ReusePrompt(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        InputText = text;
        FocusComposerRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void JumpToBottom()
    {
        IsPinnedToBottom = true;
        ScrollToBottomRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private async Task UseQuickPromptAsync(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return;
        InputText = prompt;
        if (State == AgentGuiState.Ready)
        {
            await SendPromptAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(IsTurnActive), AllowConcurrentExecutions = true)]
    public Task CancelTurnAsync() => RequestCancelActiveTurnAsync(isManualStop: true);

    private async Task RequestCancelActiveTurnAsync(bool isManualStop)
    {
        var session = _session;
        if (session is null || (!_isTurnActive && !IsTurnActive)) return;

        if (isManualStop)
        {
            _manualStopRequested = true;
            if (_urgentPrompt is { } pendingUrgent)
            {
                _urgentPrompt = null;
                QueuedPrompts.Insert(0, pendingUrgent);
                RefreshQueueState();
            }
        }

        var turnSettled = _activeTurnSettled;
        var turnCts = _turnCts;
        if (turnSettled is null || turnSettled.Task.IsCompleted) return;

        // Clicking Stop a second time on the same active turn forces an immediate local abort.
        if (ReferenceEquals(_cancellingTurn, turnSettled))
        {
            try { turnCts?.Cancel(); } catch (ObjectDisposedException) { }
            return;
        }

        _cancellingTurn = turnSettled;
        var settledTask = turnSettled.Task;
        SetUi(() => IsCancelling = true);

        try
        {
            try
            {
                await session.CancelAsync();
            }
            catch
            {
                // Best-effort cancellation notification.
            }

            if (!settledTask.IsCompleted)
            {
                using var graceCts = new CancellationTokenSource();
                var delayTask = Task.Delay(CancelGraceTimeout, graceCts.Token);
                var completed = await Task.WhenAny(settledTask, delayTask);
                if (ReferenceEquals(completed, settledTask))
                {
                    graceCts.Cancel();
                }
                else
                {
                    // Agent did not settle session/prompt within grace window -> force local RPC abort.
                    try { turnCts?.Cancel(); } catch (ObjectDisposedException) { }
                    try { await settledTask.WaitAsync(TimeSpan.FromMilliseconds(500)); } catch { }
                }
            }
        }
        finally
        {
            if (ReferenceEquals(_cancellingTurn, turnSettled))
            {
                _cancellingTurn = null;
                SetUi(() => IsCancelling = false);
            }
        }
    }

    [RelayCommand]
    public Task ReconnectAsync() => ConnectAsync();

    /// <summary>Opens a Terminal tab for the same agent and working directory (resuming session if supported).</summary>
    [RelayCommand]
    public void ContinueInTerminal()
    {
        var resumeSupported = _definition.Capabilities.Resume?.ById is { Count: > 0 }
            || _definition.Capabilities.Resume?.ContinueLatest is { Count: > 0 };
        var opts = new AgentLaunchOptions
        {
            Surface = AgentSurface.Terminal,
            Model = Composer.SelectedModelId,
            Effort = Composer.SelectedEffortId,
            Fast = Composer.ShowFastToggle ? Composer.FastMode : null,
            AccountId = _item.Launch?.AccountId,
            SessionId = _definition.Capabilities.Resume?.ById is { Count: > 0 } ? _item.AcpSessionId : null,
            Resume = resumeSupported && _definition.Capabilities.Resume?.ById is null,
        };
        _opener.OpenAgent(_definition.Id, opts, _item.WorkspaceId, _item.WorkingDirectory);
    }

    [RelayCommand]
    private async Task PickImageAsync()
    {
        if (!Composer.SupportsImageAttachments || _pickers is null) return;
        var path = await _pickers.PickImageAsync();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        await AttachFilePathsAsync([path]);
    }

    /// <summary>Attaches dropped or picked file/image paths, honoring the current agent's image capability.</summary>
    public async Task AttachFilePathsAsync(IEnumerable<string> paths)
    {
        foreach (var attachment in await ReadAttachmentsAsync(paths))
        {
            AddAttachment(attachment);
        }
    }

    /// <summary>
    /// Sends a prompt that comes from another device rather than this tab's composer (design/12 §6, G-A): it is
    /// queued like the composer's while a turn runs, and whatever the person here is typing stays where it is.
    /// Returns once the prompt is sent or queued, not when the turn ends; false when there is no session to send to.
    /// </summary>
    internal async Task<bool> SendExternalPromptAsync(string text, IEnumerable<string> attachmentPaths, IReadOnlyList<MentionToken>? mentions = null)
    {
        if (_disposed || _session is null) return false;
        var attached = await ReadAttachmentsAsync(attachmentPaths);
        if (mentions is { Count: > 0 })
        {
            // Inline @mentions from a device travel like the composer's: session / agent attachments ahead of the files.
            attached.InsertRange(0, MentionText.DistinctInOrder(MentionText.Scan(text, mentions)).Select(m => new AcpPromptAttachment
            {
                Kind = m.Kind,
                Path = m.TargetId,
                DisplayName = m.Text,
                MimeType = m.AgentId,
            }));
        }
        text = text.Trim();
        if (text.Length == 0 && attached.Count == 0) return true;
        if (_session is null) return false;
        if (text.Length > 0 && (_promptHistory.Count == 0 || _promptHistory[^1] != text))
        {
            _promptHistory.Add(text);
        }
        if (_isTurnActive || IsTurnActive)
        {
            EnqueuePrompt(text, attached);
            return true;
        }
        // The turn runs on; the timeline shows the prompt before this returns.
        _ = StartPumpAsync(CreateQueuedPrompt(text, attached));
        return true;
    }

    private async Task<List<AcpPromptAttachment>> ReadAttachmentsAsync(IEnumerable<string> paths)
    {
        var result = new List<AcpPromptAttachment>();
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            var ext = Path.GetExtension(path);
            if (ImageExtensions.Contains(ext) && Composer.SupportsImageAttachments)
            {
                try
                {
                    var bytes = await File.ReadAllBytesAsync(path);
                    var mime = ext.ToLowerInvariant() switch
                    {
                        ".jpg" or ".jpeg" => "image/jpeg",
                        ".gif" => "image/gif",
                        ".webp" => "image/webp",
                        ".bmp" => "image/bmp",
                        _ => "image/png",
                    };
                    result.Add(new AcpPromptAttachment
                    {
                        Kind = "image",
                        Path = path,
                        DisplayName = Path.GetFileName(path),
                        MimeType = mime,
                        Base64Data = Convert.ToBase64String(bytes),
                    });
                }
                catch
                {
                    // Ignore unreadable image.
                }
            }
            else
            {
                var rel = path.StartsWith(WorkingDirectory, StringComparison.OrdinalIgnoreCase)
                    ? Path.GetRelativePath(WorkingDirectory, path).Replace('\\', '/')
                    : Path.GetFileName(path);
                result.Add(new AcpPromptAttachment
                {
                    Kind = "file",
                    Path = path,
                    DisplayName = rel,
                });
            }
        }
        return result;
    }

    /// <summary>Attaches an image directly from clipboard bytes (only when current agent supports images).</summary>
    public bool AttachImageBytes(byte[] pngBytes, string displayName = "clipboard.png", string mimeType = "image/png")
    {
        if (!Composer.SupportsImageAttachments || pngBytes.Length == 0) return false;
        AddAttachment(new AcpPromptAttachment
        {
            Kind = "image",
            Path = "",
            DisplayName = displayName,
            MimeType = mimeType,
            Base64Data = Convert.ToBase64String(pngBytes),
        });
        return true;
    }

    /// <summary>Opens a file referenced by a tool call inside an Octopus code tab.</summary>
    public void OpenToolFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var full = Path.IsPathRooted(path) ? path : Path.Combine(WorkingDirectory, path);
        if (File.Exists(full))
        {
            _opener.OpenFile(full);
        }
    }

    /// <summary>Opens the Style 2 full-script viewer modal for a large/multiline Bash command.</summary>
    public void OpenToolScript(ToolCallItemViewModel tool)
    {
        if (string.IsNullOrWhiteSpace(tool.CommandText)) return;
        ScriptPreview.Open(tool.DisplayTitle, tool.ScriptLanguage, tool.CommandText!);
    }

    public void Receive(FocusSubagentRequested message)
    {
        if (!string.Equals(message.ItemId, _item.Id, StringComparison.Ordinal)) return;
        UiContext.Run(() =>
        {
            if (_subagentsById.TryGetValue(message.SubagentId, out var run))
            {
                OpenSubagentInRightPanel(run);
            }
        });
    }

    [RelayCommand]
    private void ToggleActivityTray() => IsActivityTrayExpanded = !IsActivityTrayExpanded;

    [RelayCommand]
    private void ToggleWorkflowTasksExpanded() => IsWorkflowTasksExpanded = !IsWorkflowTasksExpanded;

    [RelayCommand]
    private void OpenSingleShellLogs()
    {
        var shell = SingleBackgroundShell;
        if (shell is null) return;
        IsActivityTrayExpanded = true;
        SelectedBackgroundShell = shell;
        shell.IsLogsExpanded = true;
        OnPropertyChanged(nameof(HasExpandedBackgroundShellLogs));
    }

    [RelayCommand]
    private void SelectSubagent(SubagentRunViewModel? sub)
    {
        if (sub is null) return;
        OpenSubagentInRightPanel(sub);
    }

    private void OpenSubagentInRightPanel(SubagentRunViewModel sub)
    {
        SelectedSubagent = sub;
        WeakReferenceMessenger.Default.Send(new OpenSubagentPanelRequested(
            _item.Id,
            sub.Id,
            WorkingDirectory,
            sub,
            Subagents));
    }

    [RelayCommand]
    private void SelectBackgroundShell(BackgroundBashViewModel? shell)
    {
        if (shell is null) return;
        var nextExpanded = !shell.IsLogsExpanded;
        foreach (var other in BackgroundShells)
        {
            if (!ReferenceEquals(other, shell)) other.IsLogsExpanded = false;
        }
        shell.IsLogsExpanded = nextExpanded;
        SelectedBackgroundShell = shell;
        if (nextExpanded)
        {
            IsActivityTrayExpanded = true;
        }
        OnPropertyChanged(nameof(HasExpandedBackgroundShellLogs));
    }

    public void CollapseExpandedShellLogs()
    {
        foreach (var sh in BackgroundShells)
        {
            sh.IsLogsExpanded = false;
        }
        OnPropertyChanged(nameof(HasExpandedBackgroundShellLogs));
    }

    /// <summary>Opens the file's live Git working-tree diff inside Octopus's native DiffTabView.</summary>
    public void OpenToolDiff(string path)
    {
        var commands = _workspaceCommands?.Invoke();
        if (commands is null || string.IsNullOrWhiteSpace(path)) return;
        var full = Path.IsPathRooted(path) ? path : Path.Combine(WorkingDirectory, path);
        var rel = Path.GetRelativePath(WorkingDirectory, full).Replace('\\', '/');
        commands.OpenDiff(new DiffTabRequest(WorkingDirectory, rel, full, Staged: false));
    }

    /// <summary>Navigates prompt history up (older) when the composer is single-line.</summary>
    public bool NavigateHistoryUp()
    {
        if (_promptHistory.Count == 0 || InputText.Contains('\n')) return false;
        if (_historyCursor < 0)
        {
            _historyDraft = InputText;
            _historyCursor = _promptHistory.Count - 1;
        }
        else if (_historyCursor > 0)
        {
            _historyCursor--;
        }
        else
        {
            return true;
        }
        SetInputFromHistory(_promptHistory[_historyCursor]);
        return true;
    }

    /// <summary>Navigates prompt history down (newer, back to draft).</summary>
    public bool NavigateHistoryDown()
    {
        if (_historyCursor < 0 || InputText.Contains('\n')) return false;
        if (_historyCursor < _promptHistory.Count - 1)
        {
            _historyCursor++;
            SetInputFromHistory(_promptHistory[_historyCursor]);
        }
        else
        {
            _historyCursor = -1;
            SetInputFromHistory(_historyDraft);
        }
        return true;
    }

    private void SetInputFromHistory(string value)
    {
        _applyingHistory = true;
        try { InputText = value; }
        finally { _applyingHistory = false; }
    }

    /// <summary>Moves highlight inside an open <c>/</c> or <c>@</c> picker popup.</summary>
    public bool MovePickerSelection(int delta)
    {
        if (IsCommandPopupOpen && FilteredCommands.Count > 0)
        {
            SelectedPickerIndex = (SelectedPickerIndex + delta + FilteredCommands.Count) % FilteredCommands.Count;
            SyncPickerHighlights();
            return true;
        }
        if (IsFilePopupOpen && FilteredFiles.Count > 0)
        {
            SelectedPickerIndex = (SelectedPickerIndex + delta + FilteredFiles.Count) % FilteredFiles.Count;
            SyncPickerHighlights();
            return true;
        }
        return false;
    }

    /// <summary>Commits the currently highlighted suggestion in the open <c>/</c> or <c>@</c> picker.</summary>
    public bool CommitSelectedPickerItem()
    {
        if (IsCommandPopupOpen && FilteredCommands.Count > 0)
        {
            var idx = Math.Clamp(SelectedPickerIndex, 0, FilteredCommands.Count - 1);
            InsertSlashCommand(FilteredCommands[idx]);
            return true;
        }
        if (IsFilePopupOpen && FilteredFiles.Count > 0)
        {
            var idx = Math.Clamp(SelectedPickerIndex, 0, FilteredFiles.Count - 1);
            InsertFileMention(FilteredFiles[idx]);
            return true;
        }
        return false;
    }

    private void SyncPickerHighlights()
    {
        for (var i = 0; i < FilteredCommands.Count; i++) FilteredCommands[i].IsSelected = i == SelectedPickerIndex;
        for (var i = 0; i < FilteredFiles.Count; i++) FilteredFiles[i].IsSelected = i == SelectedPickerIndex;
    }

    [RelayCommand]
    private void ToggleCommandPopup()
    {
        if (!Composer.SupportsSlashCommands) return;
        IsFilePopupOpen = false;
        IsCommandPopupOpen = !IsCommandPopupOpen;
        if (IsCommandPopupOpen) FilterCommands("");
    }

    /// <summary>
    /// The composer's <c>@</c> button: types an <c>@</c> at the caret (which opens the picker through the normal query
    /// path, so the picker and the text never disagree), or closes an open picker.
    /// </summary>
    [RelayCommand]
    private void ToggleFilePopup()
    {
        IsCommandPopupOpen = false;
        if (IsFilePopupOpen)
        {
            IsFilePopupOpen = false;
            return;
        }
        var text = InputText;
        var caret = Math.Clamp(_caretForPickers, 0, text.Length);
        var lead = caret > 0 && !char.IsWhiteSpace(text[caret - 1]) ? " " : "";
        InputText = string.Concat(text.AsSpan(0, caret), lead + "@", text.AsSpan(caret));
        ComposerCaretIndex = caret + lead.Length + 1;
        FocusComposerRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ToggleDiagnostics() => ShowDiagnostics = !ShowDiagnostics;

    [RelayCommand]
    public void InsertSlashCommand(SlashCommandSuggestion? cmd)
    {
        if (cmd is null) return;
        InputText = $"/{cmd.Name} ";
        IsCommandPopupOpen = false;
    }

    [RelayCommand]
    public void InsertFileMention(FileMentionSuggestion? file)
    {
        if (file is null) return;
        var text = InputText;
        var caret = Math.Clamp(_caretForPickers, 0, text.Length);
        var range = MentionText.FindQuery(text, caret, _mentionSpans) is { } query ? (query.Start, query.End) : (caret, caret);

        if (file.IsSession || file.IsAgent)
        {
            // Sessions and agents become inline tokens: the sentence itself says who does what.
            var label = MentionText.BuildLabel(
                file.IsSession ? MentionAgentName(file) : file.RelativePath,
                file.IsSession ? file.RelativePath : null,
                file.Kind,
                file.TargetId,
                _draftMentions);
            if (!_draftMentions.Any(m => m.Kind == file.Kind && m.TargetId == file.TargetId))
            {
                _draftMentions.Add(new MentionToken(file.Kind, file.TargetId, label, file.AgentId));
                PublishMentionTokens();
            }
            var inserted = MentionText.InsertToken(text, range.Item1, range.Item2, label);
            IsFilePopupOpen = false;
            InputText = inserted.Text;
            ComposerCaretIndex = inserted.Caret;
            return;
        }

        AddAttachment(new AcpPromptAttachment
        {
            Kind = "file",
            Path = file.FullPath,
            DisplayName = file.RelativePath,
        });
        IsFilePopupOpen = false;
        InputText = string.Concat(text.AsSpan(0, range.Item1), text.AsSpan(range.Item2));
        ComposerCaretIndex = range.Item1;
    }

    /// <summary>Agent display name for a session suggestion (its title goes in the token's detail).</summary>
    internal string MentionAgentName(FileMentionSuggestion session)
    {
        if (_collab is not null && !string.IsNullOrEmpty(session.AgentId))
        {
            foreach (var agent in _collab.ListAvailableAgents())
            {
                if (string.Equals(agent.AgentId, session.AgentId, StringComparison.OrdinalIgnoreCase)) return agent.Name;
            }
        }
        return string.IsNullOrEmpty(session.AgentId) ? session.RelativePath : session.AgentId;
    }

    /// <summary>Session / agent mentions of the draft (in order of appearance) followed by the attached files and images.</summary>
    private List<AcpPromptAttachment> CurrentDraftAttachments()
    {
        var result = new List<AcpPromptAttachment>();
        foreach (var token in MentionText.DistinctInOrder(_mentionSpans))
        {
            result.Add(new AcpPromptAttachment
            {
                Kind = token.Kind,
                Path = token.TargetId,
                DisplayName = token.Text,
                MimeType = token.AgentId,
            });
        }
        result.AddRange(Attachments.Select(c => c.Attachment));
        return result;
    }

    private void RefreshMentionSpans()
    {
        var hadSession = HasSessionMention;
        _mentionSpans = MentionText.Scan(InputText, _draftMentions);
        if (hadSession != HasSessionMention) OnPropertyChanged(nameof(HasSessionMention));
        if (!HasSessionMention) IsDirectRelayMode = false;
    }

    private void PublishMentionTokens()
    {
        MentionTokens = _draftMentions.ToArray();
        OnPropertyChanged(nameof(MentionTokens));
        RefreshMentionSpans();
    }

    private void ClearDraftMentions()
    {
        if (_draftMentions.Count == 0) return;
        _draftMentions.Clear();
        PublishMentionTokens();
    }

    public void AddAttachment(AcpPromptAttachment attachment)
    {
        Attachments.Add(AttachmentChipViewModel.Removable(attachment, RemoveAttachment));
        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(HasSessionMention));
        NotifyComposerActionStateChanged();
    }

    private void RemoveAttachment(AttachmentChipViewModel chip)
    {
        Attachments.Remove(chip);
        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(HasSessionMention));
        if (!HasSessionMention) IsDirectRelayMode = false;
        NotifyComposerActionStateChanged();
    }

    private void UpdateInlinePickers(string text)
    {
        if (Composer.SupportsSlashCommands && text.StartsWith('/') && !text.Contains(' ') && !text.Contains('\n'))
        {
            IsFilePopupOpen = false;
            IsCommandPopupOpen = true;
            FilterCommands(text[1..]);
            return;
        }
        IsCommandPopupOpen = false;

        if (MentionText.FindQuery(text, _caretForPickers, _mentionSpans) is { } query)
        {
            // Caret moves inside the same query must not reset the highlighted suggestion.
            if (!IsFilePopupOpen || !string.Equals(query.Query, _lastFileFilterQuery, StringComparison.Ordinal))
            {
                FilterFiles(query.Query);
            }
            IsFilePopupOpen = true;
            return;
        }
        IsFilePopupOpen = false;
    }

    private void FilterCommands(string query)
    {
        FilteredCommands.Clear();
        foreach (var cmd in Composer.AllCommands)
        {
            if (string.IsNullOrEmpty(query)
                || cmd.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || cmd.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                FilteredCommands.Add(cmd);
                if (FilteredCommands.Count >= 16) break;
            }
        }
        SelectedPickerIndex = 0;
        SyncPickerHighlights();
    }

    /// <summary>
    /// Sessions of this project / workspace group (not this one) and installed agents an <c>@query</c> may mention;
    /// shared by the composer's picker and devices (<c>acp/mentionTargets</c>).
    /// </summary>
    internal List<FileMentionSuggestion> ListCollabMentionSuggestions(string query)
    {
        var result = new List<FileMentionSuggestion>();
        var q = query.Trim();
        if (_collab is { IsEnabled: true })
        {
            // 1. Active sessions in the current project / merged workspace group (excluding self).
            foreach (var peer in _collab.ListPeers(_item.Id))
            {
                if (peer.ItemId == _item.Id) continue;
                var matches = string.IsNullOrEmpty(q)
                    || peer.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || peer.AgentId.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || peer.AgentName.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrEmpty(peer.Branch) && peer.Branch.Contains(q, StringComparison.OrdinalIgnoreCase));
                if (!matches) continue;
                var sub = peer.ParentItemId == _item.Id
                    ? $"↳ {peer.Status}"
                    : string.IsNullOrEmpty(peer.Branch) ? $"{peer.Surface} · {peer.Status}" : $"{peer.Branch} · {peer.Status}";
                result.Add(new FileMentionSuggestion("session", peer.ItemId, peer.Title, peer.AgentId, sub, peer.Status));
                if (result.Count >= 6) break;
            }

            // 2. Installed coding agents eligible for delegation (e.g. @codex, @agy / @反重力, @claude).
            var agentCount = 0;
            foreach (var ag in _collab.ListAvailableAgents())
            {
                var matches = string.IsNullOrEmpty(q)
                    || ag.AgentId.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || ag.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || ag.Aliases.Any(a => a.Contains(q, StringComparison.OrdinalIgnoreCase));
                if (!matches) continue;
                result.Add(new FileMentionSuggestion("agent", ag.AgentId, ag.Name, ag.AgentId, ag.DefaultSurface));
                if (++agentCount >= 5) break;
            }
        }
        return result;
    }

    private void FilterFiles(string query)
    {
        _lastFileFilterQuery = query;
        EnsureWorkspaceFilesIndexed();
        FilteredFiles.Clear();

        var q = query.Trim();
        foreach (var suggestion in ListCollabMentionSuggestions(q))
        {
            FilteredFiles.Add(suggestion);
        }

        // 3. Workspace files.
        foreach (var rel in _workspaceFiles)
        {
            if (string.IsNullOrEmpty(q) || rel.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                FilteredFiles.Add(new FileMentionSuggestion(rel, Path.Combine(WorkingDirectory, rel)));
                if (FilteredFiles.Count >= 18) break;
            }
        }
        SelectedPickerIndex = 0;
        SyncPickerHighlights();
    }

    private void OnSessionEvent(AcpRuntimeEvent evt)
    {
        if (_disposed) return;

        // Buffer session/load history replay off the UI thread so reopening a long conversation
        // builds the timeline in a single batch instead of posting hundreds of UI mutations.
        if (evt.IsReplay)
        {
            if (!_acceptReplay) return;
            lock (_replayGate)
            {
                if (_acceptReplay) _replayBuffer.Add(evt);
            }
            return;
        }

        // Coalesce high-frequency streaming text deltas (~30fps) off the UI thread to avoid
        // per-token Dispatcher hops and repeated Markdown re-layouts.
        if (evt is AcpTextDeltaEvent textDelta)
        {
            EnqueueTextDelta(textDelta);
            return;
        }

        UiContext.Run(() =>
        {
            if (_disposed) return;
            // Flush any pending text before non-text events so chronological block ordering is exact.
            FlushPendingTextDeltas();
            switch (evt)
            {
                case AcpImageEvent img:
                    EnsureActiveTurn().AddGeneratedImage(img.Image, img.MessageId);
                    RequestAutoScrollIfPinned();
                    break;

                case AcpToolCallEvent tc:
                    HandleToolCallUpdate(tc.Update);
                    RequestAutoScrollIfPinned();
                    break;

                case AcpPlanEvent plan:
                    EnsureActiveTurn().SetPlan(plan.Entries, plan.Markdown);
                    if (plan.Entries.Count > 0)
                    {
                        SyncWorkflowTasksFromEntries(plan.Entries);
                    }
                    RequestAutoScrollIfPinned();
                    break;

                case AcpPlanRemovedEvent:
                    WorkflowTasks.Clear();
                    _activeTurn?.ClearPlan();
                    NotifyOrchestrationProperties();
                    break;

                case AcpCompactionEvent comp:
                    EnsureActiveTurn().SetCompactionNotice(comp.Update.Summary);
                    RequestAutoScrollIfPinned();
                    break;

                case AcpElicitationCompletedEvent done:
                    if (_urlElicitations.Remove(done.ElicitationId, out var activeUrlCard))
                    {
                        activeUrlCard.MarkCompletedFromServer();
                    }
                    break;

                case AcpUsageEvent usage:
                    ContextUsed = usage.Usage.Used;
                    ContextSize = usage.Usage.Size;
                    ContextCostAmount = usage.Usage.CostAmount;
                    ContextCostCurrency = usage.Usage.CostCurrency;
                    OnPropertyChanged(nameof(HasContextUsage));
                    OnPropertyChanged(nameof(ContextUsageText));
                    OnPropertyChanged(nameof(ContextUsagePercent));
                    break;

                case AcpCommandsUpdatedEvent:
                case AcpModesUpdatedEvent:
                case AcpModelsUpdatedEvent:
                case AcpConfigOptionsUpdatedEvent:
                    if (_session is not null)
                    {
                        // When launched with fullAccessArgs (e.g. agy acp --dangerously-skip-permissions), the server's
                        // reported mode may still say "default" even though full access is active -> preserve user's mode.
                        var preservePerm = Composer.SelectedPermissionId == PermissionModes.Full
                            && _definition.Acp?.FullAccessArgs is { Count: > 0 };
                        Composer.UpdateFromSession(
                            _session.Initialized,
                            _session.Modes,
                            _session.Models,
                            _session.ConfigOptions,
                            _session.AvailableCommands,
                            preserveConfiguredPermission: preservePerm);
                        _confirmedModelId = Composer.SelectedModelId;
                    }
                    break;

                case AcpTitleUpdatedEvent title:
                    if (!string.IsNullOrWhiteSpace(title.Title))
                    {
                        _projects.SetItemTitle(_item.Id, title.Title);
                    }
                    break;

                case AcpClosedEvent closed:
                    _activeTurn?.Complete();
                    _activeTurn = null;
                    State = AgentGuiState.Disconnected;
                    if (!string.IsNullOrWhiteSpace(closed.Reason.Message) && string.IsNullOrWhiteSpace(ErrorBanner))
                    {
                        ErrorBanner = closed.Reason.Message;
                        OnPropertyChanged(nameof(HasErrorBanner));
                    }
                    RefreshDiagnostics();
                    break;
            }
        });
    }

    private void EnqueueTextDelta(AcpTextDeltaEvent delta)
    {
        if (string.IsNullOrEmpty(delta.Text)) return;
        var shouldSchedule = false;
        lock (_streamGate)
        {
            if (_pendingDeltas.Count > 0)
            {
                var last = _pendingDeltas[^1];
                var compatibleId = string.IsNullOrEmpty(last.MessageId)
                    || string.IsNullOrEmpty(delta.MessageId)
                    || string.Equals(last.MessageId, delta.MessageId, StringComparison.Ordinal);
                if (last.Channel == delta.Channel && compatibleId)
                {
                    if (string.IsNullOrEmpty(last.MessageId) && !string.IsNullOrEmpty(delta.MessageId))
                    {
                        _pendingDeltas[^1] = (last.Channel, delta.MessageId, last.Buffer);
                    }
                    last.Buffer.Append(delta.Text);
                }
                else
                {
                    _pendingDeltas.Add((delta.Channel, delta.MessageId, new StringBuilder(delta.Text)));
                }
            }
            else
            {
                _pendingDeltas.Add((delta.Channel, delta.MessageId, new StringBuilder(delta.Text)));
            }

            if (!_deltaFlushScheduled)
            {
                _deltaFlushScheduled = true;
                shouldSchedule = true;
            }
        }

        if (shouldSchedule)
        {
            _ = Task.Delay(StreamFlushIntervalMs).ContinueWith(
                _ => UiContext.PostToUi(FlushPendingTextDeltas),
                TaskScheduler.Default);
        }
    }

    private void FlushPendingTextDeltas()
    {
        List<(string Channel, string? MessageId, string Text)>? batch = null;
        lock (_streamGate)
        {
            _deltaFlushScheduled = false;
            if (_pendingDeltas.Count == 0) return;
            batch = new List<(string, string?, string)>(_pendingDeltas.Count);
            for (var i = 0; i < _pendingDeltas.Count; i++)
            {
                var item = _pendingDeltas[i];
                batch.Add((item.Channel, item.MessageId, item.Buffer.ToString()));
            }
            _pendingDeltas.Clear();
        }

        if (_disposed || batch is null) return;

        var turn = EnsureActiveTurn();
        for (var i = 0; i < batch.Count; i++)
        {
            var (channel, messageId, text) = batch[i];
            if (channel == "reasoning") turn.AppendThought(text, messageId);
            else turn.AppendMessage(text, messageId);
        }
        RequestAutoScrollIfPinned();
    }

    private void ClearPendingTextDeltas()
    {
        lock (_streamGate)
        {
            _pendingDeltas.Clear();
            _deltaFlushScheduled = false;
        }
    }

    private void ClearReplayBuffer()
    {
        lock (_replayGate)
        {
            _replayBuffer.Clear();
        }
    }

    /// <summary>Materializes buffered <c>session/load</c> replay events into the timeline in one batch without live timers.</summary>
    private void FinishHistoryReplay()
    {
        List<AcpRuntimeEvent>? events = null;
        lock (_replayGate)
        {
            if (_acceptReplay && _replayBuffer.Count > 0)
            {
                events = [.. _replayBuffer];
            }
            _replayBuffer.Clear();
            _acceptReplay = false;
        }

        if (events is null || events.Count == 0) return;

        var built = new List<AgentTimelineItem>();
        UserTurnItem? currentUser = null;
        StringBuilder? userBuf = null;
        AssistantTurnItem? currentAssistant = null;

        void CommitUser()
        {
            if (currentUser is null) return;
            if (userBuf is not null)
            {
                currentUser = new UserTurnItem(userBuf.ToString(), null, ReusePrompt) { MessageId = currentUser.MessageId };
                userBuf = null;
            }
            built.Add(currentUser);
            currentUser = null;
        }

        AssistantTurnItem EnsureReplayAssistant()
        {
            CommitUser();
            if (currentAssistant is not null) return currentAssistant;
            currentAssistant = new AssistantTurnItem(
                AgentId,
                AgentName,
                OpenToolFile,
                _workspaceCommands is null ? null : OpenToolDiff,
                OpenToolScript,
                isReplay: true,
                workingDirectory: WorkingDirectory);
            built.Add(currentAssistant);
            return currentAssistant;
        }

        foreach (var evt in events)
        {
            switch (evt)
            {
                case AcpUserMessageEvent user:
                    currentAssistant?.Complete(showDuration: false);
                    currentAssistant = null;
                    if (currentUser is not null
                        && (string.IsNullOrEmpty(user.MessageId) || user.MessageId == currentUser.MessageId))
                    {
                        userBuf ??= new StringBuilder(currentUser.Text);
                        userBuf.Append(user.Text);
                    }
                    else
                    {
                        CommitUser();
                        currentUser = new UserTurnItem(user.Text, null, ReusePrompt) { MessageId = user.MessageId };
                    }
                    break;

                case AcpUserImageEvent userImg:
                {
                    currentAssistant?.Complete(showDuration: false);
                    currentAssistant = null;
                    currentUser ??= new UserTurnItem("", null, ReusePrompt) { MessageId = userImg.MessageId };
                    currentUser.Attachments.Add(new AttachmentChipViewModel(new AcpPromptAttachment
                    {
                        Kind = "image",
                        Path = userImg.Image.FilePath ?? userImg.Image.Uri ?? "",
                        DisplayName = userImg.Image.AltText ?? "image",
                        MimeType = userImg.Image.MimeType,
                    }));
                    break;
                }

                case AcpTextDeltaEvent delta:
                {
                    var turn = EnsureReplayAssistant();
                    if (delta.Channel == "reasoning") turn.AppendThought(delta.Text, delta.MessageId);
                    else turn.AppendMessage(delta.Text, delta.MessageId);
                    break;
                }

                case AcpImageEvent img:
                    EnsureReplayAssistant().AddGeneratedImage(img.Image, img.MessageId);
                    break;

                case AcpToolCallEvent tc:
                {
                    var update = tc.Update;
                    // Sub-agent internals and background-shell/task bookkeeping only make sense live.
                    if (!string.IsNullOrWhiteSpace(update.ParentToolCallId)) break;
                    if (update.WorkflowAction is "task_create" or "task_update" or "background_output" or "background_stop" or "subagent_stop" or "subagent_stop_all") break;
                    if (update.WorkflowAction == "todo_write" && update.WorkflowTasks.Count > 0)
                    {
                        EnsureReplayAssistant().SetPlan(update.WorkflowTasks, null);
                        break;
                    }
                    if (update.WorkflowAction == "exit_plan" && !string.IsNullOrWhiteSpace(update.PlanMarkdown))
                    {
                        EnsureReplayAssistant().SetPlan([], update.PlanMarkdown);
                    }
                    EnsureReplayAssistant().UpsertToolCall(update);
                    break;
                }

                case AcpPlanEvent plan:
                    EnsureReplayAssistant().SetPlan(plan.Entries, plan.Markdown);
                    break;

                case AcpPlanRemovedEvent:
                    currentAssistant?.ClearPlan();
                    break;

                case AcpCompactionEvent comp:
                    EnsureReplayAssistant().SetCompactionNotice(comp.Update.Summary);
                    break;
            }
        }

        CommitUser();
        currentAssistant?.Complete(showDuration: false);

        foreach (var item in built)
        {
            Timeline.Add(item);
        }

        if (_promptHistory.Count == 0)
        {
            foreach (var user in built.OfType<UserTurnItem>())
            {
                if (!user.IsCollabMessage && !string.IsNullOrWhiteSpace(user.PromptText)) _promptHistory.Add(user.PromptText.Trim());
            }
        }
        // The tab already carries a title from the original conversation.
        _titledFromPrompt = true;
        IsPinnedToBottom = true;
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(HasTimeline));
        OnPropertyChanged(nameof(ShowJumpToBottom));
        ScrollToBottomRequested?.Invoke(this, true);
    }

    private void RequestAutoScrollIfPinned()
    {
        if (IsPinnedToBottom)
        {
            ScrollToBottomRequested?.Invoke(this, false);
        }
    }

    private AssistantTurnItem EnsureActiveTurn()
    {
        if (_activeTurn is not null) return _activeTurn;
        var turn = new AssistantTurnItem(AgentId, AgentName, OpenToolFile, _workspaceCommands is null ? null : OpenToolDiff, OpenToolScript, workingDirectory: WorkingDirectory);
        _activeTurn = turn;
        EnsureLiveTickTimer();
        Timeline.Add(turn);
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(HasTimeline));
        return turn;
    }

    private void HandleToolCallUpdate(AcpToolCallUpdate update)
    {
        var now = DateTimeOffset.Now;

        // Hydrate client-hosted ACP terminal snapshots for tool calls referencing type="terminal" blocks.
        if (update.TerminalIds.Count > 0 && _session is not null)
        {
            foreach (var termId in update.TerminalIds)
            {
                if (_session.TryGetTerminalSnapshot(termId) is { } snap)
                {
                    update.Command ??= snap.Command;
                    if (string.IsNullOrEmpty(update.Stdout) && !string.IsNullOrEmpty(snap.Output))
                    {
                        update.Stdout = snap.Output;
                    }
                    if (!update.ExitCode.HasValue && snap.ExitStatus?.ExitCode is { } code)
                    {
                        update.ExitCode = code;
                    }
                }
            }
        }

        // 1. SubAgent launch / update (Claude Code Agent / Task tool, Antigravity invoke_subagent)
        if (update.IsSubagentLaunch)
        {
            var id = string.IsNullOrWhiteSpace(update.ToolCallId) ? $"sub-{Subagents.Count + 1}" : update.ToolCallId;
            if (!_subagentsById.TryGetValue(id, out var sub))
            {
                sub = new SubagentRunViewModel(
                    id,
                    update.SubagentType ?? "general-purpose",
                    update.Description ?? update.Title,
                    now,
                    onSelect: OpenSubagentInRightPanel,
                    onStop: StopSubagent);
                _subagentsById[id] = sub;
                Subagents.Add(sub);
                if (CurrentPhase is WorkflowPhase.Idle or WorkflowPhase.Research)
                {
                    CurrentPhase = WorkflowPhase.Executing;
                }
            }
            sub.ApplyLaunchUpdate(update, now);
            if (sub.RunsAsynchronously)
            {
                EnsureAgyTranscriptMonitor();
                if (string.IsNullOrEmpty(sub.ConversationId) && _agyUnmatchedSpawns.Count > 0)
                {
                    BindAgySpawn(sub, _agyUnmatchedSpawns.Dequeue(), now);
                }
                if (update.Status == "completed" && sub.IsRunning)
                {
                    PollAgySubagents();
                }
            }
            SyncSidebarSubagents();
            NotifyOrchestrationProperties();
            EnsureLiveTickTimer();
            return;
        }

        // 1b. SubAgent management actions (Antigravity manage_subagents kill / kill_all)
        if (update.WorkflowAction == "subagent_stop_all")
        {
            foreach (var running in Subagents.Where(s => s.IsRunning))
            {
                running.MarkInterrupted(now);
            }
            SyncSidebarSubagents();
            NotifyOrchestrationProperties();
            EnsureLiveTickTimer();
            return;
        }
        if (update.WorkflowAction == "subagent_stop")
        {
            foreach (var targetId in update.TargetSubagentIds)
            {
                if (_agySubagentsByConvId.TryGetValue(targetId, out var byConv) && byConv.IsRunning)
                {
                    byConv.MarkInterrupted(now);
                }
                else if (_subagentsById.TryGetValue(targetId, out var byId) && byId.IsRunning)
                {
                    byId.MarkInterrupted(now);
                }
            }
            SyncSidebarSubagents();
            NotifyOrchestrationProperties();
            EnsureLiveTickTimer();
            return;
        }

        // 2. Child tool call executed inside a spawned SubAgent (parentToolUseId)
        if (!string.IsNullOrWhiteSpace(update.ParentToolCallId)
            && _subagentsById.TryGetValue(update.ParentToolCallId!, out var parentSub))
        {
            parentSub.UpsertStep(update);
            SyncSidebarSubagents();
            NotifyOrchestrationProperties();
            return;
        }

        // 3. Claude Code Workflow tools (TodoWrite / TaskCreate / TaskUpdate / EnterPlanMode / ExitPlanMode)
        if (update.WorkflowAction == "todo_write" && update.WorkflowTasks.Count > 0)
        {
            SyncWorkflowTasksFromEntries(update.WorkflowTasks);
            EnsureActiveTurn().SetPlan(update.WorkflowTasks, null);
            return;
        }
        if (update.WorkflowAction == "task_create" && update.WorkflowTasks.Count > 0)
        {
            UpsertWorkflowTasks(update.WorkflowTasks, replaceAll: false);
            return;
        }
        if (update.WorkflowAction == "task_update" && update.WorkflowTasks.Count > 0)
        {
            UpsertWorkflowTasks(update.WorkflowTasks, replaceAll: false);
            return;
        }
        if (update.WorkflowAction == "enter_plan")
        {
            CurrentPhase = WorkflowPhase.PlanGate;
            NotifyOrchestrationProperties();
        }
        else if (update.WorkflowAction == "exit_plan")
        {
            if (!string.IsNullOrWhiteSpace(update.PlanMarkdown))
            {
                EnsureActiveTurn().SetPlan([], update.PlanMarkdown);
            }
            CurrentPhase = update.Status == "completed" ? WorkflowPhase.Executing : WorkflowPhase.PlanGate;
            NotifyOrchestrationProperties();
        }

        // 4. Background Bash follow-up output / stop tools (TaskOutput / BashOutput / TaskStop / KillShell)
        if (update.WorkflowAction == "background_output")
        {
            var shell = FindBackgroundShell(update.BackgroundTaskId);
            if (shell is not null)
            {
                var outText = update.Stdout ?? (update.TextOutputs.Count > 0 ? string.Join("\n", update.TextOutputs) : null);
                shell.AppendOutput(outText, update.Stderr, markUnread: true);
                if (update.ExitCode.HasValue)
                {
                    shell.MarkExited(update.ExitCode, update.Status == "failed", now);
                }
                NotifyOrchestrationProperties();
            }
            return;
        }
        if (update.WorkflowAction == "background_stop")
        {
            var shell = FindBackgroundShell(update.BackgroundTaskId);
            if (shell is not null)
            {
                shell.MarkStopped(now);
                NotifyOrchestrationProperties();
            }
            return;
        }

        // 5. Background Bash launch (Bash with run_in_background: true)
        if (update.IsBackground && (update.Kind == "execute" || !string.IsNullOrWhiteSpace(update.Command)))
        {
            UpsertBackgroundShell(update, now);
        }

        // 6. Regular timeline tool call (with Style 2 Large Bash handling)
        EnsureActiveTurn().UpsertToolCall(update);
    }

    private void UpsertBackgroundShell(AcpToolCallUpdate update, DateTimeOffset now)
    {
        var id = string.IsNullOrWhiteSpace(update.ToolCallId) ? $"bg-{BackgroundShells.Count + 1}" : update.ToolCallId;
        if (!_shellsById.TryGetValue(id, out var shell))
        {
            shell = new BackgroundBashViewModel(
                id,
                update.BackgroundTaskId,
                update.Command ?? update.Title ?? "bash",
                update.Description,
                now,
                onSelect: SelectBackgroundShell,
                onStop: StopBackgroundShell,
                onOpenTerminal: OpenBackgroundShellInTerminal);
            _shellsById[id] = shell;
            BackgroundShells.Add(shell);
            SelectedBackgroundShell ??= shell;
        }
        if (!string.IsNullOrWhiteSpace(update.BackgroundTaskId))
        {
            shell.TaskId = update.BackgroundTaskId;
            _shellsByTaskId[update.BackgroundTaskId!] = shell;
        }
        var outText = update.Stdout ?? (update.TextOutputs.Count > 0 ? string.Join("\n", update.TextOutputs) : null);
        shell.AppendOutput(outText, update.Stderr, markUnread: true);
        if (update.Status == "failed" || (update.ExitCode.HasValue && update.ExitCode.Value != 0))
        {
            shell.MarkExited(update.ExitCode, failed: true, now);
        }
        else if (update.ExitCode == 0 && !string.IsNullOrEmpty(update.Stdout)
            && !update.Stdout.Contains("running in background", StringComparison.OrdinalIgnoreCase))
        {
            shell.MarkExited(0, failed: false, now);
        }
        NotifyOrchestrationProperties();
        EnsureLiveTickTimer();
    }

    private BackgroundBashViewModel? FindBackgroundShell(string? taskId)
    {
        if (!string.IsNullOrWhiteSpace(taskId))
        {
            if (_shellsByTaskId.TryGetValue(taskId!, out var byTask)) return byTask;
            if (_shellsById.TryGetValue(taskId!, out var byId)) return byId;
        }
        return BackgroundShells.LastOrDefault(s => s.IsRunning) ?? BackgroundShells.LastOrDefault();
    }

    private void SyncWorkflowTasksFromEntries(IReadOnlyList<AcpPlanEntry> entries) => UpsertWorkflowTasks(entries, replaceAll: true);

    private void UpsertWorkflowTasks(IReadOnlyList<AcpPlanEntry> entries, bool replaceAll)
    {
        if (replaceAll)
        {
            WorkflowTasks.Clear();
            var idx = 1;
            foreach (var entry in entries)
            {
                var id = string.IsNullOrWhiteSpace(entry.Id) ? idx.ToString() : entry.Id!;
                var vm = new WorkflowTaskViewModel(id, entry.Content, FocusSubagentById);
                vm.Apply(entry, MatchSubagentIdByOwner(entry.Owner));
                WorkflowTasks.Add(vm);
                idx++;
            }
        }
        else
        {
            foreach (var entry in entries)
            {
                var id = string.IsNullOrWhiteSpace(entry.Id) ? (WorkflowTasks.Count + 1).ToString() : entry.Id!;
                var existing = WorkflowTasks.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    existing = new WorkflowTaskViewModel(id, entry.Content, FocusSubagentById);
                    WorkflowTasks.Add(existing);
                }
                existing.Apply(entry, MatchSubagentIdByOwner(entry.Owner));
            }
        }

        if (WorkflowTasks.Count > 0 && CurrentPhase != WorkflowPhase.PlanGate)
        {
            CurrentPhase = WorkflowTasks.All(t => t.IsCompleted)
                ? WorkflowPhase.Completed
                : WorkflowPhase.Executing;
        }
        NotifyOrchestrationProperties();
    }

    private string? MatchSubagentIdByOwner(string? owner)
    {
        if (string.IsNullOrWhiteSpace(owner)) return null;
        return Subagents.LastOrDefault(s => string.Equals(s.AgentType, owner, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    private void FocusSubagentById(string subagentId)
    {
        if (_subagentsById.TryGetValue(subagentId, out var sub))
        {
            OpenSubagentInRightPanel(sub);
        }
    }

    private void StopSubagent(SubagentRunViewModel sub)
    {
        sub.MarkInterrupted(DateTimeOffset.Now);
        SyncSidebarSubagents();
        NotifyOrchestrationProperties();
    }

    private void StopBackgroundShell(BackgroundBashViewModel shell)
    {
        shell.MarkStopped(DateTimeOffset.Now);
        NotifyOrchestrationProperties();
        EnsureLiveTickTimer();
    }

    private void OpenBackgroundShellInTerminal(BackgroundBashViewModel shell)
    {
        _opener.OpenTerminal(_item.WorkspaceId, WorkingDirectory);
    }

    private void SyncSidebarSubagents()
    {
        if (_subagents is null) return;
        var snapshot = Subagents.Select(s => s.ToExternalRun()).ToList();
        _subagents.ReportExternal(_item.Id, snapshot);
    }

    private void PruneFinishedForegroundSubagents()
    {
        var removed = false;
        for (var i = Subagents.Count - 1; i >= 0; i--)
        {
            if (!Subagents[i].IsRunning && !Subagents[i].IsBackground)
            {
                _subagentsById.Remove(Subagents[i].Id);
                if (ReferenceEquals(SelectedSubagent, Subagents[i])) SelectedSubagent = null;
                Subagents.RemoveAt(i);
                removed = true;
            }
        }
        if (removed)
        {
            SyncSidebarSubagents();
            NotifyOrchestrationProperties();
        }
    }

    private void ResetOrchestrationState()
    {
        StopLiveTickTimer();
        WorkflowTasks.Clear();
        Subagents.Clear();
        _subagentsById.Clear();
        _agySubagentsByConvId.Clear();
        _agyUnmatchedSpawns.Clear();
        _agyBufferedMessages.Clear();
        _agyParentTranscriptPath = null;
        _agyParentTranscriptOffset = 0;
        BackgroundShells.Clear();
        _shellsById.Clear();
        _shellsByTaskId.Clear();
        SelectedSubagent = null;
        SelectedBackgroundShell = null;
        IsActivityTrayExpanded = false;
        IsWorkflowTasksExpanded = true;
        CurrentPhase = WorkflowPhase.Idle;
        ScriptPreview.Close();
        _subagents?.ReportExternal(_item.Id, []);
        NotifyOrchestrationProperties();
    }

    private void NotifyOrchestrationProperties()
    {
        OnPropertyChanged(nameof(HasWorkflowTasks));
        OnPropertyChanged(nameof(HasSubagents));
        OnPropertyChanged(nameof(HasBackgroundShells));
        OnPropertyChanged(nameof(HasActivityTray));
        OnPropertyChanged(nameof(IsSingleBackgroundShell));
        OnPropertyChanged(nameof(IsMultiActivityTray));
        OnPropertyChanged(nameof(SingleBackgroundShell));
        OnPropertyChanged(nameof(ShowCollapsedSingleShellActions));
        OnPropertyChanged(nameof(ShowCollapsedSingleShellStop));
        OnPropertyChanged(nameof(HasRunningSubagents));
        OnPropertyChanged(nameof(HasRunningBackgroundShells));
        OnPropertyChanged(nameof(HasRunningWorkflowTasks));
        OnPropertyChanged(nameof(HasFailedWorkflowTasks));
        OnPropertyChanged(nameof(ShowSpinnerInActivityHeader));
        OnPropertyChanged(nameof(ShowRunningBashDotOnly));
        OnPropertyChanged(nameof(HasFailedBackgroundShell));
        OnPropertyChanged(nameof(HasRunningActivity));
        OnPropertyChanged(nameof(HasFailedActivity));
        OnPropertyChanged(nameof(HasCompletedActivity));
        OnPropertyChanged(nameof(HasExpandedBackgroundShellLogs));
        OnPropertyChanged(nameof(IsOnlyWorkflowTasks));
        OnPropertyChanged(nameof(ShowWorkflowTasksSectionHeader));
        OnPropertyChanged(nameof(IsWorkflowTasksListVisible));
        OnPropertyChanged(nameof(WorkflowProgressPercent));
        OnPropertyChanged(nameof(IsAllWorkflowTasksCompleted));
        OnPropertyChanged(nameof(WorkflowProgressRatioText));
        OnPropertyChanged(nameof(ActiveWorkflowTaskTitle));
        OnPropertyChanged(nameof(ShowCollapsedActiveTaskPreview));
        OnPropertyChanged(nameof(ShowMultiHeaderSummary));
        OnPropertyChanged(nameof(ActivityTrayTitle));
        OnPropertyChanged(nameof(ActivityTraySummary));
        OnPropertyChanged(nameof(WorkflowTasksSummaryText));
    }

    private void EnsureLiveTickTimer()
    {
        if (Application.Current is null || !Dispatcher.UIThread.CheckAccess()) return;
        var need = HasRunningSubagents || HasRunningBackgroundShells || (_activeTurn is { IsStreaming: true });
        if (need && _liveTickTimer is null)
        {
            _liveTickTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _liveTickTimer.Tick += OnLiveTick;
            _liveTickTimer.Start();
        }
        else if (!need && _liveTickTimer is not null)
        {
            StopLiveTickTimer();
        }
    }

    private void StopLiveTickTimer()
    {
        if (_liveTickTimer is null) return;
        _liveTickTimer.Stop();
        _liveTickTimer.Tick -= OnLiveTick;
        _liveTickTimer = null;
    }

    private void OnLiveTick(object? sender, EventArgs e)
    {
        var hasActiveTurn = _activeTurn is { IsStreaming: true };
        if (_disposed || (!HasRunningSubagents && !HasRunningBackgroundShells && !hasActiveTurn))
        {
            StopLiveTickTimer();
            return;
        }
        if (hasActiveTurn)
        {
            _activeTurn!.TickElapsed();
        }
        var now = DateTimeOffset.Now;
        foreach (var s in Subagents)
        {
            if (s.IsRunning) s.Tick(now);
        }
        foreach (var sh in BackgroundShells)
        {
            if (sh.IsRunning) sh.Tick(now);
        }
        if (Subagents.Any(s => s.IsRunning && s.RunsAsynchronously))
        {
            PollAgySubagents();
        }
    }

    private void InitAgyTranscriptMonitor(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        var isAgy = string.Equals(_definition.Acp?.Dialect, "antigravity", StringComparison.OrdinalIgnoreCase)
            || string.Equals(_definition.Id, "agy", StringComparison.OrdinalIgnoreCase);
        if (!isAgy) return;

        var path = AgySubagentTranscript.TranscriptPathFor(sessionId, AgyAppDataDirOverride);
        if (string.Equals(_agyParentTranscriptPath, path, StringComparison.Ordinal)) return;
        _agyParentTranscriptPath = path;
        _agyParentTranscriptOffset = AgySubagentTranscript.CurrentLength(path);
    }

    private void EnsureAgyTranscriptMonitor()
    {
        if (!string.IsNullOrEmpty(_agyParentTranscriptPath)) return;
        var sid = _session?.SessionId ?? _item.AcpSessionId;
        if (string.IsNullOrWhiteSpace(sid)) return;
        _agyParentTranscriptPath = AgySubagentTranscript.TranscriptPathFor(sid, AgyAppDataDirOverride);
        _agyParentTranscriptOffset = 0;
    }

    private void InterruptRunningAsyncSubagents()
    {
        var now = DateTimeOffset.Now;
        var changed = false;
        foreach (var s in Subagents)
        {
            if (s.IsRunning && s.RunsAsynchronously)
            {
                s.MarkInterrupted(now);
                changed = true;
            }
        }
        if (changed)
        {
            SyncSidebarSubagents();
            NotifyOrchestrationProperties();
            EnsureLiveTickTimer();
        }
    }

    private void PollAgySubagents(bool finalizeMissingTranscript = false)
    {
        var task = PollAgySubagentsCoreAsync(finalizeMissingTranscript);
        if (!task.IsCompleted)
        {
            _agyPollTask = task;
        }
    }

    /// <summary>Runs one deterministic Antigravity subagent transcript poll cycle. For tests.</summary>
    internal async Task PollAgySubagentsNowAsync(bool finalizeMissingTranscript = false)
    {
        while (_agyPolling || _agyPollTask is { IsCompleted: false })
        {
            if (_agyPollTask is { IsCompleted: false } inflight)
            {
                await inflight;
            }
            else
            {
                await Task.Yield();
            }
        }
        var task = PollAgySubagentsCoreAsync(finalizeMissingTranscript);
        _agyPollTask = task;
        await task;
    }

    private sealed record AgyChildPollWork(SubagentRunViewModel Subagent, string Path, long Offset);
    private sealed record AgyChildPollResult(SubagentRunViewModel Subagent, AgyChildTranscriptDelta Delta);

    private async Task PollAgySubagentsCoreAsync(bool finalizeMissingTranscript)
    {
        if (_disposed) return;
        if (_agyPolling)
        {
            _agyPollAgain = true;
            if (finalizeMissingTranscript) _agyFinalizeOnRepoll = true;
            return;
        }
        EnsureAgyTranscriptMonitor();
        var parentPath = _agyParentTranscriptPath;
        var parentOffset = _agyParentTranscriptOffset;
        var appDataDir = AgyAppDataDirOverride;

        var runningAsync = Subagents.Where(s => s.IsRunning && s.RunsAsynchronously).ToList();
        if (runningAsync.Count == 0) return;

        _agyPolling = true;
        try
        {
            // Step 1: Read parent transcript delta off the UI thread.
            AgyParentTranscriptDelta? parentDelta = null;
            var parentExists = false;
            if (!string.IsNullOrEmpty(parentPath))
            {
                (parentExists, parentDelta) = await Task.Run(() =>
                {
                    var exists = File.Exists(parentPath);
                    var delta = exists
                        ? AgySubagentTranscript.ReadParentDelta(parentPath, parentOffset, appDataDir)
                        : null;
                    return (exists, delta);
                }).ConfigureAwait(false);
            }

            // Step 2: Bind newly spawned subagents on the UI thread (before reading children so fast subagents still get their steps read).
            List<AgyChildPollWork> childWork = [];
            List<AgySubagentMessage> messages = [];
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed) return;
                var now = DateTimeOffset.Now;
                if (parentDelta is not null)
                {
                    _agyParentTranscriptOffset = parentDelta.ToOffset;
                    foreach (var spawn in parentDelta.Spawns)
                    {
                        if (_agySubagentsByConvId.ContainsKey(spawn.ConversationId)) continue;
                        var unbound = Subagents.FirstOrDefault(s => s.RunsAsynchronously && string.IsNullOrEmpty(s.ConversationId));
                        if (unbound is not null)
                        {
                            BindAgySpawn(unbound, spawn, now);
                        }
                        else
                        {
                            _agyUnmatchedSpawns.Enqueue(spawn);
                        }
                    }
                    messages.AddRange(parentDelta.Messages);
                }

                foreach (var sub in Subagents)
                {
                    if (sub.IsRunning && sub.RunsAsynchronously && !string.IsNullOrEmpty(sub.TranscriptPath))
                    {
                        childWork.Add(new AgyChildPollWork(sub, sub.TranscriptPath!, sub.TranscriptOffset));
                    }
                }
            });

            // Step 3: Read each bound child's transcript delta off the UI thread.
            List<AgyChildPollResult> childResults = [];
            if (childWork.Count > 0)
            {
                childResults = await Task.Run(() =>
                    childWork.Select(w => new AgyChildPollResult(w.Subagent, AgySubagentTranscript.ReadChildDelta(w.Path, w.Offset))).ToList()
                ).ConfigureAwait(false);
            }

            // Step 4: Apply child tool steps, then completion/error messages on the UI thread.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed) return;
                var now = DateTimeOffset.Now;
                var changed = false;

                foreach (var res in childResults)
                {
                    var sub = res.Subagent;
                    var delta = res.Delta;
                    if (delta.ToOffset != sub.TranscriptOffset)
                    {
                        sub.TranscriptOffset = delta.ToOffset;
                    }
                    foreach (var call in delta.ToolCalls)
                    {
                        sub.UpsertStep(new AcpToolCallUpdate
                        {
                            ToolCallId = $"agy-{sub.Steps.Count + 1}",
                            Name = call.Name,
                            Kind = call.Kind,
                            Title = call.Target ?? call.Name,
                            Description = call.Target,
                            Status = "in_progress",
                        });
                        changed = true;
                    }
                    if (delta.CompletedResults > 0)
                    {
                        sub.CompleteOldestRunningSteps(delta.CompletedResults, failed: false);
                        changed = true;
                    }
                    if (delta.FailedResults > 0)
                    {
                        sub.CompleteOldestRunningSteps(delta.FailedResults, failed: true);
                        changed = true;
                    }
                }

                foreach (var msg in messages)
                {
                    if (_agySubagentsByConvId.TryGetValue(msg.ConversationId, out var target))
                    {
                        ApplyAgyMessage(target, msg, now);
                        changed = true;
                    }
                    else
                    {
                        _agyBufferedMessages[msg.ConversationId] = msg;
                    }
                }

                if (finalizeMissingTranscript && !parentExists)
                {
                    foreach (var sub in Subagents)
                    {
                        if (sub.IsRunning && sub.RunsAsynchronously && string.IsNullOrEmpty(sub.TranscriptPath))
                        {
                            sub.MarkCompleted(null, now);
                            changed = true;
                        }
                    }
                }

                if (changed)
                {
                    SyncSidebarSubagents();
                    NotifyOrchestrationProperties();
                    EnsureLiveTickTimer();
                }
            });
        }
        finally
        {
            _agyPolling = false;
            if (_agyPollAgain && !_disposed)
            {
                var repollFinalize = _agyFinalizeOnRepoll;
                _agyPollAgain = false;
                _agyFinalizeOnRepoll = false;
                PollAgySubagents(repollFinalize);
            }
        }
    }

    private void BindAgySpawn(SubagentRunViewModel sub, AgySubagentSpawn spawn, DateTimeOffset now)
    {
        sub.ConversationId = spawn.ConversationId;
        sub.TranscriptPath = spawn.TranscriptPath;
        sub.TranscriptOffset = 0;
        _agySubagentsByConvId[spawn.ConversationId] = sub;
        if (_agyBufferedMessages.Remove(spawn.ConversationId, out var buffered))
        {
            ApplyAgyMessage(sub, buffered, now);
        }
    }

    private static void ApplyAgyMessage(SubagentRunViewModel sub, AgySubagentMessage msg, DateTimeOffset now)
    {
        if (msg.IsError)
        {
            sub.MarkFailed(msg.Content, now);
        }
        else
        {
            sub.MarkCompleted(msg.Content, now);
        }
    }

    private Task<string?> HandlePermissionRequestAsync(AcpPermissionRequest request, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (request.IsPlanApproval)
        {
            PlanApprovalCardItem? planCard = null;
            CancellationTokenRegistration planReg = default;
            planReg = ct.Register(() =>
            {
                tcs.TrySetResult(null);
                SetUi(() =>
                {
                    if (planCard is { IsResolved: false }) planCard.CancelCommand.Execute(null);
                });
            });
            SetUi(() =>
            {
                FlushPendingTextDeltas();
                var prev = State;
                State = AgentGuiState.Waiting;
                CurrentPhase = WorkflowPhase.PlanGate;
                planCard = new PlanApprovalCardItem(request, (optionId, feedback) =>
                {
                    planReg.Dispose();
                    if (State == AgentGuiState.Waiting) State = prev == AgentGuiState.Working ? AgentGuiState.Working : AgentGuiState.Ready;
                    if (!string.IsNullOrEmpty(optionId))
                    {
                        CurrentPhase = WorkflowPhase.Executing;
                    }
                    tcs.TrySetResult(optionId);
                    if (!string.IsNullOrWhiteSpace(feedback))
                    {
                        EnqueuePrompt(feedback!, []);
                    }
                });
                Timeline.Add(planCard);
                OnPropertyChanged(nameof(ShowEmptyState));
                OnPropertyChanged(nameof(HasTimeline));
                ScrollToBottomRequested?.Invoke(this, true);
            });
            return tcs.Task;
        }

        PermissionCardItem? card = null;
        var approvalId = Guid.NewGuid().ToString("N");
        var reg = ct.Register(() =>
        {
            _collab?.ClearChildApproval(_item.Id, approvalId);
            tcs.TrySetResult(null);
            if (card is not null)
            {
                SetUi(() => RemovePermissionCard(card));
            }
        });

        SetUi(() =>
        {
            FlushPendingTextDeltas();
            var prev = State;
            State = AgentGuiState.Waiting;
            void ResolvePermission(string? optionId)
            {
                reg.Dispose();
                _collab?.ClearChildApproval(_item.Id, approvalId);
                if (State == AgentGuiState.Waiting) State = prev == AgentGuiState.Working ? AgentGuiState.Working : AgentGuiState.Ready;
                tcs.TrySetResult(optionId);
                if (card is not null)
                {
                    var resolvedCard = card;
                    _ = DismissPermissionCardAsync(resolvedCard);
                }
            }

            card = new PermissionCardItem(request, ResolvePermission);
            Timeline.Add(card);
            OnPropertyChanged(nameof(ShowEmptyState));
            OnPropertyChanged(nameof(HasTimeline));
            ScrollToBottomRequested?.Invoke(this, true);

            // Bubble spawned child permission requests up to the parent session so the user can approve in-place.
            if (!string.IsNullOrEmpty(_item.ParentItemId) && _collab is not null)
            {
                var allowOption = request.Options.FirstOrDefault(o =>
                    string.Equals(o.Kind, "allow_once", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(o.Kind, "allow", StringComparison.OrdinalIgnoreCase))?.OptionId
                    ?? request.Options.FirstOrDefault()?.OptionId;
                var denyOption = request.Options.FirstOrDefault(o =>
                    string.Equals(o.Kind, "reject_once", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(o.Kind, "reject", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(o.Kind, "deny", StringComparison.OrdinalIgnoreCase))?.OptionId;
                var toolTitle = string.IsNullOrWhiteSpace(request.ToolCall.Title)
                    ? (request.ToolCall.Name ?? "Tool permission")
                    : request.ToolCall.Title!;
                var detail = request.ActionDetail ?? request.ToolCall.Command ?? toolTitle;
                _collab.ReportChildApproval(new CollabBubbledApproval(
                    approvalId,
                    _item.ParentItemId,
                    _item.Id,
                    AgentId,
                    _item.DisplayTitle,
                    toolTitle,
                    detail,
                    request.WarningMessage,
                    allow => SetUi(() => ResolvePermission(allow ? allowOption : denyOption))));
            }
        });
        return tcs.Task;
    }

    private async Task DismissPermissionCardAsync(PermissionCardItem card)
    {
        try
        {
            await Task.Delay(210);
        }
        catch
        {
            // Ignore delay failures.
        }
        if (_disposed) return;
        SetUi(() => RemovePermissionCard(card));
    }

    private void RemovePermissionCard(PermissionCardItem card)
    {
        if (Timeline.Remove(card))
        {
            OnPropertyChanged(nameof(ShowEmptyState));
            OnPropertyChanged(nameof(HasTimeline));
            OnPropertyChanged(nameof(ShowJumpToBottom));
        }
    }

    private Task<IReadOnlyDictionary<string, IReadOnlyList<string>>?> HandleQuestionRequestAsync(AcpQuestionRequest request, CancellationToken ct) =>
        HandleFormElicitationRequestAsync(request, ct)
            .ContinueWith(
                t => t.Result.Action == AcpElicitationAction.Accept ? t.Result.Answers : null,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

    private Task<AcpFormElicitationResponse> HandleFormElicitationRequestAsync(AcpQuestionRequest request, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<AcpFormElicitationResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        QuestionCardItem? card = null;
        CancellationTokenRegistration reg = default;
        reg = ct.Register(() =>
        {
            tcs.TrySetResult(new AcpFormElicitationResponse(AcpElicitationAction.Cancel));
            SetUi(() =>
            {
                if (card is { IsResolved: false }) card.CancelCommand.Execute(null);
            });
        });

        SetUi(() =>
        {
            FlushPendingTextDeltas();
            var prev = State;
            State = AgentGuiState.Waiting;
            card = new QuestionCardItem(request, response =>
            {
                reg.Dispose();
                if (State == AgentGuiState.Waiting) State = prev == AgentGuiState.Working ? AgentGuiState.Working : AgentGuiState.Ready;
                tcs.TrySetResult(response);
            });
            Timeline.Add(card);
            OnPropertyChanged(nameof(ShowEmptyState));
            OnPropertyChanged(nameof(HasTimeline));
            ScrollToBottomRequested?.Invoke(this, true);
        });
        return tcs.Task;
    }

    private Task<AcpElicitationAction> HandleUrlElicitationRequestAsync(AcpUrlElicitationRequest request, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<AcpElicitationAction>(TaskCreationOptions.RunContinuationsAsynchronously);
        UrlElicitationCardItem? card = null;
        CancellationTokenRegistration reg = default;
        reg = ct.Register(() =>
        {
            tcs.TrySetResult(AcpElicitationAction.Cancel);
            SetUi(() =>
            {
                if (card is not null)
                {
                    _urlElicitations.Remove(request.ElicitationId);
                    if (!card.IsResolved) card.CancelCommand.Execute(null);
                }
            });
        });

        SetUi(() =>
        {
            FlushPendingTextDeltas();
            var prev = State;
            State = AgentGuiState.Waiting;
            card = new UrlElicitationCardItem(
                request,
                action =>
                {
                    reg.Dispose();
                    _urlElicitations.Remove(request.ElicitationId);
                    if (State == AgentGuiState.Waiting) State = prev == AgentGuiState.Working ? AgentGuiState.Working : AgentGuiState.Ready;
                    tcs.TrySetResult(action);
                },
                url => _fileOpener?.OpenUrl(url));
            _urlElicitations[request.ElicitationId] = card;
            Timeline.Add(card);
            OnPropertyChanged(nameof(ShowEmptyState));
            OnPropertyChanged(nameof(HasTimeline));
            ScrollToBottomRequested?.Invoke(this, true);
        });
        return tcs.Task;
    }

    private void PopulateAuthMethods(IReadOnlyList<AcpAuthMethod>? methods)
    {
        AuthMethods.Clear();
        if (methods is { Count: > 0 })
        {
            foreach (var m in methods)
            {
                AuthMethods.Add(new AuthMethodActionViewModel(m, RunAuthMethodAsync));
            }
        }
        OnPropertyChanged(nameof(HasAuthMethods));
    }

    private async Task RunAuthMethodAsync(AcpAuthMethod method)
    {
        if (string.Equals(method.Type, "terminal", StringComparison.OrdinalIgnoreCase))
        {
            ContinueInTerminal();
            return;
        }
        if (_session is null)
        {
            await ConnectAsync();
            return;
        }
        try
        {
            await _session.AuthenticateAsync(method.Id);
            await ConnectAsync();
        }
        catch (Exception ex)
        {
            SetUi(() =>
            {
                ErrorBanner = ex.Message;
                OnPropertyChanged(nameof(HasErrorBanner));
            });
        }
    }

    [RelayCommand(CanExecute = nameof(CanLogout))]
    public async Task LogoutAsync()
    {
        if (_session is null || !CanLogout) return;
        try
        {
            await _session.LogoutAsync();
            SetUi(() =>
            {
                PopulateAuthMethods(_session.Initialized?.AuthMethods);
                IsAuthRequired = true;
                State = AgentGuiState.Disconnected;
                OnPropertyChanged(nameof(ShowEmptyState));
            });
        }
        catch (Exception ex)
        {
            SetUi(() =>
            {
                ErrorBanner = ex.Message;
                OnPropertyChanged(nameof(HasErrorBanner));
            });
        }
    }

    [RelayCommand(CanExecute = nameof(CanForkSession))]
    public async Task ForkSessionAsync()
    {
        if (_session is null || !CanForkSession) return;
        try
        {
            var forked = await _session.ForkSessionAsync(WorkingDirectory);
            if (string.IsNullOrWhiteSpace(forked.SessionId)) return;
            var opts = new AgentLaunchOptions
            {
                Surface = AgentSurface.Gui,
                Model = Composer.SelectedModelId,
                Effort = Composer.SelectedEffortId,
                Fast = Composer.ShowFastToggle ? Composer.FastMode : null,
                AccountId = _item.Launch?.AccountId,
                SessionId = forked.SessionId,
            };
            _opener.OpenAgent(_definition.Id, opts, _item.WorkspaceId, _item.WorkingDirectory);
        }
        catch (Exception ex)
        {
            SetUi(() =>
            {
                ErrorBanner = ex.Message;
                OnPropertyChanged(nameof(HasErrorBanner));
            });
        }
    }

    partial void OnIsSessionHistoryOpenChanged(bool value)
    {
        if (value)
        {
            _ = RefreshSessionHistoryAsync();
        }
    }

    [RelayCommand]
    public async Task RefreshSessionHistoryAsync()
    {
        if (_session is null || !CanListSessions || IsLoadingSessionHistory) return;
        SetUi(() =>
        {
            IsLoadingSessionHistory = true;
            OnPropertyChanged(nameof(ShowEmptyRemoteSessions));
        });
        try
        {
            var result = await _session.ListSessionsAsync(WorkingDirectory);
            var currentId = _session.SessionId;
            var canDelete = _session.Initialized?.AgentCapabilities.SessionCapabilities.SupportsDelete == true;
            SetUi(() =>
            {
                RemoteSessions.Clear();
                foreach (var info in result.Sessions)
                {
                    RemoteSessions.Add(new AcpSessionListEntryViewModel(
                        info,
                        isCurrent: string.Equals(info.SessionId, currentId, StringComparison.Ordinal),
                        canDelete: canDelete,
                        onLoad: LoadSessionByIdAsync,
                        onDelete: DeleteRemoteSessionAsync));
                }
                OnPropertyChanged(nameof(HasRemoteSessions));
                OnPropertyChanged(nameof(ShowEmptyRemoteSessions));
            });
        }
        catch (Exception ex)
        {
            SetUi(() =>
            {
                ErrorBanner = ex.Message;
                OnPropertyChanged(nameof(HasErrorBanner));
            });
        }
        finally
        {
            SetUi(() =>
            {
                IsLoadingSessionHistory = false;
                OnPropertyChanged(nameof(ShowEmptyRemoteSessions));
            });
        }
    }

    public async Task LoadSessionByIdAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        IsSessionHistoryOpen = false;
        if (IsTurnActive || _isTurnActive) await CancelTurnAsync();
        _projects.SetItemSessionId(_item.Id, sessionId, isAcp: true);
        _item.AcpSessionId = sessionId;
        DisposeAndClearTimeline();
        ResetOrchestrationState();
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(HasTimeline));
        await ConnectAsync();
    }

    private async Task DeleteRemoteSessionAsync(string sessionId)
    {
        if (_session is null || string.IsNullOrWhiteSpace(sessionId)) return;
        try
        {
            await _session.DeleteSessionAsync(sessionId);
            SetUi(() =>
            {
                var found = RemoteSessions.FirstOrDefault(s => string.Equals(s.SessionId, sessionId, StringComparison.Ordinal));
                if (found is not null) RemoteSessions.Remove(found);
                OnPropertyChanged(nameof(HasRemoteSessions));
                OnPropertyChanged(nameof(ShowEmptyRemoteSessions));
            });
        }
        catch (Exception ex)
        {
            SetUi(() =>
            {
                ErrorBanner = ex.Message;
                OnPropertyChanged(nameof(HasErrorBanner));
            });
        }
    }

    /// <summary>Resolves other existing local project directories in the same group when <c>MergeWorkspaces</c> is enabled.</summary>
    private IReadOnlyList<string>? ResolveAdditionalDirectories()
    {
        if (!ProjectLocation.IsOnThisComputer(_project) || _project.GroupId is not { } groupId)
        {
            return null;
        }
        var group = _projects.Groups.FirstOrDefault(g => g.Id == groupId);
        if (group is null || !group.MergeWorkspaces)
        {
            return null;
        }
        var comparer = OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);
        if (!string.IsNullOrWhiteSpace(WorkingDirectory))
        {
            seen.Add(Path.GetFullPath(WorkingDirectory));
        }
        var result = new List<string>();
        foreach (var member in _projects.Projects)
        {
            if (member.GroupId != groupId || !ProjectLocation.IsOnThisComputer(member)) continue;
            if (string.IsNullOrWhiteSpace(member.Path) || !Directory.Exists(member.Path)) continue;
            var full = Path.GetFullPath(member.Path);
            if (seen.Add(full))
            {
                result.Add(member.Path);
            }
        }
        return result.Count == 0 ? null : result;
    }

    private IReadOnlyList<string> BuildAllowedFileSystemRoots(IReadOnlyList<string>? additionalDirectories)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(WorkingDirectory)) roots.Add(WorkingDirectory);
        if (additionalDirectories is { Count: > 0 }) roots.AddRange(additionalDirectories);
        return roots;
    }

    private static string? FormatStopReasonNotice(string? stopReason) => stopReason switch
    {
        "max_tokens" => AppLangResources.Get(AppLangResourceKind.AgentGuiStopReasonMaxTokens),
        "max_turn_requests" => AppLangResources.Get(AppLangResourceKind.AgentGuiStopReasonMaxTurnRequests),
        "refusal" => AppLangResources.Get(AppLangResourceKind.AgentGuiStopReasonRefusal),
        _ => null,
    };

    private async Task ApplyModeAsync(string modeId)
    {
        if (_session is null) return;
        try { await _session.SetModeAsync(modeId); } catch { }
    }

    private async Task ApplyPermissionAsync(string unifiedMode)
    {
        if (_session is null || _definition.Acp?.PermissionStrategy != AcpPermissionStrategy.AgentMode) return;
        if (_definition.Acp.PermissionModes?.TryGetValue(unifiedMode, out var nativeMode) == true)
        {
            try { await _session.SetModeAsync(nativeMode); } catch { }
        }
    }

    private async Task ApplyModelAsync(string modelId) =>
        await SwitchModelAsync(modelId, configId: null);

    private async Task ApplyConfigSelectAsync(string configId, string value)
    {
        if (_session is null) return;
        if (string.Equals(configId, Composer.ModelConfigId, StringComparison.OrdinalIgnoreCase))
        {
            await SwitchModelAsync(value, configId);
            return;
        }
        try
        {
            await _session.SetConfigOptionAsync(configId, value);
            if (string.Equals(configId, Composer.EffortConfigId, StringComparison.OrdinalIgnoreCase))
            {
                _desiredEffortId = value;
                _item.Launch ??= new AgentLaunchOptions();
                _item.Launch.Effort = value;
            }
        }
        catch (Exception ex)
        {
            SetUi(() =>
            {
                ErrorBanner = ex.Message;
                OnPropertyChanged(nameof(HasErrorBanner));
                OnPropertyChanged(nameof(ShowEmptyState));
            });
        }
    }

    private async Task SwitchModelAsync(string modelId, string? configId)
    {
        if (_session is null || string.IsNullOrWhiteSpace(modelId)) return;
        var session = _session;
        var previousModel = _confirmedModelId;
        try
        {
            if (configId is not null)
            {
                await session.SetConfigOptionAsync(configId, modelId);
            }
            else
            {
                await session.SetModelAsync(modelId);
            }
            _confirmedModelId = modelId;
            _desiredModelId = modelId;
            _item.Launch ??= new AgentLaunchOptions();
            _item.Launch.Model = modelId;
            SetUi(() =>
            {
                if (!string.IsNullOrEmpty(ErrorBanner))
                {
                    ErrorBanner = null;
                    OnPropertyChanged(nameof(HasErrorBanner));
                    OnPropertyChanged(nameof(ShowEmptyState));
                }
            });
        }
        catch (AcpRpcException) when (session.Dialect.ReconnectOnCustomModelSwitch && !IsTurnActive && !_reconnectingForModel)
        {
            // Adapters like codex-acp reject session/set_config_option for third-party model ids outside their
            // built-in catalog, but honor custom models via launch config (CODEX_CONFIG) on start/resume.
            _reconnectingForModel = true;
            _desiredModelId = modelId;
            _item.Launch ??= new AgentLaunchOptions();
            _item.Launch.Model = modelId;
            try
            {
                await ConnectAsync();
            }
            finally
            {
                _reconnectingForModel = false;
            }
        }
        catch (Exception ex)
        {
            SetUi(() =>
            {
                if (!string.Equals(Composer.SelectedModelId, previousModel, StringComparison.Ordinal))
                {
                    Composer.SyncModelSelectionSilently(previousModel);
                }
                ErrorBanner = ex.Message;
                OnPropertyChanged(nameof(HasErrorBanner));
                OnPropertyChanged(nameof(ShowEmptyState));
            });
        }
    }

    private async Task ApplyConfigBoolAsync(string configId, bool value)
    {
        if (_session is null) return;
        try
        {
            await _session.SetBooleanConfigOptionAsync(configId, value);
            if (string.Equals(configId, Composer.FastConfigId, StringComparison.OrdinalIgnoreCase))
            {
                _desiredFast = value;
                _item.Launch ??= new AgentLaunchOptions();
                _item.Launch.Fast = value;
            }
        }
        catch { }
    }

    private async Task ApplyInitialLaunchPreferencesAsync(IAcpSession session, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_desiredModelId)
            && !string.Equals(Composer.SelectedModelId, _desiredModelId, StringComparison.OrdinalIgnoreCase)
            && (Composer.ShowModelSelector || Composer.ModelConfigId is not null || session.Models is not null))
        {
            try
            {
                if (Composer.ModelConfigId is { } modelCfgId)
                {
                    await session.SetConfigOptionAsync(modelCfgId, _desiredModelId, ct).ConfigureAwait(false);
                }
                else
                {
                    await session.SetModelAsync(_desiredModelId, ct).ConfigureAwait(false);
                }
                SetUi(() =>
                {
                    Composer.SyncModelSelectionSilently(_desiredModelId);
                    _confirmedModelId = _desiredModelId;
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Keep the session's active model if the server rejects the pre-selected model.
            }
        }

        if (!string.IsNullOrWhiteSpace(_desiredEffortId)
            && Composer.ShowEffortSelector
            && Composer.EffortConfigId is { } effortCfgId
            && !string.Equals(Composer.SelectedEffortId, _desiredEffortId, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await session.SetConfigOptionAsync(effortCfgId, _desiredEffortId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch { }
        }

        if (_desiredFast is { } wantFast
            && Composer.ShowFastToggle
            && Composer.FastConfigId is { } fastCfgId
            && Composer.FastMode != wantFast)
        {
            try
            {
                if (Composer.FastConfigIsBoolean)
                {
                    await session.SetBooleanConfigOptionAsync(fastCfgId, wantFast, ct).ConfigureAwait(false);
                }
                else
                {
                    await session.SetConfigOptionAsync(fastCfgId, wantFast ? "on" : "off", ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch { }
        }
    }

    private async Task DiscoverSupplementalModelsAsync(
        string executable,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken ct)
    {
        if (_modelDiscovery is null) return;
        try
        {
            var found = await _modelDiscovery.DiscoverAsync(
                _definition,
                executable,
                refresh: false,
                cancellationToken: ct,
                environmentOverrides: environment).ConfigureAwait(false);
            if (found.Count > 0 && !_disposed && !ct.IsCancellationRequested)
            {
                SetUi(() => Composer.MergeSupplementalModels(found));
            }
        }
        catch
        {
            // Supplemental third-party model discovery is best-effort.
        }
    }

    private static string? NormalizeLaunchOption(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void EnsureWorkspaceFilesIndexed()
    {
        var root = WorkingDirectory;
        if (string.IsNullOrWhiteSpace(root)) return;

        var now = DateTimeOffset.UtcNow;
        if (SharedWorkspaceFileCache.TryGetValue(root, out var cached)
            && now - cached.IndexedAt < WorkspaceFileCacheTtl)
        {
            _workspaceFiles = cached.Files;
            return;
        }

        if (_workspaceIndexing || !Directory.Exists(root)) return;
        _workspaceIndexing = true;
        _ = Task.Run(() =>
        {
            var found = new List<string>(256);
            try
            {
                var stack = new Stack<string>();
                stack.Push(root);
                while (stack.Count > 0 && found.Count < 400)
                {
                    var dir = stack.Pop();
                    foreach (var file in Directory.EnumerateFiles(dir))
                    {
                        found.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
                        if (found.Count >= 400) break;
                    }
                    foreach (var sub in Directory.EnumerateDirectories(dir))
                    {
                        var name = Path.GetFileName(sub);
                        if (name is ".git" or "node_modules" or "bin" or "obj" or ".vs" or ".idea" or "dist" or "build") continue;
                        stack.Push(sub);
                    }
                }
            }
            catch
            {
                // Best-effort workspace file indexing for @ mentions.
            }

            var snapshot = found.ToArray();
            if (SharedWorkspaceFileCache.Count >= MaxCachedWorkspaces)
            {
                // Evict oldest cached workspace entry to keep static cache bounded.
                string? oldestKey = null;
                var oldestTime = DateTimeOffset.MaxValue;
                foreach (var kv in SharedWorkspaceFileCache)
                {
                    if (kv.Value.IndexedAt < oldestTime)
                    {
                        oldestTime = kv.Value.IndexedAt;
                        oldestKey = kv.Key;
                    }
                }
                if (oldestKey is not null) SharedWorkspaceFileCache.TryRemove(oldestKey, out _);
            }
            SharedWorkspaceFileCache[root] = (DateTimeOffset.UtcNow, snapshot);

            UiContext.PostToUi(() =>
            {
                _workspaceIndexing = false;
                if (_disposed) return;
                _workspaceFiles = snapshot;
                if (IsFilePopupOpen)
                {
                    FilterFiles(_lastFileFilterQuery);
                }
            });
        });
    }

    private void RefreshDiagnostics()
    {
        DiagnosticsText = _session?.GetStderrSnapshot() ?? "";
        OnPropertyChanged(nameof(HasDiagnostics));
    }

    public Task<bool> HasRunningChildAsync() => Task.FromResult(State == AgentGuiState.Working || HasRunningSubagents || HasRunningBackgroundShells);

    public async Task CloseAsync()
    {
        StopLiveTickTimer();
        _activity?.ReportExit(_item.Id);
        _subagents?.ReportExit(_item.Id);
        await DisconnectInternalAsync();
    }

    public Task RestartAsync() => ConnectAsync();

    private async Task DisconnectInternalAsync()
    {
        ClearPendingTextDeltas();
        ClearReplayBuffer();
        _activeTurn?.Complete();
        _activeTurn = null;
        _urgentPrompt = null;
        _cancellingTurn = null;
        _manualStopRequested = false;
        _isTurnActive = false;
        SetUi(() => IsCancelling = false);
        _connectCts?.Cancel();
        _connectCts?.Dispose();
        _connectCts = null;
        _turnCts?.Cancel();
        _turnCts?.Dispose();
        _turnCts = null;

        var old = Interlocked.Exchange(ref _session, null);
        if (old is not null)
        {
            old.EventReceived -= OnSessionEvent;
            try { await old.DisposeAsync(); } catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_collab is not null)
        {
            _collab.Changed -= OnCollabChanged;
            _collab.UnregisterGuiSession(_item.Id);
        }
        WeakReferenceMessenger.Default.UnregisterAll(this);
        ScriptPreview.PropertyChanged -= OnScriptPreviewPropertyChanged;
        StopLiveTickTimer();
        ClearPendingTextDeltas();
        ClearReplayBuffer();
        _activeTurn?.Complete();
        _activeTurn = null;
        _connectCts?.Cancel();
        _turnCts?.Cancel();
        var old = Interlocked.Exchange(ref _session, null);
        if (old is not null)
        {
            old.EventReceived -= OnSessionEvent;
            _ = old.DisposeAsync().AsTask();
        }
        // Release collections & transient buffers promptly when a tab closes.
        UiContext.Run(() =>
        {
            DisposeAndClearTimeline();
            QueuedPrompts.Clear();
            Attachments.Clear();
            FilteredCommands.Clear();
            FilteredFiles.Clear();
            ResetOrchestrationState();
            DiagnosticsText = "";
        });
        _workspaceFiles = [];
        _promptHistory.Clear();
    }

    private void DisposeAndClearTimeline()
    {
        foreach (var item in Timeline)
        {
            if (item is AssistantTurnItem turn) turn.Dispose();
        }
        Timeline.Clear();
    }

    private static void SetUi(Action action) => UiContext.Run(action);

    private static string FormatTokens(long n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:0.#}M",
        >= 1_000 => $"{n / 1_000.0:0.#}k",
        _ => n.ToString(),
    };
}
