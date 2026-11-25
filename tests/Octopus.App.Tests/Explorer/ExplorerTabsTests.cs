using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Octopus.App.Explorer;
using TKCode.Arc;
using TKCode.Arc.Motion;
using TKCode.Arc.Primitives;
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

    [AvaloniaFact]
    public async Task Switching_tabs_glides_the_pill_and_animates_labels_smoothly_to_the_target()
    {
        if (ExplorerFixture.FindGit() is null) return;
        using var repo = new ExplorerFixture();
        repo.InitRepository();
        repo.Write("a.txt", "x");

        var view = new ExplorerPanelView();
        var window = new Window { Width = 320, Height = 400, Content = view };
        window.Show();
        Frames.Pump();

        view.ProjectPath = repo.Root;
        await WaitUntil(() => view.Model.IsGitRepository && view.Model.ChangeCount > 0);
        Frames.Pump();
        window.UpdateLayout();

        var group = view.GetVisualDescendants().OfType<ExplorerTabGroup>().Single();
        var indicator = group.GetVisualDescendants().OfType<GlidingIndicator>().Single();
        var labels = group.GetVisualDescendants().OfType<TabLabelReveal>().ToList();
        Assert.Equal(3, labels.Count);

        var startRect = indicator.CurrentRect;
        var filesRect0 = ContainerRect(group, indicator, 0);
        AssertRectClose(filesRect0, startRect, 1.0);
        Assert.True(labels[0].Bounds.Width > 10);
        Assert.Equal(0, labels[1].Bounds.Width, 0.5);

        // Click the Git tab (real pointer input): indicator glides rightward in steps while Files collapses and Git expands.
        var xs = new List<double>();
        var filesWidths = new List<double>();
        var gitWidths = new List<double>();
        ClickContainer(window, group, 1);
        Assert.True(Frames.Until(
            () => !labels.Any(l => l.IsRunning) && RectClose(indicator.CurrentRect, ContainerRect(group, indicator, 1), 0.5),
            () =>
            {
                xs.Add(indicator.CurrentRect.X);
                filesWidths.Add(labels[0].Bounds.Width);
                gitWidths.Add(labels[1].Bounds.Width);
            }));

        var gitFinal = ContainerRect(group, indicator, 1);
        Assert.True(xs.Count >= 4, $"expected smooth glide frames, got {xs.Count}: {string.Join(", ", xs.Select(x => x.ToString("0.0")))}");
        Assert.Contains(xs, x => x > startRect.X + 1.0 && x < gitFinal.X - 1.0);
        Assert.Contains(filesWidths, w => w > 1.0 && w < labels[0].EnsureExpandedWidth() - 1.0);
        Assert.Contains(gitWidths, w => w > 1.0 && w < labels[1].EnsureExpandedWidth() - 1.0);
        AssertRectClose(gitFinal, indicator.CurrentRect, 0.8);
        Assert.Equal(0, labels[0].Bounds.Width, 0.5);
        Assert.True(labels[1].Bounds.Width > 10);

        // Switch Git -> Files via view model command: glides smoothly back to Files.
        xs.Clear();
        view.Model.ShowFilesCommand.Execute(null);
        Assert.True(Frames.Until(
            () => !labels.Any(l => l.IsRunning) && RectClose(indicator.CurrentRect, ContainerRect(group, indicator, 0), 0.5),
            () => xs.Add(indicator.CurrentRect.X)));

        var filesFinal = ContainerRect(group, indicator, 0);
        Assert.True(xs.Count >= 4, $"expected smooth reverse glide frames, got {xs.Count}");
        Assert.Contains(xs, x => x > filesFinal.X + 1.0 && x < gitFinal.X - 1.0);
        AssertRectClose(filesFinal, indicator.CurrentRect, 0.8);

        // Mid-flight redirect: Files -> Git, then click Sessions before settling.
        ClickContainer(window, group, 1);
        Assert.True(Frames.Until(() => indicator.CurrentRect.X > filesFinal.X + 4.0));
        var midX = indicator.CurrentRect.X;
        ClickContainer(window, group, 2);
        var redirectXs = new List<double> { indicator.CurrentRect.X };
        Assert.True(Frames.Until(
            () => !labels.Any(l => l.IsRunning) && RectClose(indicator.CurrentRect, ContainerRect(group, indicator, 2), 0.5),
            () => redirectXs.Add(indicator.CurrentRect.X)));
        var sessionsFinal = ContainerRect(group, indicator, 2);
        Assert.True(redirectXs[0] >= midX - 0.5 && redirectXs[0] < sessionsFinal.X - 10.0,
            $"pill jumped on mid-flight redirect from {midX:0.0} to {redirectXs[0]:0.0} (target {sessionsFinal.X:0.0})");
        Assert.Contains(redirectXs, x => x > redirectXs[0] + 1.0 && x < sessionsFinal.X - 1.0);
        AssertRectClose(sessionsFinal, indicator.CurrentRect, 0.8);

        // Motion off: jumps immediately without waiting for spring frames.
        ArcMotion.SetLevel(window, ArcMotionLevel.Off);
        group.SelectedValue = "files";
        Frames.Pump();
        window.UpdateLayout();
        Assert.All(labels, l => Assert.False(l.IsRunning));
        AssertRectClose(ContainerRect(group, indicator, 0), indicator.CurrentRect, 0.8);

        window.Close();
        view.Model.Dispose();
    }

    private static void ClickContainer(Window window, ToggleGroup group, int index)
    {
        var container = group.ContainerFromIndex(index)!;
        var center = container.TranslatePoint(new Point(container.Bounds.Width / 2, container.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
    }

    private static Rect ContainerRect(ToggleGroup group, GlidingIndicator indicator, int index)
    {
        var container = group.ContainerFromIndex(index)!;
        var origin = container.TranslatePoint(default, indicator)!.Value;
        return new Rect(origin, container.Bounds.Size);
    }

    private static bool RectClose(Rect a, Rect b, double tolerance) =>
        Math.Abs(a.X - b.X) <= tolerance &&
        Math.Abs(a.Y - b.Y) <= tolerance &&
        Math.Abs(a.Width - b.Width) <= tolerance &&
        Math.Abs(a.Height - b.Height) <= tolerance;

    private static void AssertRectClose(Rect expected, Rect actual, double tolerance)
    {
        Assert.True(RectClose(expected, actual, tolerance), $"expected {expected}, got {actual}");
    }

    private static List<string?> Values(ToggleGroup group) => [.. group.Items.OfType<ToggleGroupOption>().Select(o => o.Value as string)];
}
