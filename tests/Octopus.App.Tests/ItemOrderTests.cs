using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Octopus.App.Composition;
using Octopus.App.Services;
using Octopus.App.Shell;
using Octopus.App.Sidebar;
using Octopus.Core.Projects;
using TKCode.Arc;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>The project panel shows terminals and agents in one list, and the order the user drags them into is the saved one.</summary>
public class ItemOrderTests
{
    [AvaloniaFact, FreshApp]
    public void Terminals_and_agents_share_one_list_that_keeps_the_dragged_order()
    {
        var dir = Directory.CreateTempSubdirectory("octopus-order-").FullName;
        var window = new MainWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var projects = AppHost.Get<IProjectService>();
            var project = projects.AddProject(dir);
            ProjectItem Add(ProjectItemKind kind, string title)
            {
                var item = new ProjectItem
                {
                    Kind = kind, Title = title,
                    AgentId = kind == ProjectItemKind.Agent ? "claude" : null,
                    FilePath = kind == ProjectItemKind.File ? Path.Combine(dir, title) : null,
                };
                projects.AddItem(project, item);
                return item;
            }
            var zsh = Add(ProjectItemKind.Terminal, "zsh");
            var claude = Add(ProjectItemKind.Agent, "Claude");
            var file = Add(ProjectItemKind.File, "a.txt");
            var bash = Add(ProjectItemKind.Terminal, "bash");
            Frames.Pump();

            var panel = AppHost.Get<ShellViewModel>().Panel;
            Assert.Equal(["zsh", "Claude", "bash"], panel.Items.Select(r => r.Title).ToArray());

            // Drag "bash" to the top: the list moves the row itself and reports it.
            var list = window.GetVisualDescendants().OfType<ReorderableList>().Single();
            list.MoveItem(2, 0);
            Frames.Pump();
            Assert.Equal(["bash", "zsh", "Claude"], panel.Items.Select(r => r.Title).ToArray());
            Assert.Equal([bash.Id, zsh.Id, claude.Id, file.Id], project.Items.Select(i => i.Id).ToArray());

            // And down again, one place.
            list.MoveItem(0, 1);
            Frames.Pump();
            Assert.Equal([zsh.Id, bash.Id, claude.Id, file.Id], project.Items.Select(i => i.Id).ToArray());

            // The saved order is what a rebuild shows.
            projects.RenameItem(claude.Id, "Reviewer");
            Frames.Pump();
            Assert.Equal(["zsh", "bash", "Reviewer"], panel.Items.Select(r => r.Title).ToArray());
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
