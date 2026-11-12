using Octopus.App.Localization;
using Octopus.App.Services;
using Octopus.Ssh;
using TKCode.Arc;

namespace Octopus.App.Dialogs;

/// <summary>Confirmations as Arc dialogs over the main window.</summary>
public sealed class DialogService(IWindowAccessor windows, IPickerService pickers, ISshHostCatalog sshHosts, IProjectIconStore? icons = null, ILocalPorts? ports = null) : IDialogService
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

    public async Task<SshHostDraft?> AskNewSshHostAsync()
    {
        if (windows.Window is null)
        {
            return null;
        }
        // The file may have changed since the last look: a host that was added by hand is refused, not written twice.
        sshHosts.Refresh();
        var aliases = sshHosts.Current.Hosts.Select(h => h.Alias).ToList();
        SshHostDraft? typed = null;
        IReadOnlyList<SshHostIssue> issues = [];
        while (true)
        {
            var form = new SshHostFormView(typed);
            form.ShowIssues(issues);
            var result = await Dialog.ShowAsync(
                windows.Window,
                AppLangResources.Get(AppLangResourceKind.SettingsSshAddTitle),
                form,
                AppLangResources.Get(AppLangResourceKind.SettingsSshAddDescription),
                new DialogAction(AppLangResources.Get(AppLangResourceKind.Cancel), false),
                new DialogAction(AppLangResources.Get(AppLangResourceKind.SettingsSshAddConfirm), true, ButtonVariant.Primary));
            if (result is not true)
            {
                return null;
            }
            typed = form.Draft;
            issues = typed.Validate(aliases);
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
        NewProjectChoice? typed = null;
        string? error = null;
        while (true)
        {
            var form = new NewProjectView(pickers, typed, hosts);
            form.ShowError(error);
            var result = await Dialog.ShowAsync(
                windows.Window,
                AppLangResources.Get(AppLangResourceKind.NewProject),
                form,
                AppLangResources.Get(AppLangResourceKind.NewProjectDescription),
                new DialogAction(AppLangResources.Get(AppLangResourceKind.Cancel), false),
                new DialogAction(AppLangResources.Get(AppLangResourceKind.NewProjectAdd), true, ButtonVariant.Primary));
            if (result is not true)
            {
                return null;
            }
            typed = new NewProjectChoice(form.FolderPath, form.ProjectName, form.SshHost);
            error = NewProjectView.Validate(typed.Path, typed.SshHost);
            if (error is null)
            {
                // A folder on a host stays as typed: this computer cannot resolve it.
                return typed.SshHost is null ? typed with { Path = Path.GetFullPath(NewProjectView.ExpandHome(typed.Path)) } : typed;
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
}
