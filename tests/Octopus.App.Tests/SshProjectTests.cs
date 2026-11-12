using Avalonia.Headless.XUnit;
using Octopus.App.Common;
using Octopus.App.Composition;
using Octopus.App.Launcher;
using Octopus.App.Services;
using Octopus.App.Sidebar;
using Octopus.Core.Projects;
using Octopus.Storage;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>A project on an SSH host: its terminals connect there, and nothing that needs this computer's files is offered.</summary>
public class SshProjectTests
{
    /// <summary>Adds a project on a host as the current one; disposing it removes it again, so other tests see their own project.</summary>
    private sealed class RemoteProjectScope(string projectId) : IDisposable
    {
        public void Dispose() => AppHost.Get<IProjectService>().RemoveProject(projectId);
    }

    private static (Project Project, RemoteProjectScope Scope) Remote(string path, string host = "prod")
    {
        var project = AppHost.Get<IProjectService>().AddProject(path, null, host);
        return (project, new RemoteProjectScope(project.Id));
    }

    [Fact]
    public async Task A_folder_on_a_host_is_kept_as_typed_and_is_a_project_of_its_own()
    {
        var projects = new ProjectService(new InMemoryProjectStore(), new PathComparer(ignoreCase: false), new InMemorySettingsStore());
        await projects.LoadAsync();

        var local = projects.AddProject("/srv/app");
        var remote = projects.AddProject("/srv/app/", null, "prod");

        Assert.NotSame(local, remote);
        Assert.Equal("/srv/app", remote.Path);
        Assert.Equal("prod", remote.SshHost);
        Assert.Equal("app", remote.Name);
        Assert.Null(local.SshHost);
        Assert.Same(remote, projects.AddProject("/srv/app", null, "prod"));
        Assert.Equal(2, projects.Projects.Count);
    }

    [AvaloniaFact]
    public void A_terminal_in_an_ssh_project_runs_ssh_in_the_remote_folder()
    {
        var project = new Project { Path = "/srv/my app", SshHost = "prod" };
        var item = new ProjectItem { Kind = ProjectItemKind.Terminal, Title = "prod" };

        var spec = AppHost.Get<IItemLaunchService>().Plan(project, item);

        var line = string.Join(' ', spec.Arguments);
        Assert.Contains("ssh", line);
        Assert.Contains("prod", line);
        Assert.Contains("/srv/my app", line);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), spec.WorkingDirectory);
    }

    [AvaloniaFact]
    public void Agents_do_not_start_in_an_ssh_project()
    {
        var (project, scope) = Remote("/srv/agents");
        using (scope)
        {
            var opener = AppHost.Get<IItemOpener>();
            Assert.Null(opener.OpenAgent("claude"));
            Assert.Null(opener.OpenDefaultAgent());
            var agent = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Claude Code" };
            Assert.Throws<InvalidOperationException>(() => AppHost.Get<IItemLaunchService>().Plan(project, agent));
        }
    }

    [AvaloniaFact]
    public void A_terminal_on_a_host_is_titled_with_the_host()
    {
        var (_, scope) = Remote("/srv/title");
        using (scope)
        {
            var item = AppHost.Get<IItemOpener>().OpenTerminal();
            Assert.Equal("prod", item!.Title);
        }
    }

    [AvaloniaFact]
    public void The_launcher_offers_no_agents_in_an_ssh_project()
    {
        var (_, scope) = Remote("/srv/launcher");
        using (scope)
        {
            var launcher = AppHost.Get<LauncherViewModel>();
            Frames.Pump();

            Assert.True(launcher.IsRemoteProject);
            Assert.True(launcher.ShowRemoteNote);
            Assert.Empty(launcher.Agents);
            Assert.False(launcher.ShowNoAgents);
        }
    }

    [AvaloniaFact]
    public void A_project_on_a_host_has_nothing_to_reveal_on_this_computer()
    {
        var remote = new ProjectChipViewModel(new Project { Name = "web", Path = "/srv/web", SshHost = "prod" }, false, _ => { }, _ => { }, _ => { }, _ => Task.CompletedTask);
        var local = new ProjectChipViewModel(new Project { Name = "app", Path = "/code/app" }, false, _ => { }, _ => { }, _ => { }, _ => Task.CompletedTask);

        Assert.True(remote.IsRemote);
        Assert.False(remote.RevealCommand.CanExecute(null));
        Assert.Equal("web\nprod:/srv/web", remote.Tip);
        Assert.False(local.IsRemote);
        Assert.True(local.RevealCommand.CanExecute(null));
        Assert.Equal("app\n/code/app", local.Tip);
    }
}
