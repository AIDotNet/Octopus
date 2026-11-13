using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Octopus.App.Explorer;
using TKCode.Arc;
using Xunit;

namespace Octopus.App.Tests.Explorer;

/// <summary>The Files/Git switch of the right panel is an <c>arc:ToggleGroup</c>: Git is an option only inside a repository.</summary>
public class ExplorerTabsTests
{
    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(condition(), "the condition did not become true in time");
    }

    [AvaloniaFact]
    public async Task The_git_option_exists_only_in_a_repository_and_follows_the_selected_tab()
    {
        if (ExplorerFixture.FindGit() is null) return;
        using var plain = new ExplorerFixture();
        plain.Write("a.txt", "x");
        using var repo = new ExplorerFixture();
        repo.InitRepository();
        repo.Write("a.txt", "x");

        var view = new ExplorerPanelView();
        var window = new Window { Width = 320, Height = 400, Content = view };
        window.Show();
        Frames.Pump();
        var group = view.GetVisualDescendants().OfType<ToggleGroup>().First();

        view.ProjectPath = plain.Root;
        await WaitUntil(() => view.Model.HasProject && !view.Model.IsGitRepository);
        Frames.Pump();
        // A folder on this computer always has the sessions tab.
        Assert.Equal(["files", "sessions"], Values(group));
        Assert.Equal("files", group.SelectedValue);

        view.ProjectPath = repo.Root;
        await WaitUntil(() => view.Model.IsGitRepository);
        Frames.Pump();
        Assert.Equal(["files", "git", "sessions"], Values(group));

        // Choosing the Git option shows the Git tab; the model changing tab moves the option.
        group.SelectedValue = "git";
        Frames.Pump();
        Assert.True(view.Model.IsGitTab);
        view.Model.ShowFilesCommand.Execute(null);
        Frames.Pump();
        Assert.Equal("files", group.SelectedValue);

        // Leaving the repository removes the option.
        view.ProjectPath = plain.Root;
        await WaitUntil(() => !view.Model.IsGitRepository);
        Frames.Pump();
        Assert.Equal(["files", "sessions"], Values(group));
        window.Close();
        view.Model.Dispose();
    }

    private static List<string?> Values(ToggleGroup group) => [.. group.Items.OfType<ToggleGroupOption>().Select(o => o.Value as string)];
}
