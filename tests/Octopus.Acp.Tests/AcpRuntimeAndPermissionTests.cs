using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Octopus.Acp.Client;
using Octopus.Acp.Dialects;
using Octopus.Acp.Protocol;
using Octopus.Core.Agents;

namespace Octopus.Acp.Tests;

public class AcpRuntimeAndPermissionTests
{
    [Fact]
    public void Permission_coordinator_enforces_modes_and_never_auto_approves_manual_requests()
    {
        var req = new AcpPermissionRequest
        {
            RequestId = "1",
            SessionId = "s1",
            ToolCall = new AcpToolCallUpdate { ToolCallId = "tc1", Kind = "execute", Title = "rm -rf" },
            Options =
            [
                new AcpPermissionOption { OptionId = "allow_1", Name = "Allow once", Kind = "allow_once" },
                new AcpPermissionOption { OptionId = "deny_1", Name = "Reject", Kind = "reject_once" },
            ],
        };

        Assert.Null(AcpPermissionCoordinator.TryAutoSelectOptionId(req, PermissionModes.Default, AcpPermissionStrategy.ClientApproval, false));
        Assert.Equal("allow_1", AcpPermissionCoordinator.TryAutoSelectOptionId(req, PermissionModes.Full, AcpPermissionStrategy.ClientApproval, false));
        Assert.Equal("allow_1", AcpPermissionCoordinator.TryAutoSelectOptionId(req, PermissionModes.Full, AcpPermissionStrategy.AgentMode, false));
        Assert.Equal("deny_1", AcpPermissionCoordinator.TryAutoSelectOptionId(req, PermissionModes.DontAsk, AcpPermissionStrategy.ClientApproval, false));

        // Manual flag (e.g. question or security warning) ALWAYS overrides full-access auto-approval.
        Assert.Null(AcpPermissionCoordinator.TryAutoSelectOptionId(req, PermissionModes.Full, AcpPermissionStrategy.ClientApproval, requireManualApproval: true));
        Assert.Null(AcpPermissionCoordinator.TryAutoSelectOptionId(req, PermissionModes.Full, AcpPermissionStrategy.AgentMode, requireManualApproval: true));

        // AcceptEdits approves edits/reads but NOT execute.
        var editReq = new AcpPermissionRequest
        {
            RequestId = "2",
            SessionId = "s1",
            ToolCall = new AcpToolCallUpdate { ToolCallId = "tc2", Kind = "edit", Title = "Edit file" },
            Options = req.Options,
        };
        Assert.Equal("allow_1", AcpPermissionCoordinator.TryAutoSelectOptionId(editReq, PermissionModes.AcceptEdits, AcpPermissionStrategy.ClientApproval, false));
        Assert.Null(AcpPermissionCoordinator.TryAutoSelectOptionId(req, PermissionModes.AcceptEdits, AcpPermissionStrategy.ClientApproval, false));
    }

