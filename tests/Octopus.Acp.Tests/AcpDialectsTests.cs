using System.Text.Json;
using Octopus.Acp.Dialects;
using Octopus.Acp.Protocol;

namespace Octopus.Acp.Tests;

public class AcpDialectsTests
{
    private readonly AcpDialectRegistry _registry = new();

    [Fact]
    public void Resolves_all_known_dialects_and_falls_back_to_generic()
    {
        string[] expected =
        [
            "opencode", "antigravity", "cursor", "grok", "gemini", "copilot",
            "qwen-code", "codebuddy", "kimi", "kiro", "goose", "droid",
            "cline", "auggie", "omp", "devin", "hermes", "claude-acp", "codex-acp"
        ];
        foreach (var id in expected)
        {
            var dialect = _registry.Resolve(id);
            Assert.Equal(id, dialect.Id);
        }
        Assert.Equal("generic", _registry.Resolve("unknown-agent").Id);
    }

    [Fact]
    public void Antigravity_spoofs_zed_for_older_versions_and_blocks_interaction_auto_approve()
    {
        var agy = _registry.Resolve("antigravity");
        Assert.Equal("zed", agy.OverrideClientInfo("1.5.0")?.Name);
        Assert.Null(agy.OverrideClientInfo("1.5.1"));

        using var doc = JsonDocument.Parse("""
            {
              "toolCall": { "toolCallId": "interaction_1", "title": "Ask user" },
              "options": [{ "optionId": "proceed", "name": "Proceed", "kind": "allow_once" }]
            }
            """);
        var handling = agy.TryHandleRequest("session/request_permission", doc.RootElement, "1");
        Assert.NotNull(handling);
        Assert.True(handling!.RequireManualApproval);
    }

    [Fact]
    public void Cursor_handles_ask_question_and_create_plan_requests()
    {
        var cursor = _registry.Resolve("cursor");
        Assert.Equal("fast", cursor.FastConfigId);
        Assert.Equal("thinking", cursor.ThinkingConfigId);
        Assert.Equal("default", cursor.ChatModelIdAliases!["auto"]);
        Assert.Equal("extra-high", cursor.ChatEffortIdAliases!["xhigh"]);

        using var qDoc = JsonDocument.Parse("""
            {
              "title": "Pick framework",
              "questions": [
                {
                  "id": "fw",
                  "prompt": "Which test runner?",
                  "options": [{ "id": "xunit", "label": "xUnit" }, { "id": "nunit", "label": "NUnit" }]
                }
              ]
            }
            """);
        var qHandling = cursor.TryHandleRequest("cursor/ask_question", qDoc.RootElement, "7");
        Assert.NotNull(qHandling?.Question);
        Assert.Equal("Pick framework", qHandling!.Question!.Title);
        var reply = qHandling.BuildQuestionReply!(new Dictionary<string, IReadOnlyList<string>> { ["fw"] = ["xunit"] });
        Assert.Equal("answered", reply["outcome"]!["outcome"]!.GetValue<string>());

        using var planDoc = JsonDocument.Parse("""{ "plan": "# Plan\n1. Ship ACP" }""");
        var planHandling = cursor.TryHandleRequest("cursor/create_plan", planDoc.RootElement, "8");
        Assert.NotNull(planHandling?.ImmediateReply);
        Assert.Contains("Ship ACP", planHandling!.PlanMarkdown);
    }

    [Fact]
    public void Normalizes_shell_output_for_opencode_and_cline_and_qwen_skills()
    {
        var opencode = _registry.Resolve("opencode");
        Assert.True(opencode.AdvertiseFormElicitation);
        Assert.Contains("clear", opencode.WithheldCommands);

        var ocUpdate = new AcpToolCallUpdate
        {
            ToolCallId = "t1",
            RawInput = JsonDocument.Parse("""{ "command": "dotnet test" }""").RootElement,
            RawOutput = JsonDocument.Parse("""{ "output": "Passed 10", "metadata": { "exit": 0 } }""").RootElement,
        };
        opencode.NormalizeToolUpdate(ocUpdate);
        Assert.Equal("dotnet test", ocUpdate.Command);
        Assert.Equal("Passed 10", ocUpdate.Stdout);
        Assert.Equal(0, ocUpdate.ExitCode);

        var qwen = _registry.Resolve("qwen-code");
        Assert.Equal("/compress", qwen.CompactCommand);
        Assert.NotNull(qwen.TryHandleRequest("craft/drainMidTurnQueue", null, "9")?.ImmediateReply);
        var qwenUpdate = new AcpToolCallUpdate
        {
            ToolCallId = "tq",
            RawOutput = JsonDocument.Parse("""{ "type": "shell_result", "output": "ok", "exitCode": 0 }""").RootElement,
        };
        qwen.NormalizeToolUpdate(qwenUpdate);
        Assert.Equal("ok", qwenUpdate.Stdout);
        Assert.Equal(0, qwenUpdate.ExitCode);

        var cline = _registry.Resolve("cline");
        var clineUpdate = new AcpToolCallUpdate
        {
            ToolCallId = "t2",
            RawOutput = JsonDocument.Parse("""[{ "result": "[Command exited with code 0]\nok" }]""").RootElement,
        };
        cline.NormalizeToolUpdate(clineUpdate);
        Assert.Equal("ok", clineUpdate.Stdout);
        Assert.Equal(0, clineUpdate.ExitCode);
    }

