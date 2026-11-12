using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Octopus.App.Dialogs;
using Octopus.Core.Projects;
using Octopus.Ssh;
using TKCode.Arc;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>The tunnel form: what was typed becomes the spec, the labels follow the direction, each problem is shown under its field.</summary>
public class TunnelFormTests
{
    private static Input Field(TunnelFormView form, string name) => form.FindControl<Input>(name)!;

    private static void Type(TunnelFormView form, string name, string text) => Field(form, name).Text = text;

    private static void Choose(TunnelFormView form, string kind) => form.FindControl<SegmentedControl>("KindControl")!.SelectedValue = kind;

    [AvaloniaFact]
    public void A_new_tunnel_starts_as_a_local_one_that_listens_on_the_loopback_address_and_reconnects()
    {
        var form = new TunnelFormView(null, () => 0);

        var spec = form.Spec;

        Assert.Equal(TunnelKind.Local, spec.Kind);
        Assert.Equal("localhost", spec.TargetHost);
        Assert.Equal("127.0.0.1", spec.BindAddress);
        Assert.Null(spec.Name);
        Assert.False(spec.AutoStart);
        Assert.True(spec.AutoReconnect);
        // Nothing typed yet: the ports are not ports, and the check says so.
        Assert.Equal(0, spec.ListenPort);
        Assert.Equal(0, spec.TargetPort);
        Assert.Equal(2, SshTunnelCommand.Validate(spec).Count);
    }

    [AvaloniaFact]
    public void What_was_typed_is_the_spec_and_an_edited_tunnel_keeps_its_id()
    {
        var initial = new TunnelSpec
        {
            Kind = TunnelKind.Remote, Name = " Callback ", BindAddress = "127.0.0.1", ListenPort = 9000, TargetHost = "localhost", TargetPort = 3000,
            AutoStart = true, AutoReconnect = false,
        };
        var form = new TunnelFormView(initial, () => 0);

        Type(form, "TargetPortInput", " 3100 ");
        var spec = form.Spec;

        Assert.Equal(initial.Id, spec.Id);
        Assert.Equal(TunnelKind.Remote, spec.Kind);
        Assert.Equal("Callback", spec.Name);
        Assert.Equal(9000, spec.ListenPort);
        Assert.Equal(3100, spec.TargetPort);
        Assert.True(spec.AutoStart);
        Assert.False(spec.AutoReconnect);
    }

    [AvaloniaFact]
    public void The_port_it_listens_on_follows_the_port_it_leads_to_until_it_is_typed()
    {
        var form = new TunnelFormView(null, () => 0);

        Type(form, "TargetPortInput", "5173");
        Assert.Equal(5173, form.Spec.ListenPort);

        Type(form, "ListenPortInput", "15173");
        Type(form, "TargetPortInput", "5174");

        Assert.Equal(15173, form.Spec.ListenPort);
        Assert.Equal(5174, form.Spec.TargetPort);
    }

    [AvaloniaFact]
    public void The_port_of_an_edited_tunnel_is_not_changed_by_its_target()
    {
        var form = new TunnelFormView(new TunnelSpec { Kind = TunnelKind.Local, ListenPort = 15432, TargetPort = 5432 }, () => 0);

        Type(form, "TargetPortInput", "5433");

        Assert.Equal(15432, form.Spec.ListenPort);
    }

    [AvaloniaFact]
    public void A_free_port_can_be_picked_for_the_local_side_only()
    {
        var form = new TunnelFormView(null, () => 54321);
        var pick = form.FindControl<TKCode.Arc.Button>("PickButton")!;
        Assert.True(pick.IsVisible);

        pick.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Assert.Equal(54321, form.Spec.ListenPort);

        // It stays what was picked when the target is typed.
        Type(form, "TargetPortInput", "80");
        Assert.Equal(54321, form.Spec.ListenPort);

        Choose(form, "remote");
        Assert.False(pick.IsVisible);
    }

    [AvaloniaFact]
    public void The_labels_say_where_each_side_is_for_the_direction_chosen()
    {
        var form = new TunnelFormView(null, () => 0);
        var target = Field(form, "TargetHostInput");
        var listen = Field(form, "ListenPortInput");
        var localTarget = target.Label;
        var localListen = listen.Label;

        Choose(form, "remote");

        Assert.Equal(TunnelKind.Remote, form.Spec.Kind);
        Assert.NotEqual(localTarget, target.Label);
        Assert.NotEqual(localListen, listen.Label);
        Assert.False(string.IsNullOrEmpty(listen.Description));

        Choose(form, "local");
        Assert.Equal(localTarget, target.Label);
        Assert.Equal(localListen, listen.Label);
    }

    [AvaloniaFact]
    public void Each_problem_is_shown_under_its_own_field_and_the_others_are_cleared()
    {
        var form = new TunnelFormView(null, () => 0);
        Type(form, "TargetHostInput", "db internal");
        Type(form, "TargetPortInput", "70000");
        Type(form, "ListenPortInput", "5173");

        form.ShowIssues(SshTunnelCommand.Validate(form.Spec));

        Assert.False(string.IsNullOrEmpty(Field(form, "TargetHostInput").ErrorMessage));
        Assert.False(string.IsNullOrEmpty(Field(form, "TargetPortInput").ErrorMessage));
        Assert.Null(Field(form, "ListenPortInput").ErrorMessage);
        Assert.Null(Field(form, "BindInput").ErrorMessage);

        form.ShowIssues([]);
        Assert.Null(Field(form, "TargetHostInput").ErrorMessage);
        Assert.Null(Field(form, "TargetPortInput").ErrorMessage);
    }

    [AvaloniaFact]
    public void A_port_that_is_not_a_number_is_not_a_port()
    {
        var form = new TunnelFormView(null, () => 0);
        Type(form, "TargetPortInput", "80a");
        Type(form, "ListenPortInput", "-5");

        Assert.Equal(0, form.Spec.TargetPort);
        Assert.Equal(0, form.Spec.ListenPort);
    }
}
