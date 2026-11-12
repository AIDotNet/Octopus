using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Octopus.App.Common;
using Octopus.App.Composition;
using Octopus.App.Explorer;
using Octopus.App.Explorer.Tunnels;
using Octopus.App.Services;
using Octopus.Core.Projects;
using Octopus.Platform;
using Octopus.Storage;
using TKCode.Arc;
using Xunit;

namespace Octopus.App.Tests.Explorer;

/// <summary>The tunnels tab: the rows of an SSH project, their switches and actions, and the tab itself (design/11-SSH隧道.md §7).</summary>
public class TunnelsPanelTests : IDisposable
{
    private sealed class RecordingOpener : IFileOpener
    {
        public List<string> Urls { get; } = [];

        public void Open(string path)
        {
        }

        public void Reveal(string path)
        {
        }

        public bool OpenUrl(string url)
        {
            Urls.Add(url);
            return true;
        }
    }

    private static readonly TunnelTimings Quick = new()
    {
        PollInterval = TimeSpan.FromMilliseconds(5),
        ReadyTimeout = TimeSpan.FromSeconds(5),
        RetryDelays = [TimeSpan.FromMilliseconds(20)],
        PortGrace = TimeSpan.FromMilliseconds(40),
    };

    private readonly FakeProcessHost _host = new();
    private readonly FakeLocalPorts _ports = new();
    private readonly ProjectService _projects = new(new InMemoryProjectStore(), new PathComparer(ignoreCase: false), new InMemorySettingsStore());
    private readonly FakeDialogService _dialogs = new();
    private readonly RecordingOpener _opener = new();
    private readonly string _ledger = Directory.CreateTempSubdirectory("octopus-ledger-").FullName;
    private readonly TunnelService _service;
    private readonly TunnelsPanelViewModel _panel;

    public TunnelsPanelTests()
    {
        _service = new TunnelService(_host, _ports, _projects, _ledger, Quick);
        // As at the start of the app: the service follows the projects (an edited tunnel runs again, a removed one stops).
        _service.Initialize();
        _panel = new TunnelsPanelViewModel(_projects, _service, _dialogs, _opener, new WindowAccessor());
    }

    public void Dispose()
    {
        _panel.Dispose();
        _service.Dispose();
        try { Directory.Delete(_ledger, recursive: true); }
        catch (IOException) { }
    }

    private Project Remote(string path = "/srv/app", string host = "prod") => _projects.AddProject(path, null, host);