    [Fact]
    public async Task End_to_end_session_initialize_stream_permission_and_elicitation()
    {
        using var clientToAgentServer = new AnonymousPipeServerStream(PipeDirection.In);
        using var clientToAgentClient = new AnonymousPipeClientStream(PipeDirection.Out, clientToAgentServer.ClientSafePipeHandle);
        using var agentToClientServer = new AnonymousPipeServerStream(PipeDirection.Out);
        using var agentToClientClient = new AnonymousPipeClientStream(PipeDirection.In, agentToClientServer.ClientSafePipeHandle);

        var receivedEvents = new System.Collections.Concurrent.ConcurrentQueue<AcpRuntimeEvent>();
        string? observedPermissionTitle = null;
        string? observedQuestionPrompt = null;

        await using var runtime = new AcpSessionRuntime(
            agentToClientClient,
            clientToAgentClient,
            new AcpSessionRuntimeOptions
            {
                Dialect = new AcpDialectRegistry().Resolve("opencode"),
                EnableFormElicitation = true,
                GetActivePermissionMode = () => PermissionModes.Default,
                OnPermissionRequest = (req, _) =>
                {
                    observedPermissionTitle = req.ToolCall.Title;
                    return Task.FromResult<string?>("allow_once_id");
                },
                OnQuestionRequest = (q, _) =>
                {
                    observedQuestionPrompt = q.Questions[0].Prompt;
                    IReadOnlyDictionary<string, IReadOnlyList<string>> answer = new Dictionary<string, IReadOnlyList<string>>
                    {
                        [q.Questions[0].Id] = ["A"],
                    };
                    return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<string>>?>(answer);
                },
            });
        runtime.EventReceived += receivedEvents.Enqueue;

        // Run a mock ACP server speaking real NDJSON over the pipe.
        var serverTask = Task.Run(async () =>
        {
            using var reader = new StreamReader(clientToAgentServer, Encoding.UTF8);
            await using var writer = new StreamWriter(agentToClientServer, new UTF8Encoding(false)) { AutoFlush = true };

            async Task SendAsync(JsonNode node) => await writer.WriteLineAsync(node.ToJsonString());

            // 1. initialize
            var initLine = await reader.ReadLineAsync();
            using var initDoc = JsonDocument.Parse(initLine!);
            var initId = initDoc.RootElement.GetProperty("id").GetInt32();
            await SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = initId,
                ["result"] = new JsonObject
                {
                    ["protocolVersion"] = 1,
                    ["agentInfo"] = new JsonObject { ["name"] = "mock-opencode", ["version"] = "1.20.0" },
                    ["agentCapabilities"] = new JsonObject
                    {
                        ["promptCapabilities"] = new JsonObject { ["image"] = true },
                    },
                },
            });

            // 2. session/new
            var newLine = await reader.ReadLineAsync();
            using var newDoc = JsonDocument.Parse(newLine!);
            var newId = newDoc.RootElement.GetProperty("id").GetInt32();
            await SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = newId,
                ["result"] = new JsonObject
                {
                    ["sessionId"] = "sess-42",
                    ["modes"] = new JsonObject
                    {
                        ["currentModeId"] = "build",
                        ["availableModes"] = new JsonArray
                        {
                            new JsonObject { ["id"] = "build", ["name"] = "Build" },
                            new JsonObject { ["id"] = "plan", ["name"] = "Plan" },
                        },
                    },
                },
            });

            // Emit available_commands_update notification
            await SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "session/update",
                ["params"] = new JsonObject
                {
                    ["sessionId"] = "sess-42",
                    ["update"] = new JsonObject
                    {
                        ["sessionUpdate"] = "available_commands_update",
                        ["availableCommands"] = new JsonArray
                        {
                            new JsonObject { ["name"] = "compact", ["description"] = "Compact context" },
                            new JsonObject { ["name"] = "clear", ["description"] = "Clear session" },
                        },
                    },
                },
            });

            // 3. session/prompt
            var promptLine = await reader.ReadLineAsync();
            using var promptDoc = JsonDocument.Parse(promptLine!);
            var promptId = promptDoc.RootElement.GetProperty("id").GetInt32();

            // Stream thought + tool_call + permission request + elicitation + message + usage
            await SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "session/update",
                ["params"] = new JsonObject
                {
                    ["sessionId"] = "sess-42",
                    ["update"] = new JsonObject
                    {
                        ["sessionUpdate"] = "agent_thought_chunk",
                        ["content"] = new JsonObject { ["type"] = "text", ["text"] = "Thinking carefully..." },
                    },
                },
            });

            await SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "session/update",
                ["params"] = new JsonObject
                {
                    ["sessionId"] = "sess-42",
                    ["update"] = new JsonObject
                    {
                        ["sessionUpdate"] = "tool_call",
                        ["toolCallId"] = "tc-1",
                        ["title"] = "Edit Program.cs",
                        ["kind"] = "edit",
                        ["status"] = "in_progress",
                        ["content"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "diff",
                                ["path"] = "Program.cs",
                                ["oldText"] = "var a = 1;",
                                ["newText"] = "var a = 2;",
                            },
                        },
                    },
                },
            });

            // Agent asks permission
            await SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "perm-1",
                ["method"] = "session/request_permission",
                ["params"] = new JsonObject
                {
                    ["sessionId"] = "sess-42",
                    ["toolCall"] = new JsonObject { ["toolCallId"] = "tc-2", ["title"] = "Run dotnet test", ["kind"] = "execute" },
                    ["options"] = new JsonArray
                    {
                        new JsonObject { ["optionId"] = "allow_once_id", ["name"] = "Allow once", ["kind"] = "allow_once" },
                        new JsonObject { ["optionId"] = "reject_id", ["name"] = "Deny", ["kind"] = "reject_once" },
                    },
                },
            });
            var permReplyLine = await reader.ReadLineAsync();
            using var permReplyDoc = JsonDocument.Parse(permReplyLine!);
            Assert.Equal("allow_once_id", permReplyDoc.RootElement.GetProperty("result").GetProperty("outcome").GetProperty("optionId").GetString());

            // Agent sends standard elicitation/create form
            await SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "elic-1",
                ["method"] = "elicitation/create",
                ["params"] = new JsonObject
                {
                    ["message"] = "Choose style",
                    ["requestedSchema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["style"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["title"] = "Which UI style?",
                                ["enum"] = new JsonArray("A", "B"),
                            },
                        },
                    },
                },
            });
            var elicReplyLine = await reader.ReadLineAsync();
            using var elicReplyDoc = JsonDocument.Parse(elicReplyLine!);
            Assert.Equal("accept", elicReplyDoc.RootElement.GetProperty("result").GetProperty("action").GetString());
            Assert.Equal("A", elicReplyDoc.RootElement.GetProperty("result").GetProperty("content").GetProperty("style").GetString());

            // Agent streams text chunk & completes prompt
            await SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "session/update",
                ["params"] = new JsonObject
                {
                    ["sessionId"] = "sess-42",
                    ["update"] = new JsonObject
                    {
                        ["sessionUpdate"] = "agent_message_chunk",
                        ["content"] = new JsonObject { ["type"] = "text", ["text"] = "Done!" },
                    },
                },
            });

            await SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = promptId,
                ["result"] = new JsonObject { ["stopReason"] = "end_turn" },
            });
        });

        var started = await runtime.StartSessionAsync("/tmp/workspace");
        Assert.Equal("sess-42", started.SessionId);
        Assert.True(runtime.Initialized!.AgentCapabilities.PromptCapabilities.Image);
        Assert.Equal(2, runtime.Modes!.AvailableModes.Count);

        var promptResult = await runtime.PromptAsync("Fix tests");
        Assert.Equal("end_turn", promptResult.StopReason);
        await serverTask;

        Assert.Equal("Run dotnet test", observedPermissionTitle);
        Assert.Equal("Which UI style?", observedQuestionPrompt);

        // OpenCode dialect filters out "clear" command while preserving "compact".
        var cmd = Assert.Single(runtime.AvailableCommands);
        Assert.Equal("compact", cmd.Name);

        Assert.Contains(receivedEvents, e => e is AcpTextDeltaEvent { Channel: "reasoning", Text: "Thinking carefully..." });
        Assert.Contains(receivedEvents, e => e is AcpTextDeltaEvent { Channel: "assistant", Text: "Done!" });
        var toolEvt = Assert.Single(receivedEvents.OfType<AcpToolCallEvent>());
        Assert.Equal("Program.cs", Assert.Single(toolEvt.Update.Diffs).Path);
    }
    [Fact]
    public async Task Session_load_forwards_replayed_history_tagged_as_replay()
    {
        using var clientToAgentServer = new AnonymousPipeServerStream(PipeDirection.In);
        using var clientToAgentClient = new AnonymousPipeClientStream(PipeDirection.Out, clientToAgentServer.ClientSafePipeHandle);
        using var agentToClientServer = new AnonymousPipeServerStream(PipeDirection.Out);
        using var agentToClientClient = new AnonymousPipeClientStream(PipeDirection.In, agentToClientServer.ClientSafePipeHandle);

        var events = new System.Collections.Concurrent.ConcurrentQueue<AcpRuntimeEvent>();
        await using var runtime = new AcpSessionRuntime(agentToClientClient, clientToAgentClient, new AcpSessionRuntimeOptions());
        runtime.EventReceived += events.Enqueue;

        string? loadMethod = null;
        var serverTask = Task.Run(async () =>
        {
            using var reader = new StreamReader(clientToAgentServer, Encoding.UTF8);
            await using var writer = new StreamWriter(agentToClientServer, new UTF8Encoding(false)) { AutoFlush = true };
            async Task SendAsync(JsonNode node) => await writer.WriteLineAsync(node.ToJsonString());
            JsonObject Update(JsonObject update) => new()
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "session/update",
                ["params"] = new JsonObject { ["sessionId"] = "prev-1", ["update"] = update },
            };

            using var initDoc = JsonDocument.Parse((await reader.ReadLineAsync())!);
            await SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = initDoc.RootElement.GetProperty("id").GetInt32(),
                ["result"] = new JsonObject
                {
                    ["protocolVersion"] = 1,
                    ["agentCapabilities"] = new JsonObject { ["loadSession"] = true },
                },
            });

            using var loadDoc = JsonDocument.Parse((await reader.ReadLineAsync())!);
            loadMethod = loadDoc.RootElement.GetProperty("method").GetString();
            await SendAsync(Update(new JsonObject
            {
                ["sessionUpdate"] = "user_message_chunk",
                ["content"] = new JsonObject { ["type"] = "text", ["text"] = "Hello agent" },
            }));
            await SendAsync(Update(new JsonObject
            {
                ["sessionUpdate"] = "agent_message_chunk",
                ["content"] = new JsonObject { ["type"] = "text", ["text"] = "Hi there" },
            }));
            await SendAsync(Update(new JsonObject
            {
                ["sessionUpdate"] = "tool_call",
                ["toolCallId"] = "tc-1",
                ["title"] = "Read file",
                ["kind"] = "read",
                ["status"] = "completed",
            }));
            await SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = loadDoc.RootElement.GetProperty("id").GetInt32(),
                ["result"] = new JsonObject(),
            });

            // A live user_message_chunk echo (outside replay) must not be surfaced.
            await SendAsync(Update(new JsonObject
            {
                ["sessionUpdate"] = "user_message_chunk",
                ["content"] = new JsonObject { ["type"] = "text", ["text"] = "echo" },
            }));
        });

        var started = await runtime.StartSessionAsync("/tmp", resumeSessionId: "prev-1");
        await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(100);

        Assert.Equal("session/load", loadMethod);
        Assert.Equal("prev-1", started.SessionId);
        var list = events.ToList();
        var user = Assert.Single(list.OfType<AcpUserMessageEvent>());
        Assert.Equal("Hello agent", user.Text);
        Assert.True(user.IsReplay);
        var text = Assert.Single(list.OfType<AcpTextDeltaEvent>());
        Assert.Equal("Hi there", text.Text);
        Assert.True(text.IsReplay);
        var tool = Assert.Single(list.OfType<AcpToolCallEvent>());
        Assert.True(tool.IsReplay);
    }
}
