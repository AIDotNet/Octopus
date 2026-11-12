using System.Globalization;
using Avalonia.Controls;
using Octopus.App.Localization;
using Octopus.Core.Projects;
using Octopus.Ssh;
using TKCode.Arc;

namespace Octopus.App.Dialogs;

/// <summary>The fields of a tunnel: which way it goes, where it leads, which port it listens on.</summary>
public partial class TunnelFormView : UserControl
{
    private readonly string _id;
    private readonly Func<int> _pickPort;
    /// <summary>A new local tunnel usually has the same port on both sides: the port it listens on follows the one it leads to until it is typed.</summary>
    private bool _mirrorPort;
    private bool _settingListenPort;

    public TunnelFormView() : this(null, () => 0)
    {
    }

    /// <param name="initial">The tunnel to edit, or what was typed before a problem sent the form back; null: a new one.</param>
    /// <param name="pickPort">Gives a port on this computer that is free now.</param>
    public TunnelFormView(TunnelSpec? initial, Func<int> pickPort)
    {
        _pickPort = pickPort;
        _id = initial?.Id ?? Guid.NewGuid().ToString("N");
        InitializeComponent();

        KindControl.SelectedValue = initial?.Kind == TunnelKind.Remote ? "remote" : "local";
        NameInput.Text = initial?.Name ?? "";
        TargetHostInput.Text = initial?.TargetHost ?? "localhost";
        TargetPortInput.Text = PortText(initial?.TargetPort);
        ListenPortInput.Text = PortText(initial?.ListenPort);
        BindInput.Text = initial?.BindAddress ?? "127.0.0.1";
        AutoStartBox.IsChecked = initial?.AutoStart ?? false;
        AutoReconnectBox.IsChecked = initial?.AutoReconnect ?? true;
        _mirrorPort = initial is null;

        KindControl.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == nameof(SegmentedControl.SelectedValue)) ApplyKind();
        };
        TargetPortInput.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == nameof(Input.Text) && _mirrorPort && Kind == TunnelKind.Local) SetListenPort(TargetPortInput.Text);
        };
        ListenPortInput.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == nameof(Input.Text) && !_settingListenPort && ListenPortInput.Text != TargetPortInput.Text) _mirrorPort = false;
        };
        PickButton.Click += (_, _) =>
        {
            if (_pickPort() is > 0 and var port)
            {
                _mirrorPort = false;
                SetListenPort(port.ToString(CultureInfo.InvariantCulture));
            }
        };
        ApplyKind();
    }

    private TunnelKind Kind => KindControl.SelectedValue as string == "remote" ? TunnelKind.Remote : TunnelKind.Local;

    /// <summary>What was typed. A port that is not a number is 0, which the check says is not a port.</summary>
    public TunnelSpec Spec => new()
    {
        Id = _id,
        Name = Blank(NameInput.Text),
        Kind = Kind,
        BindAddress = BindInput.Text?.Trim() ?? "",
        ListenPort = PortOf(ListenPortInput.Text),
        TargetHost = TargetHostInput.Text?.Trim() ?? "",
        TargetPort = PortOf(TargetPortInput.Text),
        AutoStart = AutoStartBox.IsChecked == true,
        AutoReconnect = AutoReconnectBox.IsChecked == true,
    };

    /// <summary>Puts each problem under the field it is in; the fields without a problem are cleared.</summary>
    public void ShowIssues(IReadOnlyList<TunnelIssue> issues)
    {
        foreach (var input in new[] { TargetHostInput, TargetPortInput, ListenPortInput, BindInput }) input.ErrorMessage = null;
        foreach (var issue in issues)
        {
            FieldOf(issue.Field).ErrorMessage = AppLangResources.Get(MessageOf(issue.Problem));
        }
    }

    private Input FieldOf(TunnelField field) => field switch
    {
        TunnelField.BindAddress => BindInput,
        TunnelField.ListenPort => ListenPortInput,
        TunnelField.TargetHost => TargetHostInput,
        _ => TargetPortInput,
    };

    private static AppLangResourceKind MessageOf(TunnelProblem problem) => problem switch
    {
        TunnelProblem.Required => AppLangResourceKind.SettingsSshProblemRequired,
        TunnelProblem.Port => AppLangResourceKind.SettingsSshProblemPort,
        _ => AppLangResourceKind.TunnelProblemAddress,
    };

    /// <summary>The labels say where each side is: which one is on this computer and which on the host depends on the direction.</summary>
    private void ApplyKind()
    {
        var local = Kind == TunnelKind.Local;
        TargetHostInput.Label = AppLangResources.Get(local ? AppLangResourceKind.TunnelFieldTargetLocal : AppLangResourceKind.TunnelFieldTargetRemote);
        TargetHostInput.Description = AppLangResources.Get(local ? AppLangResourceKind.TunnelFieldTargetLocalHint : AppLangResourceKind.TunnelFieldTargetRemoteHint);
        ListenPortInput.Label = AppLangResources.Get(local ? AppLangResourceKind.TunnelFieldListenLocal : AppLangResourceKind.TunnelFieldListenRemote);
        ListenPortInput.Description = local ? null : AppLangResources.Get(AppLangResourceKind.TunnelFieldListenRemoteHint);
        BindInput.Description = AppLangResources.Get(local ? AppLangResourceKind.TunnelFieldBindHintLocal : AppLangResourceKind.TunnelFieldBindHintRemote);
        // A free port can only be found on this computer.
        PickButton.IsVisible = local;
    }

    private void SetListenPort(string? text)
    {
        _settingListenPort = true;
        try
        {
            ListenPortInput.Text = text;
        }
        finally
        {
            _settingListenPort = false;
        }
    }

    private static string PortText(int? port) => port is > 0 ? port.Value.ToString(CultureInfo.InvariantCulture) : "";

    private static int PortOf(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) ? port : 0;

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
