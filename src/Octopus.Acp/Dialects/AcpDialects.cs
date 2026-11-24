using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Octopus.Acp.Protocol;
using Octopus.Acp.Transport;

namespace Octopus.Acp.Dialects;

/// <summary>Standard ACP v1 dialect used when an agent follows the spec without special quirks.</summary>
public sealed class GenericAcpDialect : IAcpDialect
{
    public static GenericAcpDialect Instance { get; } = new();
    public string Id => "generic";
}

/// <summary>Registry mapping <c>AgentAcpDefinition.Dialect</c> names to their Orca-referenced compatibility adapters.</summary>
public sealed class AcpDialectRegistry
{
    private readonly Dictionary<string, IAcpDialect> _dialects;

    public AcpDialectRegistry()
    {
        IAcpDialect[] builtIn =
        [
            GenericAcpDialect.Instance,
            new OpenCodeAcpDialect(),
            new AntigravityAcpDialect(),
            new CursorAcpDialect(),
            new GrokAcpDialect(),
            new GeminiAcpDialect(),
            new CopilotAcpDialect(),
            new QwenCodeAcpDialect(),
            new CodeBuddyAcpDialect(),
            new KimiAcpDialect(),
            new KiroAcpDialect(),
            new GooseAcpDialect(),
            new DroidAcpDialect(),
            new ClineAcpDialect(),
            new AuggieAcpDialect(),
            new OmpAcpDialect(),
            new DevinAcpDialect(),
            new HermesAcpDialect(),
            new ClaudeAdapterDialect(),
            new CodexAdapterDialect(),
        ];
        _dialects = builtIn.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IAcpDialect Resolve(string? dialectId) =>
        !string.IsNullOrWhiteSpace(dialectId) && _dialects.TryGetValue(dialectId, out var found)
            ? found
            : GenericAcpDialect.Instance;

    public IReadOnlyCollection<string> RegisteredIds => _dialects.Keys;
}

internal static class DialectJsonHelpers
{
    public static string? Str(JsonElement? el, string prop) =>
        el is { ValueKind: JsonValueKind.Object } obj
        && obj.TryGetProperty(prop, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public static int? Int(JsonElement? el, string prop) =>
        el is { ValueKind: JsonValueKind.Object } obj
        && obj.TryGetProperty(prop, out var v)
        && v.TryGetInt32(out var n)
            ? n
            : null;

    public static JsonElement? Obj(JsonElement? el, string prop) =>
        el is { ValueKind: JsonValueKind.Object } obj
        && obj.TryGetProperty(prop, out var v)
        && v.ValueKind == JsonValueKind.Object
            ? v
            : null;

    public static AcpQuestionRequest? ReadPermissionAsQuestion(JsonElement? parameters, string requestId, string fallbackTitle)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } obj) return null;
        if (!obj.TryGetProperty("options", out var optsEl) || optsEl.ValueKind != JsonValueKind.Array) return null;
        var options = new List<AcpQuestionOption>();
        foreach (var opt in optsEl.EnumerateArray())
        {
            var id = Str(opt, "optionId");
            var name = Str(opt, "name");
            if (!string.IsNullOrEmpty(id))
            {
                options.Add(new AcpQuestionOption { Id = id, Label = string.IsNullOrEmpty(name) ? id : name });
            }
        }
        if (options.Count == 0) return null;

        string? prompt = null;
        if (obj.TryGetProperty("toolCall", out var tc) && tc.ValueKind == JsonValueKind.Object)
        {
            if (tc.TryGetProperty("content", out var contentArr) && contentArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in contentArr.EnumerateArray())
                {
                    if (Str(block, "type") == "content"
                        && Obj(block, "content") is { } inner
                        && Str(inner, "type") == "text"
                        && Str(inner, "text") is { Length: > 0 } text)
                    {
                        prompt = text;
                        break;
                    }
                }
            }
            prompt ??= Str(tc, "title");
        }

        return new AcpQuestionRequest
        {
            RequestId = requestId,
            Title = fallbackTitle,
            Questions =
            [
                new AcpQuestionItem
                {
                    Id = "choice",
                    Prompt = prompt ?? fallbackTitle,
                    Options = options,
                },
            ],
        };
    }

    public static JsonNode BuildPermissionSelectedReply(IReadOnlyDictionary<string, IReadOnlyList<string>>? answers)
    {
        var chosen = answers?.Values.FirstOrDefault()?.FirstOrDefault();
        if (string.IsNullOrEmpty(chosen))
        {
            return new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "cancelled" } };
        }
        return new JsonObject
        {
            ["outcome"] = new JsonObject
            {
                ["outcome"] = "selected",
                ["optionId"] = chosen,
            },
        };
    }
}

