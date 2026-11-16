using Octopus.Core.Projects;
using Xunit;

namespace Octopus.Storage.Tests;

/// <summary>What follows from where a project's folder is.</summary>
public class ProjectLocationTests
{
    [Fact]
    public void A_folder_of_this_computer_runs_agents_and_is_written_as_its_path()
    {
        var project = new Project { Name = "Octopus", Path = "/Users/me/code/Octopus" };

        Assert.Equal(ProjectLocationKind.Local, ProjectLocation.KindOf(project));
        Assert.True(ProjectLocation.IsOnThisComputer(project));
        Assert.True(ProjectLocation.CanRunAgents(project));
        Assert.Equal("/Users/me/code/Octopus", ProjectLocation.Describe(project));
    }

    [Fact]
    public void A_folder_on_another_computer_running_octopus_runs_agents_there()
    {
        var project = new Project { Name = "api", Path = "/home/me/api", RemoteHost = "mksklhccvpcruryrp4hprnugry", RemoteId = "abc" };

        Assert.Equal(ProjectLocationKind.Remote, ProjectLocation.KindOf(project));
        Assert.False(ProjectLocation.IsOnThisComputer(project));
        Assert.True(ProjectLocation.IsOnRemoteHost(project));
        Assert.True(ProjectLocation.CanRunAgents(project));
        Assert.Equal("Studio:/home/me/api", ProjectLocation.Describe(project, "Studio"));
        Assert.Equal("mksklhccvpcruryrp4hprnugry:/home/me/api", ProjectLocation.Describe(project));
    }

    [Fact]
    public void A_folder_on_an_ssh_host_runs_no_agents_and_is_written_with_its_host()
    {
        var project = new Project { Name = "app", Path = "~/code/app", SshHost = "build-box" };

        Assert.Equal(ProjectLocationKind.Ssh, ProjectLocation.KindOf(project));
        Assert.False(ProjectLocation.IsOnThisComputer(project));
        Assert.False(ProjectLocation.CanRunAgents(project));
        Assert.Equal("build-box:~/code/app", ProjectLocation.Describe(project));
    }
}
