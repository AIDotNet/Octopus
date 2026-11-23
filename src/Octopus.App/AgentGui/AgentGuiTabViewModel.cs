using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Acp.Dialects;
using Octopus.Acp.Process;
using Octopus.Acp.Protocol;
using Octopus.Acp.Transport;
using Octopus.Agents;
using Octopus.Agents.Accounts;
using Octopus.App.Activity;
using Octopus.App.Common;
using Octopus.App.Files;
using Octopus.App.Localization;
using Octopus.App.Services;
using Octopus.App.Workspace;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.Storage;

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

/// <summary>A queued follow-up prompt waiting for the active turn to finish.</summary>
public sealed record QueuedPrompt(string Text, IReadOnlyList<AcpPromptAttachment> Attachments);

/// <summary>
/// ViewModel for a compact Style A ACP Agent GUI tab: manages the live <see cref="IAcpSession"/>,
/// document-stream timeline items, smart auto-scroll, prompt history & queuing, interactive permission
/// & question cards, and the dynamic agent-aware <see cref="AgentComposerCapabilities"/>.
/// </summary>
public sealed partial class AgentGuiTabViewModel : ObservableObject, IDisposable
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp",
    };

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

    private IAcpSession? _session;
    private AssistantTurnItem? _activeTurn;
    private CancellationTokenSource? _connectCts;
    private CancellationTokenSource? _turnCts;
    private readonly List<string> _workspaceFiles = [];
    private readonly List<string> _promptHistory = [];
    private int _historyCursor = -1;
    private string _historyDraft = "";
    private bool _applyingHistory;
    private bool _initialPromptSent;
    private bool _titledFromPrompt;
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
        Func<IWorkspaceCommands?>? workspaceCommands = null)
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

        var profile = settings.Current.Agents.ProfileOf(definition.Id);
        var effectivePerm = compiler.ResolvePermission(definition, profile, settings.Current.Agents.DefaultPermission)
            ?? PermissionModes.Default;
        var dialect = dialects.Resolve(definition.Acp?.Dialect);

        Composer = new AgentComposerCapabilities(
            definition,
            dialect,
            effectivePerm,
            item.Launch?.Model ?? profile.Model,
            item.Launch?.Effort ?? profile.Effort,
            item.Launch?.Fast ?? profile.Fast,
            onModeChanged: ApplyModeAsync,
            onPermissionChanged: ApplyPermissionAsync,
            onModelChanged: ApplyModelAsync,
            onConfigSelectChanged: ApplyConfigSelectAsync,
            onConfigBoolChanged: ApplyConfigBoolAsync);

        WorkingDirectory = !string.IsNullOrWhiteSpace(item.WorkingDirectory) ? item.WorkingDirectory! : project.Path;
        WorkingDirectoryName = Path.GetFileName(WorkingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(WorkingDirectoryName)) WorkingDirectoryName = WorkingDirectory;

        QuickPrompts =
        [
            AppLangResources.Get(AppLangResourceKind.AgentGuiQuickPromptOverview),
            AppLangResources.Get(AppLangResourceKind.AgentGuiQuickPromptDiff),
            AppLangResources.Get(AppLangResourceKind.AgentGuiQuickPromptTests),
        ];

        IndexWorkspaceFilesAsync();
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

    [ObservableProperty] public partial AgentGuiState State { get; private set; } = AgentGuiState.Connecting;
    [ObservableProperty] public partial string? StatusDetail { get; private set; }
    [ObservableProperty] public partial string InputText { get; set; } = "";
    [ObservableProperty] public partial string? ErrorBanner { get; private set; }
    [ObservableProperty] public partial bool IsAuthRequired { get; private set; }
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

    public bool IsConnecting => State == AgentGuiState.Connecting;
    public bool IsBusy => State is AgentGuiState.Connecting or AgentGuiState.Working;
    public bool IsWorking => State == AgentGuiState.Working;
    public bool CanSend => State is AgentGuiState.Ready or AgentGuiState.Waiting or AgentGuiState.Working
        && (!string.IsNullOrWhiteSpace(InputText) || Attachments.Count > 0);
    public bool ShowEmptyState => Timeline.Count == 0 && !HasErrorBanner && !IsAuthRequired;
    public bool HasTimeline => Timeline.Count > 0;
    public bool HasErrorBanner => !string.IsNullOrWhiteSpace(ErrorBanner);
    public bool HasAttachments => Attachments.Count > 0;
    public bool HasQueuedPrompts => QueuedPrompts.Count > 0;
    public string QueuedBadgeText => AppLangResources.Format(AppLangResourceKind.AgentGuiQueuedBadge, QueuedPrompts.Count);
    public bool HasContextUsage => ContextSize > 0;
    public bool HasDiagnostics => !string.IsNullOrWhiteSpace(DiagnosticsText);
    public bool ShowReconnect => State is AgentGuiState.Disconnected or AgentGuiState.Error || IsAuthRequired;
    public bool ShowJumpToBottom => !IsPinnedToBottom && Timeline.Count > 0;

    public string EmptyTitle => AppLangResources.Format(AppLangResourceKind.AgentGuiEmptyTitle, AgentName);
    public string ComposerPlaceholder => AppLangResources.Format(AppLangResourceKind.AgentGuiComposerPlaceholder, AgentName);
    public string ContextUsageText
    {
        get
        {
            if (!HasContextUsage) return "";
            var baseText = AppLangResources.Format(AppLangResourceKind.AgentGuiContextUsage, FormatTokens(ContextUsed), FormatTokens(ContextSize));
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

    public event EventHandler<bool>? ScrollToBottomRequested;

    partial void OnIsPinnedToBottomChanged(bool value) => OnPropertyChanged(nameof(ShowJumpToBottom));

    partial void OnStateChanged(AgentGuiState value)
    {
        OnPropertyChanged(nameof(IsConnecting));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsWorking));
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ShowReconnect));
        SendPromptCommand.NotifyCanExecuteChanged();
        CancelTurnCommand.NotifyCanExecuteChanged();

        var mapped = value switch
        {
            AgentGuiState.Working => ItemActivity.Working,
            AgentGuiState.Waiting => ItemActivity.Waiting,
            AgentGuiState.Error => ItemActivity.Error,
            _ => ItemActivity.Idle,
        };
        _activity?.ReportStatus(_item.Id, mapped);
    }

    partial void OnInputTextChanged(string value)
    {
        if (!_applyingHistory)
        {
            _historyCursor = -1;
        }
        OnPropertyChanged(nameof(CanSend));
        SendPromptCommand.NotifyCanExecuteChanged();
        UpdateInlinePickers(value);
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
                progress => SetUi(() => StatusDetail = progress)).ConfigureAwait(false);

            SetUi(() => StatusDetail = null);

            var callbacks = new AcpSessionCallbacks
            {
                EnableFormElicitation = agentSettings.AcpEnableFormElicitation,
                GetActivePermissionMode = () => Composer.SelectedPermissionId ?? PermissionModes.Default,
                OnPermissionRequest = HandlePermissionRequestAsync,
                OnQuestionRequest = HandleQuestionRequestAsync,
            };

            var session = _sessionFactory.Create(plan, callbacks);
            session.EventReceived += OnSessionEvent;
            _session = session;

            SetUi(() => SourceLabel = session.SourceLabel);

            var started = await session.StartAsync(
                WorkingDirectory,
                additionalDirectories: null,
                resumeSessionId: _item.AcpSessionId ?? _item.Launch?.SessionId,
                cancellationToken: ct).ConfigureAwait(false);

            _item.AcpSessionId = started.SessionId;

            var desiredPermission = Composer.SelectedPermissionId ?? PermissionModes.Default;

            SetUi(() =>
            {
                Composer.UpdateFromSession(
                    session.Initialized,
                    session.Modes ?? started.Modes,
                    session.Models ?? started.Models,
                    session.ConfigOptions.Count > 0 ? session.ConfigOptions : started.ConfigOptions,
                    session.AvailableCommands,
                    preserveConfiguredPermission: true);
                State = AgentGuiState.Ready;
                RefreshDiagnostics();
            });

            // Apply configured AgentMode permission (unless fullAccessArgs already granted full access at process launch).
            var launchedWithFullArgs = desiredPermission == PermissionModes.Full
                && _definition.Acp?.FullAccessArgs is { Count: > 0 };
            if (_definition.Acp?.PermissionStrategy == AcpPermissionStrategy.AgentMode
                && !launchedWithFullArgs
                && !string.IsNullOrEmpty(desiredPermission))
            {
                await ApplyPermissionAsync(desiredPermission).ConfigureAwait(false);
            }

            if (!_initialPromptSent && !string.IsNullOrWhiteSpace(_item.Launch?.Prompt))
            {
                _initialPromptSent = true;
                var firstPrompt = _item.Launch!.Prompt!.Trim();
                SetUi(() => InputText = firstPrompt);
                await SendPromptAsync();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelled due to close/reconnect.
        }
        catch (AcpAuthRequiredException)
        {
            SetUi(() =>
            {
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

    [RelayCommand(CanExecute = nameof(CanSend))]
    public async Task SendPromptAsync()
    {
        var session = _session;
        var text = InputText.Trim();
        if (session is null || (text.Length == 0 && Attachments.Count == 0)) return;

        var attached = Attachments.Select(c => c.Attachment).ToList();
        InputText = "";
        Attachments.Clear();
        IsCommandPopupOpen = false;
        IsFilePopupOpen = false;
        OnPropertyChanged(nameof(HasAttachments));

        if (text.Length > 0 && (_promptHistory.Count == 0 || _promptHistory[^1] != text))
        {
            _promptHistory.Add(text);
        }
        _historyCursor = -1;

        // If the agent is currently busy, queue this follow-up and dispatch automatically after the active turn finishes.
        if (State == AgentGuiState.Working)
        {
            QueuedPrompts.Add(new QueuedPrompt(text, attached));
            OnPropertyChanged(nameof(HasQueuedPrompts));
            OnPropertyChanged(nameof(QueuedBadgeText));
            return;
        }

        await ExecutePromptAsync(session, text, attached);

        // Drain queued follow-up prompts sequentially if the session stays ready.
        while (!_disposed && State == AgentGuiState.Ready && QueuedPrompts.Count > 0 && _session is { } activeSession)
        {
            var next = QueuedPrompts[0];
            QueuedPrompts.RemoveAt(0);
            OnPropertyChanged(nameof(HasQueuedPrompts));
            OnPropertyChanged(nameof(QueuedBadgeText));
            await ExecutePromptAsync(activeSession, next.Text, next.Attachments);
        }
    }

    private async Task ExecutePromptAsync(IAcpSession session, string text, IReadOnlyList<AcpPromptAttachment> attached)
    {
        var userTurn = new UserTurnItem(text, attached, ReusePrompt);
        var assistantTurn = new AssistantTurnItem(AgentId, AgentName, OpenToolFile, _workspaceCommands is null ? null : OpenToolDiff);
        _activeTurn = assistantTurn;
        Timeline.Add(userTurn);
        Timeline.Add(assistantTurn);
        IsPinnedToBottom = true;
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(HasTimeline));
        OnPropertyChanged(nameof(ShowJumpToBottom));
        ScrollToBottomRequested?.Invoke(this, true);

        if (!_titledFromPrompt && text.Length > 0)
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
            await session.PromptAsync(text, attached, _turnCts.Token);
            SetUi(() =>
            {
                assistantTurn.Complete();
                if (State == AgentGuiState.Working) State = AgentGuiState.Ready;
                RefreshDiagnostics();
            });
        }
        catch (OperationCanceledException)
        {
            SetUi(() =>
            {
                assistantTurn.Complete();
                if (State != AgentGuiState.Disconnected) State = AgentGuiState.Ready;
            });
        }
        catch (AcpAuthRequiredException)
        {
            SetUi(() =>
            {
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
        }
    }

    [RelayCommand]
    private void ClearQueuedPrompts()
    {
        QueuedPrompts.Clear();
        OnPropertyChanged(nameof(HasQueuedPrompts));
        OnPropertyChanged(nameof(QueuedBadgeText));
    }

    [RelayCommand]
    private void ClearTimeline()
    {
        if (IsWorking) return;
        Timeline.Clear();
        IsPinnedToBottom = true;
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(HasTimeline));
        OnPropertyChanged(nameof(ShowJumpToBottom));
    }

    /// <summary>Starts a clean new ACP session in this tab without carrying the previous session id.</summary>
    [RelayCommand]
    public async Task NewSessionAsync()
    {
        if (IsWorking) await CancelTurnAsync();
        _item.AcpSessionId = null;
        if (_item.Launch is not null) _item.Launch.SessionId = null;
        _titledFromPrompt = false;
        QueuedPrompts.Clear();
        Timeline.Clear();
        ContextUsed = 0;
        ContextSize = 0;
        ContextCostAmount = null;
        ContextCostCurrency = null;
        IsPinnedToBottom = true;
        OnPropertyChanged(nameof(HasQueuedPrompts));
        OnPropertyChanged(nameof(QueuedBadgeText));
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

    [RelayCommand(CanExecute = nameof(IsWorking))]
    public async Task CancelTurnAsync()
    {
        if (_session is null) return;
        _turnCts?.Cancel();
        try
        {
            await _session.CancelAsync();
        }
        catch
        {
            // Best-effort cancellation.
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
                    AddAttachment(new AcpPromptAttachment
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
                AddAttachment(new AcpPromptAttachment
                {
                    Kind = "file",
                    Path = path,
                    DisplayName = rel,
                });
            }
        }
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

    [RelayCommand]
    private void ToggleFilePopup()
    {
        IsCommandPopupOpen = false;
        IsFilePopupOpen = !IsFilePopupOpen;
        if (IsFilePopupOpen) FilterFiles("");
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
        AddAttachment(new AcpPromptAttachment
        {
            Kind = "file",
            Path = file.FullPath,
            DisplayName = file.RelativePath,
        });
        var at = InputText.LastIndexOf('@');
        if (at >= 0)
        {
            InputText = InputText[..at].TrimEnd();
            if (InputText.Length > 0) InputText += " ";
        }
        IsFilePopupOpen = false;
    }

    public void AddAttachment(AcpPromptAttachment attachment)
    {
        Attachments.Add(AttachmentChipViewModel.Removable(attachment, RemoveAttachment));
        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(CanSend));
        SendPromptCommand.NotifyCanExecuteChanged();
    }

    private void RemoveAttachment(AttachmentChipViewModel chip)
    {
        Attachments.Remove(chip);
        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(CanSend));
        SendPromptCommand.NotifyCanExecuteChanged();
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

        var at = text.LastIndexOf('@');
        if (at >= 0 && (at == 0 || char.IsWhiteSpace(text[at - 1])))
        {
            var query = text[(at + 1)..];
            if (!query.Contains(' ') && !query.Contains('\n'))
            {
                IsFilePopupOpen = true;
                FilterFiles(query);
                return;
            }
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

    private void FilterFiles(string query)
    {
        FilteredFiles.Clear();
        foreach (var rel in _workspaceFiles)
        {
            if (string.IsNullOrEmpty(query) || rel.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                FilteredFiles.Add(new FileMentionSuggestion(rel, Path.Combine(WorkingDirectory, rel)));
                if (FilteredFiles.Count >= 16) break;
            }
        }
        SelectedPickerIndex = 0;
        SyncPickerHighlights();
    }

    private void OnSessionEvent(AcpRuntimeEvent evt) => UiContext.Run(() =>
    {
        if (_disposed) return;
        switch (evt)
        {
            case AcpTextDeltaEvent delta:
                var turn = EnsureActiveTurn();
                if (delta.Channel == "reasoning") turn.AppendThought(delta.Text);
                else turn.AppendMessage(delta.Text);
                RequestAutoScrollIfPinned();
                break;

            case AcpToolCallEvent tc:
                EnsureActiveTurn().UpsertToolCall(tc.Update);
                RequestAutoScrollIfPinned();
                break;

            case AcpPlanEvent plan:
                EnsureActiveTurn().SetPlan(plan.Entries, plan.Markdown);
                RequestAutoScrollIfPinned();
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
        var turn = new AssistantTurnItem(AgentId, AgentName, OpenToolFile, _workspaceCommands is null ? null : OpenToolDiff);
        _activeTurn = turn;
        Timeline.Add(turn);
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(HasTimeline));
        return turn;
    }

    private Task<string?> HandlePermissionRequestAsync(AcpPermissionRequest request, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        PermissionCardItem? card = null;
        var reg = ct.Register(() =>
        {
            tcs.TrySetResult(null);
            if (card is not null)
            {
                SetUi(() => RemovePermissionCard(card));
            }
        });

        SetUi(() =>
        {
            var prev = State;
            State = AgentGuiState.Waiting;
            card = new PermissionCardItem(request, optionId =>
            {
                reg.Dispose();
                if (State == AgentGuiState.Waiting) State = prev == AgentGuiState.Working ? AgentGuiState.Working : AgentGuiState.Ready;
                tcs.TrySetResult(optionId);
                // Let the card play its smooth collapse/fade exit before removing it from the timeline.
                var resolvedCard = card!;
                _ = DismissPermissionCardAsync(resolvedCard);
            });
            Timeline.Add(card);
            OnPropertyChanged(nameof(ShowEmptyState));
            OnPropertyChanged(nameof(HasTimeline));
            ScrollToBottomRequested?.Invoke(this, true);
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

    private Task<IReadOnlyDictionary<string, IReadOnlyList<string>>?> HandleQuestionRequestAsync(AcpQuestionRequest request, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<IReadOnlyDictionary<string, IReadOnlyList<string>>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = ct.Register(() => tcs.TrySetResult(null));

        SetUi(() =>
        {
            var prev = State;
            State = AgentGuiState.Waiting;
            var card = new QuestionCardItem(request, answers =>
            {
                if (State == AgentGuiState.Waiting) State = prev == AgentGuiState.Working ? AgentGuiState.Working : AgentGuiState.Ready;
                tcs.TrySetResult(answers);
            });
            Timeline.Add(card);
            OnPropertyChanged(nameof(ShowEmptyState));
            OnPropertyChanged(nameof(HasTimeline));
            ScrollToBottomRequested?.Invoke(this, true);
        });
        return tcs.Task;
    }

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

    private async Task ApplyModelAsync(string modelId)
    {
        if (_session is null) return;
        try { await _session.SetModelAsync(modelId); } catch { }
    }

    private async Task ApplyConfigSelectAsync(string configId, string value)
    {
        if (_session is null) return;
        try { await _session.SetConfigOptionAsync(configId, value); } catch { }
    }

    private async Task ApplyConfigBoolAsync(string configId, bool value)
    {
        if (_session is null) return;
        try { await _session.SetBooleanConfigOptionAsync(configId, value); } catch { }
    }

    private void IndexWorkspaceFilesAsync()
    {
        var root = WorkingDirectory;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
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
            UiContext.PostToUi(() =>
            {
                if (_disposed) return;
                _workspaceFiles.Clear();
                _workspaceFiles.AddRange(found);
            });
        });
    }

    private void RefreshDiagnostics()
    {
        DiagnosticsText = _session?.GetStderrSnapshot() ?? "";
        OnPropertyChanged(nameof(HasDiagnostics));
    }

    public Task<bool> HasRunningChildAsync() => Task.FromResult(State == AgentGuiState.Working);

    public async Task CloseAsync()
    {
        _activity?.ReportExit(_item.Id);
        await DisconnectInternalAsync();
    }

    public Task RestartAsync() => ConnectAsync();

    private async Task DisconnectInternalAsync()
    {
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
        _connectCts?.Cancel();
        _turnCts?.Cancel();
        var old = Interlocked.Exchange(ref _session, null);
        if (old is not null)
        {
            old.EventReceived -= OnSessionEvent;
            _ = old.DisposeAsync();
        }
    }

    private static void SetUi(Action action) => UiContext.Run(action);

    private static string FormatTokens(long n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:0.#}M",
        >= 1_000 => $"{n / 1_000.0:0.#}k",
        _ => n.ToString(),
    };
}
