using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Octopus.App.Composition;
using Octopus.App.Localization;
using Octopus.App.Workspace;
using Octopus.Platform;
using Octopus.Terminal.Avalonia;

namespace Octopus.App.Terminals;

public partial class TerminalTabView : UserControl, ITabContent
{
    public TerminalTabView()
    {
        InitializeComponent();
        Terminal.ExitedMessageFormat = AppLangResources.Get(AppLangResourceKind.TerminalExited);
        var search = new TerminalSearchBar();
        search.Attach(Terminal);
        Terminal.SearchBar = search;
        Terminal.PasteConfirmer = (_, preview) => PasteConfirmation.ShowAsync(this, preview);
        Terminal.LinkActivated += (_, url) => AppHost.Get<IFileOpener>().OpenUrl(url);
        Terminal.PathActivated += OnPathActivated;
        Terminal.LinkExited += OnLinkExited;
        Terminal.StartFailed += OnStartFailed;
        // Every run of the process (the first one and each restart) begins the connection's progress.
        Terminal.PropertyChanged += (_, e) =>
        {
            if (e.Property == TerminalControl.IsRunningProperty && Terminal.IsRunning) Model?.Connect?.Begin();
        };
        // The session's name and its spinner travel in the terminal title (OSC 0/2).
        Terminal.TitleChanged += (_, title) =>
        {
            if (Model is { } model)
            {
                AppHost.Get<Activity.IItemActivityService>().ReportTitle(model.Item.Id, title);
            }
        };
    }

    /// <summary>A file goes to a tab of the workspace, a folder to the system's file manager.</summary>
    private void OnPathActivated(object? sender, TerminalPathActivatedEventArgs e)
    {
        if (Directory.Exists(e.Path))
        {
            AppHost.Get<IFileOpener>().Reveal(e.Path);
        }
        else
        {
            AppHost.Get<Services.IItemOpener>().OpenFile(e.Path);
        }
    }

