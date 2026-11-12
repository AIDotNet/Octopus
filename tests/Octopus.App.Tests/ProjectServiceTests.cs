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
        var service = new ProjectService(store, new PathComparer(ignoreCase: false), new InMemorySettingsStore());
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
        var service = new ProjectService(new InMemoryProjectStore(document), new PathComparer(ignoreCase: false), new InMemorySettingsStore());
        await service.LoadAsync();
        Assert.True(service.WasRestored(item.Id));
        var created = new ProjectItem { Kind = ProjectItemKind.Terminal, Title = "zsh" };
        service.AddItem(service.Projects[0], created);
        Assert.False(service.WasRestored(created.Id));
    }

    [Fact]
    public void A_new_group_takes_its_projects_and_is_the_open_one()
    {
        var (service, _) = Create();
        var a = service.AddProject("/tmp/p1");
        var b = service.AddProject("/tmp/p2");
        var c = service.AddProject("/tmp/p3");
        var kinds = new List<ProjectChangeKind>();
        service.Changed += (_, e) => kinds.Add(e.Kind);

        var work = service.AddGroup("  Work ", [a.Id, c.Id]);

        Assert.Equal("Work", work.Name);
        Assert.Equal(ProjectService.Palette[0], work.Color);
        Assert.Equal(work.Id, a.GroupId);
        Assert.Null(b.GroupId);
        Assert.Equal(work.Id, c.GroupId);
        // Members go last, in the order given; projects in no group keep theirs.
        Assert.Equal(new[] { b, a, c }, service.Projects);
        Assert.Equal(work.Id, service.ExpandedGroupId);
        Assert.Equal(new[] { ProjectChangeKind.Groups }, kinds);
    }

    [Fact]
    public void Only_one_group_is_open_at_a_time()
    {
        var (service, _) = Create();
        var a = service.AddProject("/tmp/p1");
        var b = service.AddProject("/tmp/p2");
        var work = service.AddGroup("Work", [a.Id]);
        var oss = service.AddGroup("OSS", [b.Id]);
        Assert.Equal(oss.Id, service.ExpandedGroupId);

        service.SetExpandedGroup(work.Id);
        Assert.Equal(work.Id, service.ExpandedGroupId);
        service.SetExpandedGroup(null);
        Assert.Null(service.ExpandedGroupId);
        service.SetExpandedGroup("ghost");
        Assert.Null(service.ExpandedGroupId);
    }

    [Fact]
    public void Projects_move_between_groups_and_out_of_them()
    {
        var (service, _) = Create();
        var a = service.AddProject("/tmp/p1");
        var b = service.AddProject("/tmp/p2");
        var c = service.AddProject("/tmp/p3");
        var work = service.AddGroup("Work", [a.Id, b.Id]);
        var oss = service.AddGroup("OSS");

        service.MoveProjectToGroup(a.Id, oss.Id);
        Assert.Equal(oss.Id, a.GroupId);
        service.MoveProjectToGroup(b.Id, null);
        Assert.Null(b.GroupId);
        // Last among the projects in no group: after c.
        Assert.Equal(new[] { c, a, b }, service.Projects);

        var changes = 0;
        service.Changed += (_, _) => changes++;
        service.MoveProjectToGroup(a.Id, oss.Id);
        service.MoveProjectToGroup(a.Id, "ghost");
        Assert.Equal(0, changes);
        Assert.Equal(oss.Id, a.GroupId);
        Assert.Contains(work, service.Groups);
    }

    [Fact]
    public void A_project_moves_next_to_another_and_into_its_group()
    {
        var (service, _) = Create();
        var a = service.AddProject("/tmp/p1");
        var b = service.AddProject("/tmp/p2");
        var c = service.AddProject("/tmp/p3");
        var d = service.AddProject("/tmp/p4");
        var work = service.AddGroup("Work", [c.Id, d.Id]);
        var kinds = new List<ProjectChangeKind>();
        service.Changed += (_, e) => kinds.Add(e.Kind);

        // Within no group.
        service.MoveProject(b.Id, a.Id, after: false);
        Assert.Equal(new[] { b, a, c, d }, service.Projects);
        // Next to a project in a group: into that group, at that place.
        service.MoveProject(a.Id, d.Id, after: false);
        Assert.Equal(work.Id, a.GroupId);
        Assert.Equal(new[] { b, c, a, d }, service.Projects);
        // Next to a project in no group: out of the group.
        service.MoveProject(d.Id, b.Id, after: true);
        Assert.Null(d.GroupId);
        Assert.Equal(new[] { b, d, c, a }, service.Projects);
        Assert.Equal(3, kinds.Count);
        Assert.All(kinds, k => Assert.Equal(ProjectChangeKind.Groups, k));

        // Where it already is, on itself, or next to nothing: no change.
        service.MoveProject(d.Id, b.Id, after: true);
        service.MoveProject(d.Id, d.Id, after: true);
        service.MoveProject(d.Id, "ghost", after: true);
        Assert.Equal(3, kinds.Count);
    }

    [Fact]
    public void Groups_move_before_and_after_each_other()
    {
        var (service, _) = Create();
        var one = service.AddGroup("One");
        var two = service.AddGroup("Two");
        var three = service.AddGroup("Three");

        service.MoveGroup(three.Id, one.Id, after: false);
        Assert.Equal(new[] { three, one, two }, service.Groups);
        service.MoveGroup(three.Id, two.Id, after: true);
        Assert.Equal(new[] { one, two, three }, service.Groups);

        var changes = 0;
        service.Changed += (_, _) => changes++;
        service.MoveGroup(three.Id, two.Id, after: true);
        service.MoveGroup(three.Id, "ghost", after: true);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Opening_a_group_is_its_own_change()
    {
        var (service, _) = Create();
        var work = service.AddGroup("Work");
        service.SetExpandedGroup(null);
        var kinds = new List<ProjectChangeKind>();
        service.Changed += (_, e) => kinds.Add(e.Kind);
        service.SetExpandedGroup(work.Id);
        Assert.Equal(new[] { ProjectChangeKind.GroupExpanded }, kinds);
    }

    [Fact]
    public void Removing_a_group_keeps_its_projects()
    {
        var (service, _) = Create();
        var a = service.AddProject("/tmp/p1");
        var b = service.AddProject("/tmp/p2");
        var work = service.AddGroup("Work", [a.Id, b.Id]);
        service.RenameGroup(work.Id, " Clients ");
        service.SetGroupColor(work.Id, "#14B8A6");
        Assert.Equal("Clients", work.Name);
        Assert.Equal("#14B8A6", work.Color);

        service.RemoveGroup(work.Id);

        Assert.Empty(service.Groups);
        Assert.Equal(2, service.Projects.Count);
        Assert.All(service.Projects, p => Assert.Null(p.GroupId));
        Assert.Null(service.ExpandedGroupId);
    }
}
