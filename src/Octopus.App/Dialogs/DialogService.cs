using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
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

    public Task<bool> ConfirmAsync(string title, string description, string confirmText, bool isDanger = false) =>
        OnUiAsync(() =>
        {
            if (windows.Window is null)
            {
                return Task.FromResult(false);
            }
            return Dialog.ConfirmAsync(
                windows.Window, title, description, confirmText,
                AppLangResources.Get(AppLangResourceKind.Cancel), isDanger);
        });

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
        var typed = existing?.ToDraft();
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

    public async Task<NewProjectChoice?> AskNewProjectAsync(NewProjectChoice? initial = null)
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

        var typed = initial;
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

    public async Task<ProjectAppearanceChoice?> AskDeviceAppearanceAsync(
        string deviceKey,
        string deviceName,
        string subtitle,
        DeviceAppearance? current,
        string defaultColor,
        string? defaultIcon)
    {
        if (windows.Window is null)
        {
            return null;
        }
        var color = current?.Color ?? defaultColor;
        // When uncustomized, pre-select the device's default Lucide icon; once customized, keep whatever the user picked (including null = Initial).
        var icon = current is null ? defaultIcon : current.Icon;
        var image = current?.Image;
        var form = new ProjectAppearanceView("dev-" + deviceKey, deviceName, subtitle, color, icon, image, pickers, icons, circularPreview: true);
        var result = await Dialog.ShowAsync(
            windows.Window,
            AppLangResources.Get(AppLangResourceKind.DeviceAppearanceTitle),
            form,
            AppLangResources.Get(AppLangResourceKind.DeviceAppearanceDescription),
            new DialogAction(AppLangResources.Get(AppLangResourceKind.Cancel), false),
            new DialogAction(AppLangResources.Get(AppLangResourceKind.Save), true, ButtonVariant.Primary));
        var choice = result is true ? form.Choice : null;
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

    public Task<Octopus.App.Services.Workspaces.CreateWorkspaceRequest?> AskCreateWorkspaceAsync(
        Project project,
        string repositoryRoot,
        IReadOnlyList<Octopus.Git.GitBranch> branches,
        IReadOnlyList<Octopus.Git.GitWorktree> worktrees,
        Octopus.App.Services.Workspaces.IGitHubTaskProvider taskProvider,
        Func<string, string> computePath,
        string homeDirectory,
        string? defaultAgentName,
        Octopus.App.Services.Workspaces.CreateWorkspaceRequest? initial = null,
        string? initialError = null) =>
        OnUiAsync(async () =>
        {
            if (windows.Window is null)
            {
                return null;
            }
            var model = new CreateWorkspaceViewModel(
                project,
                repositoryRoot,
                branches,
                worktrees,
                taskProvider,
                computePath,
                homeDirectory,
                defaultAgentName,
                initial,
                initialError,
                pickFolder: () => pickers.PickFolderAsync());
            var projectDir = Path.GetDirectoryName(computePath("branch"))
                ?? WorkspacePaths.ProjectWorkspacesDirectory(project.Name, project.Path, homeDirectory);
            var displayProjectDir = WorkspacePaths.FormatHomePath(projectDir, homeDirectory).TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
            while (true)
            {
                var dialog = new Dialog
                {
                    Size = ControlSize.Large,
                    Title = AppLangResources.Get(AppLangResourceKind.WorkspaceDialogTitle),
                    Description = AppLangResources.Format(AppLangResourceKind.WorkspaceDialogDescription, displayProjectDir),
                    Content = new CreateWorkspaceView(model),
                    Actions = Dialog.BuildActions(
                    [
                        new DialogAction(AppLangResources.Get(AppLangResourceKind.Cancel), false),
                        new DialogAction(AppLangResources.Get(AppLangResourceKind.WorkspaceDialogCreate), true, ButtonVariant.Primary),
                    ]),
                };
                var result = await dialog.ShowAsync(windows.Window);
                if (result is not true)
                {
                    return null;
                }
                if (model.BuildRequest() is { } request)
                {
                    return request;
                }
            }
        });

    public Task<Octopus.App.Services.Workspaces.WorkspaceCleanupRunSummary?> ShowInspectWorkspacesAsync(
        Octopus.App.Services.Workspaces.IWorkspaceCleanupService cleanup,
        IProjectService projects,
        Octopus.Platform.IFileOpener? fileOpener = null) =>
        OnUiAsync(async () =>
        {
            if (windows.Window is null)
            {
                return null;
            }
            var settings = Composition.AppHost.Get<Octopus.Storage.ISettingsStore>();
            var model = new InspectWorkspacesViewModel(cleanup, projects, fileOpener, settings.Current.WorkspaceCleanup.DeleteMergedBranch);
            var view = new InspectWorkspacesView(model);
            Octopus.App.Services.Workspaces.WorkspaceCleanupRunSummary? summary = null;

            var cancelButton = new TKCode.Arc.Button
            {
                Content = AppLangResources.Get(AppLangResourceKind.Cancel),
                Variant = ButtonVariant.Secondary,
                Size = ControlSize.Small,
            };
            var removeButton = new TKCode.Arc.Button
            {
                Content = model.RemoveButtonText,
                Variant = ButtonVariant.Danger,
                Size = ControlSize.Small,
                IsEnabled = false,
            };
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
            };
            actions.Children.Add(cancelButton);
            actions.Children.Add(removeButton);

            var dialog = new Dialog
            {
                Size = ControlSize.Large,
                Title = AppLangResources.Get(AppLangResourceKind.WorkspaceInspectTitle),
                Description = AppLangResources.Get(AppLangResourceKind.WorkspaceInspectDescription),
                Content = view,
                Actions = actions,
            };
            dialog.Resources[DialogSurfaceTokens.Key(DialogSurfaceTokenKind.PanelWidthLG)] = 740.0;

            void SyncButtons()
            {
                removeButton.Content = model.RemoveButtonText;
                removeButton.IsEnabled = model.CanConfirmRemove;
                removeButton.IsLoading = model.IsRemoving;
                cancelButton.IsEnabled = !model.IsRemoving;
            }

            model.SelectionStateChanged += (_, _) => Dispatcher.UIThread.Post(SyncButtons);
            cancelButton.Click += (_, _) => dialog.Close(false);
            removeButton.Click += async (_, _) =>
            {
                if (!model.CanConfirmRemove) return;
                summary = await model.RemoveSelectedAsync();
                dialog.Close(true);
            };
            dialog.Opened += (_, _) => _ = model.LoadAsync();

            await dialog.ShowAsync(windows.Window);
            return summary;
        });

    public Task<Octopus.App.Bots.NewBotRequest?> AskNewBotAsync(string? initialTemplateId = null) =>
        OnUiAsync(async () =>
        {
            if (windows.Window is null)
            {
                return null;
            }
            var catalog = Composition.AppHost.Get<Octopus.Agents.IAgentCatalog>();
            var scanner = Composition.AppHost.Get<Octopus.Agents.IAgentScanner>();
            var settings = Composition.AppHost.Get<Octopus.Storage.ISettingsStore>();
            var hostOptions = new List<SelectOption>
            {
                new("local", AppLangResources.Get(AppLangResourceKind.NewProjectLocalMachine)),
            };
            var remoteRuntime = Composition.AppHost.Get<RemoteClientRuntime>();
            foreach (var peer in remoteRuntime.Hosts.Peers)
            {
                hostOptions.Add(new SelectOption("remote:" + peer.Id, peer.Name));
            }
            sshHosts.Refresh();
            foreach (var ssh in sshHosts.Current.Hosts)
            {
                hostOptions.Add(new SelectOption("ssh:" + ssh.Alias, ssh.Alias));
            }
            var initialHost = projects?.ActiveDeviceKey ?? "local";
            var model = new Octopus.App.Bots.NewBotDialogViewModel(catalog, scanner, settings, initialTemplateId, hostOptions, initialHost, remoteRuntime, pickers, icons);
            var view = new Octopus.App.Bots.NewBotDialogView(model);
            var dialog = new Dialog
            {
                Size = ControlSize.Medium,
                Title = AppLangResources.Get(AppLangResourceKind.BotDialogTitle),
                Description = AppLangResources.Get(AppLangResourceKind.BotDialogDescription),
                Content = view,
                Actions = Dialog.BuildActions(
                [
                    new DialogAction(AppLangResources.Get(AppLangResourceKind.Cancel), false),
                    new DialogAction(AppLangResources.Get(AppLangResourceKind.BotDialogCreate), true, ButtonVariant.Primary),
                ]),
            };
            dialog.Resources[DialogSurfaceTokens.Key(DialogSurfaceTokenKind.PanelWidthMD)] = 520.0;
            var result = await dialog.ShowAsync(windows.Window);
            var request = result is true ? model.BuildRequest() : null;
            model.DiscardUnusedImages(request?.Image);
            return request;
        });

    public Task ShowConnectorCatalogAsync(Octopus.Core.Bots.BotDefinition bot, Octopus.App.Bots.IBotService bots) =>
        OnUiAsync(async () =>
        {
            if (windows.Window is null)
            {
                return;
            }
            var model = new Octopus.App.Bots.ConnectorCatalogViewModel(bot, bots);
            var view = new Octopus.App.Bots.ConnectorCatalogView(model);
            var dialog = new Dialog
            {
                Size = ControlSize.Large,
                Title = model.DialogTitle,
                Description = "",
                Content = view,
                Actions = Dialog.BuildActions(
                [
                    new DialogAction(AppLangResources.Get(AppLangResourceKind.UpdateClose), true, ButtonVariant.Secondary),
                ]),
            };
            dialog.Resources[DialogSurfaceTokens.Key(DialogSurfaceTokenKind.PanelWidthLG)] = 740.0;
            await dialog.ShowAsync(windows.Window);
        });

    public Task<Octopus.App.Bots.BotAvatarChoice?> AskBotAvatarAsync(Octopus.Core.Bots.BotDefinition bot) =>
        OnUiAsync(async () =>
        {
            if (windows.Window is null)
            {
                return null;
            }
            var model = new Octopus.App.Bots.BotAvatarPickerViewModel(bot, pickers, icons);
            var view = new Octopus.App.Bots.BotAvatarPickerView(model);
            var dialog = new Dialog
            {
                Size = ControlSize.Medium,
                Title = AppLangResources.Get(AppLangResourceKind.BotAvatarPickerTitle),
                Description = "",
                Content = view,
                Actions = Dialog.BuildActions(
                [
                    new DialogAction(AppLangResources.Get(AppLangResourceKind.Cancel), false),
                    new DialogAction(AppLangResources.Get(AppLangResourceKind.Save), true, ButtonVariant.Primary),
                ]),
            };
            dialog.Resources[DialogSurfaceTokens.Key(DialogSurfaceTokenKind.PanelWidthMD)] = 460.0;
            var result = await dialog.ShowAsync(windows.Window);
            var choice = result is true ? model.Choice : null;
            model.DiscardUnused(choice?.Image);
            return choice;
        });

    public Task ShowBotShareAsync(Octopus.Core.Bots.BotDefinition bot, Octopus.App.Bots.IBotService bots) =>
        OnUiAsync(async () =>
        {
            if (windows.Window is null)
            {
                return;
            }
            var model = new Octopus.App.Bots.BotShareDialogViewModel(bot, bots, icons);
            var view = new Octopus.App.Bots.BotShareDialogView(model);
            var cancelBtn = new TKCode.Arc.Button
            {
                Content = AppLangResources.Get(AppLangResourceKind.Cancel),
                Variant = ButtonVariant.Secondary,
                Size = ControlSize.Small,
            };
            var copyBundleBtn = new TKCode.Arc.Button
            {
                Content = AppLangResources.Get(AppLangResourceKind.BotShareCopyConfig),
                Variant = ButtonVariant.Secondary,
                Size = ControlSize.Small,
            };
            var copyLinkBtn = new TKCode.Arc.Button
            {
                Content = AppLangResources.Get(AppLangResourceKind.BotShareCopyLink),
                Variant = ButtonVariant.Primary,
                Size = ControlSize.Small,
            };
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
            };
            actions.Children.Add(cancelBtn);
            actions.Children.Add(copyBundleBtn);
            actions.Children.Add(copyLinkBtn);

            var dialog = new Dialog
            {
                Size = ControlSize.Medium,
                Title = AppLangResources.Get(AppLangResourceKind.BotSettingsShare),
                Description = "",
                Content = view,
                Actions = actions,
            };
            dialog.Resources[DialogSurfaceTokens.Key(DialogSurfaceTokenKind.PanelWidthMD)] = 480.0;

            cancelBtn.Click += (_, _) => dialog.Close(false);
            copyBundleBtn.Click += async (_, _) =>
            {
                var bundle = model.BuildShareBundle();
                if (TopLevel.GetTopLevel(windows.Window)?.Clipboard is { } clipboard)
                {
                    await clipboard.SetTextAsync(bundle);
                }
                model.MarkCopied();
                copyBundleBtn.Content = AppLangResources.Get(AppLangResourceKind.BotShareCopied);
            };
            copyLinkBtn.Click += async (_, _) =>
            {
                var link = model.BuildShareLink();
                if (TopLevel.GetTopLevel(windows.Window)?.Clipboard is { } clipboard)
                {
                    await clipboard.SetTextAsync(link);
                }
                model.MarkCopied();
                copyLinkBtn.Content = AppLangResources.Get(AppLangResourceKind.BotShareCopyLinkCopied);
            };
            await dialog.ShowAsync(windows.Window);
        });

    public Task<bool> ConfirmBotImportAsync(Octopus.Core.Bots.BotSharePayload payload) =>
        OnUiAsync(async () =>
        {
            if (windows.Window is null)
            {
                return false;
            }
            var model = new Octopus.App.Bots.BotImportDialogViewModel(payload);
            var view = new Octopus.App.Bots.BotImportDialogView(model);
            var dialog = new Dialog
            {
                Size = ControlSize.Medium,
                Title = AppLangResources.Get(AppLangResourceKind.BotImportTitle),
                Description = "",
                Content = view,
                Actions = Dialog.BuildActions(
                [
                    new DialogAction(AppLangResources.Get(AppLangResourceKind.Cancel), false),
                    new DialogAction(AppLangResources.Get(AppLangResourceKind.BotImportConfirm), true, ButtonVariant.Primary),
                ]),
            };
            dialog.Resources[DialogSurfaceTokens.Key(DialogSurfaceTokenKind.PanelWidthMD)] = 460.0;
            return await dialog.ShowAsync(windows.Window) is true;
        });

    public Task<Octopus.App.Bots.Groups.BotGroupDialogResult?> AskNewBotGroupAsync(IReadOnlyList<Octopus.Core.Bots.BotDefinition> bots) =>
        ShowBotGroupDialogAsync(existingGroup: null, bots);

    public Task<Octopus.App.Bots.Groups.BotGroupDialogResult?> AskEditBotGroupAsync(
        Octopus.Core.Bots.BotGroupDefinition group,
        IReadOnlyList<Octopus.Core.Bots.BotDefinition> bots) =>
        ShowBotGroupDialogAsync(group, bots);

    private Task<Octopus.App.Bots.Groups.BotGroupDialogResult?> ShowBotGroupDialogAsync(
        Octopus.Core.Bots.BotGroupDefinition? existingGroup,
        IReadOnlyList<Octopus.Core.Bots.BotDefinition> bots) =>
        OnUiAsync(async () =>
        {
            if (windows.Window is null)
            {
                return null;
            }
            var catalog = Composition.AppHost.Get<Octopus.Agents.IAgentCatalog>();
            var model = new Octopus.App.Bots.Groups.BotGroupDialogViewModel(bots, catalog, icons, existingGroup);
            var view = new Octopus.App.Bots.Groups.BotGroupDialogView(model);

            var cancelButton = new TKCode.Arc.Button
            {
                Content = AppLangResources.Get(AppLangResourceKind.Cancel),
                Variant = ButtonVariant.Secondary,
                Size = ControlSize.Small,
            };
            var confirmButton = new TKCode.Arc.Button
            {
                Content = AppLangResources.Get(existingGroup is null ? AppLangResourceKind.BotGroupDialogCreate : AppLangResourceKind.Save),
                Variant = ButtonVariant.Primary,
                Size = ControlSize.Small,
                IsEnabled = model.CanConfirm,
            };
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
            };
            actions.Children.Add(cancelButton);
            actions.Children.Add(confirmButton);

            var dialog = new Dialog
            {
                Size = ControlSize.Medium,
                Title = AppLangResources.Get(existingGroup is null ? AppLangResourceKind.BotGroupDialogCreateTitle : AppLangResourceKind.BotGroupDialogEditTitle),
                Description = AppLangResources.Get(existingGroup is null ? AppLangResourceKind.BotGroupDialogCreateDescription : AppLangResourceKind.BotGroupDialogEditDescription),
                Content = view,
                Actions = actions,
            };
            dialog.Resources[DialogSurfaceTokens.Key(DialogSurfaceTokenKind.PanelWidthMD)] = 500.0;

            model.ValidationChanged += (_, _) => Dispatcher.UIThread.Post(() => confirmButton.IsEnabled = model.CanConfirm);
            cancelButton.Click += (_, _) => dialog.Close(false);
            confirmButton.Click += (_, _) =>
            {
                if (model.CanConfirm)
                {
                    dialog.Close(true);
                }
            };

            var result = await dialog.ShowAsync(windows.Window);
            return result is true ? model.BuildResult() : null;
        });

    private static Task<T> OnUiAsync<T>(Func<Task<T>> action) =>
        Dispatcher.UIThread.CheckAccess() ? action() : Dispatcher.UIThread.InvokeAsync(action);

    private static Task OnUiAsync(Func<Task> action) =>
        Dispatcher.UIThread.CheckAccess() ? action() : Dispatcher.UIThread.InvokeAsync(action);
}