/// <summary>OpenCode & MiMo Code (Orca <c>opencode-dialect.ts</c>).</summary>
public sealed class OpenCodeAcpDialect : IAcpDialect
{
    public string Id => "opencode";
    public bool AdvertiseFormElicitation => true;
    public bool LinksFileMentions => true;
    public IReadOnlyCollection<string> WithheldCommands { get; } = ["clear", "reset"];
    public IReadOnlyDictionary<string, string> InteractionModes { get; } = new Dictionary<string, string>
    {
        ["default"] = "build",
        ["plan"] = "plan",
    };

    public void NormalizeToolUpdate(AcpToolCallUpdate update)
    {
        if (update.RawInput is { ValueKind: JsonValueKind.Object } input)
        {
            update.Command ??= DialectJsonHelpers.Str(input, "command");
        }
        if (update.RawOutput is not { ValueKind: JsonValueKind.Object } raw) return;
        var meta = DialectJsonHelpers.Obj(raw, "metadata");
        if (update.Stdout is null)
        {
            update.Stdout = DialectJsonHelpers.Str(raw, "output") ?? DialectJsonHelpers.Str(meta, "output");
        }
        if (update.ExitCode is null)
        {
            update.ExitCode = DialectJsonHelpers.Int(meta, "exit");
        }
    }
}

/// <summary>Antigravity CLI (<c>agy acp</c>, Orca <c>antigravity-dialect.ts</c>).</summary>
public sealed partial class AntigravityAcpDialect : IAcpDialect
{
    public string Id => "antigravity";
    public bool AdvertiseFormElicitation => true;
    public bool ReactiveWakeups => true;
    public IReadOnlyCollection<string> WithheldCommands { get; } = ["logout"];

    public AcpImplementation? OverrideClientInfo(string? agentVersion)
    {
        // agy < 1.5.1 / managed server only advertises non-Gemini models to allowlisted editors such as zed.
        if (!string.IsNullOrWhiteSpace(agentVersion)
            && Version.TryParse(agentVersion.Split('-', '+')[0], out var parsed)
            && parsed >= new Version(1, 5, 1))
        {
            return null;
        }
        return new AcpImplementation { Name = "zed", Version = "0.0.0" };
    }

    public void NormalizeToolUpdate(AcpToolCallUpdate update)
    {
        if (update.Kind == "execute" && string.IsNullOrEmpty(update.Name))
        {
            update.Name = "execute";
        }
        if (update.RawInput is { ValueKind: JsonValueKind.Object } input)
        {
            update.Command ??= DialectJsonHelpers.Str(input, "CommandLine")
                ?? DialectJsonHelpers.Str(input, "command_line")
                ?? DialectJsonHelpers.Str(input, "commandLine");
            update.WorkingDirectory ??= DialectJsonHelpers.Str(input, "Cwd")
                ?? DialectJsonHelpers.Str(input, "WorkingDirectory")
                ?? DialectJsonHelpers.Str(input, "working_dir")
                ?? DialectJsonHelpers.Str(input, "workingDir")
                ?? DialectJsonHelpers.Str(input, "cwd");
        }
        if (update.RawOutput is { ValueKind: JsonValueKind.Object } output)
        {
            update.Stdout ??= DialectJsonHelpers.Str(output, "combinedOutput")
                ?? DialectJsonHelpers.Str(output, "combined_output")
                ?? DialectJsonHelpers.Str(output, "output");
            update.ExitCode ??= DialectJsonHelpers.Int(output, "exitCode")
                ?? DialectJsonHelpers.Int(output, "exit_code");
            if (!string.IsNullOrEmpty(update.Stdout) && update.TextOutputs.Count > 0)
            {
                var trimmedStdout = update.Stdout.Trim();
                update.TextOutputs.RemoveAll(t => string.Equals(t.Trim(), trimmedStdout, StringComparison.Ordinal));
            }
        }
    }

