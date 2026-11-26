using System.Text.Json;
using Octopus.Agents.Subagents;

namespace Octopus.Agents.Tests;

public class SubagentTimelineTranscriptTests
{
    [Fact]
    public void Reads_agy_subagent_transcript_incrementally_with_prompt_thought_tools_diff_and_markdown()
    {
        var tempDir = Directory.CreateTempSubdirectory("octopus-sub-tl-agy-").FullName;
        try
        {
            var path = Path.Combine(tempDir, "transcript.jsonl");
            var lines1 = new[]
            {
                JsonSerializer.Serialize(new
                {
                    type = "USER_INPUT",
                    status = "DONE",
                    content = "<USER_REQUEST>\nInspect right panel layout\n</USER_REQUEST>\n<ADDITIONAL_METADATA>ignored</ADDITIONAL_METADATA>",
                }),
                JsonSerializer.Serialize(new
                {
                    type = "PLANNER_RESPONSE",
                    status = "DONE",
                    thought = "Check ExplorerPanelView first",
                    tool_calls = new object[]
                    {
                        new { id = "tc-1", name = "read_file", args = new { Path = "\"/repo/src/ExplorerPanelView.axaml\"" } },
                        new
                        {
                            id = "tc-2",
                            name = "edit_file",
                            args = new
                            {
                                Path = JsonSerializer.Serialize("/repo/src/ExplorerTab.cs"),
                                Target = JsonSerializer.Serialize("Sessions"),
                                Replacement = JsonSerializer.Serialize("Sessions,\nSubagent"),
                            },
                        },
                    },
                    content = "",
                }),
                JsonSerializer.Serialize(new
                {
                    type = "GENERIC",
                    status = "DONE",
                    content = "Created At: 2026-11-25T10:00:00Z\nCompleted At: 2026-11-25T10:00:01Z\nfile body ok",
                }),
            };
            File.WriteAllLines(path, lines1);

            var delta1 = SubagentTimelineTranscript.ReadDelta(path, 0);
            Assert.True(delta1.ToOffset > 0);
            Assert.False(delta1.Reset);
            Assert.Equal(5, delta1.Events.Count);

            Assert.Equal(SubagentTranscriptEventKind.UserPrompt, delta1.Events[0].Kind);
            Assert.Equal("Inspect right panel layout", delta1.Events[0].Text);

            Assert.Equal(SubagentTranscriptEventKind.Thought, delta1.Events[1].Kind);
            Assert.Equal("Check ExplorerPanelView first", delta1.Events[1].Text);

            Assert.Equal(SubagentTranscriptEventKind.ToolCall, delta1.Events[2].Kind);
            Assert.Equal("read_file", delta1.Events[2].ToolName);
            Assert.Equal("read", delta1.Events[2].ToolKind);
            Assert.Equal("ExplorerPanelView.axaml", delta1.Events[2].ToolTarget);
            Assert.Equal("/repo/src/ExplorerPanelView.axaml", delta1.Events[2].FilePath);

            Assert.Equal(SubagentTranscriptEventKind.ToolCall, delta1.Events[3].Kind);
            Assert.Equal("edit_file", delta1.Events[3].ToolName);
            Assert.Equal("Sessions", delta1.Events[3].DiffOld);
            Assert.Equal("Sessions,\nSubagent", delta1.Events[3].DiffNew);

            Assert.Equal(SubagentTranscriptEventKind.ToolResult, delta1.Events[4].Kind);
            Assert.False(delta1.Events[4].IsError);
            Assert.Equal("file body ok", delta1.Events[4].Output);

            // Re-reading with no file growth is a zero-event no-op.
            var noop = SubagentTimelineTranscript.ReadDelta(path, delta1.ToOffset);
            Assert.Equal(delta1.ToOffset, noop.ToOffset);
            Assert.Empty(noop.Events);

            // Append second tool result (error) and final Markdown reply.
            var lines2 = new[]
            {
                JsonSerializer.Serialize(new { type = "GENERIC", status = "ERROR", content = "patch conflict" }),
                JsonSerializer.Serialize(new { type = "PLANNER_RESPONSE", status = "DONE", content = "## Findings\nAll checked." }),
            };
            File.AppendAllLines(path, lines2);

            var delta2 = SubagentTimelineTranscript.ReadDelta(path, delta1.ToOffset);
            Assert.Equal(2, delta2.Events.Count);
            Assert.Equal(SubagentTranscriptEventKind.ToolResult, delta2.Events[0].Kind);
            Assert.True(delta2.Events[0].IsError);
            Assert.Equal("patch conflict", delta2.Events[0].Output);
            Assert.Equal(SubagentTranscriptEventKind.AssistantText, delta2.Events[1].Kind);
            Assert.Equal("## Findings\nAll checked.", delta2.Events[1].Text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Reads_claude_code_subagent_transcript_with_thinking_tool_use_tool_result_and_reply()
    {
        var tempDir = Directory.CreateTempSubdirectory("octopus-sub-tl-claude-").FullName;
        try
        {
            var path = Path.Combine(tempDir, "agent-abc123.jsonl");
            var lines = new[]
            {
                """{"type":"user","message":{"role":"user","content":"Audit memory usage in ExplorerPanelViewModel"}}""",
                """{"type":"assistant","message":{"role":"assistant","content":[{"type":"thinking","thinking":"Inspect ExplorerPanelViewModel fields"},{"type":"tool_use","id":"toolu_1","name":"Bash","input":{"command":"dotnet test","description":"Run tests"}}]}}""",
                """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1","is_error":false,"content":"Passed! 12 tests"}]}}""",
                """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Memory stays bounded under **0.5 MB**."}]}}""",
            };
            File.WriteAllLines(path, lines);

            var delta = SubagentTimelineTranscript.ReadDelta(path, 0);
            Assert.Equal(5, delta.Events.Count);

            Assert.Equal(SubagentTranscriptEventKind.UserPrompt, delta.Events[0].Kind);
            Assert.Equal("Audit memory usage in ExplorerPanelViewModel", delta.Events[0].Text);

            Assert.Equal(SubagentTranscriptEventKind.Thought, delta.Events[1].Kind);
            Assert.Equal("Inspect ExplorerPanelViewModel fields", delta.Events[1].Text);

            Assert.Equal(SubagentTranscriptEventKind.ToolCall, delta.Events[2].Kind);
            Assert.Equal("toolu_1", delta.Events[2].ToolCallId);
            Assert.Equal("Bash", delta.Events[2].ToolName);
            Assert.Equal("execute", delta.Events[2].ToolKind);
            Assert.Equal("dotnet test", delta.Events[2].Command);

            Assert.Equal(SubagentTranscriptEventKind.ToolResult, delta.Events[3].Kind);
            Assert.Equal("toolu_1", delta.Events[3].ToolCallId);
            Assert.False(delta.Events[3].IsError);
            Assert.Equal("Passed! 12 tests", delta.Events[3].Output);

            Assert.Equal(SubagentTranscriptEventKind.AssistantText, delta.Events[4].Kind);
            Assert.Equal("Memory stays bounded under **0.5 MB**.", delta.Events[4].Text);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch (IOException) { }
        }
    }
}
