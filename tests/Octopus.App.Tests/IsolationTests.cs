using Octopus.App.Composition;
using Octopus.App.Services;
using Octopus.Storage;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>
/// Every test starts on a new container (<see cref="FreshAppAttribute"/>). Both tests leave something behind and expect to
/// find nothing of the other, whichever runs first.
/// </summary>
public class IsolationTests
{
    [Fact]
    public void The_first_test_finds_an_empty_world_and_leaves_state_behind() => FindNothingThenLeaveState();

    [Fact]
    public void The_second_test_finds_an_empty_world_too() => FindNothingThenLeaveState();

    private static void FindNothingThenLeaveState()
    {
        var settings = AppHost.Get<ISettingsStore>();
        var projects = AppHost.Get<IProjectService>();
        Assert.Empty(projects.Projects);
        Assert.False(settings.Current.Sidebar.IsCollapsed);
        Assert.Equal(0, AppHost.Get<RecordingProjectStore>().Saves);

        settings.Update(s => s.Sidebar.IsCollapsed = true);
        projects.AddProject(Directory.CreateTempSubdirectory("octopus-iso-").FullName);
        Assert.Single(projects.Projects);
    }

    [Fact]
    public void Resetting_the_host_twice_is_safe()
    {
        AppHost.Get<ISettingsStore>();
        AppHost.Reset();
        AppHost.Reset();
        Assert.Empty(AppHost.Get<IProjectService>().Projects);
    }
}