    public AcpDialectRequestHandling? TryHandleRequest(string method, JsonElement? parameters, string requestId)
    {
        if (method != "session/request_permission" || parameters is not { ValueKind: JsonValueKind.Object } obj)
        {
            return null;
        }
        var tcId = DialectJsonHelpers.Str(DialectJsonHelpers.Obj(obj, "toolCall"), "toolCallId");
        // Formless servers ask via interaction_ permissions: never auto-approve even in full-access mode.
        if (tcId is not null && tcId.StartsWith("interaction_", StringComparison.Ordinal))
        {
            var question = DialectJsonHelpers.ReadPermissionAsQuestion(parameters, requestId, "Antigravity Question");
            if (question is not null)
            {
                return new AcpDialectRequestHandling
                {
                    Question = question,
                    BuildQuestionReply = DialectJsonHelpers.BuildPermissionSelectedReply,
                    RequireManualApproval = true,
                };
            }
        }
        // Surface agy.security.warning metadata and force manual approval.
        if (obj.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
        {
            foreach (var opt in opts.EnumerateArray())
            {
                var warn = DialectJsonHelpers.Obj(DialectJsonHelpers.Obj(opt, "_meta"), "agy.security.warning");
                var message = DialectJsonHelpers.Str(warn, "message") ?? DialectJsonHelpers.Str(warn, "title");
                if (!string.IsNullOrWhiteSpace(message))
                {
                    return new AcpDialectRequestHandling
                    {
                        RequireManualApproval = true,
                        ApprovalNotice = message.Length > 512 ? message[..512] : message,
                    };
                }
            }
        }
        return null;
    }

    public string? FormatPromptError(AcpRpcException error)
    {
        var text = error.DataSummary() ?? error.Message;
        var match = CloudCodePrefixRegex().Match(text);
        if (!match.Success) return null;
        try
        {
            using var doc = JsonDocument.Parse(text[match.Length..]);
            return DialectJsonHelpers.Str(DialectJsonHelpers.Obj(doc.RootElement, "error"), "message");
        }
        catch (JsonException) { return null; }
    }

    [GeneratedRegex(@"^Cloud Code Assist API error \(\d{3}\):\s*", RegexOptions.CultureInvariant)]
    private static partial Regex CloudCodePrefixRegex();
}

/// <summary>Cursor Agent (<c>cursor-agent acp</c>, Orca <c>cursor-dialect.ts</c>).</summary>
public sealed partial class CursorAcpDialect : IAcpDialect
{
    private const string AskQuestionMethod = "cursor/ask_question";
    private const string CreatePlanMethod = "cursor/create_plan";
    private const string QuestionFallbackSkip = "__ask_question_skip__";
    private static readonly HashSet<string> ReportMethods = ["cursor/update_todos", "cursor/task", "cursor/generate_image"];

    public string Id => "cursor";
    public string FastConfigId => "fast";
    public string ThinkingConfigId => "thinking";
    public IReadOnlyDictionary<string, JsonNode> ExtraClientCapabilitiesMeta { get; } = new Dictionary<string, JsonNode>
    {
        ["parameterizedModelPicker"] = JsonValue.Create(true)!,
    };
    public IReadOnlyDictionary<string, string> ChatModelIdAliases { get; } = new Dictionary<string, string>
    {
        ["auto"] = "default",
    };
    public IReadOnlyDictionary<string, string> ChatEffortIdAliases { get; } = new Dictionary<string, string>
    {
        ["xhigh"] = "extra-high",
    };

    public void NormalizeToolUpdate(AcpToolCallUpdate update)
    {
        foreach (var diff in update.Diffs)
        {
            if (diff.OldText == "-- /dev/null")
            {
                diff.OldText = null;
                diff.NewText = NewFileHeaderRegex().Replace(diff.NewText, "");
            }
        }
    }

    public string? ExtractSkillDescription(AcpAvailableCommand command)
    {
        var cleaned = SkillScopeRegex().Replace(command.Description, "");
        return cleaned == command.Description ? null : cleaned;
    }