    [Fact]
    public void Claude_dialect_parses_subagents_parent_tool_calls_workflow_tasks_plan_gate_and_background_bash()
    {
        var claude = _registry.Resolve("claude-acp");
        Assert.True(claude.ReactiveWakeups);

        // 1. SubAgent launch (Agent tool)
        using var agentDoc = JsonDocument.Parse("""
            {
              "toolCallId": "toolu_sub_1",
              "title": "Explore repository",
              "kind": "other",
              "status": "in_progress",
              "_meta": { "claudeCode": { "toolName": "Agent" } },
              "rawInput": {
                "subagent_type": "Explore",
                "description": "Scan ACP protocol files",
                "model": "haiku",
                "isolation": "worktree",
                "run_in_background": true
              }
            }
            """);
        var subLaunch = AcpProtocolParser.ParseToolCallUpdate(agentDoc.RootElement, claude);
        Assert.True(subLaunch.IsSubagentLaunch);
        Assert.Equal("Explore", subLaunch.SubagentType);
        Assert.Equal("Scan ACP protocol files", subLaunch.Description);
        Assert.Equal("haiku", subLaunch.SubagentModel);
        Assert.Equal("worktree", subLaunch.WorktreeIsolation);
        Assert.True(subLaunch.IsBackground);

        // 2. Child tool call inside the SubAgent (parentToolUseId)
        using var childDoc = JsonDocument.Parse("""
            {
              "toolCallId": "toolu_child_1",
              "title": "Grep AcpSessionRuntime",
              "kind": "search",
              "status": "completed",
              "_meta": { "claudeCode": { "toolName": "Grep", "parentToolUseId": "toolu_sub_1" } }
            }
            """);
        var child = AcpProtocolParser.ParseToolCallUpdate(childDoc.RootElement, claude);
        Assert.Equal("toolu_sub_1", child.ParentToolCallId);
        Assert.Equal("Grep", child.Name);

        // 3. TodoWrite workflow tasks
        using var todoDoc = JsonDocument.Parse("""
            {
              "toolCallId": "toolu_todo_1",
              "kind": "other",
              "status": "completed",
              "_meta": { "claudeCode": { "toolName": "TodoWrite" } },
              "rawInput": {
                "todos": [
                  { "id": "1", "content": "Inspect protocol", "status": "completed", "owner": "Explore" },
                  { "id": "2", "content": "Implement HUD", "status": "in_progress", "activeForm": "Implementing HUD", "blockedBy": ["1"] }
                ]
              }
            }
            """);
        var todo = AcpProtocolParser.ParseToolCallUpdate(todoDoc.RootElement, claude);
        Assert.Equal("todo_write", todo.WorkflowAction);
        Assert.Equal(2, todo.WorkflowTasks.Count);
        Assert.Equal("Explore", todo.WorkflowTasks[0].Owner);
        Assert.Equal(["1"], todo.WorkflowTasks[1].BlockedBy);

        // 4. ExitPlanMode interactive plan approval gate (never auto-approved)
        using var planGateDoc = JsonDocument.Parse("""
            {
              "toolCall": {
                "toolCallId": "toolu_plan_1",
                "title": "ExitPlanMode",
                "_meta": { "claudeCode": { "toolName": "ExitPlanMode" } },
                "rawInput": { "plan": "## Plan\n- Step 1\n- Step 2" }
              },
              "options": [{ "optionId": "accept_edits", "name": "Approve & Accept Edits", "kind": "allow_once" }]
            }
            """);
        var planHandling = claude.TryHandleRequest("session/request_permission", planGateDoc.RootElement, "99");
        Assert.NotNull(planHandling);
        Assert.True(planHandling!.IsPlanApproval);
        Assert.True(planHandling.RequireManualApproval);
        Assert.Contains("Step 1", planHandling.PlanMarkdown);

        // 5. Background Bash launch & task id extraction
        using var bgBashDoc = JsonDocument.Parse("""
            {
              "toolCallId": "toolu_bg_1",
              "kind": "execute",
              "status": "completed",
              "_meta": { "claudeCode": { "toolName": "Bash" } },
              "rawInput": { "command": "npm run dev -- --port 5173", "description": "Start dev server", "run_in_background": true },
              "rawOutput": { "content": "Command running in background with ID: b7a9c1" }
            }
            """);
        var bgBash = AcpProtocolParser.ParseToolCallUpdate(bgBashDoc.RootElement, claude);
        Assert.True(bgBash.IsBackground);
        Assert.Equal("b7a9c1", bgBash.BackgroundTaskId);
        Assert.Equal("Start dev server", bgBash.Description);
    }
}
