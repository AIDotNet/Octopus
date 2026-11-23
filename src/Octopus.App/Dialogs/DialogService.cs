using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Octopus.App.Common;
using Octopus.App.Localization;
using Octopus.App.Remote;
using Octopus.App.Services;
using Octopus.Core.Projects;
using Octopus.FileSystem;
using Octopus.Ssh;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;

namespace Octopus.App.Dialogs;

/// <summary>Confirmations as Arc dialogs over the main window.</summary>
public sealed class DialogService(
    IWindowAccessor windows,
    IPickerService pickers,
    ISshHostCatalog sshHosts,
    IProjectIconStore? icons = null,
    ILocalPorts? ports = null,
    ISshHostTester? sshTester = null,
    RemoteFiles? remoteFiles = null,
    IProjectService? projects = null,
    PathComparer? pathComparer = null) : IDialogService
{
    public async Task<Octopus.Core.Projects.TunnelSpec?> AskTunnelAsync(Octopus.Core.Projects.TunnelSpec? initial, string host)
    {
        if (windows.Window is null)
        {
            return null;
        }
        var typed = initial;
        IReadOnlyList<TunnelIssue> issues = [];
        while (true)
        {
            var form = new TunnelFormView(typed, () => ports?.FindFree() ?? 0);
            form.ShowIssues(issues);
            var result = await Dialog.ShowAsync(
                windows.Window,
                AppLangResources.Get(initial is null ? AppLangResourceKind.TunnelFormTitleNew : AppLangResourceKind.TunnelFormTitleEdit),
                form,
                AppLangResources.Format(AppLangResourceKind.TunnelFormDescription, host),
                new DialogAction(AppLangResources.Get(AppLangResourceKind.Cancel), false),
                new DialogAction(AppLangResources.Get(initial is null ? AppLangResourceKind.TunnelFormCreate : AppLangResourceKind.Save), true, ButtonVariant.Primary));
            if (result is not true)
            {
                return null;
            }
            typed = form.Spec;
            issues = SshTunnelCommand.Validate(typed);
            if (issues.Count > 0)
            {
                // Not valid: the dialog comes back with what was typed and says under which field what is wrong.
                continue;
            }
            // A port that other devices can reach is a decision to be made on purpose.
            if (!SshTunnelCommand.IsLoopback(typed.BindAddress)
                && !await ConfirmAsync(
                    AppLangResources.Get(AppLangResourceKind.TunnelExposeTitle),
                    AppLangResources.Format(
                        typed.Kind == Octopus.Core.Projects.TunnelKind.Local ? AppLangResourceKind.TunnelExposeLocal : AppLangResourceKind.TunnelExposeRemote,
                        typed.BindAddress,
                        typed.ListenPort),
                    AppLangResources.Get(AppLangResourceKind.TunnelExposeConfirm),
                    isDanger: true))
            {
                continue;
            }
            return typed;
        }
    }

    public Task<bool> ConfirmAsync(string title, string description, string confirmText, bool isDanger = false)
    {
        if (windows.Window is null)
        {
            return Task.FromResult(false);
        }
        return Dialog.ConfirmAsync(
            windows.Window, title, description, confirmText,
            AppLangResources.Get(AppLangResourceKind.Cancel), isDanger);
    }

    public async Task<string?> AskNameAsync(string title, string label, string? initial, string confirmText)
    {
        if (windows.Window is null)
        {
            return null;
        }
        var form = new NameInputView(label, initial);
        var result = await Dialog.ShowAsync(
            windows.Window,
            title,
            form,
            "",
            new DialogAction(AppLangResources.Get(AppLangResourceKind.Cancel), false),
            new DialogAction(confirmText, true, ButtonVariant.Primary));
        return result is true ? form.Name : null;
    }

    public async Task<Octopus.Core.Agents.AgentDefinition?> AskAgentDefinitionAsync(Octopus.App.Settings.Agents.AgentEditorModel model)
    {
        if (windows.Window is null)
        {
            return null;
        }
        while (true)
        {
            var result = await Dialog.ShowAsync(
                windows.Window,
                model.IsNew
                    ? AppLangResources.Get(AppLangResourceKind.AgentEditorAddTitle)
                    : AppLangResources.Format(AppLangResourceKind.AgentEditorEditTitle, model.Name),
                new Octopus.App.Settings.Agents.AgentEditorView(model),
                AppLangResources.Get(AppLangResourceKind.AgentEditorDescription),
                new DialogAction(AppLangResources.Get(AppLangResourceKind.Cancel), false),
                new DialogAction(AppLangResources.Get(AppLangResourceKind.Save), true, ButtonVariant.Primary));
            if (result is not true)
            {
                return null;
            }
            if (model.Build() is { } definition)
            {
                return definition;
            }
            // Not valid: the dialog comes back with what was typed and the reason under it.
        }
    }

    public Task<SshHostDraft?> AskNewSshHostAsync() => AskSshHostAsync(null);

    public Task<SshHostDraft?> AskEditSshHostAsync(SshHost host) => AskSshHostAsync(host);

    /// <summary>The host form, shown again until what is typed is valid. <paramref name="existing"/> is the host being edited: its alias is its own.</summary>
    private async Task<SshHostDraft?> AskSshHostAsync(SshHost? existing)
    {
        if (windows.Window is null)
        {
            return null;
        }
        // The file may have changed since the last look: a host that was added by hand is refused, not written twice.
        sshHosts.Refresh();
        var aliases = sshHosts.Current.Hosts.Select(h => h.Alias)
            .Where(a => existing is null || !a.Equals(existing.Alias, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var editing = existing is not null;
        SshHostDraft? typed = existing?.ToDraft();
        IReadOnlyList<SshHostIssue> issues = [];
        Func<SshHostDraft, CancellationToken, Task<SshTestResult>>? tester = sshTester is null ? null : sshTester.TestAsync;
        while (true)
        {
            var form = new SshHostFormView(typed, editing, tester);
            form.ShowIssues(issues);
            var result = await Dialog.ShowAsync(
                windows.Window,
                AppLangResources.Get(editing ? AppLangResourceKind.SettingsSshEditTitle : AppLangResourceKind.SettingsSshAddTitle),
                form,
                AppLangResources.Get(editing ? AppLangResourceKind.SettingsSshEditDescription : AppLangResourceKind.SettingsSshAddDescription),
                new DialogAction(AppLangResources.Get(AppLangResourceKind.Cancel), false),
                new DialogAction(AppLangResources.Get(editing ? AppLangResourceKind.Save : AppLangResourceKind.SettingsSshAddConfirm), true, ButtonVariant.Primary));
            if (result is not true)
            {
                return null;
            }
            typed = form.Draft;
            // The alias of an existing host is not typed here: it is kept, even if it has characters a new alias could not.
            issues = typed.Validate(aliases).Where(i => !(editing && i.Field == SshHostField.Alias)).ToList();
            if (issues.Count == 0)
            {
                return typed;
            }
            // Not valid: the dialog comes back with what was typed and says under which field what is wrong.
        }
    }

    public async Task<NewProjectChoice?> AskNewProjectAsync()
    {
        if (windows.Window is null)
        {
            return null;
        }
        // ~/.ssh/config may have changed since the last look: the hosts offered are the ones it has now.
        sshHosts.Refresh();
        var hosts = sshHosts.Current.Hosts;
        // The Octopus hosts this computer is paired with. Looked up here, not injected: the hosts need the window too.
        var remoteRuntime = Composition.AppHost.Get<RemoteClientRuntime>();
        var remoteHosts = remoteRuntime.Hosts.Peers.Select(peer => (peer.Id, peer.Name)).ToList();
        var sshFiles = remoteFiles ?? Composition.AppHost.Get<RemoteFiles>();
        var localProjects = projects ?? Composition.AppHost.Get<IProjectService>();
        var comparer = pathComparer ?? Composition.AppHost.Get<PathComparer>();

        IFolderBrowser? CreateBrowser(FolderBrowserTarget target)
        {
            if (target.RemoteHost is { } hostId)
            {
                return new HostFolderBrowser(remoteRuntime, hostId, target.DisplayName,
                    AppLangResources.Get(AppLangResourceKind.FolderBrowserUnsupportedHost));
            }
            if (target.SshHost is { } alias)
            {
                return sshFiles.BrowserFor(alias, target.DisplayName);
            }
            return new LocalFolderBrowser(
                target.DisplayName,
                () => [.. localProjects.Projects.Where(ProjectLocation.IsOnThisComputer).Select(p => new FolderBrowseProject(p.Name, p.Path))],
                comparer);
        }

        NewProjectChoice? typed = null;
        string? error = null;
        while (true)
        {
            var form = new NewProjectView(pickers, typed, hosts, remoteHosts, CreateBrowser);
            form.ShowError(error);

            var formActions = Dialog.BuildActions(
            [
                new DialogAction(AppLangResources.Get(AppLangResourceKind.Cancel), false),
                new DialogAction(AppLangResources.Get(AppLangResourceKind.NewProjectAdd), true, ButtonVariant.Primary),
            ]);

            var browseCancel = new TKCode.Arc.Button
            {
                Content = AppLangResources.Get(AppLangResourceKind.Cancel),
                Variant = ButtonVariant.Secondary,
                Size = ControlSize.Small,
            };
            browseCancel.Click += (_, _) => form.CancelBrowse();

            var browseSelect = new TKCode.Arc.Button
            {
                Content = AppLangResources.Get(AppLangResourceKind.FolderBrowserSelect),
                Variant = ButtonVariant.Primary,
                Size = ControlSize.Small,
                IsEnabled = false,
            };
            browseSelect.Click += (_, _) => form.ConfirmBrowse();

            var browseActions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
            };
            browseActions.Children.Add(browseCancel);
            browseActions.Children.Add(browseSelect);

            var dialog = new Dialog
            {
                Size = ControlSize.Medium,
                Title = AppLangResources.Get(AppLangResourceKind.NewProject),
                Description = form.DialogDescription,
                Content = form,
                Actions = formActions,
            };

            void SyncDialogChrome()
            {
                if (form.IsBrowsing)
                {
                    dialog.Size = ControlSize.Large;
                    dialog.Title = form.BrowseDialogTitle;
                    dialog.Description = "";
                    dialog.Actions = browseActions;
                    dialog.CloseOnEscape = false;
                    browseSelect.IsEnabled = form.CanConfirmBrowse;
                }
                else
                {
                    dialog.Size = ControlSize.Medium;
                    dialog.Title = AppLangResources.Get(AppLangResourceKind.NewProject);
                    dialog.Description = form.DialogDescription;
                    dialog.Actions = formActions;
                    dialog.CloseOnEscape = true;
                }
            }

            dialog.AddHandler(InputElement.KeyDownEvent, (_, e) =>
            {
                if (e.Key == Key.Escape && form.IsBrowsing)
                {
                    e.Handled = form.HandleEscape();
                }
            }, RoutingStrategies.Tunnel);

            form.BrowseModeChanged += SyncDialogChrome;
            form.LocationChanged += () =>
            {
                if (!form.IsBrowsing) dialog.Description = form.DialogDescription;
            };
            form.BrowseSelectionChanged += () => browseSelect.IsEnabled = form.CanConfirmBrowse;

            var result = await dialog.ShowAsync(windows.Window);
            if (result is not true)
            {
                return null;
            }
            typed = new NewProjectChoice(form.FolderPath, form.ProjectName, form.SshHost, form.RemoteHost);
            error = NewProjectView.Validate(typed.Path, typed.SshHost, typed.RemoteHost);
            if (error is null)
            {
                // A folder on a host stays as typed: this computer cannot resolve it.
                return typed.SshHost is null && typed.RemoteHost is null ? typed with { Path = Path.GetFullPath(NewProjectView.ExpandHome(typed.Path)) } : typed;
            }
            // Not a usable folder: the dialog comes back with what was typed and says why.
        }
    }

    public async Task<ProjectAppearanceChoice?> AskProjectAppearanceAsync(Octopus.Core.Projects.Project project)
    {
        if (windows.Window is null)
        {
            return null;
        }
        var form = new ProjectAppearanceView(project, pickers, icons);
        var result = await Dialog.ShowAsync(
            windows.Window,
            AppLangResources.Get(AppLangResourceKind.ProjectAppearanceTitle),
            form,
            AppLangResources.Get(AppLangResourceKind.ProjectAppearanceDescription),
            new DialogAction(AppLangResources.Get(AppLangResourceKind.Cancel), false),
            new DialogAction(AppLangResources.Get(AppLangResourceKind.Save), true, ButtonVariant.Primary));
        var choice = result is true ? form.Choice : null;
        // Thumbnails made while choosing and then not kept (or the whole dialog dismissed) are not left behind.
        form.DiscardUnused(choice?.Image);
        return choice;
    }

    /// <summary>Maximum panel width of the interactive account login dialog (leaves room for 80+ terminal columns).</summary>
    internal const double AccountLoginDialogMaxWidth = 760.0;

    internal static Dialog CreateAccountLoginDialog(string title, string description, AccountLoginTerminalView view)
    {
        var dialog = new Dialog
        {
            Size = ControlSize.Large,
            Title = title,
            Description = description,
            Content = view,
            Actions = Dialog.BuildActions(
            [
                new DialogAction(AppLangResources.Get(AppLangResourceKind.Cancel), false),
                new DialogAction(AppLangResources.Get(AppLangResourceKind.AccountLoginDone), true, ButtonVariant.Primary),
            ]),
            // Switching to the browser for OAuth and clicking back on the window, or pressing Esc inside a CLI TUI,
            // must not accidentally abort the in-progress login session.
            CloseOnOutsidePress = false,
            CloseOnEscape = false,
        };
        dialog.Resources[DialogSurfaceTokens.Key(DialogSurfaceTokenKind.PanelWidthLG)] = AccountLoginDialogMaxWidth;
        dialog.Resources[DialogSurfaceTokens.Key(DialogSurfaceTokenKind.ActionsMargin)] = new Thickness(0, 16, 0, 0);
        dialog.Opened += (_, _) => Dispatcher.UIThread.Post(view.FocusTerminal, DispatcherPriority.Input);
        return dialog;
    }

    public async Task<bool> RunAccountLoginAsync(string title, string description, Octopus.Core.Terminals.LaunchSpec spec, bool autoCloseOnSuccess = true)
    {
        if (windows.Window is null)
        {
            return false;
        }

        var factory = Composition.AppHost.Get<Octopus.Terminal.ITerminalConnectionFactory>();
        var settings = Composition.AppHost.Get<Octopus.Storage.ISettingsStore>();
        var view = new AccountLoginTerminalView(spec, factory, settings);
        var completedByExit = false;

        var dialog = CreateAccountLoginDialog(title, description, view);

        view.Exited += (_, code) =>
        {
            if (autoCloseOnSuccess && code == 0)
            {
                completedByExit = true;
                Dispatcher.UIThread.Post(() => dialog.Close(true));
            }
        };

        object? result;
        try
        {
            result = await dialog.ShowAsync(windows.Window);
        }
        finally
        {
            await view.StopAsync();
        }
        return completedByExit || result is true;
    }
}