    private static TunnelSpec Local(int port = 5173) => new() { Kind = TunnelKind.Local, ListenPort = port, TargetPort = port };

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return;
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for " + what);
            await Task.Delay(5);
        }
    }

    [AvaloniaFact]
    public void A_project_on_this_computer_has_no_tunnels()
    {
        _projects.AddProject(Directory.CreateTempSubdirectory("octopus-local-").FullName);

        Assert.Null(_panel.Host);
        Assert.Empty(_panel.Rows);
        Assert.True(_panel.IsEmpty);
    }

    [AvaloniaFact]
    public void The_rows_are_the_tunnels_of_the_current_project_on_an_ssh_host()
    {
        var project = Remote();
        _projects.AddTunnel(project, Local(5173));
        _projects.AddTunnel(project, new TunnelSpec { Kind = TunnelKind.Remote, ListenPort = 9000, TargetPort = 3000, Name = "Callback" });

        Assert.Equal("prod", _panel.Host);
        Assert.Equal(2, _panel.Rows.Count);
        Assert.True(_panel.HasRows);
        Assert.Contains("5173", _panel.Rows[0].Title);
        Assert.Equal("Callback", _panel.Rows[1].Title);
        Assert.Contains("9000", _panel.Rows[1].Route);
        Assert.All(_panel.Rows, row => Assert.False(row.IsOn));
    }

    [AvaloniaFact]
    public void Another_project_has_other_rows()
    {
        var first = Remote("/srv/a");
        var second = Remote("/srv/b");
        _projects.AddTunnel(first, Local(5173));
        // The project that was added last is the current one.
        Assert.Empty(_panel.Rows);

        _projects.SetCurrent(first.Id);
        Assert.Single(_panel.Rows);
        _projects.SetCurrent(second.Id);

        Assert.Empty(_panel.Rows);
        Assert.True(_panel.IsEmpty);
    }

    [AvaloniaFact]
    public async Task Adding_a_tunnel_keeps_it_in_the_project_and_starts_it()
    {
        var project = Remote();
        var spec = Local();
        _dialogs.NewTunnel = spec;

        await _panel.AddAsync();

        Assert.Equal("prod", _dialogs.LastTunnelHost);
        Assert.Null(_dialogs.LastTunnelAsked);
        Assert.Same(spec, Assert.Single(project.Tunnels));
        var row = Assert.Single(_panel.Rows);
        Assert.True(row.IsOn);
        await Eventually(() => _host.Started.Count == 1, "ssh to start");
        Assert.Contains("-L 127.0.0.1:5173:localhost:5173 -- prod", _host.Started[0].Line);
    }

    [AvaloniaFact]
    public async Task A_dismissed_dialog_adds_nothing()
    {
        var project = Remote();
        _dialogs.NewTunnel = null;

        await _panel.AddAsync();

        Assert.Empty(project.Tunnels);
        Assert.Empty(_host.Started);
    }

    [AvaloniaFact]
    public async Task The_switch_starts_and_stops_the_tunnel()
    {
        var project = Remote();
        _projects.AddTunnel(project, Local());
        var row = _panel.Rows[0];

        row.IsOn = true;
        await Eventually(() => _host.Started.Count == 1, "ssh to start");
        Assert.True(row.IsOn);

        row.IsOn = false;
        Assert.False(row.IsOn);
        Assert.True(_host.Started[0].Killed);

        row.IsOn = true;
        await Eventually(() => _host.Started.Count == 2, "ssh to start again");
    }

    [AvaloniaFact]
    public async Task A_running_tunnel_is_shown_as_running_and_counted()
    {
        var project = Remote();
        _projects.AddTunnel(project, Local());
        _projects.AddTunnel(project, Local(5174));
        var row = _panel.Rows[0];
        Assert.Equal(0, _panel.RunningCount);

        row.IsOn = true;
        await Eventually(() => row.IsBusy, "the connecting state");
        Assert.False(row.IsRunning);
        await Eventually(() => _host.Started.Count == 1, "ssh to start");
        _host.Started[0].Up();

        await Eventually(() => row.IsRunning, "the tunnel to run");
        await Eventually(() => _panel.RunningCount == 1, "the count");
        Assert.False(row.IsBusy);
        Assert.False(_panel.Rows[1].IsRunning);
    }

    [AvaloniaFact]
    public async Task A_tunnel_that_failed_says_why_in_its_row_and_its_switch_goes_off()
    {
        var project = Remote();
        _projects.AddTunnel(project, Local());
        _ports.Taken.Add(5173);
        var row = _panel.Rows[0];

        row.IsOn = true;

        await Eventually(() => row.IsFailed, "the tunnel to fail");
        Assert.True(row.IsProblem);
        Assert.True(row.HasMessage);
        Assert.Contains("5173", row.Message);
        Assert.False(row.IsOn);
    }

    [AvaloniaFact]
    public async Task A_connection_that_fails_through_a_running_tunnel_is_a_warning_in_its_row()
    {
        var project = Remote();
        _projects.AddTunnel(project, Local());
        var row = _panel.Rows[0];
        row.IsOn = true;
        await Eventually(() => _host.Started.Count == 1, "ssh to start");
        _host.Started[0].Up();
        await Eventually(() => row.IsRunning, "the tunnel to run");

        _host.Started[0].Say("channel 2: open failed: connect failed: Connection refused");

        await Eventually(() => row.IsWarning, "the warning");
        Assert.True(row.HasMessage);
        Assert.False(row.IsProblem);
        Assert.True(row.IsRunning);
    }

    [AvaloniaFact]
    public async Task Deleting_asks_first_and_then_stops_and_removes_the_tunnel()
    {
        var project = Remote();
        _projects.AddTunnel(project, Local());
        _panel.Rows[0].IsOn = true;
        await Eventually(() => _host.Started.Count == 1, "ssh to start");

        _dialogs.Answer = false;
        await _panel.Rows[0].DeleteCommand.ExecuteAsync(null);
        Assert.Single(project.Tunnels);
        Assert.False(_host.Started[0].Killed);

        _dialogs.Answer = true;
        await _panel.Rows[0].DeleteCommand.ExecuteAsync(null);

        Assert.Empty(project.Tunnels);
        Assert.Empty(_panel.Rows);
        Assert.True(_host.Started[0].Killed);
        Assert.Equal(2, _dialogs.Asked);
    }

    [AvaloniaFact]
    public async Task Editing_a_running_tunnel_runs_it_again_with_the_edit()
    {
        var project = Remote();
        var spec = Local(5173);
        _projects.AddTunnel(project, spec);
        _panel.Rows[0].IsOn = true;
        await Eventually(() => _host.Started.Count == 1, "ssh to start");
        _host.Started[0].Up();
        await Eventually(() => _panel.Rows[0].IsRunning, "the tunnel to run");

        var edited = spec.Clone();
        edited.ListenPort = 7000;
        edited.TargetPort = 7000;
        _dialogs.NewTunnel = edited;
        await _panel.Rows[0].EditCommand.ExecuteAsync(null);

        // The dialog was given a copy with the same id, not the tunnel that runs.
        Assert.NotSame(spec, _dialogs.LastTunnelAsked);
        Assert.Equal(spec.Id, _dialogs.LastTunnelAsked?.Id);
        Assert.Same(edited, Assert.Single(project.Tunnels));
        await Eventually(() => _host.Started.Count == 2, "ssh to start again");
        Assert.True(_host.Started[0].Killed);
        Assert.Contains("-L 127.0.0.1:7000:localhost:7000", _host.Started[1].Line);
        Assert.Contains("7000", _panel.Rows[0].Route);
    }

    [AvaloniaFact]
    public async Task A_dismissed_edit_changes_nothing()
    {
        var project = Remote();
        var spec = Local();
        _projects.AddTunnel(project, spec);
        _dialogs.NewTunnel = null;

        await _panel.Rows[0].EditCommand.ExecuteAsync(null);

        Assert.Same(spec, Assert.Single(project.Tunnels));
    }

    [AvaloniaFact]
    public void A_local_tunnel_opens_its_address_in_the_browser_and_a_remote_one_cannot()
    {
        var project = Remote();
        _projects.AddTunnel(project, Local(5173));
        _projects.AddTunnel(project, new TunnelSpec { Kind = TunnelKind.Remote, ListenPort = 9000, TargetPort = 3000 });

        Assert.True(_panel.Rows[0].CanOpen);
        Assert.False(_panel.Rows[1].CanOpen);
        _panel.Rows[0].OpenCommand.Execute(null);

        Assert.Equal(["http://localhost:5173"], _opener.Urls);
        Assert.Equal("localhost:5173", _panel.Rows[0].Address);
        // A remote tunnel's port is on the host: that is where it is used.
        Assert.Equal("localhost:9000", _panel.Rows[1].Address);
    }

    [AvaloniaFact]
    public async Task Start_all_and_stop_all_act_on_every_row()
    {
        var project = Remote();
        _projects.AddTunnel(project, Local(5173));
        _projects.AddTunnel(project, Local(5174));

        _panel.StartAllCommand.Execute(null);
        await Eventually(() => _host.Started.Count == 2, "both to start");
        Assert.All(_panel.Rows, row => Assert.True(row.IsOn));

        _panel.StopAllCommand.Execute(null);

        Assert.All(_panel.Rows, row => Assert.False(row.IsOn));
        Assert.All(_host.Started, p => Assert.True(p.Killed));
    }

    [AvaloniaFact]
    public void The_log_of_a_row_opens_and_shows_what_ssh_said()
    {
        var project = Remote();
        _projects.AddTunnel(project, Local());
        var row = _panel.Rows[0];
        Assert.False(row.IsLogOpen);

        row.ToggleLogCommand.Execute(null);

        Assert.True(row.IsLogOpen);
        Assert.False(string.IsNullOrWhiteSpace(row.LogText));
    }

    // ---- a host that needs a password: the tunnel rides on the connection of a terminal ----

    private async Task<(TunnelsPanelViewModel Panel, TunnelService Service, FakeSharedConnection Shared, TunnelRowViewModel Row)> OnSharedConnection(string host = "legacy")
    {
        var shared = new FakeSharedConnection();
        var service = new TunnelService(_host, _ports, _projects, _ledger, new TunnelTimings
        {
            PollInterval = TimeSpan.FromMilliseconds(5),
            WaitPoll = TimeSpan.FromMilliseconds(15),
            AnchorSettle = TimeSpan.FromMilliseconds(40),
            MonitorPoll = TimeSpan.FromMilliseconds(30),
            RetryDelays = [TimeSpan.FromMilliseconds(20)],
            PortGrace = TimeSpan.FromMilliseconds(40),
        }, shared, shared);
        service.Initialize();
        var panel = new TunnelsPanelViewModel(_projects, service, _dialogs, _opener, new WindowAccessor());
        var project = _projects.AddProject("/srv/db", null, host);
        _projects.AddTunnel(project, Local(5432));
        var row = panel.Rows[0];
        row.IsOn = true;
        await Eventually(() => _host.Started.Count == 1, "the tunnel's own ssh");
        _host.Started[0].Say("root@legacy: Permission denied (publickey,password).");
        _host.Started[0].Exit(255);
        return (panel, service, shared, row);
    }

    [AvaloniaFact]
    public async Task A_tunnel_that_waits_for_a_terminal_says_which_host_to_sign_in_to_and_stays_on()
    {
        var (panel, service, _, row) = await OnSharedConnection();
        try
        {
            await Eventually(() => row.Status.State == TunnelState.Waiting, "the tunnel to wait");

            Assert.True(row.IsOn);
            Assert.True(row.IsBusy);
            Assert.False(row.IsRunning);
            Assert.False(row.IsProblem);
            Assert.True(row.HasMessage);
            Assert.Contains("legacy", row.Message);
        }
        finally
        {
            panel.Dispose();
            service.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task A_tunnel_on_the_terminals_connection_runs_and_says_so()
    {
        var (panel, service, shared, row) = await OnSharedConnection();
        try
        {
            shared.MasterUp = true;
            await Eventually(() => row.IsRunning, "the tunnel to run");

            Assert.True(row.Status.Shared);
            Assert.True(row.HasMessage);
            Assert.False(row.IsWarning);
            Assert.False(row.IsProblem);
            await Eventually(() => panel.RunningCount == 1, "the count");
        }
        finally
        {
            panel.Dispose();
            service.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task A_forward_that_the_terminals_connection_refuses_is_a_problem_in_the_row_with_ssh_words()
    {
        var (panel, service, shared, row) = await OnSharedConnection();
        try
        {
            shared.MasterUp = true;
            shared.ForwardError = "mux_client_forward: forwarding request failed: something unexpected";
            await Eventually(() => row.IsFailed, "the tunnel to fail");

            Assert.True(row.IsProblem);
            Assert.Contains("something unexpected", row.Message);
            Assert.False(row.IsOn);
        }
        finally
        {
            panel.Dispose();
            service.Dispose();
        }
    }

    // ---- the tab ----

    [AvaloniaFact]
    public async Task The_tunnels_option_exists_only_in_a_project_on_an_ssh_host()
    {
        var restore = FakeSshContainer.Use(_host, _ports);
        try
        {
            var view = new ExplorerPanelView();
            var window = new Window { Width = 320, Height = 400, Content = view };
            window.Show();
            Frames.Pump();
            var group = view.GetVisualDescendants().OfType<ToggleGroup>().First();
            var local = Directory.CreateTempSubdirectory("octopus-tabs-").FullName;

            view.SetProject(local, "plain", null);
            await Eventually(() => view.Model.HasProject, "the local project");
            Frames.Pump();
            Assert.False(view.Model.IsSshProject);
            Assert.Single(group.Items);

            view.SetProject("/srv/app", "app", "prod");
            await Eventually(() => view.Model.IsSshProject, "the project on the host");
            Frames.Pump();
            Assert.Equal(2, group.Items.Count);

            // Choosing the option shows the tab and keeps the choice; the model changing tab moves the option.
            group.SelectedValue = "tunnels";
            Frames.Pump();
            Assert.True(view.Model.IsTunnelsTab);
            Assert.False(view.Model.IsFilesTab);
            Assert.Equal("tunnels", AppHost.Get<ISettingsStore>().Current.RightPanel.Tab);
            view.Model.ShowFilesCommand.Execute(null);
            Frames.Pump();
            Assert.Equal("files", group.SelectedValue);

            // Back on this computer the option goes, and the tab that was chosen cannot show.
            view.Model.ShowTunnelsCommand.Execute(null);
            view.SetProject(local, "plain", null);
            await Eventually(() => !view.Model.IsSshProject, "the local project again");
            Frames.Pump();
            Assert.Single(group.Items);
            Assert.False(view.Model.IsTunnelsTab);
            Assert.True(view.Model.IsFilesTab);
            Assert.Equal("files", view.Model.TabKey);
            view.Model.Dispose();
            window.Close();
        }
        finally
        {
            restore();
        }
    }

    [AvaloniaFact]
    public async Task The_tunnels_tab_is_not_chosen_in_a_project_on_this_computer()
    {
        var restore = FakeSshContainer.Use(_host, _ports);
        try
        {
            var panel = AppHost.Get<ExplorerPanelViewModel>();
            await panel.SetProjectAsync(Directory.CreateTempSubdirectory("octopus-tabs-").FullName, "plain");

            panel.ShowTunnelsCommand.Execute(null);

            Assert.False(panel.IsTunnelsTab);
            Assert.True(panel.IsFilesTab);
            panel.Dispose();
        }
        finally
        {
            restore();
        }
    }
}
