using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Messaging;
using Octopus.Acp.Protocol;
using Octopus.App.AgentGui;
using Octopus.App.Explorer;
using Octopus.App.Explorer.Subagent;
using TKCode.Arc;
using Xunit;

namespace Octopus.App.Tests.Explorer;

/// <summary>
/// Verifies Scheme A (dynamic right-panel Subagent tab + singleton shared AgentTimelineView):
/// opening a subagent adds and selects the tab, renders with the exact main-Agent timeline items,
/// releases timeline memory and visual nodes immediately when switched away or hidden, and removes
/// the tab cleanly on close.
/// </summary>
public class SubagentPanelTests
{
    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(condition(), "the condition did not become true in time");
    }

    [AvaloniaFact]
    public async Task Dynamic_subagent_tab_mounts_shared_timeline_and_releases_when_off_tab_or_closed()
    {
        using var fixture = new ExplorerFixture();
        fixture.Write("README.md", "# test");
        var transcriptPath = fixture.Write("subagent-transcript.jsonl", string.Join("\n",
            JsonSerializer.Serialize(new { type = "USER_INPUT", status = "DONE", content = "<USER_REQUEST>\nAnalyze subagent panel\n</USER_REQUEST>" }),
            JsonSerializer.Serialize(new
            {
                type = "PLANNER_RESPONSE",
                status = "DONE",
                thought = "Inspecting shared AgentTimelineView",
                tool_calls = new object[]
                {
                    new { id = "tc-1", name = "read_file", args = new { Path = "\"/repo/AgentTimelineView.axaml\"" } },
                },
                content = "",
            }),
            JsonSerializer.Serialize(new { type = "GENERIC", status = "DONE", content = "ok content" }),
            JsonSerializer.Serialize(new { type = "PLANNER_RESPONSE", status = "DONE", content = "Shared timeline verified." })) + "\n");

        var view = new ExplorerPanelView();
        var window = new Window { Width = 440, Height = 560, Content = view };
        window.Show();
        Frames.Pump();

        view.ProjectPath = fixture.Root;
        await WaitUntil(() => view.Model.HasProject);
        Frames.Pump();

        var group = view.GetVisualDescendants().OfType<ToggleGroup>().First();
        // Before opening any subagent: dynamic Subagent tab does NOT exist and 0 AgentTimelineView nodes are mounted.
        Assert.DoesNotContain("subagent", TabValues(group));
        Assert.Null(view.Model.MountedSubagent);
        Assert.Empty(view.GetVisualDescendants().OfType<AgentTimelineView>());

        var sub1 = new SubagentRunViewModel("sub-1", "research", "Codebase Researcher", DateTimeOffset.Now)
        {
            TranscriptPath = transcriptPath,
        };
        sub1.MarkCompleted("Shared timeline verified.", DateTimeOffset.Now);

        var sub2 = new SubagentRunViewModel("sub-2", "architect", "Memory Profiler", DateTimeOffset.Now);
        sub2.ApplyLaunchUpdate(new AcpToolCallUpdate
        {
            ToolCallId = "sub-2",
            SubagentType = "architect",
            Description = "Memory Profiler",
            SubagentPrompt = "Check memory usage",
            Status = "in_progress",
        }, DateTimeOffset.Now);
        sub2.UpsertStep(new AcpToolCallUpdate
        {
            ToolCallId = "s2-tc-1",
            Name = "Read",
            Kind = "read",
            Title = "ExplorerPanelViewModel.cs",
            Status = "completed",
        });

        var siblings = new ObservableCollection<SubagentRunViewModel> { sub1, sub2 };

        // Opening sub1 adds the dynamic "subagent" tab, selects it, and mounts the shared AgentTimelineView.
        view.Model.ShowSubagent(new OpenSubagentPanelRequested("parent-1", sub1.Id, fixture.Root, sub1, siblings));
        await WaitUntil(() => view.Model.Subagent.Timeline.Count >= 2);
        Frames.Pump();
        window.UpdateLayout();

        Assert.Contains("subagent", TabValues(group));
        Assert.Equal("subagent", group.SelectedValue);
        Assert.True(view.Model.IsSubagentTab);
        Assert.Same(view.Model.Subagent, view.Model.MountedSubagent);
        Assert.Single(view.GetVisualDescendants().OfType<AgentTimelineView>());

        // Timeline uses exact same main-Agent models: UserTurnItem + AssistantTurnItem (with Thought, ToolCall, Markdown).
        var userTurn = Assert.IsType<UserTurnItem>(view.Model.Subagent.Timeline[0]);
        Assert.Equal("Analyze subagent panel", userTurn.Text);
        var assistantTurn = Assert.IsType<AssistantTurnItem>(view.Model.Subagent.Timeline[1]);
        Assert.Single(assistantTurn.ToolCalls);
        Assert.Equal("Shared timeline verified.", assistantTurn.CopyReplyValue);

        // Switching sibling replaces the single timeline instance in-place (fallback snapshot for sub2 without transcript).
        Assert.Equal(2, view.Model.Subagent.Siblings.Count);
        view.Model.Subagent.Siblings[1].SelectCommand.Execute(null);
        Frames.Pump();
        window.UpdateLayout();
        Assert.Equal("sub-2", view.Model.Subagent.SubagentId);
        Assert.Single(view.GetVisualDescendants().OfType<AgentTimelineView>());
        Assert.Equal("Check memory usage", Assert.IsType<UserTurnItem>(view.Model.Subagent.Timeline[0]).Text);

        // Switching away to Files immediately unmounts AgentTimelineView and clears Timeline (0 memory/visual nodes).
        view.Model.ShowFilesCommand.Execute(null);
        Frames.Pump();
        window.UpdateLayout();
        Assert.True(view.Model.IsFilesTab);
        Assert.Null(view.Model.MountedSubagent);
        Assert.Empty(view.Model.Subagent.Timeline);
        Assert.Empty(view.GetVisualDescendants().OfType<AgentTimelineView>());

        // Switching back to Subagent tab reloads seamlessly on demand.
        view.Model.ShowSubagentTabCommand.Execute(null);
        Frames.Pump();
        window.UpdateLayout();
        Assert.True(view.Model.IsSubagentTab);
        Assert.NotEmpty(view.Model.Subagent.Timeline);
        Assert.Single(view.GetVisualDescendants().OfType<AgentTimelineView>());

        // Collapsing the right panel (IsActive = false) immediately unmounts and clears Timeline.
        view.Model.IsActive = false;
        Frames.Pump();
        window.UpdateLayout();
        Assert.Null(view.Model.MountedSubagent);
        Assert.Empty(view.Model.Subagent.Timeline);

        // Re-opening right panel restores the subagent timeline, and Close removes the dynamic tab.
        view.Model.IsActive = true;
        Frames.Pump();
        Assert.NotEmpty(view.Model.Subagent.Timeline);

        view.Model.Subagent.CloseCommand.Execute(null);
        Frames.Pump();
        window.UpdateLayout();
        Assert.False(view.Model.HasSubagentTab);
        Assert.DoesNotContain("subagent", TabValues(group));
        Assert.True(view.Model.IsFilesTab);
        Assert.Empty(view.Model.Subagent.Timeline);

        window.Close();
        view.Model.Dispose();
    }

    [AvaloniaFact]
    public void Clicking_subagent_in_shell_expands_right_panel_to_readable_width_and_opens_tab()
    {
        var shell = Octopus.App.Composition.AppHost.Get<Octopus.App.Shell.ShellViewModel>();
        shell.IsRightPanelOpen = false;
        shell.RightPanelWidth = 260;

        OpenSubagentPanelRequested? forwarded = null;
        shell.SubagentOpenRequested += (_, req) => forwarded = req;

        var sub = new SubagentRunViewModel("sub-shell-1", "research", "Explore Shell", DateTimeOffset.Now);
        var req = new OpenSubagentPanelRequested("item-1", sub.Id, "/tmp/proj", sub, [sub]);
        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(req);
        Dispatcher.UIThread.RunJobs();

        Assert.True(shell.IsRightPanelOpen);
        Assert.True(shell.RightPanelWidth >= Octopus.App.Shell.ShellViewModel.MinSubagentPanelWidth);
        Assert.Same(req, forwarded);
    }

    private static List<string?> TabValues(ToggleGroup group) =>
        [.. group.Items.OfType<ToggleGroupOption>().Select(o => o.Value as string)];
}
