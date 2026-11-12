using Octopus.App.Common;
using Octopus.App.Services;
using Octopus.Core.Projects;
using Octopus.Storage;
using Xunit;

namespace Octopus.App.Tests;

public class ProjectServiceTests
{
    private static (ProjectService Service, InMemoryProjectStore Store) Create()
    {
        var store = new InMemoryProjectStore();
        var service = new ProjectService(store, new PathComparer(ignoreCase: false));
        service.LoadAsync().GetAwaiter().GetResult();
        return (service, store);
    }

    [Fact]
    public void Adding_a_directory_twice_returns_the_same_project()
    {
        var (service, _) = Create();
        var a = service.AddProject("/tmp/octopus-a");
        var b = service.AddProject("/tmp/octopus-a/");
        Assert.Same(a, b);
        Assert.Single(service.Projects);
        Assert.Equal("octopus-a", a.Name);
    }

    [Fact]
    public void New_projects_take_palette_colors_in_turn_and_become_current()
    {
        var (service, _) = Create();
        var first = service.AddProject("/tmp/p1");
        var second = service.AddProject("/tmp/p2");
        Assert.Equal(ProjectService.Palette[0], first.Color);
        Assert.Equal(ProjectService.Palette[1], second.Color);
        Assert.Same(second, service.Current);
        service.SetCurrent(first.Id);
        Assert.Same(first, service.Current);
    }

    [Fact]
    public void Rename_recolor_and_remove_a_project()
    {
        var (service, _) = Create();
        var a = service.AddProject("/tmp/p1");
        var b = service.AddProject("/tmp/p2");
        service.RenameProject(a.Id, "  Alpha ");
        service.SetProjectColor(a.Id, "#112233");
        Assert.Equal("Alpha", a.Name);
        Assert.Equal("#112233", a.Color);

        service.RemoveProject(b.Id);
        Assert.Single(service.Projects);
        Assert.Same(a, service.Current);
        service.RemoveProject(a.Id);
        Assert.Null(service.Current);
    }

    [Fact]
    public void Items_are_added_activated_renamed_and_removed()
    {
        var (service, _) = Create();
        var project = service.AddProject("/tmp/p1");
        var terminal = new ProjectItem { Kind = ProjectItemKind.Terminal, Title = "zsh" };
        var file = new ProjectItem { Kind = ProjectItemKind.File, Title = "README.md", FilePath = "/tmp/p1/README.md" };
        service.AddItem(project, terminal);
        service.AddItem(project, file);
        Assert.Equal(file.Id, project.ActiveItemId);

        service.ActivateItem(terminal.Id);
        Assert.Equal(terminal.Id, project.ActiveItemId);

        service.RenameItem(terminal.Id, "build");
        Assert.Equal("build", terminal.DisplayTitle);
        service.RenameItem(terminal.Id, "");
        Assert.Equal("zsh", terminal.DisplayTitle);

        service.RemoveItem(terminal.Id);
        Assert.Single(project.Items);
        Assert.Equal(file.Id, project.ActiveItemId);
        Assert.Same(project, service.FindProjectOf(file.Id));
    }

    [Fact]
    public void Changes_raise_events_of_the_right_kind()
    {
        var (service, _) = Create();
        var kinds = new List<ProjectChangeKind>();
        service.Changed += (_, e) => kinds.Add(e.Kind);
        var project = service.AddProject("/tmp/p1");
        service.AddItem(project, new ProjectItem { Kind = ProjectItemKind.Terminal, Title = "zsh" });
        service.SetDockLayout(project, "{}");
        Assert.Contains(ProjectChangeKind.Projects, kinds);
        Assert.Contains(ProjectChangeKind.Items, kinds);
        Assert.Contains(ProjectChangeKind.Layout, kinds);
    }

    [Fact]
    public async Task Flush_writes_the_document_at_once_and_the_dock_layout_is_kept()
    {
        var (service, store) = Create();
        var project = service.AddProject("/tmp/p1");
        service.SetDockLayout(project, "{\"v\":1}");
        await service.FlushAsync();
        var saved = await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(project.Id, Assert.Single(saved.Projects).Id);
        Assert.Equal("{\"v\":1}", saved.Projects[0].DockLayout);
        Assert.Equal(project.Id, saved.ActiveProjectId);
    }

    [Fact]
    public async Task Items_loaded_from_the_file_count_as_restored()
    {
        var item = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Claude" };
        var document = new ProjectsDocument { Projects = [new Project { Name = "p", Path = "/tmp/p", Items = [item] }] };
        var service = new ProjectService(new InMemoryProjectStore(document), new PathComparer(ignoreCase: false));
        await service.LoadAsync();
        Assert.True(service.WasRestored(item.Id));
        var created = new ProjectItem { Kind = ProjectItemKind.Terminal, Title = "zsh" };
        service.AddItem(service.Projects[0], created);
        Assert.False(service.WasRestored(created.Id));
    }
}