    public AcpDialectRequestHandling? TryHandleRequest(string method, JsonElement? parameters, string requestId)
    {
        if (ReportMethods.Contains(method))
        {
            return new AcpDialectRequestHandling { ImmediateReply = new JsonObject() };
        }
        if (method == CreatePlanMethod)
        {
            var plan = DialectJsonHelpers.Str(parameters, "plan")
                ?? DialectJsonHelpers.Str(parameters, "overview")
                ?? "The agent finished planning.";
            return new AcpDialectRequestHandling
            {
                ImmediateReply = new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "accepted" } },
                PlanMarkdown = plan,
            };
        }
        if (method == AskQuestionMethod && parameters is { ValueKind: JsonValueKind.Object } obj
            && obj.TryGetProperty("questions", out var qArr) && qArr.ValueKind == JsonValueKind.Array)
        {
            var items = new List<AcpQuestionItem>();
            foreach (var q in qArr.EnumerateArray())
            {
                var qid = DialectJsonHelpers.Str(q, "id") ?? $"q{items.Count + 1}";
                var prompt = DialectJsonHelpers.Str(q, "prompt") ?? "";
                var multi = q.TryGetProperty("allowMultiple", out var mEl) && mEl.ValueKind == JsonValueKind.True;
                var opts = new List<AcpQuestionOption>();
                if (q.TryGetProperty("options", out var oArr) && oArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var o in oArr.EnumerateArray())
                    {
                        var oid = DialectJsonHelpers.Str(o, "id");
                        var label = DialectJsonHelpers.Str(o, "label");
                        if (!string.IsNullOrEmpty(oid))
                        {
                            opts.Add(new AcpQuestionOption { Id = oid, Label = label ?? oid });
                        }
                    }
                }
                if (opts.Count > 0)
                {
                    items.Add(new AcpQuestionItem { Id = qid, Prompt = prompt, MultiSelect = multi, Options = opts });
                }
            }
            if (items.Count > 0)
            {
                return new AcpDialectRequestHandling
                {
                    Question = new AcpQuestionRequest
                    {
                        RequestId = requestId,
                        Title = DialectJsonHelpers.Str(obj, "title") ?? "Cursor Question",
                        Questions = items,
                    },
                    BuildQuestionReply = answers =>
                    {
                        if (answers is null)
                        {
                            return new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "cancelled" } };
                        }
                        var arr = new JsonArray();
                        foreach (var (qid, selected) in answers)
                        {
                            var selArr = new JsonArray();
                            foreach (var s in selected) selArr.Add((JsonNode)JsonValue.Create(s));
                            arr.Add((JsonNode)new JsonObject { ["questionId"] = qid, ["selectedOptionIds"] = selArr });
                        }
                        return new JsonObject
                        {
                            ["outcome"] = new JsonObject { ["outcome"] = "answered", ["answers"] = arr },
                        };
                    },
                };
            }
        }
        if (method == "session/request_permission" && parameters is { ValueKind: JsonValueKind.Object } permObj
            && permObj.TryGetProperty("options", out var pOpts) && pOpts.ValueKind == JsonValueKind.Array)
        {
            var isFallbackQuestion = false;
            foreach (var opt in pOpts.EnumerateArray())
            {
                if (DialectJsonHelpers.Str(opt, "optionId") == QuestionFallbackSkip)
                {
                    isFallbackQuestion = true;
                    break;
                }
            }
            if (isFallbackQuestion && DialectJsonHelpers.ReadPermissionAsQuestion(parameters, requestId, "Cursor Question") is { } q)
            {
                return new AcpDialectRequestHandling
                {
                    Question = q,
                    BuildQuestionReply = DialectJsonHelpers.BuildPermissionSelectedReply,
                    RequireManualApproval = true,
                };
            }
        }
        return null;
    }

    [GeneratedRegex(@"^\+\+ b/[^\n]*\n?", RegexOptions.CultureInvariant)]
    private static partial Regex NewFileHeaderRegex();

    [GeneratedRegex(@"\s*\((?:project |user |builtin )?skill\)$", RegexOptions.CultureInvariant)]
    private static partial Regex SkillScopeRegex();
}

/// <summary>Grok CLI (<c>grok agent stdio</c>, Orca <c>grok-dialect.ts</c>).</summary>
public sealed class GrokAcpDialect : IAcpDialect
{
    public string Id => "grok";

    public string? SelectAuthMethod(IReadOnlyList<AcpAuthMethod> advertised, IReadOnlyDictionary<string, string> env)
    {
        if (env.TryGetValue("XAI_API_KEY", out var key) && !string.IsNullOrWhiteSpace(key)
            && advertised.Any(m => m.Id == "xai.api_key"))
        {
            return "xai.api_key";
        }
        return advertised.Any(m => m.Id == "cached_token") ? "cached_token" : null;
    }
}

/// <summary>Gemini CLI (<c>gemini --acp</c>, Orca <c>gemini-dialect.ts</c>).</summary>
public sealed class GeminiAcpDialect : IAcpDialect
{
    public string Id => "gemini";
    public bool LinksFileMentions => true;
}

/// <summary>GitHub Copilot CLI (<c>copilot --acp</c>, Orca <c>copilot-dialect.ts</c>).</summary>
public sealed class CopilotAcpDialect : IAcpDialect
{
    public string Id => "copilot";
    public IReadOnlyCollection<string> WithheldCommands { get; } = ["clear", "logout"];

    public void NormalizeToolUpdate(AcpToolCallUpdate update)
    {
        if (update.RawOutput is not { ValueKind: JsonValueKind.Object } raw) return;
        var detailed = DialectJsonHelpers.Str(raw, "detailedContent") ?? DialectJsonHelpers.Str(raw, "content");
        if (!string.IsNullOrEmpty(detailed) && update.Stdout is null)
        {
            update.Stdout = detailed;
        }
    }
}

/// <summary>Qwen Code (<c>qwen --acp</c>, Orca <c>qwen-code-dialect.ts</c>).</summary>
public sealed class QwenCodeAcpDialect : IAcpDialect
{
    public string Id => "qwen-code";
    public bool ReactiveWakeups => true;
    public string CompactCommand => "/compress";
    public IReadOnlyCollection<string> WithheldCommands { get; } = ["compress", "clear", "reset", "new"];

