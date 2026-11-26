using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Octopus.Acp.Client;
using Octopus.Acp.Dialects;
using Octopus.Acp.Protocol;
using Octopus.Acp.Transport;
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

    [Fact]
    public async Task Session_load_failure_falls_back_to_session_new_without_leaking_session_id()
    {
        using var clientToAgentServer = new AnonymousPipeServerStream(PipeDirection.In);
        using var clientToAgentClient = new AnonymousPipeClientStream(PipeDirection.Out, clientToAgentServer.ClientSafePipeHandle);
        using var agentToClientServer = new AnonymousPipeServerStream(PipeDirection.Out);
        using var agentToClientClient = new AnonymousPipeClientStream(PipeDirection.In, agentToClientServer.ClientSafePipeHandle);

        await using var runtime = new AcpSessionRuntime(agentToClientClient, clientToAgentClient);
        var methods = new List<string>();
        bool? newHadSessionId = null;

        var serverTask = Task.Run(async () =>
        {
            using var reader = new StreamReader(clientToAgentServer, Encoding.UTF8);
            await using var writer = new StreamWriter(agentToClientServer, new UTF8Encoding(false)) { AutoFlush = true };
            async Task SendAsync(JsonNode node) => await writer.WriteLineAsync(node.ToJsonString());

            using var initDoc = JsonDocument.Parse((await reader.ReadLineAsync())!);
            await SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = initDoc.RootElement.GetProperty("id").GetInt32(),
                ["result"] = new JsonObject
                {
                    ["protocolVersion"] = 1,
                    ["agentCapabilities"] = new JsonObject
                    {
                        ["loadSession"] = true,
                        ["sessionCapabilities"] = new JsonObject { ["resume"] = new JsonObject() },
                    },
                },
            });

            // 1. session/load rejects the stale session id.
            using var loadDoc = JsonDocument.Parse((await reader.ReadLineAsync())!);
            methods.Add(loadDoc.RootElement.GetProperty("method").GetString()!);
            await SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = loadDoc.RootElement.GetProperty("id").GetInt32(),
                ["error"] = new JsonObject { ["code"] = -32602, ["message"] = "session not found" },
            });

            // 2. Runtime falls back cleanly to session/new without forwarding the stale sessionId param.
            using var newDoc = JsonDocument.Parse((await reader.ReadLineAsync())!);
            methods.Add(newDoc.RootElement.GetProperty("method").GetString()!);
            newHadSessionId = newDoc.RootElement.GetProperty("params").TryGetProperty("sessionId", out _);
            await SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = newDoc.RootElement.GetProperty("id").GetInt32(),
                ["result"] = new JsonObject { ["sessionId"] = "fresh-sess-2" },
            });
        });

        var started = await runtime.StartSessionAsync("/tmp", resumeSessionId: "stale-sess-1");
        await serverTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["session/load", "session/new"], methods);
        Assert.False(newHadSessionId);
        Assert.Equal("new", started.Kind);
        Assert.Equal("fresh-sess-2", started.SessionId);
    }

    [Fact]
    public async Task Runtime_streams_image_generation_notifications_message_images_and_cursor_generate_image()
    {
        using var clientToAgentServer = new AnonymousPipeServerStream(PipeDirection.In);
        using var clientToAgentClient = new AnonymousPipeClientStream(PipeDirection.Out, clientToAgentServer.ClientSafePipeHandle);
        using var agentToClientServer = new AnonymousPipeServerStream(PipeDirection.Out);
        using var agentToClientClient = new AnonymousPipeClientStream(PipeDirection.In, agentToClientServer.ClientSafePipeHandle);

        var events = new System.Collections.Concurrent.ConcurrentQueue<AcpRuntimeEvent>();
        await using var runtime = new AcpSessionRuntime(
            agentToClientClient,
            clientToAgentClient,
            new AcpSessionRuntimeOptions
            {
                Dialect = new AcpDialectRegistry().Resolve("cursor"),
            });
        runtime.EventReceived += events.Enqueue;

        const string tinyPngB64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
        var cursorAckReceived = false;

        var serverTask = Task.Run(async () =>
        {
            using var reader = new StreamReader(clientToAgentServer, Encoding.UTF8);
            await using var writer = new StreamWriter(agentToClientServer, new UTF8Encoding(false)) { AutoFlush = true };
            async Task SendAsync(JsonNode node) => await writer.WriteLineAsync(node.ToJsonString());

            using var initDoc = JsonDocument.Parse((await reader.ReadLineAsync())!);
            await SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = initDoc.RootElement.GetProperty("id").GetInt32(),
                ["result"] = new JsonObject { ["protocolVersion"] = 1 },
            });

            using var newDoc = JsonDocument.Parse((await reader.ReadLineAsync())!);
            await SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = newDoc.RootElement.GetProperty("id").GetInt32(),
                ["result"] = new JsonObject { ["sessionId"] = "img-sess-1" },
            });

            using var promptDoc = JsonDocument.Parse((await reader.ReadLineAsync())!);
            var promptId = promptDoc.RootElement.GetProperty("id").GetInt32();

            // 1. Custom Codex-style image_generation_begin / partial / end session updates
            await SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "session/update",
                ["params"] = new JsonObject
                {
                    ["sessionId"] = "img-sess-1",
                    ["update"] = new JsonObject
                    {
                        ["sessionUpdate"] = "image_generation_begin",
                        ["call_id"] = "ig_stream_1",
                        ["prompt"] = "Cyberpunk ramen shop in rain",
                        ["aspectRatio"] = "16:9",
                    },
                },
            });
            await SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "session/update",
                ["params"] = new JsonObject
                {
                    ["sessionId"] = "img-sess-1",
                    ["update"] = new JsonObject
                    {
                        ["sessionUpdate"] = "image_generation_partial",
                        ["call_id"] = "ig_stream_1",
                        ["progress"] = 50,
                        ["partial_image_b64"] = tinyPngB64,
                    },
                },
            });
            await SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "session/update",
                ["params"] = new JsonObject
                {
                    ["sessionId"] = "img-sess-1",
                    ["update"] = new JsonObject
                    {
                        ["sessionUpdate"] = "image_generation_end",
                        ["call_id"] = "ig_stream_1",
                        ["result"] = tinyPngB64,
                        ["revised_prompt"] = "Neon cyberpunk ramen shop at night in pouring rain",
                    },
                },
            });

            // 2. Direct agent_message_chunk with ACP ContentBlock type="image"
            await SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "session/update",
                ["params"] = new JsonObject
                {
                    ["sessionId"] = "img-sess-1",
                    ["update"] = new JsonObject
                    {
                        ["sessionUpdate"] = "agent_message_chunk",
                        ["messageId"] = "msg-img-1",
                        ["content"] = new JsonObject
                        {
                            ["type"] = "image",
                            ["data"] = tinyPngB64,
                            ["mimeType"] = "image/png",
                        },
                    },
                },
            });

            // 3. Cursor incoming cursor/generate_image JSON-RPC request -> auto-replies {} and emits AcpToolCallEvent
            await SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "cur-gen-1",
                ["method"] = "cursor/generate_image",
                ["params"] = new JsonObject
                {
                    ["id"] = "cur-img-call",
                    ["prompt"] = "App icon",
                    ["status"] = "completed",
                    ["data"] = tinyPngB64,
                    ["mimeType"] = "image/png",
                },
            });
            var cursorReplyLine = await reader.ReadLineAsync();
            using var cursorReplyDoc = JsonDocument.Parse(cursorReplyLine!);
            cursorAckReceived = cursorReplyDoc.RootElement.GetProperty("id").GetString() == "cur-gen-1"
                && cursorReplyDoc.RootElement.TryGetProperty("result", out _);

            await SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = promptId,
                ["result"] = new JsonObject { ["stopReason"] = "end_turn" },
            });
        });

        await runtime.StartSessionAsync("/tmp/img-workspace");
        var promptResult = await runtime.PromptAsync("Draw two pictures");
        await serverTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("end_turn", promptResult.StopReason);
        Assert.True(cursorAckReceived);

        var all = events.ToList();
        var igEvents = all.OfType<AcpToolCallEvent>().Where(e => e.Update.ToolCallId == "ig_stream_1").ToList();
        Assert.Equal(3, igEvents.Count);
        Assert.All(igEvents, e => Assert.True(e.Update.IsImageGeneration));
        Assert.Equal("in_progress", igEvents[0].Update.Status);
        Assert.Equal(0.5, igEvents[1].Update.ImageProgress!.Value, precision: 3);
        Assert.Single(igEvents[1].Update.Images);
        Assert.Equal("completed", igEvents[2].Update.Status);
        Assert.Equal("Neon cyberpunk ramen shop at night in pouring rain", igEvents[2].Update.ImagePrompt);

        var directImg = Assert.Single(all.OfType<AcpImageEvent>());
        Assert.Equal("msg-img-1", directImg.MessageId);
        Assert.Equal(tinyPngB64, directImg.Image.Base64Data);
        Assert.Equal("image/png", directImg.Image.MimeType);

        var cursorEvt = Assert.Single(all.OfType<AcpToolCallEvent>(), e => e.Update.ToolCallId == "cur-img-call");
        Assert.True(cursorEvt.Update.IsImageGeneration);
        Assert.Equal("App icon", cursorEvt.Update.ImagePrompt);
        Assert.Single(cursorEvt.Update.Images);
    }

    [Fact]
    public async Task Runtime_supports_v2_lifecycle_auth_mcp_fs_terminal_url_elicitation_and_rich_content()
    {
        var tempDir = Directory.CreateTempSubdirectory("octopus-acp-e2e-").FullName;
        try
        {
            using var clientToAgentServer = new AnonymousPipeServerStream(PipeDirection.In);
            using var clientToAgentClient = new AnonymousPipeClientStream(PipeDirection.Out, clientToAgentServer.ClientSafePipeHandle);
            using var agentToClientServer = new AnonymousPipeServerStream(PipeDirection.Out);
            using var agentToClientClient = new AnonymousPipeClientStream(PipeDirection.In, agentToClientServer.ClientSafePipeHandle);

            var events = new System.Collections.Concurrent.ConcurrentQueue<AcpRuntimeEvent>();
            var fsHandler = new AcpFileSystemHandler([tempDir]);
            await using var termHandler = new AcpTerminalHandler(tempDir);

            string? observedUrlElicitUrl = null;
            string? observedUrlElicitId = null;
            AcpQuestionItem? observedNumberField = null;

            await using var runtime = new AcpSessionRuntime(
                agentToClientClient,
                clientToAgentClient,
                new AcpSessionRuntimeOptions
                {
                    PreferredProtocolVersion = 2,
                    EnableFormElicitation = true,
                    EnableUrlElicitation = true,
                    EnableClientFileSystem = true,
                    EnableClientTerminal = true,
                    FileSystemHandler = fsHandler,
                    TerminalHandler = termHandler,
                    McpServers =
                    [
                        new AcpMcpServerConfig { Name = "local-stdio", Transport = "stdio", Command = "npx", Args = ["-y", "mcp-local"] },
                        new AcpMcpServerConfig { Name = "remote-http", Transport = "http", Url = "https://mcp.example.com/rpc" },
                        new AcpMcpServerConfig { Name = "unsupported-sse", Transport = "sse", Url = "https://mcp.example.com/sse" },
                    ],
                    OnUrlElicitationRequest = (req, _) =>
                    {
                        observedUrlElicitUrl = req.Url;
                        observedUrlElicitId = req.ElicitationId;
                        return Task.FromResult(AcpElicitationAction.Accept);
                    },
                    OnFormElicitationRequest = (q, _) =>
                    {
                        observedNumberField ??= q.Questions.FirstOrDefault(item => item.Id == "retries");
                        if (q.Title == "Decline me")
                        {
                            return Task.FromResult(new AcpFormElicitationResponse(AcpElicitationAction.Decline, null));
                        }
                        return Task.FromResult(new AcpFormElicitationResponse(
                            AcpElicitationAction.Accept,
                            new Dictionary<string, IReadOnlyList<string>> { ["retries"] = ["3"] }));
                    },
                });
            runtime.EventReceived += events.Enqueue;

            int? observedRetriesJson = null;
            string? observedDeclineAction = null;
            string? observedUrlReplyAction = null;
            string? observedReadBack = null;
            string? observedTermOutput = null;
            List<string> observedMcpNames = [];
            List<string> observedAddDirs = [];
            List<string> observedPromptBlockTypes = [];

            var targetFile = Path.Combine(tempDir, "hello.txt");

            var serverTask = Task.Run(async () =>
            {
                using var reader = new StreamReader(clientToAgentServer, Encoding.UTF8);
                await using var writer = new StreamWriter(agentToClientServer, new UTF8Encoding(false)) { AutoFlush = true };
                async Task SendAsync(JsonNode node) => await writer.WriteLineAsync(node.ToJsonString());

                // 1. initialize (verify v2 + fs + terminal + url elicitation + auth.terminal caps)
                using (var initDoc = JsonDocument.Parse((await reader.ReadLineAsync())!))
                {
                    var initParams = initDoc.RootElement.GetProperty("params");
                    Assert.Equal(2, initParams.GetProperty("protocolVersion").GetInt32());
                    var caps = initParams.GetProperty("clientCapabilities");
                    Assert.True(caps.GetProperty("fs").GetProperty("readTextFile").GetBoolean());
                    Assert.True(caps.GetProperty("fs").GetProperty("writeTextFile").GetBoolean());
                    Assert.True(caps.GetProperty("terminal").GetBoolean());
                    Assert.True(caps.GetProperty("elicitation").TryGetProperty("url", out _));
                    Assert.True(caps.GetProperty("auth").GetProperty("terminal").GetBoolean());

                    await SendAsync(new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = initDoc.RootElement.GetProperty("id").GetInt32(),
                        ["result"] = new JsonObject
                        {
                            ["protocolVersion"] = 2,
                            ["agentCapabilities"] = new JsonObject
                            {
                                ["loadSession"] = true,
                                ["promptCapabilities"] = new JsonObject
                                {
                                    ["image"] = true,
                                    ["audio"] = true,
                                    ["embeddedContext"] = true,
                                },
                                ["mcpCapabilities"] = new JsonObject { ["http"] = true, ["sse"] = false },
                                ["auth"] = new JsonObject { ["logout"] = true },
                                ["sessionCapabilities"] = new JsonObject
                                {
                                    ["list"] = new JsonObject(),
                                    ["fork"] = new JsonObject(),
                                    ["close"] = new JsonObject(),
                                    ["delete"] = new JsonObject(),
                                    ["archive"] = new JsonObject(),
                                    ["compaction"] = new JsonObject(),
                                    ["cursors"] = new JsonObject(),
                                    ["additionalDirectories"] = new JsonObject(),
                                },
                            },
                            ["authMethods"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["id"] = "api_key",
                                    ["name"] = "API Key",
                                    ["type"] = "env_var",
                                    ["link"] = "https://example.com/keys",
                                    ["vars"] = new JsonArray
                                    {
                                        new JsonObject { ["name"] = "MY_API_KEY", ["label"] = "Key", ["secret"] = true },
                                    },
                                },
                            },
                        },
                    });
                }

                // 2. session/new (verify filtered mcpServers & additionalDirectories)
                using (var newDoc = JsonDocument.Parse((await reader.ReadLineAsync())!))
                {
                    var p = newDoc.RootElement.GetProperty("params");
                    foreach (var s in p.GetProperty("mcpServers").EnumerateArray())
                        observedMcpNames.Add(s.GetProperty("name").GetString()!);
                    foreach (var d in p.GetProperty("additionalDirectories").EnumerateArray())
                        observedAddDirs.Add(d.GetString()!);

                    await SendAsync(new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = newDoc.RootElement.GetProperty("id").GetInt32(),
                        ["result"] = new JsonObject { ["sessionId"] = "v2-sess-1" },
                    });
                }

                // 3. authenticate
                using (var authDoc = JsonDocument.Parse((await reader.ReadLineAsync())!))
                {
                    Assert.Equal("authenticate", authDoc.RootElement.GetProperty("method").GetString());
                    Assert.Equal("api_key", authDoc.RootElement.GetProperty("params").GetProperty("methodId").GetString());
                    await SendAsync(new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = authDoc.RootElement.GetProperty("id").GetInt32(),
                        ["result"] = new JsonObject(),
                    });
                }

                // 4. session/list
                using (var listDoc = JsonDocument.Parse((await reader.ReadLineAsync())!))
                {
                    Assert.Equal("session/list", listDoc.RootElement.GetProperty("method").GetString());
                    await SendAsync(new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = listDoc.RootElement.GetProperty("id").GetInt32(),
                        ["result"] = new JsonObject
                        {
                            ["nextCursor"] = "page-2",
                            ["sessions"] = new JsonArray
                            {
                                new JsonObject { ["sessionId"] = "v2-sess-1", ["cwd"] = tempDir, ["title"] = "Main work" },
                            },
                        },
                    });
                }

                // 5. session/fork
                using (var forkDoc = JsonDocument.Parse((await reader.ReadLineAsync())!))
                {
                    Assert.Equal("session/fork", forkDoc.RootElement.GetProperty("method").GetString());
                    Assert.Equal("cur-42", forkDoc.RootElement.GetProperty("params").GetProperty("cursorId").GetString());
                    await SendAsync(new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = forkDoc.RootElement.GetProperty("id").GetInt32(),
                        ["result"] = new JsonObject { ["sessionId"] = "v2-sess-fork-2" },
                    });
                }

                // 6. session/delete -> returns -32002 (idempotent success per ACP spec)
                using (var delDoc = JsonDocument.Parse((await reader.ReadLineAsync())!))
                {
                    Assert.Equal("session/delete", delDoc.RootElement.GetProperty("method").GetString());
                    await SendAsync(new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = delDoc.RootElement.GetProperty("id").GetInt32(),
                        ["error"] = new JsonObject { ["code"] = -32002, ["message"] = "already deleted" },
                    });
                }

                // 7. session/archive
                using (var archDoc = JsonDocument.Parse((await reader.ReadLineAsync())!))
                {
                    Assert.Equal("session/archive", archDoc.RootElement.GetProperty("method").GetString());
                    await SendAsync(new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = archDoc.RootElement.GetProperty("id").GetInt32(),
                        ["result"] = new JsonObject(),
                    });
                }

                // 8. session/prompt with embedded resource + audio + annotations, then agent callbacks & v2 notifications
                using (var promptDoc = JsonDocument.Parse((await reader.ReadLineAsync())!))
                {
                    var promptId = promptDoc.RootElement.GetProperty("id").GetInt32();
                    foreach (var blk in promptDoc.RootElement.GetProperty("params").GetProperty("prompt").EnumerateArray())
                        observedPromptBlockTypes.Add(blk.GetProperty("type").GetString()!);

                    // fs/write_text_file
                    await SendAsync(new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = "fs-w-1",
                        ["method"] = "fs/write_text_file",
                        ["params"] = new JsonObject { ["sessionId"] = "v2-sess-1", ["path"] = targetFile, ["content"] = "alpha\nbeta\ngamma" },
                    });
                    using (var wResp = JsonDocument.Parse((await reader.ReadLineAsync())!))
                        Assert.True(wResp.RootElement.TryGetProperty("result", out _));

                    // fs/read_text_file with line + limit
                    await SendAsync(new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = "fs-r-1",
                        ["method"] = "fs/read_text_file",
                        ["params"] = new JsonObject { ["sessionId"] = "v2-sess-1", ["path"] = targetFile, ["line"] = 2, ["limit"] = 1 },
                    });
                    using (var rResp = JsonDocument.Parse((await reader.ReadLineAsync())!))
                        observedReadBack = rResp.RootElement.GetProperty("result").GetProperty("content").GetString();

                    // terminal/create -> wait_for_exit -> output -> tool_call(type=terminal) -> release
                    var cmd = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
                    var args = OperatingSystem.IsWindows() ? new JsonArray("/c", "echo term-ok") : new JsonArray("-c", "printf 'term-ok'");
                    await SendAsync(new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = "t-create",
                        ["method"] = "terminal/create",
                        ["params"] = new JsonObject { ["sessionId"] = "v2-sess-1", ["command"] = cmd, ["args"] = args, ["cwd"] = tempDir },
                    });
                    string termId;
                    using (var tcResp = JsonDocument.Parse((await reader.ReadLineAsync())!))
                        termId = tcResp.RootElement.GetProperty("result").GetProperty("terminalId").GetString()!;

                    await SendAsync(new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = "t-wait",
                        ["method"] = "terminal/wait_for_exit",
                        ["params"] = new JsonObject { ["sessionId"] = "v2-sess-1", ["terminalId"] = termId },
                    });
                    using (var twResp = JsonDocument.Parse((await reader.ReadLineAsync())!))
                        Assert.Equal(0, twResp.RootElement.GetProperty("result").GetProperty("exitCode").GetInt32());

                    await SendAsync(new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = "t-out",
                        ["method"] = "terminal/output",
                        ["params"] = new JsonObject { ["sessionId"] = "v2-sess-1", ["terminalId"] = termId },
                    });
                    using (var toResp = JsonDocument.Parse((await reader.ReadLineAsync())!))
                        observedTermOutput = toResp.RootElement.GetProperty("result").GetProperty("output").GetString();

                    // Tool call referencing client terminalId
                    await SendAsync(new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["method"] = "session/update",
                        ["params"] = new JsonObject
                        {
                            ["sessionId"] = "v2-sess-1",
                            ["update"] = new JsonObject
                            {
                                ["sessionUpdate"] = "tool_call",
                                ["toolCallId"] = "tc-term-1",
                                ["title"] = "Client terminal",
                                ["kind"] = "execute",
                                ["status"] = "completed",
                                ["content"] = new JsonArray { new JsonObject { ["type"] = "terminal", ["terminalId"] = termId } },
                            },
                        },
                    });

                    await SendAsync(new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = "t-rel",
                        ["method"] = "terminal/release",
                        ["params"] = new JsonObject { ["sessionId"] = "v2-sess-1", ["terminalId"] = termId },
                    });
                    using (var trResp = JsonDocument.Parse((await reader.ReadLineAsync())!))
                        Assert.True(trResp.RootElement.TryGetProperty("result", out _));

                    // URL elicitation + elicitation/complete notification
                    await SendAsync(new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = "el-url",
                        ["method"] = "elicitation/create",
                        ["params"] = new JsonObject
                        {
                            ["mode"] = "url",
                            ["elicitationId"] = "el-url-1",
                            ["url"] = "https://oauth.example.com/start",
                            ["message"] = "Authorize GitHub access",
                        },
                    });
                    using (var urlResp = JsonDocument.Parse((await reader.ReadLineAsync())!))
                        observedUrlReplyAction = urlResp.RootElement.GetProperty("result").GetProperty("action").GetString();

                    await SendAsync(new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["method"] = "elicitation/complete",
                        ["params"] = new JsonObject { ["elicitationId"] = "el-url-1" },
                    });

                    // Form elicitation with integer schema & required + default
                    await SendAsync(new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = "el-num",
                        ["method"] = "elicitation/create",
                        ["params"] = new JsonObject
                        {
                            ["message"] = "Configure retries",
                            ["requestedSchema"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["required"] = new JsonArray("retries"),
                                ["properties"] = new JsonObject
                                {
                                    ["retries"] = new JsonObject
                                    {
                                        ["type"] = "integer",
                                        ["title"] = "Max retries",
                                        ["default"] = 2,
                                        ["minimum"] = 1,
                                        ["maximum"] = 5,
                                    },
                                },
                            },
                        },
                    });
                    using (var numResp = JsonDocument.Parse((await reader.ReadLineAsync())!))
                        observedRetriesJson = numResp.RootElement.GetProperty("result").GetProperty("content").GetProperty("retries").GetInt32();

                    // Form elicitation declined by user
                    await SendAsync(new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = "el-dec",
                        ["method"] = "elicitation/create",
                        ["params"] = new JsonObject
                        {
                            ["message"] = "Decline me",
                            ["requestedSchema"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["note"] = new JsonObject { ["type"] = "string", ["title"] = "Note" },
                                },
                            },
                        },
                    });
                    using (var decResp = JsonDocument.Parse((await reader.ReadLineAsync())!))
                        observedDeclineAction = decResp.RootElement.GetProperty("result").GetProperty("action").GetString();

                    // Rich notifications: audio chunk, plan_removed, compaction_update, cursor_update, current_model_update, session_info_update
                    await SendAsync(new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["method"] = "session/update",
                        ["params"] = new JsonObject
                        {
                            ["sessionId"] = "v2-sess-1",
                            ["update"] = new JsonObject
                            {
                                ["sessionUpdate"] = "agent_message_chunk",
                                ["content"] = new JsonObject { ["type"] = "audio", ["data"] = "UklGRg==", ["mimeType"] = "audio/wav" },
                            },
                        },
                    });
                    await SendAsync(new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["method"] = "session/update",
                        ["params"] = new JsonObject
                        {
                            ["sessionId"] = "v2-sess-1",
                            ["update"] = new JsonObject { ["sessionUpdate"] = "plan_removed" },
                        },
                    });
                    await SendAsync(new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["method"] = "session/update",
                        ["params"] = new JsonObject
                        {
                            ["sessionId"] = "v2-sess-1",
                            ["update"] = new JsonObject { ["sessionUpdate"] = "compaction_update", ["status"] = "completed", ["summary"] = "Trimmed history" },
                        },
                    });
                    await SendAsync(new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["method"] = "session/update",
                        ["params"] = new JsonObject
                        {
                            ["sessionId"] = "v2-sess-1",
                            ["update"] = new JsonObject { ["sessionUpdate"] = "cursor_update", ["cursorId"] = "cur-99", ["messageId"] = "m-9" },
                        },
                    });
                    await SendAsync(new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["method"] = "session/update",
                        ["params"] = new JsonObject
                        {
                            ["sessionId"] = "v2-sess-1",
                            ["update"] = new JsonObject { ["sessionUpdate"] = "current_model_update", ["currentModelId"] = "gpt-5" },
                        },
                    });
                    await SendAsync(new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["method"] = "session/update",
                        ["params"] = new JsonObject
                        {
                            ["sessionId"] = "v2-sess-1",
                            ["update"] = new JsonObject { ["sessionUpdate"] = "session_info_update", ["title"] = "Renamed title" },
                        },
                    });

                    await SendAsync(new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = promptId,
                        ["result"] = new JsonObject
                        {
                            ["stopReason"] = "max_tokens",
                            ["usage"] = new JsonObject { ["inputTokens"] = 100, ["outputTokens"] = 50, ["totalTokens"] = 150 },
                        },
                    });
                }

                // 9. auth/logout & session/close
                using (var logoutDoc = JsonDocument.Parse((await reader.ReadLineAsync())!))
                {
                    Assert.Equal("auth/logout", logoutDoc.RootElement.GetProperty("method").GetString());
                    await SendAsync(new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = logoutDoc.RootElement.GetProperty("id").GetInt32(),
                        ["result"] = new JsonObject(),
                    });
                }

                using (var closeDoc = JsonDocument.Parse((await reader.ReadLineAsync())!))
                {
                    Assert.Equal("session/close", closeDoc.RootElement.GetProperty("method").GetString());
                    await SendAsync(new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = closeDoc.RootElement.GetProperty("id").GetInt32(),
                        ["result"] = new JsonObject(),
                    });
                }
            });

            var started = await runtime.StartSessionAsync(tempDir, additionalDirectories: ["/tmp/extra-dir"]);
            Assert.Equal("v2-sess-1", started.SessionId);
            Assert.Equal(2, runtime.Initialized!.ProtocolVersion);
            Assert.True(runtime.Initialized.AgentCapabilities.AuthCapabilities.Logout);
            Assert.Equal("env_var", Assert.Single(runtime.Initialized.AuthMethods).Type);
            Assert.Equal("MY_API_KEY", Assert.Single(runtime.Initialized.AuthMethods[0].EnvVars!).Name);
            Assert.Equal(["local-stdio", "remote-http"], observedMcpNames);
            Assert.Equal(["/tmp/extra-dir"], observedAddDirs);

            await runtime.AuthenticateAsync("api_key", new Dictionary<string, string> { ["source"] = "test" });
            var listed = await runtime.ListSessionsAsync(tempDir);
            Assert.Equal("page-2", listed.NextCursor);
            Assert.Equal("Main work", Assert.Single(listed.Sessions).Title);

            var forked = await runtime.ForkSessionAsync(cursorId: "cur-42");
            Assert.Equal("fork", forked.Kind);
            Assert.Equal("v2-sess-fork-2", forked.SessionId);

            await runtime.DeleteSessionAsync("missing-sess");
            await runtime.ArchiveSessionAsync();

            var promptRes = await runtime.PromptAsync(
                "Process these",
                [
                    new AcpPromptAttachment
                    {
                        Kind = "resource",
                        Path = Path.Combine(tempDir, "ctx.cs"),
                        DisplayName = "ctx.cs",
                        MimeType = "text/plain",
                        TextContent = "class C {}",
                        Annotations = new AcpContentAnnotations { Audience = ["assistant"], Priority = 0.9 },
                    },
                    new AcpPromptAttachment
                    {
                        Kind = "audio",
                        Path = "",
                        DisplayName = "clip.wav",
                        MimeType = "audio/wav",
                        Base64Data = "UklGRg==",
                    },
                ]);

            await runtime.LogoutAsync();
            await runtime.CloseSessionAsync();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal("max_tokens", promptRes.StopReason);
            Assert.Equal(150, promptRes.Usage!.TotalTokens);
            Assert.Equal(["text", "resource", "audio"], observedPromptBlockTypes);
            Assert.Equal("beta", observedReadBack);
            Assert.Contains("term-ok", observedTermOutput);
            Assert.Equal("https://oauth.example.com/start", observedUrlElicitUrl);
            Assert.Equal("el-url-1", observedUrlElicitId);
            Assert.Equal("accept", observedUrlReplyAction);
            Assert.Equal(3, observedRetriesJson);
            Assert.Equal("decline", observedDeclineAction);
            Assert.NotNull(observedNumberField);
            Assert.Equal("integer", observedNumberField!.ValueType);
            Assert.True(observedNumberField.Required);
            Assert.Equal("2", observedNumberField.DefaultValue);

            var all = events.ToList();
            Assert.Single(all.OfType<AcpToolCallEvent>(), e => e.Update.ToolCallId == "tc-term-1" && e.Update.TerminalIds.Count == 1);
            Assert.Single(all.OfType<AcpElicitationCompletedEvent>(), e => e.ElicitationId == "el-url-1");
            Assert.Single(all.OfType<AcpAudioEvent>(), e => e.Audio.MimeType == "audio/wav");
            Assert.Single(all.OfType<AcpPlanRemovedEvent>());
            Assert.Single(all.OfType<AcpCompactionEvent>(), e => e.Update.Status == "completed" && e.Update.Summary == "Trimmed history");
            Assert.Single(all.OfType<AcpCursorUpdatedEvent>(), e => e.Cursor.CursorId == "cur-99");
            Assert.Contains(all.OfType<AcpModelsUpdatedEvent>(), e => e.Models.CurrentModelId == "gpt-5");
            Assert.Single(all.OfType<AcpTitleUpdatedEvent>(), e => e.Title == "Renamed title");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task Runtime_rejects_fs_and_terminal_callbacks_when_disabled_by_default()
    {
        using var clientToAgentServer = new AnonymousPipeServerStream(PipeDirection.In);
        using var clientToAgentClient = new AnonymousPipeClientStream(PipeDirection.Out, clientToAgentServer.ClientSafePipeHandle);
        using var agentToClientServer = new AnonymousPipeServerStream(PipeDirection.Out);
        using var agentToClientClient = new AnonymousPipeClientStream(PipeDirection.In, agentToClientServer.ClientSafePipeHandle);

        await using var runtime = new AcpSessionRuntime(agentToClientClient, clientToAgentClient);
        int? fsErrorCode = null;
        int? termErrorCode = null;

        var serverTask = Task.Run(async () =>
        {
            using var reader = new StreamReader(clientToAgentServer, Encoding.UTF8);
            await using var writer = new StreamWriter(agentToClientServer, new UTF8Encoding(false)) { AutoFlush = true };
            async Task SendAsync(JsonNode node) => await writer.WriteLineAsync(node.ToJsonString());

            using (var initDoc = JsonDocument.Parse((await reader.ReadLineAsync())!))
            {
                var caps = initDoc.RootElement.GetProperty("params").GetProperty("clientCapabilities");
                Assert.False(caps.GetProperty("fs").GetProperty("readTextFile").GetBoolean());
                Assert.False(caps.GetProperty("fs").GetProperty("writeTextFile").GetBoolean());
                Assert.False(caps.GetProperty("terminal").GetBoolean());
                await SendAsync(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = initDoc.RootElement.GetProperty("id").GetInt32(),
                    ["result"] = new JsonObject { ["protocolVersion"] = 1 },
                });
            }

            using (var newDoc = JsonDocument.Parse((await reader.ReadLineAsync())!))
            {
                await SendAsync(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = newDoc.RootElement.GetProperty("id").GetInt32(),
                    ["result"] = new JsonObject { ["sessionId"] = "safe-sess" },
                });
            }

            using (var promptDoc = JsonDocument.Parse((await reader.ReadLineAsync())!))
            {
                await SendAsync(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = "fs-1",
                    ["method"] = "fs/read_text_file",
                    ["params"] = new JsonObject { ["sessionId"] = "safe-sess", ["path"] = "/etc/hosts" },
                });
                using (var r1 = JsonDocument.Parse((await reader.ReadLineAsync())!))
                    fsErrorCode = r1.RootElement.GetProperty("error").GetProperty("code").GetInt32();

                await SendAsync(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = "tm-1",
                    ["method"] = "terminal/create",
                    ["params"] = new JsonObject { ["sessionId"] = "safe-sess", ["command"] = "id" },
                });
                using (var r2 = JsonDocument.Parse((await reader.ReadLineAsync())!))
                    termErrorCode = r2.RootElement.GetProperty("error").GetProperty("code").GetInt32();

                await SendAsync(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = promptDoc.RootElement.GetProperty("id").GetInt32(),
                    ["result"] = new JsonObject { ["stopReason"] = "end_turn" },
                });
            }
        });

        await runtime.StartSessionAsync("/tmp");
        await runtime.PromptAsync("hi");
        await serverTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(-32601, fsErrorCode);
        Assert.Equal(-32601, termErrorCode);
    }

    private sealed class CustomErrorAndNotificationDialect : IAcpDialect
    {
        public string Id => "custom-test";

        public string? FormatPromptError(AcpRpcException error) =>
            $"Formatted({error.Code}): {error.Message}";

        public AcpRuntimeEvent? TryTranslateNotification(string method, JsonElement? parameters) =>
            method == "_vendor/progress"
                ? new AcpDiagnosticEvent("vendor-progress-seen")
                : null;
    }

    [Fact]
    public async Task Runtime_applies_dialect_format_prompt_error_translate_notification_and_drain_notifications()
    {
        using var clientToAgentServer = new AnonymousPipeServerStream(PipeDirection.In);
        using var clientToAgentClient = new AnonymousPipeClientStream(PipeDirection.Out, clientToAgentServer.ClientSafePipeHandle);
        using var agentToClientServer = new AnonymousPipeServerStream(PipeDirection.Out);
        using var agentToClientClient = new AnonymousPipeClientStream(PipeDirection.In, agentToClientServer.ClientSafePipeHandle);

        var events = new System.Collections.Concurrent.ConcurrentQueue<AcpRuntimeEvent>();
        await using var runtime = new AcpSessionRuntime(
            agentToClientClient,
            clientToAgentClient,
            new AcpSessionRuntimeOptions { Dialect = new CustomErrorAndNotificationDialect() });
        runtime.EventReceived += events.Enqueue;

        var serverTask = Task.Run(async () =>
        {
            using var reader = new StreamReader(clientToAgentServer, Encoding.UTF8);
            await using var writer = new StreamWriter(agentToClientServer, new UTF8Encoding(false)) { AutoFlush = true };
            async Task SendAsync(JsonNode node) => await writer.WriteLineAsync(node.ToJsonString());

            using (var initDoc = JsonDocument.Parse((await reader.ReadLineAsync())!))
            {
                await SendAsync(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = initDoc.RootElement.GetProperty("id").GetInt32(),
                    ["result"] = new JsonObject { ["protocolVersion"] = 1 },
                });
            }
            using (var newDoc = JsonDocument.Parse((await reader.ReadLineAsync())!))
            {
                await SendAsync(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = newDoc.RootElement.GetProperty("id").GetInt32(),
                    ["result"] = new JsonObject { ["sessionId"] = "dial-sess" },
                });
            }
            using (var promptDoc = JsonDocument.Parse((await reader.ReadLineAsync())!))
            {
                await SendAsync(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["method"] = "_vendor/progress",
                    ["params"] = new JsonObject { ["pct"] = 50 },
                });
                await SendAsync(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = promptDoc.RootElement.GetProperty("id").GetInt32(),
                    ["error"] = new JsonObject { ["code"] = -32010, ["message"] = "rate limited" },
                });
            }
        });

        await runtime.StartSessionAsync("/tmp");
        var promptEx = await Assert.ThrowsAsync<AcpRpcException>(() => runtime.PromptAsync("go"));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("Formatted(-32010): rate limited", promptEx.Message);
        Assert.Contains(events, e => e is AcpDiagnosticEvent { Message: "vendor-progress-seen" });

        // DrainNotifications is safe to invoke before disposal.
        runtime.DrainNotifications();
    }
}
