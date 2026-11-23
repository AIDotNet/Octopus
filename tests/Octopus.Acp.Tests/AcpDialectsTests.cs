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
}