    public void NormalizeToolUpdate(AcpToolCallUpdate update)
    {
        if (DialectJsonHelpers.Str(update.Meta, "toolName") is { Length: > 0 } toolName)
        {
            update.Name = toolName;
        }
        if (DialectJsonHelpers.Str(update.Meta, "phase") == "preparing")
        {
            update.RawInput = null;
            return;
        }
        if (update.RawOutput is { ValueKind: JsonValueKind.Object } raw && DialectJsonHelpers.Str(raw, "type") == "shell_result")
        {
            update.Stdout = DialectJsonHelpers.Str(raw, "output") ?? "";
            update.Stderr = DialectJsonHelpers.Str(raw, "error");
            update.ExitCode = DialectJsonHelpers.Int(raw, "exitCode");
            update.TextOutputs.Clear();
        }
    }

    public AcpDialectRequestHandling? TryHandleRequest(string method, JsonElement? parameters, string requestId) => method switch
    {
        "craft/drainMidTurnQueue" => new AcpDialectRequestHandling
        {
            ImmediateReply = new JsonObject { ["messages"] = new JsonArray(), ["hasQueuedPrompt"] = false },
        },
        "_qwencode/start_turn" => new AcpDialectRequestHandling
        {
            ImmediateReply = new JsonObject { ["accepted"] = true },
        },
        _ => null,
    };
}

/// <summary>CodeBuddy Code (<c>codebuddy --acp</c>, Orca <c>codebuddy-dialect.ts</c>).</summary>
public sealed partial class CodeBuddyAcpDialect : IAcpDialect
{
    public string Id => "codebuddy";
    public bool ReactiveWakeups => true;
    public IReadOnlyCollection<string> WithheldCommands { get; } = ["clear", "new", "fork", "branch"];

    public void NormalizeToolUpdate(AcpToolCallUpdate update)
    {
        if (DialectJsonHelpers.Str(update.Meta, "codebuddy.ai/toolName") is { Length: > 0 } name)
        {
            update.Name = name;
        }
        if (update.Status is null && update.RawOutput is null)
        {
            update.TextOutputs.Clear();
            return;
        }
        if (update.Name == "Bash" && update.RawOutput is { ValueKind: JsonValueKind.Object } raw
            && DialectJsonHelpers.Str(raw, "text") is { Length: > 0 } text)
        {
            var exitCode = DialectJsonHelpers.Int(DialectJsonHelpers.Obj(update.Meta, "codebuddy.ai/rawResponse"), "exitCode");
            var m = ShellTailRegex().Match(text);
            if (m.Success)
            {
                var head = text[..m.Index];
                const string stdoutPrefix = "Stdout: ";
                const string stderrSep = "\nStderr: ";
                var split = head.LastIndexOf(stderrSep, StringComparison.Ordinal);
                if (head.StartsWith(stdoutPrefix, StringComparison.Ordinal) && split >= 0)
                {
                    var stdout = CleanStream(head[stdoutPrefix.Length..split]);
                    var stderr = CleanStream(head[(split + stderrSep.Length)..]);
                    update.Stdout = stdout;
                    if (stderr.Length > 0) update.Stderr = stderr;
                    if (exitCode is { } code)
                    {
                        update.ExitCode = code;
                        if (code != 0) update.Status = "failed";
                    }
                    update.TextOutputs.Clear();
                }
            }
        }
    }

    public string? ExtractSkillDescription(AcpAvailableCommand command) =>
        DialectJsonHelpers.Str(command.Meta, "type") == "skill" ? command.Description : null;

    private static string CleanStream(string s)
    {
        var trimmed = s.Replace("\r\n", "\n").TrimEnd();
        return trimmed == "(empty)" ? "" : trimmed;
    }

