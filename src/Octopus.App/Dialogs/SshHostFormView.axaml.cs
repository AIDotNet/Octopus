using Avalonia.Controls;
using Avalonia.Interactivity;
using Octopus.App.Localization;
using Octopus.Ssh;
using TKCode.Arc;

namespace Octopus.App.Dialogs;

/// <summary>
/// The fields of an SSH host: one line each, and a button that tries what is typed. Only the alias is required. The
/// protocol decides which fields apply: a telnet host has no user, key or jump host.
/// </summary>
public partial class SshHostFormView : UserControl
{
    private readonly Func<SshHostDraft, CancellationToken, Task<SshTestResult>>? _tester;
    private CancellationTokenSource? _testing;

    public SshHostFormView() : this(null)
    {
    }

    /// <param name="initial">What the fields start with.</param>
    /// <param name="editing">True for a host that exists: its alias is what projects refer to, so it is shown but cannot be typed over.</param>
    /// <param name="tester">What the test button runs; without it the button is not shown.</param>
    public SshHostFormView(SshHostDraft? initial, bool editing = false, Func<SshHostDraft, CancellationToken, Task<SshTestResult>>? tester = null)
    {
        _tester = tester;
        InitializeComponent();
        AliasInput.Text = initial?.Alias ?? "";
        AliasInput.IsEnabled = !editing;
        HostNameInput.Text = initial?.HostName ?? "";
        UserInput.Text = initial?.User ?? "";
        PortInput.Text = initial?.Port ?? "";
        IdentityFileInput.Text = initial?.IdentityFile ?? "";
        ProxyJumpInput.Text = initial?.ProxyJump ?? "";

        ProtocolSelect.ItemsSource = new List<SelectOption>
        {
            new(nameof(SshHostProtocol.Ssh), "SSH"),
            new(nameof(SshHostProtocol.Mosh), "Mosh"),
            new(nameof(SshHostProtocol.Telnet), "Telnet"),
        };
        ProtocolSelect.SelectedValue = (initial?.Protocol ?? SshHostProtocol.Ssh).ToString();
        // The property, not an event: the event is raised later, from the dispatcher.
        ProtocolSelect.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == nameof(ProtocolSelect.SelectedValue))
            {
                OnProtocolChanged();
            }
        };
        TestRow.IsVisible = tester is not null;
        TestButton.Click += OnTestClick;
        OnProtocolChanged();
    }

    /// <summary>What the person chose; ssh until they choose another.</summary>
    public SshHostProtocol Protocol =>
        Enum.TryParse<SshHostProtocol>(ProtocolSelect.SelectedValue as string, out var protocol) ? protocol : SshHostProtocol.Ssh;

    /// <summary>What was typed, trimmed; an empty optional field is null.</summary>
    public SshHostDraft Draft => new(
        AliasInput.Text?.Trim() ?? "",
        Blank(HostNameInput.Text),
        Blank(UserInput.Text),
        Blank(PortInput.Text),
        Blank(IdentityFileInput.Text),
        Blank(ProxyJumpInput.Text),
        Protocol);

    /// <summary>Puts each problem under the field it is in; the fields without a problem are cleared.</summary>
    public void ShowIssues(IReadOnlyList<SshHostIssue> issues)
    {
        foreach (var input in Inputs()) input.ErrorMessage = null;
        foreach (var issue in issues)
        {
            FieldOf(issue.Field).ErrorMessage = AppLangResources.Get(MessageOf(issue.Problem));
        }
    }

    /// <summary>The protocol decides the fields that apply and what is said about it.</summary>
    private void OnProtocolChanged()
    {
        var telnet = Protocol == SshHostProtocol.Telnet;
        UserInput.IsVisible = IdentityFileInput.IsVisible = ProxyJumpInput.IsVisible = !telnet;
        // Without the user the port takes the whole row.
        Grid.SetColumn(PortInput, telnet ? 0 : 1);
        Grid.SetColumnSpan(PortInput, telnet ? 2 : 1);
        PortInput.Placeholder = telnet ? "23" : "22";
        ProtocolHint.Text = AppLangResources.Get(Protocol switch
        {
            SshHostProtocol.Mosh => AppLangResourceKind.SettingsSshProtocolHintMosh,
            SshHostProtocol.Telnet => AppLangResourceKind.SettingsSshProtocolHintTelnet,
            _ => AppLangResourceKind.SettingsSshProtocolHintSsh,
        });
        // What the last test said was about another protocol.
        TestResult.IsVisible = false;
    }

    private async void OnTestClick(object? sender, RoutedEventArgs e)
    {
        if (_tester is null)
        {
            return;
        }
        var draft = Draft;
        // What cannot be used is shown under its field, as for a save. The alias being taken is not a problem for a test.
        var issues = draft.Validate([]);
        ShowIssues(issues);
        if (issues.Count > 0)
        {
            return;
        }

        _testing?.Cancel();
        var run = _testing = new CancellationTokenSource();
        TestButton.IsEnabled = false;
        TestButton.Content = AppLangResources.Get(AppLangResourceKind.SettingsSshTesting);
        TestResult.IsVisible = false;
        SshTestResult result;
        try
        {
            result = await _tester(draft, run.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (ReferenceEquals(_testing, run))
            {
                TestButton.IsEnabled = true;
                TestButton.Content = AppLangResources.Get(AppLangResourceKind.SettingsSshTest);
            }
        }
        if (run.IsCancellationRequested)
        {
            return;
        }
        ShowResult(result);
    }

    private void ShowResult(SshTestResult result)
    {
        (TestResult.Tone, TestResult.Content) = result.Outcome switch
        {
            SshTestOutcome.Connected => (AlertTone.Success, AppLangResources.Get(AppLangResourceKind.SettingsSshTestOk)),
            SshTestOutcome.AuthenticationNeeded => (AlertTone.Info, AppLangResources.Get(AppLangResourceKind.SettingsSshTestAuth)),
            SshTestOutcome.MoshServerMissing => (AlertTone.Warning, AppLangResources.Get(AppLangResourceKind.SettingsSshTestMoshMissing)),
            _ => (AlertTone.Danger, AppLangResources.Format(AppLangResourceKind.SettingsSshTestFailed, result.Detail)),
        };
        TestResult.IsVisible = true;
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        // The dialog closed: the answer of a test that is still running is not wanted.
        _testing?.Cancel();
        base.OnDetachedFromVisualTree(e);
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
