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
            RawInput = JsonElement.Parse("""{ "command": "dotnet test" }"""),
            RawOutput = JsonElement.Parse("""{ "output": "Passed 10", "metadata": { "exit": 0 } }"""),
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
            RawOutput = JsonElement.Parse("""{ "type": "shell_result", "output": "ok", "exitCode": 0 }"""),
        };
        qwen.NormalizeToolUpdate(qwenUpdate);
        Assert.Equal("ok", qwenUpdate.Stdout);
        Assert.Equal(0, qwenUpdate.ExitCode);

        var cline = _registry.Resolve("cline");
        var clineUpdate = new AcpToolCallUpdate
        {
            ToolCallId = "t2",
            RawOutput = JsonElement.Parse("""[{ "result": "[Command exited with code 0]\nok" }]"""),
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

    [Fact]
    public void Antigravity_dialect_parses_invoke_subagent_update_steps_manage_subagents_and_run_command()
    {
        var agy = _registry.Resolve("antigravity");
        Assert.True(agy.ReactiveWakeups);

        // 1. invoke_subagent launch (async by design in Antigravity CLI)
        using var subDoc = JsonDocument.Parse("""
            {
              "toolCallId": "call_sub_1",
              "name": "invoke_subagent",
              "title": "Run invoke_subagent",
              "kind": "other",
              "status": "completed",
              "rawInput": {
                "TypeName": "self",
                "Role": "G-A Host ACP Bridge Engineer",
                "Model": "inherit",
                "Workspace": "share",
                "Prompt": "Finish host ACP bridge"
              }
            }
            """);
        var sub = AcpProtocolParser.ParseToolCallUpdate(subDoc.RootElement, agy);
        Assert.True(sub.IsSubagentLaunch);
        Assert.True(sub.SubagentRunsAsynchronously);
        Assert.True(sub.IsBackground);
        Assert.Equal("self", sub.SubagentType);
        Assert.Equal("G-A Host ACP Bridge Engineer", sub.Description);
        Assert.Equal("G-A Host ACP Bridge Engineer", sub.Title);
        Assert.Null(sub.SubagentModel);
        Assert.Equal("share", sub.WorktreeIsolation);

        // Explicit non-inherit model is preserved; inherit workspace is omitted
        using var subDoc2 = JsonDocument.Parse("""
            {
              "toolCallId": "call_sub_2",
              "name": "invoke_subagent",
              "title": "Run invoke_subagent",
              "status": "in_progress",
              "rawInput": {
                "TypeName": "research",
                "Role": "Greeter",
                "Model": "flash_lite",
                "Workspace": "inherit"
              }
            }
            """);
        var sub2 = AcpProtocolParser.ParseToolCallUpdate(subDoc2.RootElement, agy);
        Assert.True(sub2.IsSubagentLaunch);
        Assert.Equal("research", sub2.SubagentType);
        Assert.Equal("flash_lite", sub2.SubagentModel);
        Assert.Null(sub2.WorktreeIsolation);

        // 2. update_steps -> workflow tasks with "done" normalized to "completed"
        using var stepsDoc = JsonDocument.Parse("""
            {
              "toolCallId": "call_steps_1",
              "name": "update_steps",
              "title": "Run update_steps",
              "status": "completed",
              "rawInput": {
                "steps": [
                  { "status": "done", "title": "Inspect worktrees" },
                  { "status": "in_progress", "title": "Finish G-A host ACP bridge" },
                  { "status": "pending", "title": "Run full tests" }
                ]
              }
            }
            """);
        var steps = AcpProtocolParser.ParseToolCallUpdate(stepsDoc.RootElement, agy);
        Assert.Equal("todo_write", steps.WorkflowAction);
        Assert.Equal(3, steps.WorkflowTasks.Count);
        Assert.Equal("completed", steps.WorkflowTasks[0].Status);
        Assert.Equal("Inspect worktrees", steps.WorkflowTasks[0].Content);
        Assert.Equal("in_progress", steps.WorkflowTasks[1].Status);

        // 3. manage_subagents kill & kill_all
        using var killDoc = JsonDocument.Parse("""
            {
              "toolCallId": "call_kill_1",
              "name": "manage_subagents",
              "status": "completed",
              "rawInput": {
                "Action": "kill",
                "ConversationIds": ["bf1142f9-15a0-43b6-a074-b3d2e0bacaab"]
              }
            }
            """);
        var kill = AcpProtocolParser.ParseToolCallUpdate(killDoc.RootElement, agy);
        Assert.Equal("subagent_stop", kill.WorkflowAction);
        Assert.Equal(["bf1142f9-15a0-43b6-a074-b3d2e0bacaab"], kill.TargetSubagentIds);

        using var killAllDoc = JsonDocument.Parse("""
            {
              "toolCallId": "call_kill_all",
              "name": "manage_subagents",
              "status": "completed",
              "rawInput": { "Action": "kill_all" }
            }
            """);
        Assert.Equal("subagent_stop_all", AcpProtocolParser.ParseToolCallUpdate(killAllDoc.RootElement, agy).WorkflowAction);

        // 4. run_command toolSummary & IsDaemon
        using var cmdDoc = JsonDocument.Parse("""
            {
              "toolCallId": "call_cmd_1",
              "name": "run_command",
              "kind": "execute",
              "status": "in_progress",
              "rawInput": {
                "CommandLine": "dotnet watch run",
                "toolSummary": "Start dev server",
                "IsDaemon": true
              }
            }
            """);
        var cmd = AcpProtocolParser.ParseToolCallUpdate(cmdDoc.RootElement, agy);
        Assert.Equal("dotnet watch run", cmd.Command);
        Assert.Equal("Start dev server", cmd.Description);
        Assert.True(cmd.IsBackground);
    }

    [Fact]
    public void Codex_dialect_selects_api_key_auth_and_merges_codex_config_launch_preferences()
    {
        var codex = new AcpDialectRegistry().Resolve("codex-acp");
        Assert.True(codex.ReconnectOnCustomModelSwitch);
        Assert.Equal("fast-mode", codex.FastConfigId);

        var methods = new List<AcpAuthMethod>
        {
            new() { Id = "api-key", Name = "API Key" },
            new() { Id = "chat-gpt", Name = "ChatGPT" },
        };
        Assert.Null(codex.SelectAuthMethod(methods, new Dictionary<string, string>()));
        Assert.Equal("api-key", codex.SelectAuthMethod(methods, new Dictionary<string, string> { ["OPENAI_API_KEY"] = "sk-1" }));
        Assert.Equal("api-key", codex.SelectAuthMethod(methods, new Dictionary<string, string> { ["CODEX_API_KEY"] = "sk-2" }));

        var env = new Dictionary<string, string>
        {
            ["CODEX_CONFIG"] = """{"model_provider":"magpie"}""",
        };
        codex.ApplyLaunchPreferences(env, "deepseek-v4-pro", "xhigh");
        using var doc = JsonDocument.Parse(env["CODEX_CONFIG"]);
        Assert.Equal("magpie", doc.RootElement.GetProperty("model_provider").GetString());
        Assert.Equal("deepseek-v4-pro", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal("xhigh", doc.RootElement.GetProperty("model_reasoning_effort").GetString());
    }

    [Fact]
    public void Codex_dialect_normalizes_image_generation_tool_calls_and_partial_progress()
    {
        var codex = _registry.Resolve("codex-acp");

        // 1. Initial Codex image_generation call with ig_* id and prompt + size -> aspect ratio
        using var beginDoc = JsonDocument.Parse("""
            {
              "toolCallId": "ig_001",
              "title": "Generating image",
              "status": "generating",
              "_meta": { "codex": { "toolName": "image_generation" } },
              "rawInput": {
                "prompt": "Isometric cozy studio desk at sunset",
                "size": "1536x1024"
              },
              "rawOutput": {
                "progress": 0.4,
                "partial_image_b64": "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="
              }
            }
            """);
        var partial = AcpProtocolParser.ParseToolCallUpdate(beginDoc.RootElement, codex);
        Assert.True(partial.IsImageGeneration);
        Assert.Equal("image", partial.Kind);
        Assert.Equal("in_progress", partial.Status);
        Assert.Equal("Isometric cozy studio desk at sunset", partial.ImagePrompt);
        Assert.Equal(0.4, partial.ImageProgress!.Value, precision: 3);
        Assert.Equal(1.5, partial.ImageAspectRatio!.Value, precision: 3);
        var partialImg = Assert.Single(partial.Images);
        Assert.False(string.IsNullOrEmpty(partialImg.Base64Data));
        Assert.Equal("image/png", partialImg.MimeType);

        // 2. Final Codex image_generation_call output with revised_prompt, result base64, and saved_path
        using var doneDoc = JsonDocument.Parse("""
            {
              "toolCallId": "ig_001",
              "status": "completed",
              "rawOutput": {
                "type": "image_generation_call",
                "revised_prompt": "Detailed isometric studio desk bathed in golden sunset light",
                "result": "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==",
                "saved_path": "/tmp/studio.png"
              }
            }
            """);
        var done = AcpProtocolParser.ParseToolCallUpdate(doneDoc.RootElement, codex);
        Assert.True(done.IsImageGeneration);
        Assert.Equal("image", done.Kind);
        Assert.Equal("completed", done.Status);
        Assert.Equal("Detailed isometric studio desk bathed in golden sunset light", done.ImagePrompt);
        Assert.NotEmpty(done.Images);
        Assert.Contains(done.Images, img => img.FilePath == "/tmp/studio.png");
        Assert.Contains(done.Images, img => !string.IsNullOrEmpty(img.Base64Data));
    }

    [Fact]
    public void Cursor_dialect_handles_generate_image_request_as_image_tool_call()
    {
        var cursor = _registry.Resolve("cursor");
        using var doc = JsonDocument.Parse("""
            {
              "id": "cur_img_1",
              "prompt": "Minimalist monochrome octopus emblem",
              "status": "completed",
              "aspectRatio": "16:9",
              "path": "/tmp/octopus.png",
              "data": "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=",
              "mimeType": "image/png"
            }
            """);

        var handling = cursor.TryHandleRequest("cursor/generate_image", doc.RootElement, "42");
        Assert.NotNull(handling);
        Assert.NotNull(handling!.ImmediateReply);
        Assert.NotNull(handling.ToolCallUpdate);
        var update = handling.ToolCallUpdate!;
        Assert.True(update.IsImageGeneration);
        Assert.Equal("image", update.Kind);
        Assert.Equal("cur_img_1", update.ToolCallId);
        Assert.Equal("completed", update.Status);
        Assert.Equal("Minimalist monochrome octopus emblem", update.ImagePrompt);
        Assert.Equal(16d / 9d, update.ImageAspectRatio!.Value, precision: 3);
        Assert.Contains(update.Images, img => img.FilePath == "/tmp/octopus.png" || !string.IsNullOrEmpty(img.Base64Data));
    }

    [Fact]
    public void Parser_extracts_standard_acp_image_content_blocks_and_mcp_image_generation_tools()
    {
        var generic = _registry.Resolve("generic");
        using var doc = JsonDocument.Parse("""
            {
              "toolCallId": "mcp_img_1",
              "name": "mcp__media__generate_image",
              "title": "Generate banner",
              "status": "completed",
              "rawInput": { "prompt": "Futuristic terminal banner", "aspect_ratio": "4:3" },
              "content": [
                {
                  "type": "content",
                  "content": {
                    "type": "image",
                    "data": "data:image/webp;base64,UklGRh4AAABXRUJQVlA4TBEAAAAvAAAAAAfQ//73v/+BiOh/AAA=",
                    "uri": "file:///tmp/banner.webp"
                  }
                }
              ]
            }
            """);
        var update = AcpProtocolParser.ParseToolCallUpdate(doc.RootElement, generic);
        Assert.True(update.IsImageGeneration);
        Assert.Equal("image", update.Kind);
        Assert.Equal("Futuristic terminal banner", update.ImagePrompt);
        Assert.Equal(4d / 3d, update.ImageAspectRatio!.Value, precision: 3);
        var image = Assert.Single(update.Images);
        Assert.Equal("image/webp", image.MimeType);
        Assert.Equal("UklGRh4AAABXRUJQVlA4TBEAAAAvAAAAAAfQ//73v/+BiOh/AAA=", image.Base64Data);
        Assert.Equal("/tmp/banner.webp", image.FilePath);
    }
}