    [GeneratedRegex(@"\nExit Code: -?\d+\nSignal: [^\n]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ShellTailRegex();
}

/// <summary>Kimi Code CLI (<c>kimi acp</c>, Orca <c>kimi-dialect.ts</c>).</summary>
public sealed partial class KimiAcpDialect : IAcpDialect
{
    public string Id => "kimi";
    public bool AdvertiseFormElicitation => true;

    public void NormalizeToolUpdate(AcpToolCallUpdate update)
    {
        if (update.Status is "pending" or "in_progress")
        {
            update.TextOutputs.Clear();
            return;
        }
        if (update.RawOutput is { ValueKind: JsonValueKind.String } strEl && strEl.GetString() is { } output)
        {
            if (update.Status == "completed" && output == "Command executed successfully.")
            {
                update.Stdout = "";
                update.ExitCode = 0;
                update.TextOutputs.RemoveAll(t => t == output);
            }
            else if (update.Status == "failed")
            {
                var m = ShellFailureRegex().Match(output);
                if (m.Success && int.TryParse(m.Groups[2].Value, out var code))
                {
                    var stdout = m.Groups[1].Value;
                    update.Stdout = stdout == $"Process exited with code {code}" ? "" : stdout;
                    update.ExitCode = code;
                    update.TextOutputs.RemoveAll(t => t == output);
                }
            }
        }
    }

    public string? ExtractSkillDescription(AcpAvailableCommand command) =>
        command.Name.StartsWith("skill:", StringComparison.Ordinal) ? command.Description : null;

    public AcpDialectRequestHandling? TryHandleRequest(string method, JsonElement? parameters, string requestId)
    {
        if (method != "session/request_permission" || parameters is not { ValueKind: JsonValueKind.Object } obj)
        {
            return null;
        }
        if (DialectJsonHelpers.Obj(obj, "toolCall") is { } tc
            && tc.TryGetProperty("content", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in arr.EnumerateArray())
            {
                if (DialectJsonHelpers.Str(block, "type") == "content"
                    && DialectJsonHelpers.Obj(block, "content") is { } inner
                    && DialectJsonHelpers.Str(inner, "text") is { } text)
                {
                    var m = ApprovalActionRegex().Match(text);
                    if (m.Success)
                    {
                        var action = m.Groups[1].Value.Trim();
                        return new AcpDialectRequestHandling
                        {
                            ApprovalNotice = action.Length > 512 ? action[..512] : action,
                        };
                    }
                }
            }
        }
        return null;
    }

    [GeneratedRegex(@"^(?:([\s\S]*)\n)?Command failed with exit code: (-?\d+)\.$", RegexOptions.CultureInvariant)]
    private static partial Regex ShellFailureRegex();

    [GeneratedRegex(@"^Requesting approval to (.+)$", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ApprovalActionRegex();
}

/// <summary>Kiro CLI (<c>kiro-cli acp --agent-engine v3</c>, Orca <c>kiro-dialect.ts</c>).</summary>
public sealed class KiroAcpDialect : IAcpDialect
{
    public string Id => "kiro";

    public void NormalizeToolUpdate(AcpToolCallUpdate update)
    {
        foreach (var diff in update.Diffs)
        {
            if (diff.Path.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                && Uri.TryCreate(diff.Path, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                diff.Path = uri.LocalPath;
            }
        }
    }
}

/// <summary>Goose (<c>goose acp</c>, Orca <c>goose-dialect.ts</c>).</summary>
public sealed class GooseAcpDialect : IAcpDialect
{
    public string Id => "goose";
    public IReadOnlyCollection<string> WithheldCommands { get; } = ["clear"];

    public void NormalizeToolUpdate(AcpToolCallUpdate update)
    {
        if (DialectJsonHelpers.Str(DialectJsonHelpers.Obj(DialectJsonHelpers.Obj(update.Meta, "goose"), "toolCall"), "toolName") is { Length: > 0 } name)
        {
            update.Name = name;
        }
        if (update.RawOutput is { ValueKind: JsonValueKind.Object } raw
            && DialectJsonHelpers.Int(raw, "exit_code") is { } exitCode)
        {
            update.Stdout ??= DialectJsonHelpers.Str(raw, "stdout");
            update.Stderr ??= DialectJsonHelpers.Str(raw, "stderr");
            update.ExitCode = exitCode;
            var trailer = $"\n\nCommand exited with code {exitCode}";
            for (var i = 0; i < update.TextOutputs.Count; i++)
            {
                if (update.TextOutputs[i].EndsWith(trailer, StringComparison.Ordinal))
                {
                    update.TextOutputs[i] = update.TextOutputs[i][..^trailer.Length];
                }
            }
        }
    }

    public bool IsAuthenticationRequired(AcpRpcException error) =>
        error.Code == -32000
        || (error.Code == -32603 && (error.DataSummary() ?? "").Contains("Configuration value not found:", StringComparison.Ordinal));
}

/// <summary>Factory Droid (<c>droid exec --output-format acp</c>, Orca <c>droid-dialect.ts</c>).</summary>
public sealed partial class DroidAcpDialect : IAcpDialect
{
    public string Id => "droid";
    public bool AdvertiseFormElicitation => true;

    public void NormalizeToolUpdate(AcpToolCallUpdate update)
    {
        if (update.Status is not ("completed" or "failed")) return;
        if (update.RawOutput is { ValueKind: JsonValueKind.String } strEl && strEl.GetString() is { } output)
        {
            var m = ExitLineRegex().Match(output);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var code))
            {
                var body = output[..m.Index].TrimEnd('\n');
                body = FailureLineRegex().Replace(body, "");
                update.Stdout = body == "Command completed successfully" ? "" : body;
                update.ExitCode = code;
                update.TextOutputs.RemoveAll(t => t == output);
            }
        }
    }

    public string? ExtractSkillDescription(AcpAvailableCommand command) => command.Description;

    [GeneratedRegex(@"(?:^|\n)\[Process exited with code (-?\d+)\]$", RegexOptions.CultureInvariant)]
    private static partial Regex ExitLineRegex();

    [GeneratedRegex(@"^Error: Command failed \(exit code: -?\d+\)(?:\n|$)", RegexOptions.CultureInvariant)]
    private static partial Regex FailureLineRegex();
}

/// <summary>Cline (<c>cline --acp</c>, Orca <c>cline-dialect.ts</c>).</summary>
public sealed partial class ClineAcpDialect : IAcpDialect
{
    public string Id => "cline";
    public bool DropsPromptImages => true;

    public void NormalizeToolUpdate(AcpToolCallUpdate update)
    {
        if (string.IsNullOrEmpty(update.Name) && !string.IsNullOrEmpty(update.Title))
        {
            var idx = update.Title.IndexOf(": ", StringComparison.Ordinal);
            update.Name = idx > 0 ? update.Title[..idx] : update.Title;
        }
        if (update.RawInput is { ValueKind: JsonValueKind.Object } input
            && input.TryGetProperty("commands", out var cmds) && cmds.ValueKind == JsonValueKind.Array)
        {
            var lines = cmds.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
            if (lines.Count > 0) update.Command = string.Join('\n', lines);
        }
        if (update.RawOutput is { ValueKind: JsonValueKind.Array } arr)
        {
            var parts = new List<string>();
            int? exitCode = null;
            var anyFailed = false;
            foreach (var entry in arr.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (entry.TryGetProperty("success", out var sEl) && sEl.ValueKind == JsonValueKind.False) anyFailed = true;
                var res = DialectJsonHelpers.Str(entry, "result") ?? "";
                var m = ExitLineRegex().Match(res);
                if (m.Success)
                {
                    if (exitCode is null && int.TryParse(m.Groups[1].Value, out var code)) exitCode = code;
                    res = res[m.Length..];
                }
                var text = string.IsNullOrEmpty(res) ? DialectJsonHelpers.Str(entry, "error") : res;
                if (!string.IsNullOrEmpty(text)) parts.Add(text);
            }
            if (parts.Count > 0) update.Stdout = string.Join('\n', parts);
            if (exitCode is not null) update.ExitCode = exitCode;
            if (anyFailed && update.Status == "completed") update.Status = "failed";
        }
    }

    [GeneratedRegex(@"^\[Command exited with code (-?\d+)\](?:\n|$)", RegexOptions.CultureInvariant)]
    private static partial Regex ExitLineRegex();
}

/// <summary>Auggie CLI (<c>auggie --acp</c>, Orca <c>auggie-dialect.ts</c>).</summary>
public sealed class AuggieAcpDialect : IAcpDialect
{
    public string Id => "auggie";

    public bool IsAuthenticationRequired(AcpRpcException error) =>
        error.Code == -32000
        || (error.Message.Contains("not authenticated", StringComparison.OrdinalIgnoreCase)
            || error.Message.Contains("login", StringComparison.OrdinalIgnoreCase));
}

/// <summary>OMP (<c>omp acp</c>, Orca <c>omp-dialect.ts</c>).</summary>
public sealed class OmpAcpDialect : IAcpDialect
{
    public string Id => "omp";
    public bool AdvertiseFormElicitation => true;
}

/// <summary>Devin (<c>devin acp --process-model in-process</c>, Orca <c>devin-dialect.ts</c>).</summary>
public sealed class DevinAcpDialect : IAcpDialect
{
    public string Id => "devin";
    public IReadOnlyCollection<string> WithheldCommands { get; } = ["login", "logout", "mode"];
}

/// <summary>Hermes (<c>hermes acp</c>, Orca <c>hermes-dialect.ts</c>).</summary>
public sealed partial class HermesAcpDialect : IAcpDialect
{
    public string Id => "hermes";
    public string CompactCommand => "/compress";
    public IReadOnlyCollection<string> WithheldCommands { get; } = ["compress", "reset"];

    public void NormalizeToolUpdate(AcpToolCallUpdate update)
    {
        if (string.IsNullOrEmpty(update.Name) && !string.IsNullOrEmpty(update.Title))
        {
            var m = ToolTitleRegex().Match(update.Title);
            if (m.Success) update.Name = m.Groups[1].Value;
        }
        if (update.Status is "completed" or "failed" && update.TextOutputs.Count > 0)
        {
            var first = update.TextOutputs[0];
            var m = TerminalResultRegex().Match(first);
            if (m.Success && int.TryParse(m.Groups[2].Value, out var code))
            {
                update.Stdout = m.Groups[1].Value;
                update.ExitCode = code;
                update.TextOutputs.RemoveAt(0);
            }
        }
    }

    public AcpDialectRequestHandling? TryHandleRequest(string method, JsonElement? parameters, string requestId)
    {
        if (method != "session/request_permission") return null;
        var tcId = DialectJsonHelpers.Str(DialectJsonHelpers.Obj(parameters, "toolCall"), "toolCallId");
        if (tcId is not null && DecisionRegex().IsMatch(tcId))
        {
            return new AcpDialectRequestHandling { RequireManualApproval = true };
        }
        return null;
    }

    [GeneratedRegex(@"^([a-z][a-z0-9_]*)(?::\s+([\s\S]+)|$)", RegexOptions.CultureInvariant)]
    private static partial Regex ToolTitleRegex();

    [GeneratedRegex(@"^terminal result\n(?:- \*\*output:\*\* ([\s\S]*?)\n)?- \*\*exit_code:\*\* (-?\d+)(?:\n- \*\*(?:approval|error):\*\* [^\n]*)*$", RegexOptions.CultureInvariant)]
    private static partial Regex TerminalResultRegex();

    [GeneratedRegex(@"^(?:clarify|model-confirm)-", RegexOptions.CultureInvariant)]
    private static partial Regex DecisionRegex();
}

/// <summary>Official <c>@agentclientprotocol/claude-agent-acp</c> adapter.</summary>
public sealed class ClaudeAdapterDialect : IAcpDialect
{
    public string Id => "claude-acp";
    public bool AdvertiseFormElicitation => true;
    public bool ReactiveWakeups => true;
    public IReadOnlyCollection<string> WithheldCommands { get; } = ["clear", "logout", "exit"];

    public void NormalizeToolUpdate(AcpToolCallUpdate update)
    {
        var ccMeta = DialectJsonHelpers.Obj(update.Meta, "claudeCode");
        if (string.IsNullOrEmpty(update.Name) && DialectJsonHelpers.Str(ccMeta, "toolName") is { Length: > 0 } toolName)
        {
            update.Name = toolName;
        }
        if (string.IsNullOrEmpty(update.ParentToolCallId))
        {
            update.ParentToolCallId = DialectJsonHelpers.Str(ccMeta, "parentToolUseId")
                ?? DialectJsonHelpers.Str(ccMeta, "parent_tool_use_id");
        }

        // claude-agent-acp sometimes puts rawOutput as { content: "...", is_error: bool } or { stdout, stderr, interrupted }.
        if (update.RawOutput is { ValueKind: JsonValueKind.Object } raw)
        {
            if (update.Stdout is null && DialectJsonHelpers.Str(raw, "content") is { Length: > 0 } content)
            {
                if (update.Kind == "execute" || string.Equals(update.Name, "Bash", StringComparison.OrdinalIgnoreCase))
                {
                    update.Stdout = content;
                }
            }
            if (update.RawOutput.Value.TryGetProperty("is_error", out var errEl) && errEl.ValueKind == JsonValueKind.True
                && update.Status == "completed")
            {
                update.Status = "failed";
            }
        }

        // Re-run semantic classifier if dialect resolved Name/Meta after initial parse.
        AcpProtocolParser.ClassifyToolSemantics(update);
    }

    public AcpDialectRequestHandling? TryHandleRequest(string method, JsonElement? parameters, string requestId)
    {
        if (method != "session/request_permission" || parameters is not { ValueKind: JsonValueKind.Object } obj)
        {
            return null;
        }
        var tc = DialectJsonHelpers.Obj(obj, "toolCall");
        if (tc is null) return null;

        var ccMeta = DialectJsonHelpers.Obj(DialectJsonHelpers.Obj(tc, "_meta"), "claudeCode");
        var toolName = DialectJsonHelpers.Str(ccMeta, "toolName")
            ?? DialectJsonHelpers.Str(tc, "name")
            ?? DialectJsonHelpers.Str(tc, "title")
            ?? "";

        if (toolName.Contains("ExitPlanMode", StringComparison.OrdinalIgnoreCase))
        {
            var rawInput = DialectJsonHelpers.Obj(tc, "rawInput");
            var plan = DialectJsonHelpers.Str(rawInput, "plan");
            if (string.IsNullOrWhiteSpace(plan) && tc.Value.TryGetProperty("content", out var contentArr) && contentArr.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (var block in contentArr.EnumerateArray())
                {
                    if (DialectJsonHelpers.Str(block, "type") == "content"
                        && DialectJsonHelpers.Obj(block, "content") is { } inner
                        && DialectJsonHelpers.Str(inner, "text") is { Length: > 0 } text)
                    {
                        parts.Add(text);
                    }
                }
                if (parts.Count > 0) plan = string.Join("\n\n", parts);
            }
            return new AcpDialectRequestHandling
            {
                RequireManualApproval = true,
                IsPlanApproval = true,
                PlanMarkdown = plan,
            };
        }

        return null;
    }
}

/// <summary>Official <c>@agentclientprotocol/codex-acp</c> adapter.</summary>
public sealed class CodexAdapterDialect : IAcpDialect
{
    public string Id => "codex-acp";
    public bool AdvertiseFormElicitation => true;
    public IReadOnlyCollection<string> WithheldCommands { get; } = ["clear", "logout", "exit"];
}
