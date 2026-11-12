using Avalonia.Controls;
using Octopus.App.Localization;
using Octopus.Ssh;
using TKCode.Arc;

namespace Octopus.App.Dialogs;

/// <summary>The fields of a new SSH host: one line each. Only the alias is required.</summary>
public partial class SshHostFormView : UserControl
{
    public SshHostFormView() : this(null)
    {
    }

    public SshHostFormView(SshHostDraft? initial)
    {
        InitializeComponent();
        AliasInput.Text = initial?.Alias ?? "";
        HostNameInput.Text = initial?.HostName ?? "";
        UserInput.Text = initial?.User ?? "";
        PortInput.Text = initial?.Port ?? "";
        IdentityFileInput.Text = initial?.IdentityFile ?? "";
        ProxyJumpInput.Text = initial?.ProxyJump ?? "";
    }

    /// <summary>What was typed, trimmed; an empty optional field is null.</summary>
    public SshHostDraft Draft => new(
        AliasInput.Text?.Trim() ?? "",
        Blank(HostNameInput.Text),
        Blank(UserInput.Text),
        Blank(PortInput.Text),
        Blank(IdentityFileInput.Text),
        Blank(ProxyJumpInput.Text));

    /// <summary>Puts each problem under the field it is in; the fields without a problem are cleared.</summary>
    public void ShowIssues(IReadOnlyList<SshHostIssue> issues)
    {
        foreach (var input in Inputs()) input.ErrorMessage = null;
        foreach (var issue in issues)
        {
            FieldOf(issue.Field).ErrorMessage = AppLangResources.Get(MessageOf(issue.Problem));
        }
    }

    private IEnumerable<Input> Inputs() => [AliasInput, HostNameInput, UserInput, PortInput, IdentityFileInput, ProxyJumpInput];

    private Input FieldOf(SshHostField field) => field switch
    {
        SshHostField.Alias => AliasInput,
        SshHostField.HostName => HostNameInput,
        SshHostField.User => UserInput,
        SshHostField.Port => PortInput,
        SshHostField.IdentityFile => IdentityFileInput,
        _ => ProxyJumpInput,
    };

    private static AppLangResourceKind MessageOf(SshHostProblem problem) => problem switch
    {
        SshHostProblem.Required => AppLangResourceKind.SettingsSshProblemRequired,
        SshHostProblem.AliasFormat => AppLangResourceKind.SettingsSshProblemAliasFormat,
        SshHostProblem.AliasExists => AppLangResourceKind.SettingsSshProblemAliasExists,
        SshHostProblem.Word => AppLangResourceKind.SettingsSshProblemWord,
        SshHostProblem.Port => AppLangResourceKind.SettingsSshProblemPort,
        _ => AppLangResourceKind.SettingsSshProblemKeyPath,
    };

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
