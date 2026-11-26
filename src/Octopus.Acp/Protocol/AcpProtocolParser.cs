using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Octopus.Acp.Dialects;
using Octopus.Core.Agents;

namespace Octopus.Acp.Protocol;

/// <summary>Zero-reflection <see cref="JsonElement"/> parsers and request builders for ACP schema-v1.21.0 + v2 preview.</summary>
public static class AcpProtocolParser
{
    public static AcpInitializeResult ParseInitialize(JsonElement root)
    {
        var result = new AcpInitializeResult
        {
            ProtocolVersion = TryInt(root, "protocolVersion") ?? AcpProtocolConstants.ProtocolVersion,
            RawMeta = TryClone(root, "_meta"),
        };

        if (TryObj(root, "agentInfo") is { } info)
        {
            result.AgentInfo = new AcpImplementation
            {
                Name = TryStr(info, "name") ?? "",
                Title = TryStr(info, "title"),
                Version = TryStr(info, "version") ?? "",
            };
        }

        if (TryObj(root, "agentCapabilities") is { } caps)
        {
            result.AgentCapabilities.LoadSession = TryBool(caps, "loadSession") == true;
            if (TryObj(caps, "promptCapabilities") is { } pc)
            {
                result.AgentCapabilities.PromptCapabilities = new AcpPromptCapabilities
                {
                    Image = TryBool(pc, "image") == true,
                    Audio = TryBool(pc, "audio") == true,
                    EmbeddedContext = TryBool(pc, "embeddedContext") == true,
                };
            }
            if (TryObj(caps, "mcpCapabilities") is { } mc)
            {
                result.AgentCapabilities.McpCapabilities = new AcpMcpCapabilities
                {
                    Http = TryBool(mc, "http") == true,
                    Sse = TryBool(mc, "sse") == true,
                };
            }
            if (TryObj(caps, "auth") is { } ac)
            {
                result.AgentCapabilities.AuthCapabilities = new AcpAuthCapabilities
                {
                    Logout = HasNonNull(ac, "logout") && TryBool(ac, "logout") != false,
                    Terminal = HasNonNull(ac, "terminal") && TryBool(ac, "terminal") != false,
                };
            }
            if (TryObj(caps, "sessionCapabilities") is { } sc)
            {
                result.AgentCapabilities.SessionCapabilities = new AcpSessionCapabilities
                {
                    SupportsList = HasNonNull(sc, "list"),
                    SupportsDelete = HasNonNull(sc, "delete"),
                    SupportsFork = HasNonNull(sc, "fork"),
                    SupportsResume = HasNonNull(sc, "resume"),
                    SupportsClose = HasNonNull(sc, "close"),
                    SupportsAdditionalDirectories = HasNonNull(sc, "additionalDirectories"),
                    SupportsArchive = HasNonNull(sc, "archive"),
                    SupportsCompaction = HasNonNull(sc, "compaction"),
                    SupportsCursors = HasNonNull(sc, "cursors"),
                };
            }
        }

        if (TryArr(root, "authMethods") is { } auths)
        {
            foreach (var a in auths.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object) continue;
                var id = TryStr(a, "id");
                if (string.IsNullOrEmpty(id)) continue;
                var method = new AcpAuthMethod
                {
                    Id = id,
                    Name = TryStr(a, "name") ?? id,
                    Description = TryStr(a, "description"),
                    Type = TryStr(a, "type"),
                    Link = TryStr(a, "link"),
                };
                if (TryArr(a, "args") is { } argsEl)
                {
                    method.Args = argsEl.EnumerateArray()
                        .Where(x => x.ValueKind == JsonValueKind.String)
                        .Select(x => x.GetString()!)
                        .ToList();
                }
                if ((TryArr(a, "vars") ?? TryArr(a, "envVars")) is { } varsEl)
                {
                    var envVars = new List<AcpAuthEnvVar>();
                    foreach (var v in varsEl.EnumerateArray())
                    {
                        if (v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } nameStr)
                        {
                            envVars.Add(new AcpAuthEnvVar { Name = nameStr });
                        }
                        else if (v.ValueKind == JsonValueKind.Object && TryStr(v, "name") is { Length: > 0 } varName)
                        {
                            envVars.Add(new AcpAuthEnvVar
                            {
                                Name = varName,
                                Label = TryStr(v, "label"),
                                Secret = TryBool(v, "secret") ?? true,
                                Link = TryStr(v, "link"),
                            });
                        }
                    }
                    if (envVars.Count > 0) method.EnvVars = envVars;
                }
                result.AuthMethods.Add(method);
            }
        }

        return result;
    }

    public static AcpSessionStartResult ParseSessionStart(string kind, string? fallbackSessionId, JsonElement root)
    {
        return new AcpSessionStartResult
        {
            Kind = kind,
            SessionId = TryStr(root, "sessionId") ?? fallbackSessionId ?? "",
            Modes = TryObj(root, "modes") is { } m ? ParseModeState(m) : null,
            ConfigOptions = TryArr(root, "configOptions") is { } c ? ParseConfigOptions(c) : [],
            Models = TryObj(root, "models") is { } md ? ParseModelState(md) : null,
        };
    }

    public static AcpSessionModeState ParseModeState(JsonElement obj)
    {
        var state = new AcpSessionModeState
        {
            CurrentModeId = TryStr(obj, "currentModeId") ?? "",
        };
        if (TryArr(obj, "availableModes") is { } arr)
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = TryStr(item, "id");
                if (string.IsNullOrEmpty(id)) continue;
                state.AvailableModes.Add(new AcpSessionMode
                {
                    Id = id,
                    Name = TryStr(item, "name") ?? id,
                    Description = TryStr(item, "description"),
                });
            }
        }
        return state;
    }

    public static List<AcpSessionConfigOption> ParseConfigOptions(JsonElement arr)
    {
        var list = new List<AcpSessionConfigOption>();
        if (arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = TryStr(item, "id");
            if (string.IsNullOrEmpty(id)) continue;
            var type = TryStr(item, "type") ?? "select";
            var opt = new AcpSessionConfigOption
            {
                Id = id,
                Name = TryStr(item, "name") ?? id,
                Description = TryStr(item, "description"),
                Category = TryStr(item, "category"),
                Type = type,
            };
            if (type == "boolean")
            {
                opt.CurrentBoolValue = TryBool(item, "currentValue");
            }
            else
            {
                opt.CurrentValue = TryStr(item, "currentValue");
                if (TryArr(item, "options") is { } optsEl)
                {
                    foreach (var entry in optsEl.EnumerateArray())
                    {
                        if (entry.ValueKind != JsonValueKind.Object) continue;
                        if (TryArr(entry, "options") is { } grouped)
                        {
                            var groupName = TryStr(entry, "name") ?? TryStr(entry, "group");
                            foreach (var sub in grouped.EnumerateArray())
                            {
                                AddSelectOption(opt.Options, sub, groupName);
                            }
                        }
                        else
                        {
                            AddSelectOption(opt.Options, entry, null);
                        }
                    }
                }
            }
            list.Add(opt);
        }
        return list;
    }

    public static AcpSessionModelState ParseModelState(JsonElement obj)
    {
        var state = new AcpSessionModelState
        {
            CurrentModelId = TryStr(obj, "currentModelId") ?? "",
        };
        if (TryArr(obj, "availableModels") is { } arr)
        {
            foreach (var m in arr.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object) continue;
                var id = TryStr(m, "modelId");
                if (string.IsNullOrEmpty(id)) continue;
                state.AvailableModels.Add(new AcpModelInfo
                {
                    ModelId = id,
                    Name = TryStr(m, "name") ?? id,
                    Description = TryStr(m, "description"),
                    Meta = TryClone(m, "_meta"),
                });
            }
        }
        return state;
    }

    public static AcpPromptResult ParsePromptResult(JsonElement root)
    {
        var result = new AcpPromptResult
        {
            StopReason = TryStr(root, "stopReason") ?? "end_turn",
            Meta = TryClone(root, "_meta"),
        };
        if (TryObj(root, "usage") is { } u)
        {
            result.Usage = new AcpUsage
            {
                TotalTokens = TryLong(u, "totalTokens") ?? 0,
                InputTokens = TryLong(u, "inputTokens") ?? 0,
                OutputTokens = TryLong(u, "outputTokens") ?? 0,
                ThoughtTokens = TryLong(u, "thoughtTokens"),
                CachedReadTokens = TryLong(u, "cachedReadTokens"),
                CachedWriteTokens = TryLong(u, "cachedWriteTokens"),
            };
        }
        return result;
    }

    public static AcpToolCallUpdate ParseToolCallUpdate(JsonElement obj, IAcpDialect dialect)
    {
        var tc = new AcpToolCallUpdate
        {
            ToolCallId = TryStr(obj, "toolCallId") ?? "",
            Title = TryStr(obj, "title"),
            Name = TryStr(obj, "name"),
            Kind = TryStr(obj, "kind"),
            Status = TryStr(obj, "status"),
            RawInput = TryClone(obj, "rawInput") ?? TryClone(obj, "args"),
            RawOutput = TryClone(obj, "rawOutput"),
            Meta = TryClone(obj, "_meta"),
        };

        if (TryArr(obj, "locations") is { } locs)
        {
            foreach (var loc in locs.EnumerateArray())
            {
                if (loc.ValueKind != JsonValueKind.Object) continue;
                var path = TryStr(loc, "path");
                if (!string.IsNullOrEmpty(path))
                {
                    tc.Locations.Add(new AcpToolLocation { Path = path, Line = TryInt(loc, "line") });
                }
            }
        }

        if (TryArr(obj, "content") is { } contents)
        {
            foreach (var block in contents.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object) continue;
                var bType = TryStr(block, "type");
                if (bType == "diff")
                {
                    var path = TryStr(block, "path") ?? "";
                    tc.Diffs.Add(new AcpToolDiff
                    {
                        Path = path,
                        OldText = TryStr(block, "oldText"),
                        NewText = TryStr(block, "newText") ?? "",
                    });
                }
                else if (bType == "terminal")
                {
                    var termId = TryStr(block, "terminalId");
                    if (!string.IsNullOrWhiteSpace(termId) && !tc.TerminalIds.Contains(termId, StringComparer.Ordinal))
                    {
                        tc.TerminalIds.Add(termId);
                    }
                }
                else if (bType == "content" && TryObj(block, "content") is { } inner)
                {
                    if (ExtractImageContent(inner) is { } img)
                    {
                        AddUniqueImage(tc.Images, img);
                    }
                    else
                    {
                        var text = ExtractContentBlockText(inner);
                        if (!string.IsNullOrEmpty(text)) tc.TextOutputs.Add(text);
                    }
                }
                else if (ExtractImageContent(block) is { } directImg)
                {
                    AddUniqueImage(tc.Images, directImg);
                }
            }
        }

        // Extract generic command/stdout/stderr/exitCode and subagent/workflow/background metadata before dialect hook.
        tc.ParentToolCallId ??= TryStr(obj, "parentToolCallId")
            ?? TryStr(obj, "parentToolUseId")
            ?? TryStr(obj, "parent_tool_use_id")
            ?? TryStr(tc.Meta, "parentToolCallId")
            ?? TryStr(tc.Meta, "parentToolUseId")
            ?? TryStr(tc.Meta, "parent_tool_use_id")
            ?? TryStr(TryObj(tc.Meta, "claudeCode"), "parentToolUseId")
            ?? TryStr(TryObj(tc.Meta, "claudeCode"), "parent_tool_use_id");

        tc.Name ??= TryStr(TryObj(tc.Meta, "claudeCode"), "toolName")
            ?? TryStr(tc.Meta, "toolName");

        if (tc.RawInput is { ValueKind: JsonValueKind.Object } rawIn)
        {
            tc.Command ??= TryStr(rawIn, "command") ?? TryStr(rawIn, "cmd");
            tc.WorkingDirectory ??= TryStr(rawIn, "cwd");
            tc.Description ??= TryStr(rawIn, "description") ?? TryStr(rawIn, "toolSummary");
            if (TryBool(rawIn, "run_in_background") == true || TryBool(rawIn, "runInBackground") == true || TryBool(rawIn, "background") == true || TryBool(rawIn, "IsDaemon") == true)
            {
                tc.IsBackground = true;
            }
            tc.SubagentType ??= TryStr(rawIn, "subagent_type")
                ?? TryStr(rawIn, "subagentType")
                ?? TryStr(rawIn, "agent_type")
                ?? TryStr(rawIn, "agentType")
                ?? TryStr(rawIn, "TypeName")
                ?? TryStr(rawIn, "typeName");
            var model = TryStr(rawIn, "model") ?? TryStr(rawIn, "Model");
            if (!string.IsNullOrWhiteSpace(model) && !string.Equals(model, "inherit", StringComparison.OrdinalIgnoreCase))
            {
                tc.SubagentModel ??= model;
            }
            var isolation = TryStr(rawIn, "isolation") ?? TryStr(rawIn, "Workspace") ?? TryStr(rawIn, "workspace");
            if (!string.IsNullOrWhiteSpace(isolation) && !string.Equals(isolation, "inherit", StringComparison.OrdinalIgnoreCase))
            {
                tc.WorktreeIsolation ??= isolation;
            }
            tc.BackgroundTaskId ??= TryStr(rawIn, "task_id")
                ?? TryStr(rawIn, "taskId")
                ?? TryStr(rawIn, "TaskId")
                ?? TryStr(rawIn, "shell_id")
                ?? TryStr(rawIn, "shellId")
                ?? TryStr(rawIn, "bash_id")
                ?? TryStr(rawIn, "bashId");
            tc.PlanMarkdown ??= TryStr(rawIn, "plan");
        }
        if (tc.RawOutput is { ValueKind: JsonValueKind.Object } rawOut)
        {
            tc.Stdout ??= TryStr(rawOut, "stdout");
            tc.Stderr ??= TryStr(rawOut, "stderr");
            tc.ExitCode ??= TryInt(rawOut, "exitCode") ?? TryInt(rawOut, "exit_code");
            tc.BackgroundTaskId ??= TryStr(rawOut, "backgroundTaskId")
                ?? TryStr(rawOut, "task_id")
                ?? TryStr(rawOut, "taskId")
                ?? TryStr(rawOut, "shell_id")
                ?? TryStr(rawOut, "shellId")
                ?? TryStr(rawOut, "bash_id");
            if (TryBool(rawOut, "isBackground") == true || TryBool(rawOut, "background") == true)
            {
                tc.IsBackground = true;
            }
        }

        ClassifyToolSemantics(tc);
        dialect.NormalizeToolUpdate(tc);
        return tc;
    }

    internal static void ClassifyToolSemantics(AcpToolCallUpdate tc)
    {
        var toolName = tc.Name;
        if (string.IsNullOrWhiteSpace(toolName) && !string.IsNullOrWhiteSpace(tc.Title))
        {
            var trimmed = tc.Title.Trim();
            if (trimmed.StartsWith("Run ", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[4..].TrimStart();
            }
            var sep = trimmed.IndexOfAny([':', '(', ' ']);
            var head = sep > 0 ? trimmed[..sep] : trimmed;
            if (head is "Agent" or "Task" or "TodoWrite" or "TaskCreate" or "TaskUpdate" or "EnterPlanMode" or "ExitPlanMode"
                or "TaskOutput" or "BashOutput" or "TaskStop" or "KillShell" or "Bash"
                or "invoke_subagent" or "update_steps" or "manage_subagents" or "manage_task"
                or "image_generation" or "image_gen" or "generate_image" or "create_image" or "text_to_image")
            {
                toolName = head;
            }
        }

        if (string.Equals(tc.Status, "generating", StringComparison.OrdinalIgnoreCase)
            || string.Equals(tc.Status, "running", StringComparison.OrdinalIgnoreCase))
        {
            tc.Status = "in_progress";
        }

        if (IsImageGenerationTool(toolName, tc.Kind, tc.Title))
        {
            tc.IsImageGeneration = true;
            if (string.IsNullOrWhiteSpace(tc.Kind)
                || string.Equals(tc.Kind, "other", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tc.Kind, "image_generation", StringComparison.OrdinalIgnoreCase))
            {
                tc.Kind = "image";
            }
        }

        ExtractImageGenerationMetadata(tc);

        if (string.Equals(toolName, "invoke_subagent", StringComparison.OrdinalIgnoreCase))
        {
            tc.IsSubagentLaunch = true;
            tc.IsBackground = true;
            tc.SubagentRunsAsynchronously = true;
            tc.SubagentType ??= "self";
            if (tc.RawInput is { ValueKind: JsonValueKind.Object } ri)
            {
                tc.Description ??= TryStr(ri, "Role") ?? TryStr(ri, "role");
                tc.SubagentPrompt ??= TryStr(ri, "Prompt") ?? TryStr(ri, "prompt");
                if (string.IsNullOrWhiteSpace(tc.Description) && tc.SubagentPrompt is { Length: > 0 } prompt)
                {
                    var firstLine = prompt.Replace("\r\n", "\n")
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(firstLine))
                    {
                        tc.Description = firstLine.Length > 100 ? firstLine[..100] + "…" : firstLine;
                    }
                }
            }
        }
        else if (string.Equals(toolName, "Agent", StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolName, "Task", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(tc.SubagentType))
        {
            tc.IsSubagentLaunch = true;
            tc.SubagentType ??= "general-purpose";
            if (tc.RawInput is { ValueKind: JsonValueKind.Object } ri)
            {
                tc.SubagentPrompt ??= TryStr(ri, "prompt") ?? TryStr(ri, "Prompt");
            }
        }
        else if (string.Equals(toolName, "EnterPlanMode", StringComparison.OrdinalIgnoreCase))
        {
            tc.WorkflowAction = "enter_plan";
        }
        else if (string.Equals(toolName, "ExitPlanMode", StringComparison.OrdinalIgnoreCase))
        {
            tc.WorkflowAction = "exit_plan";
        }
        else if (string.Equals(toolName, "TodoWrite", StringComparison.OrdinalIgnoreCase))
        {
            tc.WorkflowAction = "todo_write";
            if (tc.RawInput is { ValueKind: JsonValueKind.Object } ri && TryArr(ri, "todos") is { } todos)
            {
                tc.WorkflowTasks = ParsePlanEntries(todos);
            }
        }
        else if (string.Equals(toolName, "update_steps", StringComparison.OrdinalIgnoreCase))
        {
            tc.WorkflowAction = "todo_write";
            if (tc.RawInput is { ValueKind: JsonValueKind.Object } ri && TryArr(ri, "steps") is { } steps)
            {
                tc.WorkflowTasks = ParsePlanEntries(steps);
            }
        }
        else if (string.Equals(toolName, "manage_subagents", StringComparison.OrdinalIgnoreCase))
        {
            if (tc.RawInput is { ValueKind: JsonValueKind.Object } ri)
            {
                var action = TryStr(ri, "Action") ?? TryStr(ri, "action");
                if (string.Equals(action, "kill_all", StringComparison.OrdinalIgnoreCase))
                {
                    tc.WorkflowAction = "subagent_stop_all";
                }
                else if (string.Equals(action, "kill", StringComparison.OrdinalIgnoreCase))
                {
                    tc.WorkflowAction = "subagent_stop";
                    if (TryArr(ri, "ConversationIds") is { } ids)
                    {
                        foreach (var idEl in ids.EnumerateArray())
                        {
                            if (idEl.ValueKind == JsonValueKind.String && idEl.GetString() is { Length: > 0 } id)
                            {
                                tc.TargetSubagentIds.Add(id);
                            }
                        }
                    }
                }
            }
        }
        else if (string.Equals(toolName, "manage_task", StringComparison.OrdinalIgnoreCase))
        {
            if (tc.RawInput is { ValueKind: JsonValueKind.Object } ri)
            {
                var action = TryStr(ri, "Action") ?? TryStr(ri, "action");
                if (string.Equals(action, "status", StringComparison.OrdinalIgnoreCase))
                {
                    tc.WorkflowAction = "background_output";
                }
                else if (string.Equals(action, "kill", StringComparison.OrdinalIgnoreCase))
                {
                    tc.WorkflowAction = "background_stop";
                }
            }
        }
        else if (string.Equals(toolName, "TaskCreate", StringComparison.OrdinalIgnoreCase))
        {
            tc.WorkflowAction = "task_create";
            if (tc.RawInput is { ValueKind: JsonValueKind.Object } ri)
            {
                var subject = TryStr(ri, "subject") ?? TryStr(ri, "content") ?? TryStr(ri, "description");
                if (!string.IsNullOrWhiteSpace(subject))
                {
                    tc.WorkflowTasks =
                    [
                        new AcpPlanEntry
                        {
                            Id = TryStr(ri, "taskId") ?? TryStr(ri, "id") ?? tc.ToolCallId,
                            Content = subject,
                            ActiveForm = TryStr(ri, "activeForm"),
                            Status = TryStr(ri, "status") ?? "pending",
                            Owner = TryStr(ri, "owner"),
                        },
                    ];
                }
            }
        }
        else if (string.Equals(toolName, "TaskUpdate", StringComparison.OrdinalIgnoreCase))
        {
            tc.WorkflowAction = "task_update";
            if (tc.RawInput is { ValueKind: JsonValueKind.Object } ri)
            {
                var entry = new AcpPlanEntry
                {
                    Id = TryStr(ri, "taskId") ?? TryStr(ri, "id"),
                    Content = TryStr(ri, "subject") ?? TryStr(ri, "content") ?? "",
                    ActiveForm = TryStr(ri, "activeForm"),
                    Status = TryStr(ri, "status") ?? "",
                    Owner = TryStr(ri, "owner"),
                };
                if (TryArr(ri, "addBlockedBy") is { } deps)
                {
                    foreach (var d in deps.EnumerateArray())
                    {
                        var depId = d.ValueKind == JsonValueKind.String ? d.GetString() : d.GetRawText();
                        if (!string.IsNullOrWhiteSpace(depId)) entry.BlockedBy.Add(depId);
                    }
                }
                tc.WorkflowTasks = [entry];
            }
        }
        else if (string.Equals(toolName, "TaskOutput", StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolName, "BashOutput", StringComparison.OrdinalIgnoreCase))
        {
            tc.WorkflowAction = "background_output";
        }
        else if (string.Equals(toolName, "TaskStop", StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolName, "KillShell", StringComparison.OrdinalIgnoreCase))
        {
            tc.WorkflowAction = "background_stop";
        }

        if (tc.IsBackground && string.IsNullOrWhiteSpace(tc.BackgroundTaskId))
        {
            var probe = tc.Stdout ?? tc.TextOutputs.FirstOrDefault()
                ?? (tc.RawOutput is { ValueKind: JsonValueKind.String } rs ? rs.GetString() : null);
            if (!string.IsNullOrEmpty(probe))
            {
                var idx = probe.IndexOf("ID:", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var tail = probe[(idx + 3)..].TrimStart();
                    var len = 0;
                    while (len < tail.Length && (char.IsLetterOrDigit(tail[len]) || tail[len] is '-' or '_')) len++;
                    if (len > 0) tc.BackgroundTaskId = tail[..len];
                }
            }
        }
    }

    public static AcpPermissionRequest? ParsePermissionRequest(JsonElement? parameters, string requestId, IAcpDialect dialect)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } obj) return null;
        var sessionId = TryStr(obj, "sessionId") ?? "";
        var tc = TryObj(obj, "toolCall") is { } tcEl
            ? ParseToolCallUpdate(tcEl, dialect)
            : new AcpToolCallUpdate();
        var options = new List<AcpPermissionOption>();
        if (TryArr(obj, "options") is { } opts)
        {
            foreach (var o in opts.EnumerateArray())
            {
                if (o.ValueKind != JsonValueKind.Object) continue;
                var id = TryStr(o, "optionId");
                if (string.IsNullOrEmpty(id)) continue;
                var kind = TryStr(o, "kind") ?? "allow_once";
                var name = TryStr(o, "name");
                // Goose and similar CLIs sometimes echo raw kind ids like "allow_once" as name.
                if (string.IsNullOrEmpty(name) || name == id)
                {
                    name = kind switch
                    {
                        "allow_once" => "Allow",
                        "allow_always" => "Always allow",
                        "reject_once" => "Deny",
                        "reject_always" => "Always deny",
                        _ => name ?? id,
                    };
                }
                options.Add(new AcpPermissionOption
                {
                    OptionId = id,
                    Name = name,
                    Kind = kind,
                    Meta = TryClone(o, "_meta"),
                });
            }
        }
        if (options.Count == 0) return null;
        return new AcpPermissionRequest
        {
            RequestId = requestId,
            SessionId = sessionId,
            ToolCall = tc,
            Options = options,
        };
    }

    /// <summary>Parses standard ACP <c>elicitation/create</c> (<c>mode == "form"</c>) into an interactive question card.</summary>
    public static AcpQuestionRequest? ParseFormElicitation(JsonElement? parameters, string requestId)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } obj) return null;
        var mode = TryStr(obj, "mode");
        if (mode is not null && !string.Equals(mode, "form", StringComparison.OrdinalIgnoreCase)) return null;

        var message = TryStr(obj, "message") ?? "The agent needs your input";
        var elicitationId = TryStr(obj, "elicitationId");
        var questions = new List<AcpQuestionItem>();

        if (TryObj(obj, "requestedSchema") is { } schema && TryObj(schema, "properties") is { } props)
        {
            var requiredSet = new HashSet<string>(StringComparer.Ordinal);
            if (TryArr(schema, "required") is { } reqArr)
            {
                foreach (var r in reqArr.EnumerateArray())
                {
                    if (r.ValueKind == JsonValueKind.String && r.GetString() is { Length: > 0 } rName)
                    {
                        requiredSet.Add(rName);
                    }
                }
            }

            foreach (var prop in props.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                var title = TryStr(prop.Value, "title") ?? TryStr(prop.Value, "description") ?? prop.Name;
                var type = TryStr(prop.Value, "type") ?? "string";
                var hasEnumOrOneOf = prop.Value.TryGetProperty("enum", out _) || prop.Value.TryGetProperty("oneOf", out _);
                string? defaultValue = null;
                if (prop.Value.TryGetProperty("default", out var defEl) && defEl.ValueKind != JsonValueKind.Null)
                {
                    defaultValue = defEl.ValueKind == JsonValueKind.String ? defEl.GetString() : defEl.GetRawText();
                }

                var item = new AcpQuestionItem
                {
                    Id = prop.Name,
                    Prompt = title,
                    ValueType = type,
                    Required = requiredSet.Contains(prop.Name),
                    DefaultValue = defaultValue,
                    Minimum = TryDouble(prop.Value, "minimum"),
                    Maximum = TryDouble(prop.Value, "maximum"),
                    MinLength = TryInt(prop.Value, "minLength"),
                    MaxLength = TryInt(prop.Value, "maxLength"),
                    Format = TryStr(prop.Value, "format"),
                    MultiSelect = type == "array",
                    AllowFreeText = (type is "string" or "number" or "integer") && !hasEnumOrOneOf,
                };

                ReadSchemaEnumOptions(prop.Value, item.Options);
                if (type == "array" && TryObj(prop.Value, "items") is { } arrItems)
                {
                    ReadSchemaEnumOptions(arrItems, item.Options);
                }
                if (type == "boolean" && item.Options.Count == 0)
                {
                    item.Options.Add(new AcpQuestionOption { Id = "true", Label = "Yes" });
                    item.Options.Add(new AcpQuestionOption { Id = "false", Label = "No" });
                }
                questions.Add(item);
            }
        }

        if (questions.Count == 0)
        {
            questions.Add(new AcpQuestionItem
            {
                Id = "answer",
                Prompt = message,
                ValueType = "string",
                AllowFreeText = true,
            });
        }

        return new AcpQuestionRequest
        {
            RequestId = requestId,
            ElicitationId = elicitationId,
            Title = message,
            Questions = questions,
        };
    }

    /// <summary>Parses standard ACP <c>elicitation/create</c> (<c>mode == "url"</c>) into an out-of-band URL authorization request.</summary>
    public static AcpUrlElicitationRequest? ParseUrlElicitation(JsonElement? parameters, string requestId)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } obj) return null;
        if (!string.Equals(TryStr(obj, "mode"), "url", StringComparison.OrdinalIgnoreCase)) return null;
        var url = TryStr(obj, "url");
        if (string.IsNullOrWhiteSpace(url)) return null;
        return new AcpUrlElicitationRequest
        {
            RequestId = requestId,
            ElicitationId = TryStr(obj, "elicitationId") ?? requestId,
            SessionId = TryStr(obj, "sessionId"),
            Message = TryStr(obj, "message") ?? "Authorization required",
            Url = url.Trim(),
        };
    }

    public static JsonNode BuildUrlElicitationReply(AcpElicitationAction action) => new JsonObject
    {
        ["action"] = action switch
        {
            AcpElicitationAction.Accept => "accept",
            AcpElicitationAction.Decline => "decline",
            _ => "cancel",
        },
    };

    public static JsonNode BuildFormElicitationReply(
        AcpQuestionRequest request,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? answers) =>
        BuildFormElicitationReply(
            request,
            answers is null
                ? new AcpFormElicitationResponse(AcpElicitationAction.Cancel)
                : new AcpFormElicitationResponse(AcpElicitationAction.Accept, answers));

    public static JsonNode BuildFormElicitationReply(
        AcpQuestionRequest request,
        AcpFormElicitationResponse response)
    {
        if (response.Action != AcpElicitationAction.Accept || response.Answers is null)
        {
            return new JsonObject
            {
                ["action"] = response.Action == AcpElicitationAction.Decline ? "decline" : "cancel",
            };
        }
        var answers = response.Answers;
        var content = new JsonObject();
        foreach (var q in request.Questions)
        {
            if (!answers.TryGetValue(q.Id, out var vals) || vals.Count == 0) continue;
            if (q.MultiSelect)
            {
                var arr = new JsonArray();
                foreach (var v in vals) arr.Add((JsonNode)JsonValue.Create(v));
                content[q.Id] = arr;
            }
            else if (string.Equals(q.ValueType, "boolean", StringComparison.OrdinalIgnoreCase)
                || (q.Options.Count == 2 && q.Options[0].Id == "true" && q.Options[1].Id == "false"))
            {
                content[q.Id] = string.Equals(vals[0], "true", StringComparison.OrdinalIgnoreCase);
            }
            else if (string.Equals(q.ValueType, "integer", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(vals[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var intVal))
            {
                content[q.Id] = intVal;
            }
            else if (string.Equals(q.ValueType, "number", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(vals[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var dblVal))
            {
                content[q.Id] = dblVal;
            }
            else
            {
                content[q.Id] = vals[0];
            }
        }
        return new JsonObject
        {
            ["action"] = "accept",
            ["content"] = content,
        };
    }

    public static JsonArray SerializeMcpServers(
        IReadOnlyList<AcpMcpServerConfig>? servers,
        AcpMcpCapabilities agentCaps)
    {
        var arr = new JsonArray();
        if (servers is not { Count: > 0 }) return arr;

        foreach (var srv in servers)
        {
            if (srv is null || !srv.Enabled || string.IsNullOrWhiteSpace(srv.Name)) continue;
            var transport = string.IsNullOrWhiteSpace(srv.Transport) ? "stdio" : srv.Transport.Trim().ToLowerInvariant();
            if (transport == "stdio")
            {
                if (string.IsNullOrWhiteSpace(srv.Command)) continue;
                var argsArr = new JsonArray();
                foreach (var a in srv.Args) argsArr.Add((JsonNode)JsonValue.Create(a));
                var envArr = new JsonArray();
                foreach (var (k, v) in srv.Env)
                {
                    if (!string.IsNullOrWhiteSpace(k))
                    {
                        envArr.Add((JsonNode)new JsonObject { ["name"] = k, ["value"] = v });
                    }
                }
                arr.Add((JsonNode)new JsonObject
                {
                    ["name"] = srv.Name,
                    ["command"] = srv.Command,
                    ["args"] = argsArr,
                    ["env"] = envArr,
                });
            }
            else if ((transport == "http" && agentCaps.Http) || (transport == "sse" && agentCaps.Sse))
            {
                if (string.IsNullOrWhiteSpace(srv.Url)) continue;
                var hdrArr = new JsonArray();
                foreach (var (k, v) in srv.Headers)
                {
                    if (!string.IsNullOrWhiteSpace(k))
                    {
                        hdrArr.Add((JsonNode)new JsonObject { ["name"] = k, ["value"] = v });
                    }
                }
                arr.Add((JsonNode)new JsonObject
                {
                    ["type"] = transport,
                    ["name"] = srv.Name,
                    ["url"] = srv.Url,
                    ["headers"] = hdrArr,
                });
            }
        }
        return arr;
    }

    public static AcpSessionListResult ParseSessionListResult(JsonElement root)
    {
        var result = new AcpSessionListResult
        {
            NextCursor = TryStr(root, "nextCursor"),
        };
        if (TryArr(root, "sessions") is { } arr)
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = TryStr(item, "sessionId");
                if (string.IsNullOrEmpty(id)) continue;
                DateTimeOffset? updatedAt = null;
                if (TryStr(item, "updatedAt") is { Length: > 0 } ts
                    && DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
                {
                    updatedAt = dto;
                }
                result.Sessions.Add(new AcpSessionInfo
                {
                    SessionId = id,
                    Cwd = TryStr(item, "cwd"),
                    Title = TryStr(item, "title"),
                    UpdatedAt = updatedAt,
                    Meta = TryClone(item, "_meta"),
                });
            }
        }
        return result;
    }

    public static AcpSessionInfoUpdate ParseSessionInfoUpdate(JsonElement obj)
    {
        DateTimeOffset? updatedAt = null;
        if (TryStr(obj, "updatedAt") is { Length: > 0 } ts
            && DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
        {
            updatedAt = dto;
        }
        return new AcpSessionInfoUpdate
        {
            Title = TryStr(obj, "title"),
            UpdatedAt = updatedAt,
            Meta = TryClone(obj, "_meta"),
        };
    }

    public static AcpCompactionUpdate ParseCompactionUpdate(string sessionUpdateKind, JsonElement obj)
    {
        var status = TryStr(obj, "status") ?? (sessionUpdateKind == "compaction_summary_chunk" ? "in_progress" : "started");
        var summary = TryStr(obj, "summary");
        if (string.IsNullOrEmpty(summary) && TryObj(obj, "content") is { } block)
        {
            summary = ExtractContentBlockText(block);
        }
        return new AcpCompactionUpdate
        {
            CompactionId = TryStr(obj, "compactionId") ?? TryStr(obj, "id") ?? "compaction",
            Status = status,
            Summary = summary,
            PreTokens = TryLong(obj, "preTokens") ?? TryLong(obj, "tokensBefore"),
            PostTokens = TryLong(obj, "postTokens") ?? TryLong(obj, "tokensAfter"),
            Meta = TryClone(obj, "_meta"),
        };
    }

    public static AcpCursorUpdate? ParseCursorUpdate(JsonElement obj)
    {
        var cursorId = TryStr(obj, "cursorId") ?? TryStr(obj, "cursor");
        if (string.IsNullOrWhiteSpace(cursorId)) return null;
        return new AcpCursorUpdate
        {
            CursorId = cursorId,
            MessageId = TryStr(obj, "messageId"),
            Meta = TryClone(obj, "_meta"),
        };
    }

    public static AcpContentAnnotations? ParseAnnotations(JsonElement block)
    {
        if (TryObj(block, "annotations") is not { } ann) return null;
        var res = new AcpContentAnnotations
        {
            Priority = TryDouble(ann, "priority"),
        };
        if (TryArr(ann, "audience") is { } aud)
        {
            foreach (var item in aud.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } role)
                {
                    res.Audience.Add(role);
                }
            }
        }
        if (TryStr(ann, "lastModified") is { Length: > 0 } lm
            && DateTimeOffset.TryParse(lm, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
        {
            res.LastModified = dto;
        }
        return res.Audience.Count == 0 && res.Priority is null && res.LastModified is null ? null : res;
    }

    public static AcpAudioContent? ExtractAudioContent(JsonElement block)
    {
        if (block.ValueKind != JsonValueKind.Object) return null;
        if (!string.Equals(TryStr(block, "type"), "audio", StringComparison.OrdinalIgnoreCase)) return null;
        var data = TryStr(block, "data");
        if (string.IsNullOrWhiteSpace(data)) return null;
        return new AcpAudioContent
        {
            Base64Data = data,
            MimeType = TryStr(block, "mimeType") ?? "audio/wav",
            Annotations = ParseAnnotations(block),
        };
    }

    public static AcpEmbeddedResourceContent? ExtractEmbeddedResource(JsonElement block)
    {
        if (block.ValueKind != JsonValueKind.Object) return null;
        if (!string.Equals(TryStr(block, "type"), "resource", StringComparison.OrdinalIgnoreCase)) return null;
        if (TryObj(block, "resource") is not { } res) return null;
        var uri = TryStr(res, "uri");
        if (string.IsNullOrWhiteSpace(uri)) return null;
        return new AcpEmbeddedResourceContent
        {
            Uri = uri,
            MimeType = TryStr(res, "mimeType"),
            Text = TryStr(res, "text"),
            Blob = TryStr(res, "blob"),
            Annotations = ParseAnnotations(block),
        };
    }

    public static List<AcpPlanEntry> ParsePlanEntries(JsonElement arr)
    {
        var list = new List<AcpPlanEntry>();
        if (arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var content = TryStr(item, "content") ?? TryStr(item, "subject") ?? TryStr(item, "title");
            if (string.IsNullOrEmpty(content)) continue;
            var rawStatus = TryStr(item, "status") ?? "pending";
            var normalizedStatus = rawStatus switch
            {
                "done" or "skipped" => "completed",
                _ => rawStatus,
            };
            var entry = new AcpPlanEntry
            {
                Id = TryStr(item, "id"),
                Content = content,
                ActiveForm = TryStr(item, "activeForm"),
                Priority = TryStr(item, "priority") ?? "medium",
                Status = normalizedStatus,
                Owner = TryStr(item, "owner"),
            };
            if (TryArr(item, "blockedBy") is { } deps)
            {
                foreach (var d in deps.EnumerateArray())
                {
                    var depId = d.ValueKind == JsonValueKind.String ? d.GetString() : d.GetRawText();
                    if (!string.IsNullOrWhiteSpace(depId)) entry.BlockedBy.Add(depId);
                }
            }
            list.Add(entry);
        }
        return list;
    }

    public static List<AcpAvailableCommand> ParseAvailableCommands(JsonElement arr, IAcpDialect dialect)
    {
        var list = new List<AcpAvailableCommand>();
        if (arr.ValueKind != JsonValueKind.Array) return list;
        var withheld = dialect.WithheldCommands;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var name = TryStr(item, "name");
            if (string.IsNullOrEmpty(name)) continue;
            var bare = name.TrimStart('/');
            if (withheld.Contains(bare, StringComparer.OrdinalIgnoreCase)) continue;

            var cmd = new AcpAvailableCommand
            {
                Name = bare,
                Description = TryStr(item, "description") ?? "",
                InputHint = TryStr(TryObj(item, "input"), "hint"),
                Meta = TryClone(item, "_meta"),
            };
            if (dialect.ExtractSkillDescription(cmd) is { } skillDesc)
            {
                cmd.IsSkill = true;
                cmd.Description = skillDesc;
            }
            list.Add(cmd);
        }
        return list;
    }

    public static AcpContextUsage ParseUsageUpdate(JsonElement obj)
    {
        var usage = new AcpContextUsage
        {
            Used = TryLong(obj, "used") ?? 0,
            Size = TryLong(obj, "size") ?? 0,
        };
        if (TryObj(obj, "cost") is { } cost)
        {
            if (cost.TryGetProperty("amount", out var amt) && amt.TryGetDouble(out var d)) usage.CostAmount = d;
            usage.CostCurrency = TryStr(cost, "currency");
        }
        return usage;
    }

    public static string? ExtractContentBlockText(JsonElement block)
    {
        if (block.ValueKind != JsonValueKind.Object) return null;
        return TryStr(block, "type") switch
        {
            "text" => TryStr(block, "text"),
            "resource_link" => TryStr(block, "name") ?? TryStr(block, "uri"),
            "resource" => TryStr(TryObj(block, "resource"), "text"),
            _ => null,
        };
    }

    /// <summary>Extracts an ACP <c>type: "image"</c> content block (or image resource / image resource_link).</summary>
    public static AcpImageContent? ExtractImageContent(JsonElement block)
    {
        if (block.ValueKind != JsonValueKind.Object) return null;
        var bType = TryStr(block, "type");
        if (string.Equals(bType, "image", StringComparison.OrdinalIgnoreCase))
        {
            var rawData = TryStr(block, "data") ?? TryStr(block, "b64_json") ?? TryStr(block, "b64Json");
            var mime = TryStr(block, "mimeType") ?? TryStr(block, "mime_type");
            var uri = TryStr(block, "uri") ?? TryStr(block, "url");
            var path = TryStr(block, "path") ?? TryStr(block, "filePath") ?? TryStr(block, "file_path");
            var alt = TryStr(block, "altText") ?? TryStr(block, "alt") ?? TryStr(block, "description") ?? TryStr(block, "name");
            var created = CreateImageContent(rawData, mime, uri, path, alt, TryInt(block, "width"), TryInt(block, "height"));
            created?.Annotations = ParseAnnotations(block);
            return created;
        }
        if (string.Equals(bType, "resource", StringComparison.OrdinalIgnoreCase) && TryObj(block, "resource") is { } res)
        {
            var mime = TryStr(res, "mimeType") ?? TryStr(res, "mime_type");
            var uri = TryStr(res, "uri");
            var blob = TryStr(res, "blob") ?? TryStr(res, "data");
            if ((mime is not null && mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(uri) && HasImageExtension(uri)))
            {
                return CreateImageContent(blob, mime, uri, null, TryStr(res, "name") ?? TryStr(block, "name"), null, null);
            }
        }
        if (string.Equals(bType, "resource_link", StringComparison.OrdinalIgnoreCase))
        {
            var mime = TryStr(block, "mimeType") ?? TryStr(block, "mime_type");
            var uri = TryStr(block, "uri");
            if ((mime is not null && mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(uri) && HasImageExtension(uri)))
            {
                return CreateImageContent(null, mime, uri, null, TryStr(block, "name") ?? TryStr(block, "title"), null, null);
            }
        }
        return null;
    }

    /// <summary>Translates Codex/agent custom <c>image_generation_*</c> session updates into a unified <see cref="AcpToolCallUpdate"/>.</summary>
    public static AcpToolCallUpdate ParseImageGenerationNotification(string sessionUpdateKind, JsonElement upd, IAcpDialect dialect)
    {
        var callId = TryStr(upd, "toolCallId")
            ?? TryStr(upd, "callId")
            ?? TryStr(upd, "call_id")
            ?? TryStr(upd, "id")
            ?? "image-gen-1";

        var status = TryStr(upd, "status") ?? sessionUpdateKind switch
        {
            "image_generation_begin" => "in_progress",
            "image_generation_partial" or "image_generation_update" => "in_progress",
            "image_generation_end" => "completed",
            _ => "in_progress",
        };
        if (TryStr(upd, "error") is { Length: > 0 } errText)
        {
            status = "failed";
        }
        else
        {
            errText = null;
        }

        var tc = new AcpToolCallUpdate
        {
            ToolCallId = callId,
            Name = TryStr(upd, "name") ?? "image_generation",
            Title = TryStr(upd, "title") ?? TryStr(upd, "revised_prompt") ?? TryStr(upd, "revisedPrompt") ?? TryStr(upd, "prompt") ?? "Image generation",
            Kind = "image",
            Status = status,
            IsImageGeneration = true,
            ImagePrompt = TryStr(upd, "revised_prompt") ?? TryStr(upd, "revisedPrompt") ?? TryStr(upd, "prompt"),
            ImageProgress = ExtractProgress(upd),
            ImageAspectRatio = ExtractAspectRatio(upd),
            RawOutput = upd.Clone(),
            Meta = TryClone(upd, "_meta"),
        };

        if (!string.IsNullOrEmpty(errText))
        {
            tc.Stderr = errText;
        }

        ExtractImagesFromPayload(upd, tc.Images, tc.ImagePrompt);
        ClassifyToolSemantics(tc);
        dialect.NormalizeToolUpdate(tc);
        return tc;
    }

    internal static bool IsImageGenerationTool(string? toolName, string? kind, string? title)
    {
        if (string.Equals(kind, "image", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "image_generation", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (MatchesImageGenName(toolName)) return true;
        if (!string.IsNullOrWhiteSpace(title))
        {
            var trimmed = title.Trim();
            if (trimmed.StartsWith("Run ", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[4..].TrimStart();
            var sep = trimmed.IndexOfAny([':', '(', ' ']);
            var head = sep > 0 ? trimmed[..sep] : trimmed;
            if (MatchesImageGenName(head)) return true;
            if (trimmed.Contains("image_generation", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("generate_image", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("Generating image", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("Generate image", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool MatchesImageGenName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var n = name.Trim();
        if (n.Equals("image_generation", StringComparison.OrdinalIgnoreCase)
            || n.Equals("image_gen", StringComparison.OrdinalIgnoreCase)
            || n.Equals("generate_image", StringComparison.OrdinalIgnoreCase)
            || n.Equals("create_image", StringComparison.OrdinalIgnoreCase)
            || n.Equals("text_to_image", StringComparison.OrdinalIgnoreCase)
            || n.Equals("cursor/generate_image", StringComparison.OrdinalIgnoreCase)
            || n.Equals("ImageGeneration", StringComparison.OrdinalIgnoreCase)
            || n.Equals("GenerateImage", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        // MCP or namespaced image generation tools (e.g. mcp__openai__generate_image, codex.image_generation).
        return n.Contains("generate_image", StringComparison.OrdinalIgnoreCase)
            || n.Contains("image_generation", StringComparison.OrdinalIgnoreCase)
            || n.Contains("image_gen", StringComparison.OrdinalIgnoreCase)
            || n.Contains("text_to_image", StringComparison.OrdinalIgnoreCase);
    }

    internal static void ExtractImageGenerationMetadata(AcpToolCallUpdate tc)
    {
        if (tc.RawInput is { ValueKind: JsonValueKind.Object } rawIn)
        {
            if (tc.IsImageGeneration || tc.Images.Count > 0)
            {
                tc.ImagePrompt ??= TryStr(rawIn, "revised_prompt")
                    ?? TryStr(rawIn, "revisedPrompt")
                    ?? TryStr(rawIn, "prompt")
                    ?? TryStr(rawIn, "description");
                tc.ImageAspectRatio ??= ExtractAspectRatio(rawIn);
            }
        }

        if (tc.RawOutput is { ValueKind: JsonValueKind.Object } rawOut)
        {
            var hasExplicitImageFields = rawOut.TryGetProperty("b64_json", out _)
                || rawOut.TryGetProperty("b64Json", out _)
                || rawOut.TryGetProperty("partial_image_b64", out _)
                || rawOut.TryGetProperty("partialImageB64", out _)
                || rawOut.TryGetProperty("image_base64", out _)
                || rawOut.TryGetProperty("imageBase64", out _)
                || (rawOut.TryGetProperty("revised_prompt", out _) && (rawOut.TryGetProperty("result", out _) || rawOut.TryGetProperty("saved_path", out _)));
            if (hasExplicitImageFields)
            {
                tc.IsImageGeneration = true;
                if (string.IsNullOrWhiteSpace(tc.Kind) || string.Equals(tc.Kind, "other", StringComparison.OrdinalIgnoreCase))
                {
                    tc.Kind = "image";
                }
            }

            if (tc.IsImageGeneration || tc.Images.Count > 0)
            {
                tc.ImagePrompt = TryStr(rawOut, "revised_prompt")
                    ?? TryStr(rawOut, "revisedPrompt")
                    ?? TryStr(rawOut, "prompt")
                    ?? tc.ImagePrompt;
                tc.ImageProgress ??= ExtractProgress(rawOut);
                tc.ImageAspectRatio ??= ExtractAspectRatio(rawOut);
                ExtractImagesFromPayload(rawOut, tc.Images, tc.ImagePrompt);
            }
        }
        else if (tc.IsImageGeneration && tc.RawOutput is { ValueKind: JsonValueKind.String } rawOutStr
            && rawOutStr.GetString() is { Length: > 0 } strOut)
        {
            if (CreateImageFromAmbiguousValue(strOut, null, tc.ImagePrompt) is { } img)
            {
                AddUniqueImage(tc.Images, img);
            }
        }

        if (tc.Meta is { ValueKind: JsonValueKind.Object } meta)
        {
            if (tc.IsImageGeneration || tc.Images.Count > 0)
            {
                tc.ImageProgress ??= ExtractProgress(meta);
                tc.ImageAspectRatio ??= ExtractAspectRatio(meta);
                tc.ImagePrompt ??= TryStr(meta, "revised_prompt") ?? TryStr(meta, "revisedPrompt") ?? TryStr(meta, "prompt");
                ExtractImagesFromPayload(meta, tc.Images, tc.ImagePrompt);
            }
        }

        if (tc.IsImageGeneration && tc.Locations.Count > 0)
        {
            foreach (var loc in tc.Locations)
            {
                if (HasImageExtension(loc.Path))
                {
                    if (CreateImageContent(null, null, null, loc.Path, tc.ImagePrompt, null, null) is { } locImg)
                    {
                        AddUniqueImage(tc.Images, locImg);
                    }
                }
            }
        }

        if (tc.IsImageGeneration && !string.IsNullOrWhiteSpace(tc.ImagePrompt))
        {
            for (var i = 0; i < tc.Images.Count; i++)
            {
                tc.Images[i].AltText ??= tc.ImagePrompt;
            }
        }
    }

    internal static void ExtractImagesFromPayload(JsonElement obj, List<AcpImageContent> target, string? defaultAlt)
    {
        if (obj.ValueKind != JsonValueKind.Object) return;

        var mime = TryStr(obj, "mimeType") ?? TryStr(obj, "mime_type") ?? TryStr(obj, "output_format");
        if (!string.IsNullOrEmpty(mime) && !mime.Contains('/'))
        {
            mime = $"image/{mime.ToLowerInvariant()}";
        }
        var width = TryInt(obj, "width");
        var height = TryInt(obj, "height");
        var alt = TryStr(obj, "revised_prompt") ?? TryStr(obj, "revisedPrompt") ?? TryStr(obj, "altText") ?? TryStr(obj, "alt") ?? defaultAlt;
        var path = TryStr(obj, "saved_path")
            ?? TryStr(obj, "savedPath")
            ?? TryStr(obj, "image_path")
            ?? TryStr(obj, "imagePath")
            ?? TryStr(obj, "output_path")
            ?? TryStr(obj, "outputPath")
            ?? TryStr(obj, "file_path")
            ?? TryStr(obj, "filePath")
            ?? TryStr(obj, "filename")
            ?? TryStr(obj, "path");
        var uri = TryStr(obj, "uri") ?? TryStr(obj, "image_url") ?? TryStr(obj, "imageUrl") ?? TryStr(obj, "url");

        var b64 = TryStr(obj, "b64_json")
            ?? TryStr(obj, "b64Json")
            ?? TryStr(obj, "image_base64")
            ?? TryStr(obj, "imageBase64")
            ?? TryStr(obj, "partial_image_b64")
            ?? TryStr(obj, "partialImageB64")
            ?? TryStr(obj, "base64")
            ?? TryStr(obj, "data");

        if (b64 is not null || path is not null || uri is not null)
        {
            if (CreateImageContent(b64, mime, uri, path, alt, width, height) is { } direct)
            {
                AddUniqueImage(target, direct);
            }
        }

        if (obj.TryGetProperty("result", out var resultEl) && resultEl.ValueKind == JsonValueKind.String
            && resultEl.GetString() is { Length: > 0 } resStr)
        {
            if (CreateImageFromAmbiguousValue(resStr, mime, alt) is { } resImg)
            {
                if (!string.IsNullOrEmpty(path) && string.IsNullOrEmpty(resImg.FilePath)) resImg.FilePath = path;
                AddUniqueImage(target, resImg);
            }
        }

        if (obj.TryGetProperty("image", out var imageEl))
        {
            if (imageEl.ValueKind == JsonValueKind.Object)
            {
                ExtractImagesFromPayload(imageEl, target, alt);
            }
            else if (imageEl.ValueKind == JsonValueKind.String && imageEl.GetString() is { Length: > 0 } imgStr
                && CreateImageFromAmbiguousValue(imgStr, mime, alt) is { } strImg)
            {
                AddUniqueImage(target, strImg);
            }
        }

        if (TryArr(obj, "images") is { } imagesArr)
        {
            foreach (var entry in imagesArr.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object)
                {
                    if (ExtractImageContent(entry) is { } blockImg)
                    {
                        blockImg.AltText ??= alt;
                        AddUniqueImage(target, blockImg);
                    }
                    else
                    {
                        ExtractImagesFromPayload(entry, target, alt);
                    }
                }
                else if (entry.ValueKind == JsonValueKind.String && entry.GetString() is { Length: > 0 } s
                    && CreateImageFromAmbiguousValue(s, mime, alt) is { } sImg)
                {
                    AddUniqueImage(target, sImg);
                }
            }
        }
    }

    internal static void AddUniqueImage(List<AcpImageContent> list, AcpImageContent candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.Base64Data)
            && string.IsNullOrWhiteSpace(candidate.FilePath)
            && string.IsNullOrWhiteSpace(candidate.Uri))
        {
            return;
        }
        for (var i = 0; i < list.Count; i++)
        {
            var existing = list[i];
            var sameFile = !string.IsNullOrEmpty(existing.FilePath) && !string.IsNullOrEmpty(candidate.FilePath)
                && string.Equals(existing.FilePath, candidate.FilePath, StringComparison.OrdinalIgnoreCase);
            var sameUri = !string.IsNullOrEmpty(existing.Uri) && !string.IsNullOrEmpty(candidate.Uri)
                && string.Equals(existing.Uri, candidate.Uri, StringComparison.OrdinalIgnoreCase);
            var sameData = !string.IsNullOrEmpty(existing.Base64Data) && !string.IsNullOrEmpty(candidate.Base64Data)
                && string.Equals(existing.Base64Data, candidate.Base64Data, StringComparison.Ordinal);
            if (sameFile || sameUri || sameData)
            {
                existing.Base64Data ??= candidate.Base64Data;
                existing.MimeType ??= candidate.MimeType;
                existing.Uri ??= candidate.Uri;
                existing.FilePath ??= candidate.FilePath;
                existing.AltText ??= candidate.AltText;
                existing.Width ??= candidate.Width;
                existing.Height ??= candidate.Height;
                return;
            }
        }
        list.Add(candidate);
    }

    private static AcpImageContent? CreateImageFromAmbiguousValue(string value, string? mime, string? alt)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return null;
        if (trimmed.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            return CreateImageContent(trimmed, mime, null, null, alt, null, null);
        }
        if (trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return CreateImageContent(null, mime, trimmed, null, alt, null, null);
        }
        if (HasImageExtension(trimmed) && !trimmed.Contains('\n'))
        {
            return CreateImageContent(null, mime, null, trimmed, alt, null, null);
        }
        if (LooksLikeImageBase64(trimmed, out var detectedMime))
        {
            return CreateImageContent(trimmed, mime ?? detectedMime, null, null, alt, null, null);
        }
        return null;
    }

    internal static AcpImageContent? CreateImageContent(
        string? rawData,
        string? mimeType,
        string? uri,
        string? filePath,
        string? altText,
        int? width,
        int? height)
    {
        string? b64 = null;
        var mime = string.IsNullOrWhiteSpace(mimeType) ? null : mimeType.Trim();
        if (!string.IsNullOrWhiteSpace(rawData))
        {
            var trimmed = rawData.Trim();
            if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = trimmed.IndexOf(',');
                if (comma > 5)
                {
                    var header = trimmed[5..comma];
                    var semi = header.IndexOf(';');
                    var headerMime = semi > 0 ? header[..semi] : header;
                    if (!string.IsNullOrWhiteSpace(headerMime)) mime ??= headerMime;
                    b64 = trimmed[(comma + 1)..].Trim();
                }
            }
            else
            {
                b64 = trimmed;
            }
        }

        var normalizedPath = string.IsNullOrWhiteSpace(filePath) ? null : filePath.Trim();
        var normalizedUri = string.IsNullOrWhiteSpace(uri) ? null : uri.Trim();
        if (normalizedPath is null && normalizedUri is not null
            && normalizedUri.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(normalizedUri, UriKind.Absolute, out var parsedUri)
            && parsedUri.IsFile)
        {
            normalizedPath = parsedUri.LocalPath;
        }
        if (normalizedPath is not null && normalizedPath.StartsWith("~/", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                normalizedPath = Path.Combine(home, normalizedPath[2..]);
            }
        }

        if (b64 is null && normalizedPath is null && normalizedUri is null)
        {
            return null;
        }

        mime ??= InferImageMime(b64, normalizedPath ?? normalizedUri);
        return new AcpImageContent
        {
            Base64Data = b64,
            MimeType = mime,
            Uri = normalizedUri,
            FilePath = normalizedPath,
            AltText = string.IsNullOrWhiteSpace(altText) ? null : altText.Trim(),
            Width = width,
            Height = height,
        };
    }

    private static bool LooksLikeImageBase64(string value, out string? mime)
    {
        mime = null;
        if (value.Length < 48) return false;
        if (value.StartsWith("iVBORw0KGgo", StringComparison.Ordinal)) { mime = "image/png"; return true; }
        if (value.StartsWith("/9j/", StringComparison.Ordinal)) { mime = "image/jpeg"; return true; }
        if (value.StartsWith("UklGR", StringComparison.Ordinal)) { mime = "image/webp"; return true; }
        if (value.StartsWith("R0lGOD", StringComparison.Ordinal)) { mime = "image/gif"; return true; }
        if (value.StartsWith("Qk", StringComparison.Ordinal) && value.Length >= 100 && IsBase64Text(value)) { mime = "image/bmp"; return true; }
        return false;
    }

    private static bool IsBase64Text(string value)
    {
        var checkLen = Math.Min(value.Length, 256);
        for (var i = 0; i < checkLen; i++)
        {
            var c = value[i];
            if (!char.IsLetterOrDigit(c) && c is not ('+' or '/' or '=' or '\r' or '\n'))
            {
                return false;
            }
        }
        return true;
    }

    private static string? InferImageMime(string? b64, string? pathOrUri)
    {
        if (!string.IsNullOrEmpty(b64))
        {
            if (b64.StartsWith("iVBORw0KGgo", StringComparison.Ordinal)) return "image/png";
            if (b64.StartsWith("/9j/", StringComparison.Ordinal)) return "image/jpeg";
            if (b64.StartsWith("UklGR", StringComparison.Ordinal)) return "image/webp";
            if (b64.StartsWith("R0lGOD", StringComparison.Ordinal)) return "image/gif";
        }
        if (!string.IsNullOrEmpty(pathOrUri))
        {
            var clean = pathOrUri.Split('?', '#')[0];
            if (clean.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return "image/png";
            if (clean.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || clean.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)) return "image/jpeg";
            if (clean.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)) return "image/webp";
            if (clean.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)) return "image/gif";
            if (clean.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)) return "image/bmp";
        }
        return null;
    }

    internal static bool HasImageExtension(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var clean = path.Trim().Split('?', '#')[0];
        return clean.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || clean.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || clean.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            || clean.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
            || clean.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
            || clean.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase);
    }

    internal static double? ExtractProgress(JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (TryDouble(obj, "progress") is { } p)
        {
            if (p > 1.0 && p <= 100.0) p /= 100.0;
            return Math.Clamp(p, 0.0, 1.0);
        }
        if (TryDouble(obj, "percent") is { } pct)
        {
            return Math.Clamp(pct > 1.0 ? pct / 100.0 : pct, 0.0, 1.0);
        }
        if (TryInt(obj, "step") is { } step && TryInt(obj, "totalSteps") is > 0 and var total)
        {
            return Math.Clamp((double)step / total, 0.0, 1.0);
        }
        if (TryInt(obj, "partial_image_index") is { } pIdx && pIdx >= 0)
        {
            var pTotal = TryInt(obj, "partial_images") ?? 4;
            if (pTotal <= pIdx) pTotal = pIdx + 2;
            return Math.Clamp((pIdx + 1.0) / (pTotal + 1.0), 0.1, 0.92);
        }
        return null;
    }

    internal static double? ExtractAspectRatio(JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (TryInt(obj, "width") is > 0 and var w && TryInt(obj, "height") is > 0 and var h)
        {
            return Math.Clamp((double)w / h, 0.35, 3.0);
        }
        var raw = TryStr(obj, "aspect_ratio") ?? TryStr(obj, "aspectRatio") ?? TryStr(obj, "size") ?? TryStr(obj, "dimensions");
        return TryParseAspectRatio(raw);
    }

    internal static double? TryParseAspectRatio(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim().ToLowerInvariant();
        if (s == "square") return 1.0;
        if (s == "landscape") return 1.5;
        if (s == "portrait") return 0.75;
        var sepIdx = s.IndexOfAny([':', 'x', '×', '/']);
        if (sepIdx > 0 && sepIdx < s.Length - 1
            && double.TryParse(s[..sepIdx], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a)
            && double.TryParse(s[(sepIdx + 1)..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b)
            && a > 0 && b > 0)
        {
            return Math.Clamp(a / b, 0.35, 3.0);
        }
        return null;
    }

    internal static double? TryDouble(JsonElement? el, string prop) =>
        el is { ValueKind: JsonValueKind.Object } obj
        && obj.TryGetProperty(prop, out var v)
        && v.TryGetDouble(out var d)
            ? d
            : null;

    public static JsonArray BuildPromptBlocks(
        string text,
        IReadOnlyList<AcpPromptAttachment>? attachments,
        IAcpDialect dialect,
        bool agentSupportsImages,
        bool agentSupportsAudio = false,
        bool agentSupportsEmbeddedContext = false)
    {
        var blocks = new JsonArray();
        var promptText = text;
        var extraRefs = new List<string>();

        if (attachments is { Count: > 0 })
        {
            foreach (var att in attachments)
            {
                if (att.Kind == "image")
                {
                    if (agentSupportsImages && !dialect.DropsPromptImages && !string.IsNullOrEmpty(att.Base64Data))
                    {
                        var imgObj = new JsonObject
                        {
                            ["type"] = "image",
                            ["data"] = att.Base64Data,
                            ["mimeType"] = att.MimeType ?? "image/png",
                        };
                        AttachAnnotations(imgObj, att.Annotations);
                        blocks.Add((JsonNode)imgObj);
                    }
                    else if (!string.IsNullOrEmpty(att.Path))
                    {
                        extraRefs.Add(att.Path);
                    }
                }
                else if (att.Kind == "audio")
                {
                    if (agentSupportsAudio && !string.IsNullOrEmpty(att.Base64Data))
                    {
                        var audioObj = new JsonObject
                        {
                            ["type"] = "audio",
                            ["data"] = att.Base64Data,
                            ["mimeType"] = att.MimeType ?? "audio/wav",
                        };
                        AttachAnnotations(audioObj, att.Annotations);
                        blocks.Add((JsonNode)audioObj);
                    }
                    else if (!string.IsNullOrEmpty(att.Path))
                    {
                        extraRefs.Add(att.Path);
                    }
                }
                else if (att.Kind == "resource"
                    || (att.Kind == "file" && agentSupportsEmbeddedContext && !string.IsNullOrEmpty(att.TextContent)))
                {
                    if (agentSupportsEmbeddedContext && (!string.IsNullOrEmpty(att.TextContent) || !string.IsNullOrEmpty(att.Base64Data)))
                    {
                        var uri = !string.IsNullOrEmpty(att.Path)
                            ? (att.Path.StartsWith("file://", StringComparison.OrdinalIgnoreCase) || att.Path.Contains("://", StringComparison.Ordinal)
                                ? att.Path
                                : new Uri(Path.GetFullPath(att.Path)).AbsoluteUri)
                            : $"file:///{Uri.EscapeDataString(string.IsNullOrEmpty(att.DisplayName) ? "context" : att.DisplayName)}";
                        var innerRes = new JsonObject { ["uri"] = uri };
                        if (!string.IsNullOrEmpty(att.MimeType)) innerRes["mimeType"] = att.MimeType;
                        if (!string.IsNullOrEmpty(att.TextContent)) innerRes["text"] = att.TextContent;
                        else if (!string.IsNullOrEmpty(att.Base64Data)) innerRes["blob"] = att.Base64Data;
                        var resBlock = new JsonObject
                        {
                            ["type"] = "resource",
                            ["resource"] = innerRes,
                        };
                        AttachAnnotations(resBlock, att.Annotations);
                        blocks.Add((JsonNode)resBlock);
                    }
                    else if (!string.IsNullOrEmpty(att.Path))
                    {
                        extraRefs.Add($"@{att.DisplayName}");
                    }
                }
                else if (att.Kind == "file" && !string.IsNullOrEmpty(att.Path))
                {
                    if (dialect.LinksFileMentions)
                    {
                        var uri = att.Path.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                            ? att.Path
                            : new Uri(Path.GetFullPath(att.Path)).AbsoluteUri;
                        var linkObj = new JsonObject
                        {
                            ["type"] = "resource_link",
                            ["name"] = string.IsNullOrEmpty(att.DisplayName) ? Path.GetFileName(att.Path) : att.DisplayName,
                            ["uri"] = uri,
                        };
                        if (!string.IsNullOrEmpty(att.MimeType)) linkObj["mimeType"] = att.MimeType;
                        AttachAnnotations(linkObj, att.Annotations);
                        blocks.Add((JsonNode)linkObj);
                    }
                    else
                    {
                        extraRefs.Add($"@{att.DisplayName}");
                    }
                }
            }
        }

        if (extraRefs.Count > 0)
        {
            promptText = string.IsNullOrWhiteSpace(promptText)
                ? string.Join(' ', extraRefs)
                : $"{promptText}\n\n{string.Join(' ', extraRefs)}";
        }

        blocks.Insert(0, new JsonObject
        {
            ["type"] = "text",
            ["text"] = promptText,
        });

        return blocks;
    }

    private static void AttachAnnotations(JsonObject block, AcpContentAnnotations? annotations)
    {
        if (annotations is null) return;
        var ann = new JsonObject();
        if (annotations.Audience.Count > 0)
        {
            var aud = new JsonArray();
            foreach (var role in annotations.Audience) aud.Add((JsonNode)JsonValue.Create(role));
            ann["audience"] = aud;
        }
        if (annotations.Priority.HasValue) ann["priority"] = annotations.Priority.Value;
        if (annotations.LastModified.HasValue) ann["lastModified"] = annotations.LastModified.Value.ToString("O", CultureInfo.InvariantCulture);
        if (ann.Count > 0) block["annotations"] = ann;
    }

    private static void ReadSchemaEnumOptions(JsonElement schemaObj, List<AcpQuestionOption> target)
    {
        if (TryArr(schemaObj, "oneOf") is { } oneOf)
        {
            foreach (var item in oneOf.EnumerateArray())
            {
                var val = TryStr(item, "const");
                if (!string.IsNullOrEmpty(val))
                {
                    target.Add(new AcpQuestionOption
                    {
                        Id = val,
                        Label = TryStr(item, "title") ?? val,
                        Description = TryStr(item, "description"),
                    });
                }
            }
        }
        else if (TryArr(schemaObj, "enum") is { } enums)
        {
            var names = TryArr(schemaObj, "enumNames");
            var idx = 0;
            foreach (var e in enums.EnumerateArray())
            {
                var val = e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText();
                if (string.IsNullOrEmpty(val)) { idx++; continue; }
                string? label = null;
                if (names is { ValueKind: JsonValueKind.Array } nArr && idx < nArr.GetArrayLength())
                {
                    label = nArr[idx].GetString();
                }
                target.Add(new AcpQuestionOption { Id = val, Label = label ?? val });
                idx++;
            }
        }
    }

    private static void AddSelectOption(List<AcpConfigSelectOption> target, JsonElement entry, string? groupName)
    {
        if (entry.ValueKind != JsonValueKind.Object) return;
        var val = TryStr(entry, "value");
        if (string.IsNullOrEmpty(val)) return;
        target.Add(new AcpConfigSelectOption
        {
            Value = val,
            Name = TryStr(entry, "name") ?? val,
            Description = TryStr(entry, "description"),
            GroupName = groupName,
        });
    }

    internal static string? TryStr(JsonElement? el, string prop) =>
        el is { ValueKind: JsonValueKind.Object } obj
        && obj.TryGetProperty(prop, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    internal static int? TryInt(JsonElement? el, string prop) =>
        el is { ValueKind: JsonValueKind.Object } obj
        && obj.TryGetProperty(prop, out var v)
        && v.TryGetInt32(out var n)
            ? n
            : null;

    internal static long? TryLong(JsonElement? el, string prop) =>
        el is { ValueKind: JsonValueKind.Object } obj
        && obj.TryGetProperty(prop, out var v)
        && v.TryGetInt64(out var n)
            ? n
            : null;

    internal static bool? TryBool(JsonElement? el, string prop) =>
        el is { ValueKind: JsonValueKind.Object } obj
        && obj.TryGetProperty(prop, out var v)
        && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;

    internal static JsonElement? TryObj(JsonElement? el, string prop) =>
        el is { ValueKind: JsonValueKind.Object } obj
        && obj.TryGetProperty(prop, out var v)
        && v.ValueKind == JsonValueKind.Object
            ? v
            : null;

    internal static JsonElement? TryArr(JsonElement? el, string prop) =>
        el is { ValueKind: JsonValueKind.Object } obj
        && obj.TryGetProperty(prop, out var v)
        && v.ValueKind == JsonValueKind.Array
            ? v
            : null;

    internal static JsonElement? TryClone(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(prop, out var v)
        && v.ValueKind != JsonValueKind.Null
            ? v.Clone()
            : null;

    private static bool HasNonNull(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var v) && v.ValueKind != JsonValueKind.Null;
}