    private TerminalTabViewModel? _boundModel;
    private TerminalTabViewModel? Model => DataContext as TerminalTabViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _boundModel?.PropertyChanged -= OnModelPropertyChanged;
        _boundModel = null;
        ApplyFont();
        if (Model is { } model)
        {
            _boundModel = model;
            model.PropertyChanged += OnModelPropertyChanged;
        }
    }

    private void OnModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(TerminalTabViewModel.TerminalFontFamily))
        {
            ApplyFont();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // First time on screen: only now does the process start (a restored tab that is never shown never runs).
        EnsureStarted();
    }

    internal void EnsureStarted(bool startWhenHidden = false)
    {
        if (startWhenHidden) _startWhenHidden = true;
        if (Model is { Started: false } model)
        {
            model.Started = true;
            // The files of a terminal on another machine are not on this computer: its paths are not links.
            Terminal.PathLinksEnabled = model.Connect is null && !model.IsMirror;
            Terminal.IsMirror = model.IsMirror;
            Terminal.Start(
                leaseId =>
                {
                    var launch = model.TryCreateLaunch(leaseId: leaseId);
                    if (launch is not null)
                    {
                        _startedAt = Environment.TickCount64;
                    }
                    else
                    {
                        var err = model.Error is { Length: > 0 } message
                            ? $"Terminal failed to start: {message}"
                            : "Terminal failed to start.";
                        NotifyCollabAborted(model.Item.Id, err);
                    }
                    return launch;
                },
                model.Factory,
                model.Sessions,
                model.Item.Id,
                _startWhenHidden);
            return;
        }
        if (startWhenHidden && Model is { Started: true } existing && !Terminal.IsRunning && existing.Sessions?.Find(existing.Item.Id) is null)
        {
            Terminal.EnsureStartedWhenHidden();
        }
    }

    private void OnStartFailed(object? sender, Exception ex)
    {
        if (Model is { } failed)
        {
            AppHost.Get<Activity.IItemActivityService>().ReportExit(failed.Item.Id);
            AppHost.Get<Activity.ISubagentService>().ReportExit(failed.Item.Id);
            NotifyCollabAborted(failed.Item.Id, $"Terminal failed to start: {ex.Message}");
        }
    }

    /// <summary>How soon a failed "continue the last session" counts as "there was nothing to continue".</summary>
    public static readonly TimeSpan ResumeFailureWindow = TimeSpan.FromSeconds(15);

    private long _startedAt;
    private bool _startWhenHidden;

    /// <summary>
    /// A restored agent is started with its "continue" argument. Where no session exists (the tab was opened and
    /// closed without a conversation) the agent exits at once with an error: it is then started fresh, once, instead
    /// of leaving a dead tab whose restart would fail the same way.
    /// </summary>
    private void OnLinkExited(object? sender, TerminalLinkExitedEventArgs e)
    {
        var code = e.ExitCode;
        var reason = code is { } exitCode
            ? $"Terminal session exited unexpectedly (code {exitCode})."
            : "Terminal session exited unexpectedly.";
        if (!e.IsCurrentLink)
        {
            // A superseded link's delayed exit must only settle work accepted by that retired link,
            // never affecting a restarted link's live state, retry logic, or pending work.
            if (Model is { } staleModel && !string.IsNullOrWhiteSpace(e.LinkId))
            {
                NotifyCollabAborted(staleModel.Item.Id, reason, onlyOwnerLinkId: e.LinkId);
            }
            return;
        }
        Model?.Connect?.Ended(code);
        if (Model is { } exited)
        {
            AppHost.Get<Activity.IItemActivityService>().ReportExit(exited.Item.Id);
            AppHost.Get<Activity.ISubagentService>().ReportExit(exited.Item.Id);
        }
        if (Model is { Resumed: true } model
            && code is not (null or 0)
            && Environment.TickCount64 - _startedAt <= ResumeFailureWindow.TotalMilliseconds)
        {
            // Any collaboration work already accepted by the exited resumed process cannot be answered by the fresh retry.
            NotifyCollabAborted(model.Item.Id, reason);
            // Clear any stale saved session id so the next restart does not hit the same dead session again.
            if (!string.IsNullOrWhiteSpace(model.Item.Launch?.SessionId))
            {
                AppHost.Get<Services.IProjectService>().SetItemSessionId(model.Item.Id, null, isAcp: false);
            }
            Terminal.Start(
                leaseId =>
                {
                    var launch = model.TryCreateLaunch(allowResume: false, leaseId: leaseId);
                    if (launch is not null)
                    {
                        _startedAt = Environment.TickCount64;
                    }
                    else
                    {
                        var err = model.Error is { Length: > 0 } message
                            ? $"Terminal failed to start: {message}"
                            : "Terminal failed to start.";
                        NotifyCollabAborted(model.Item.Id, err);
                    }
                    return launch;
                },
                model.Factory,
                model.Sessions,
                model.Item.Id,
                _startWhenHidden);
            return;
        }
        if (Model is { } ended)
        {
            NotifyCollabAborted(ended.Item.Id, reason);
        }
    }

    private static void NotifyCollabAborted(string itemId, string reason, string? onlyOwnerLinkId = null)
    {
        var collab = AppHost.Services.GetService(typeof(Collab.ICollabCoordinator)) as Collab.ICollabCoordinator;
        if (collab is Collab.CollabCoordinator coordinator)
        {
            var ownerLinks = !string.IsNullOrWhiteSpace(onlyOwnerLinkId)
                ? new HashSet<string>(StringComparer.Ordinal) { onlyOwnerLinkId }
                : null;
            coordinator.NotifySessionAbortedAllPending(itemId, reason, failed: true, onlyOwnerLinkIds: ownerLinks);
        }
        else if (string.IsNullOrWhiteSpace(onlyOwnerLinkId))
        {
            collab?.NotifyTurnAborted(itemId, reason, failed: true);
        }
    }

    private void ApplyFont()
    {
        if (Model?.TerminalFontFamily is { } family)
        {
            Terminal.FontFamily = new FontFamily(family);
        }
        else
        {
            Terminal.ClearValue(Octopus.Terminal.Avalonia.TerminalControl.FontFamilyProperty);
        }
    }

    public Task<bool> HasRunningChildAsync() => Terminal.HasRunningChildAsync();

    public Task CloseAsync() => Terminal.CloseAsync();

    public Task RestartAsync() => Terminal.RestartAsync();

    public void FocusContent() => Terminal.FocusTerminal();
}
