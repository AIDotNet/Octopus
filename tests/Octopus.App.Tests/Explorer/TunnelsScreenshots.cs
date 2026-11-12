using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Octopus.App.Composition;
using Octopus.App.Dialogs;
using Octopus.App.Explorer;
using Octopus.App.Services;
using Octopus.Core.Projects;
using Octopus.Ssh;
using TKCode.Arc;
using Xunit;

namespace Octopus.App.Tests.Explorer;

/// <summary>
/// Renders the tunnels tab (320 x 560) with Skia on the headless platform and saves PNGs to <c>tests/Octopus.App.Tests/bin/shots</c>
/// for a person to look at: tunnels in each state, light and dark, and the empty tab.
/// </summary>
public class TunnelsScreenshots
{
    private static readonly string s_dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "shots"));

    private static TunnelSpec Spec(string? name, TunnelKind kind, int listen, string target, int targetPort, string bind = "127.0.0.1") => new()
    {
        Name = name, Kind = kind, ListenPort = listen, TargetHost = target, TargetPort = targetPort, BindAddress = bind,
    };

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
            Frames.Pump();
        }
        Assert.True(condition(), "the condition did not become true in time: " + what);
    }

    [AvaloniaFact]
    public async Task Render_the_tunnels_tab()
    {
        Directory.CreateDirectory(s_dir);
        var host = new FakeProcessHost();
        var ports = new FakeLocalPorts();
        var restore = FakeSshContainer.Use(host, ports);
        try
        {
            var projects = AppHost.Get<IProjectService>();
            var tunnels = AppHost.Get<ITunnelService>();
            tunnels.Initialize();
            var project = projects.AddProject("/srv/app", "prod-web", "prod");

            var frontend = Spec("Frontend dev server", TunnelKind.Local, 5173, "localhost", 5173);
            var database = Spec("PostgreSQL", TunnelKind.Local, 15432, "db.internal", 5432);
            var callback = Spec(null, TunnelKind.Remote, 9000, "localhost", 3000);
            var cache = Spec("Redis", TunnelKind.Local, 6379, "localhost", 6379);
            var staging = Spec("Staging API", TunnelKind.Local, 8080, "10.0.3.7", 80);
            var shared = Spec(null, TunnelKind.Local, 3000, "localhost", 3000, bind: "0.0.0.0");
            foreach (var spec in new[] { frontend, database, callback, cache, staging, shared }) projects.AddTunnel(project, spec);

            foreach (var dark in new[] { false, true })
            {
                ArcTheme.Apply(t => t.Appearance(dark ? ArcAppearance.Dark : ArcAppearance.Light));
                var suffix = dark ? "dark" : "light";
                tunnels.StopAll();

                var view = new ExplorerPanelView();
                var window = new Window { Width = 320, Height = 560, Content = view };
                window.Show();
                Frames.Pump();
                view.SetProject("/srv/app", "prod-web", "prod");
                await WaitUntil(() => view.Model.IsSshProject, "the project on the host");
                view.Model.ShowTunnelsCommand.Execute(null);
                await WaitUntil(() => view.Model.Tunnels.Rows.Count == 6, "the rows");

                // Running, and one with a connection through it that failed.
                ports.Taken.Clear();
                ports.Taken.Add(6379);
                var before = host.Started.Count;
                tunnels.Start(project, frontend);
                tunnels.Start(project, database);
                tunnels.Start(project, cache);
                tunnels.Start(project, staging);
                await WaitUntil(() => host.Started.Count == before + 3, "ssh runs");
                // ssh has set the forwards up once it has logged in.
                host.Started.Last(p => p.Line.Contains(":5173:")).Up();
                host.Started.Last(p => p.Line.Contains(":15432:")).Up();
                await WaitUntil(() => tunnels.RunOf(frontend.Id).Status.State == TunnelState.Running, "the frontend tunnel to run");
                await WaitUntil(() => tunnels.RunOf(database.Id).Status.State == TunnelState.Running, "the database tunnel to run");
                host.Started.Last(p => p.Line.Contains("15432")).Say("channel 3: open failed: connect failed: Connection refused");
                await WaitUntil(() => tunnels.RunOf(database.Id).Status.Warning != TunnelWarning.None, "the warning");
                await WaitUntil(() => tunnels.RunOf(cache.Id).Status.State == TunnelState.Failed, "the cache tunnel to fail");
                view.Model.Tunnels.Rows[3].ToggleLogCommand.Execute(null);
                await Task.Delay(700);
                Frames.Pump();
                Frames.Save(window, Path.Combine(s_dir, $"tunnels-{suffix}.png"));
                window.Close();
                view.Model.Dispose();
            }

            // A project on a host with no tunnels yet.
            tunnels.StopAll();
            foreach (var spec in project.Tunnels.ToList()) projects.RemoveTunnel(project, spec.Id);
            var emptyView = new ExplorerPanelView();
            var emptyWindow = new Window { Width = 320, Height = 560, Content = emptyView };
            emptyWindow.Show();
            Frames.Pump();
            emptyView.SetProject("/srv/app", "prod-web", "prod");
            await WaitUntil(() => emptyView.Model.IsSshProject, "the project on the host");
            emptyView.Model.ShowTunnelsCommand.Execute(null);
            await Task.Delay(700);
            Frames.Pump();
            Frames.Save(emptyWindow, Path.Combine(s_dir, "tunnels-empty.png"));
            emptyWindow.Close();
            emptyView.Model.Dispose();
        }
        finally
        {
            restore();
        }
    }

    [AvaloniaFact]
    public async Task Render_the_tunnel_form()
    {
        Directory.CreateDirectory(s_dir);
        foreach (var dark in new[] { false, true })
        {
            ArcTheme.Apply(t => t.Appearance(dark ? ArcAppearance.Dark : ArcAppearance.Light));
            var suffix = dark ? "dark" : "light";

            // A new tunnel, with what is wrong with it shown under the fields.
            var form = new TunnelFormView(new TunnelSpec { Kind = TunnelKind.Local, TargetHost = "db internal", TargetPort = 5432, BindAddress = "0.0.0.0" }, () => 54321);
            form.ShowIssues(SshTunnelCommand.Validate(form.Spec));
            var window = new Window { Width = 460, Height = 700, Content = new Border { Padding = new Avalonia.Thickness(24), Child = form } };
            window.Show();
            Frames.Pump();
            await Task.Delay(500);
            Frames.Pump();
            Frames.Save(window, Path.Combine(s_dir, $"tunnel-form-local-{suffix}.png"));
            window.Close();

            // A remote one being edited.
            var remote = new TunnelFormView(new TunnelSpec { Kind = TunnelKind.Remote, Name = "Callback", ListenPort = 9000, TargetPort = 3000, AutoStart = true }, () => 54321);
            var second = new Window { Width = 460, Height = 700, Content = new Border { Padding = new Avalonia.Thickness(24), Child = remote } };
            second.Show();
            Frames.Pump();
            await Task.Delay(500);
            Frames.Pump();
            Frames.Save(second, Path.Combine(s_dir, $"tunnel-form-remote-{suffix}.png"));
            second.Close();
        }
    }

    [AvaloniaFact]
    public async Task Render_the_tunnels_of_a_host_that_needs_a_password()
    {
        Directory.CreateDirectory(s_dir);
        var host = new FakeProcessHost();
        var ports = new FakeLocalPorts();
        var shared = new FakeSharedConnection();
        var restore = FakeSshContainer.Use(host, ports, shared);
        try
        {
            var projects = AppHost.Get<IProjectService>();
            var tunnels = AppHost.Get<ITunnelService>();
            tunnels.Initialize();
            var project = projects.AddProject("/srv/db", "legacy-db", "legacy");
            var postgres = Spec("PostgreSQL", TunnelKind.Local, 15432, "localhost", 5432);
            var admin = Spec("Admin console", TunnelKind.Local, 8443, "10.1.0.4", 443);
            var grafana = Spec(null, TunnelKind.Local, 3000, "localhost", 3000);
            foreach (var spec in new[] { postgres, admin, grafana }) projects.AddTunnel(project, spec);
            ArcTheme.Apply(t => t.Appearance(ArcAppearance.Light));

            var view = new ExplorerPanelView();
            var window = new Window { Width = 320, Height = 420, Content = view };
            window.Show();
            Frames.Pump();
            view.SetProject("/srv/db", "legacy-db", "legacy");
            await WaitUntil(() => view.Model.IsSshProject, "the project on the host");
            view.Model.ShowTunnelsCommand.Execute(null);
            await WaitUntil(() => view.Model.Tunnels.Rows.Count == 3, "the rows");

            // The host refuses the tunnels' own login: they wait for a terminal.
            tunnels.Start(project, postgres);
            await WaitUntil(() => host.Started.Count == 1, "the first ssh");
            host.Started[0].Say("root@legacy: Permission denied (publickey,password).");
            host.Started[0].Exit(255);
            // Once the host is known to need a terminal's connection, the next tunnels do not try a login of their own.
            await WaitUntil(() => tunnels.RunOf(postgres.Id).Status.State == TunnelState.Waiting, "the first tunnel to wait");
            tunnels.Start(project, admin);
            tunnels.Start(project, grafana);
            await WaitUntil(() => tunnels.RunOf(admin.Id).Status.State == TunnelState.Waiting && tunnels.RunOf(grafana.Id).Status.State == TunnelState.Waiting, "waiting");
            await Task.Delay(700);
            Frames.Pump();
            Frames.Save(window, Path.Combine(s_dir, "tunnels-waiting-light.png"));

            // A terminal signs in; one of the ports is taken by something else.
            ports.Taken.Add(3000);
            shared.ForwardError = null;
            shared.MasterUp = true;
            await WaitUntil(() => tunnels.RunOf(postgres.Id).Status.State == TunnelState.Running && tunnels.RunOf(admin.Id).Status.State == TunnelState.Running, "running");
            shared.ForwardError = "mux_client_forward: forwarding request failed: Port forwarding failed";
            tunnels.Start(project, grafana);
            await WaitUntil(() => tunnels.RunOf(grafana.Id).Status.State == TunnelState.Failed, "the failure");
            await Task.Delay(700);
            Frames.Pump();
            Frames.Save(window, Path.Combine(s_dir, "tunnels-shared-light.png"));
            window.Close();
            view.Model.Dispose();
        }
        finally
        {
            restore();
        }
    }
}
