using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Octopus.App.AgentGui;
using TKCode.Arc;
using Xunit;

namespace Octopus.App.Tests;

public sealed class UserTurnCollapseTests
{
    private static (Window Window, Border Bubble, ExpandableContent Expandable) MountRegularUserTurn(UserTurnItem item, double width = 720)
    {
        var timeline = new AgentTimelineView();
        var presenter = new ContentControl { Content = item };
        var container = new StackPanel();
        container.DataTemplates.AddRange(timeline.DataTemplates);
        container.Children.Add(presenter);

        var window = new Window { Width = width, Height = 900, Content = container };
        window.Show();
        Flush(window);

        var bubble = presenter.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("userBubble"));
        var expandable = bubble.GetVisualDescendants().OfType<ExpandableContent>().Single();
        return (window, bubble, expandable);
    }

    private static void Flush(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    [AvaloniaFact]
    public void ShortPromptDoesNotOverflowAndKeepsCompactBubble()
    {
        var item = new UserTurnItem("hello\nworld");
        var (window, bubble, expandable) = MountRegularUserTurn(item);
        try
        {
            Assert.Equal("hello\nworld", item.DisplayText);
            Assert.False(expandable.IsOverflowing);
            Assert.False(expandable.IsExpanded);
            Assert.True(bubble.Bounds.Width < 200, $"Expected compact bubble for short text, got width {bubble.Bounds.Width}");
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void LongPromptClampsAndExpandsViaArcExpandableContent()
    {
        var text = string.Join(" ", Enumerable.Repeat("Long prompt segment for wrapping.", 80));
        var item = new UserTurnItem(text);
        var (window, _, expandable) = MountRegularUserTurn(item);
        try
        {
            Assert.Equal(text, item.DisplayText);
            Assert.True(expandable.IsOverflowing);
            Assert.False(expandable.IsExpanded);
            var collapsedHeight = expandable.Bounds.Height;

            // Expanding via the control updates the two-way bound ViewModel state and grows height.
            expandable.IsExpanded = true;
            Flush(window);
            Assert.True(item.IsExpanded);
            Assert.True(expandable.Bounds.Height > collapsedHeight,
                $"Expected expanded height ({expandable.Bounds.Height}) > collapsed height ({collapsedHeight})");

            // Collapsing via the ViewModel updates the control.
            item.IsExpanded = false;
            Flush(window);
            Assert.False(expandable.IsExpanded);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void ManyLinesCollapseAndAppendTriggersOverflow()
    {
        var item = new UserTurnItem("line 1");
        var (window, _, expandable) = MountRegularUserTurn(item);
        try
        {
            Assert.False(expandable.IsOverflowing);

            item.Append(string.Concat(Enumerable.Repeat("\nmore text", 20)));
            Flush(window);

            Assert.True(expandable.IsOverflowing);
            Assert.Contains("\nmore text", item.DisplayText);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void LongCollabMessageAlsoUsesArcExpandableContent()
    {
        var body = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"Collab line {i}"));
        var item = new UserTurnItem($"[Octopus Collab · Task]\n{body}");
        var timeline = new AgentTimelineView();
        var presenter = new ContentControl { Content = item };
        var container = new StackPanel();
        container.DataTemplates.AddRange(timeline.DataTemplates);
        container.Children.Add(presenter);

        var window = new Window { Width = 720, Height = 900, Content = container };
        window.Show();
        try
        {
            Flush(window);
            Assert.True(item.IsCollabMessage);
            var expandable = presenter.GetVisualDescendants()
                .OfType<ExpandableContent>()
                .Single(e => e.IsEffectivelyVisible);
            Assert.True(expandable.IsOverflowing);
            Assert.False(expandable.IsExpanded);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
