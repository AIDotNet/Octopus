using System.Text.Json;
using System.Text.Json.Nodes;
using Octopus.Acp.Dialects;

namespace Octopus.Acp.Protocol;

/// <summary>Zero-reflection <see cref="JsonElement"/> parsers and request builders for ACP schema-v1.21.0.</summary>
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
                };
                if (TryArr(a, "args") is { } argsEl)
                {
                    method.Args = argsEl.EnumerateArray()
                        .Where(x => x.ValueKind == JsonValueKind.String)
                        .Select(x => x.GetString()!)
                        .ToList();
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
                else if (bType == "content" && TryObj(block, "content") is { } inner)
                {
                    var text = ExtractContentBlockText(inner);
                    if (!string.IsNullOrEmpty(text)) tc.TextOutputs.Add(text);
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
                or "invoke_subagent" or "update_steps" or "manage_subagents" or "manage_task")
            {
                toolName = head;
            }
        }

        if (string.Equals(toolName, "invoke_subagent", StringComparison.OrdinalIgnoreCase))
        {
            tc.IsSubagentLaunch = true;
            tc.IsBackground = true;
            tc.SubagentRunsAsynchronously = true;
            tc.SubagentType ??= "self";
            if (tc.RawInput is { ValueKind: JsonValueKind.Object } ri)
            {
                tc.Description ??= TryStr(ri, "Role") ?? TryStr(ri, "role");
                if (string.IsNullOrWhiteSpace(tc.Description) && TryStr(ri, "Prompt") is { Length: > 0 } prompt)
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
        if (mode is not null && mode != "form") return null;

        var message = TryStr(obj, "message") ?? "The agent needs your input";
        var questions = new List<AcpQuestionItem>();

        if (TryObj(obj, "requestedSchema") is { } schema && TryObj(schema, "properties") is { } props)
        {
            foreach (var prop in props.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                var title = TryStr(prop.Value, "title") ?? TryStr(prop.Value, "description") ?? prop.Name;
                var type = TryStr(prop.Value, "type") ?? "string";
                var item = new AcpQuestionItem
                {
                    Id = prop.Name,
                    Prompt = title,
                    MultiSelect = type == "array",
                    AllowFreeText = type == "string" && !prop.Value.TryGetProperty("enum", out _) && !prop.Value.TryGetProperty("oneOf", out _),
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
                AllowFreeText = true,
            });
        }

        return new AcpQuestionRequest
        {
            RequestId = requestId,
            Title = message,
            Questions = questions,
        };
    }

    public static JsonNode BuildFormElicitationReply(
        AcpQuestionRequest request,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? answers)
    {
        if (answers is null)
        {
            return new JsonObject { ["action"] = "cancel" };
        }
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
            else if (q.Options.Count == 2 && q.Options[0].Id == "true" && q.Options[1].Id == "false")
            {
                content[q.Id] = vals[0] == "true";
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

    public static JsonArray BuildPromptBlocks(
        string text,
        IReadOnlyList<AcpPromptAttachment>? attachments,
        IAcpDialect dialect,
        bool agentSupportsImages)
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
                        blocks.Add((JsonNode)new JsonObject
                        {
                            ["type"] = "image",
                            ["data"] = att.Base64Data,
                            ["mimeType"] = att.MimeType ?? "image/png",
                        });
                    }
                    else if (!string.IsNullOrEmpty(att.Path))
                    {
                        extraRefs.Add(att.Path);
                    }
                }
                else if (att.Kind == "file" && !string.IsNullOrEmpty(att.Path))
                {
                    if (dialect.LinksFileMentions)
                    {
                        var uri = att.Path.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                            ? att.Path
                            : new Uri(Path.GetFullPath(att.Path)).AbsoluteUri;
                        blocks.Add((JsonNode)new JsonObject
                        {
                            ["type"] = "resource_link",
                            ["name"] = string.IsNullOrEmpty(att.DisplayName) ? Path.GetFileName(att.Path) : att.DisplayName,
                            ["uri"] = uri,
                        });
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
