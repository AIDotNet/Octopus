using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Octopus.Terminal.Avalonia;
using Octopus.App.Composition;
using Octopus.App.Localization;
using Octopus.Platform;
using Octopus.App.Workspace;

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
        Terminal.Exited += OnExited;
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

    private TerminalTabViewModel? Model => DataContext as TerminalTabViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        ApplyFont();
        if (Model is { } model)
        {
            model.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(TerminalTabViewModel.TerminalFontFamily))
                {
                    ApplyFont();
                }
            };
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // First time on screen: only now does the process start (a restored tab that is never shown never runs).
        if (Model is { Started: false } model)
        {
            model.Started = true;
            // The files of a terminal on an SSH host are not on this computer: its paths are not links.
            Terminal.PathLinksEnabled = model.Connect is null;
            if (model.TryCreateSpec() is { } spec)
            {
                _startedAt = Environment.TickCount64;
                Terminal.Start(spec, model.Factory);
            }
        }
    }

    /// <summary>How soon a failed "continue the last session" counts as "there was nothing to continue".</summary>
    public static readonly TimeSpan ResumeFailureWindow = TimeSpan.FromSeconds(15);

    private long _startedAt;

    /// <summary>
    /// A restored agent is started with its "continue" argument. Where no session exists (the tab was opened and
    /// closed without a conversation) the agent exits at once with an error: it is then started fresh, once, instead
    /// of leaving a dead tab whose restart would fail the same way.
    /// </summary>
    private void OnExited(object? sender, int? code)
    {
        Model?.Connect?.Ended(code);
        if (Model is { } exited)
        {
            AppHost.Get<Activity.IItemActivityService>().ReportExit(exited.Item.Id);
        }
        if (Model is not { Resumed: true } model || code is null or 0)
        {
            return;
        }
        if (Environment.TickCount64 - _startedAt > ResumeFailureWindow.TotalMilliseconds)
        {
            return;
        }
        if (model.TryCreateSpec(allowResume: false) is { } fresh)
        {
            Terminal.Start(fresh, model.Factory);
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
