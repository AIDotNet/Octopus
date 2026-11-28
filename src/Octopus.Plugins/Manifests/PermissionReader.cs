using System.Text.Json;
using Octopus.Core.Plugins;

namespace Octopus.Plugins.Manifests;

/// <summary>
/// Reads the <c>permissions</c> block (design/plugins/04-权限与文件系统.md §2–3, 11-补充能力.md §1.5 and 1A.4).
/// Each permission can be written in a short form (<c>"git": "read"</c>, <c>"theme": true</c>) or as an object that adds
/// <c>"optional": true</c>.
/// </summary>
internal static class PermissionReader
{
    /// <summary>Signal channels that carry what the user typed or the arguments of tool calls.</summary>
    private static readonly HashSet<string> SensitiveChannels = new(StringComparer.Ordinal)
    {
        "prompt-submit", "permission-request", "pre-tool-use", "post-tool-use",
    };

    private static readonly string[] Known =
    [
        "fs", "git", "projects", "devices", "system", "workspace", "clipboard", "notifications", "shell", "net", "theme", "settings",
        "secrets", "terminal", "agents", "process",
    ];

    public static PluginPermissions Read(ManifestObject? block, bool unsafeEval)
    {
        var requests = new List<PermissionRequest>();
        FsProjectRequest? project = null;
        var external = new List<FsPathRequest>();
        var agentData = new List<FsAgentDataRequest>();

        if (block is { } permissions)
        {
            permissions.WarnUnknown(Known);
            if (permissions.Object("fs") is { } fs)
            {
                fs.WarnUnknown("project", "external", "agentData");
                project = ReadProject(fs, requests);
                ReadExternal(fs, external, requests);
                ReadAgentData(fs, agentData, requests);
            }
            Level(permissions, "git", ["read"], PermissionRisk.Low, requests);
            Level(permissions, "projects", ["read"], PermissionRisk.Low, requests);
            Level(permissions, "devices", ["read"], PermissionRisk.Low, requests);
            ReadSystem(permissions, requests);
            Level(permissions, "workspace", ["write"], PermissionRisk.Medium, requests);
            Level(permissions, "settings", ["read"], PermissionRisk.Low, requests);
            Flag(permissions, "notifications", PermissionRisk.Low, requests);
            Flag(permissions, "theme", PermissionRisk.Low, requests);
            Flag(permissions, "secrets", PermissionRisk.High, requests);
            Each(permissions, "clipboard", new() { ["read"] = PermissionRisk.Medium, ["write"] = PermissionRisk.Low }, requests);
            Each(permissions, "terminal", new() { ["read"] = PermissionRisk.High, ["write"] = PermissionRisk.High, ["create"] = PermissionRisk.High }, requests);
            ReadNet(permissions, requests);
            List(permissions, "shell", "open", PermissionRisk.Medium, requests, key: "shell.open");
            ReadProcess(permissions, requests);
            ReadAgents(permissions, requests);
        }
        if (unsafeEval)
        {
            requests.Add(new PermissionRequest("unsafeEval", PermissionRisk.High, false, []));
        }
        return new PluginPermissions { Requests = requests, FsProject = project, FsExternal = external, FsAgentData = agentData };
    }

    // ---------------------------------------------------------------- shapes

    /// <summary><c>"git": "read"</c> or <c>{"access": "read", "optional": true}</c>.</summary>
    private static void Level(ManifestObject permissions, string name, string[] levels, PermissionRisk risk, List<PermissionRequest> requests)
    {
        if (permissions.Raw(name) is not { } value) return;
        var optional = false;
        string? level = null;
        if (value.ValueKind == JsonValueKind.String)
        {
            level = value.GetString();
        }
        else if (value.ValueKind == JsonValueKind.True)
        {
            level = levels[0];
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            var obj = permissions.Object(name)!.Value;
            obj.WarnUnknown("access", "optional");
            level = obj.String("access") ?? levels[0];
            optional = obj.Bool("optional");
        }
        if (level is null || !levels.Contains(level))
        {
            permissions.Log.Error(permissions.At(name), $"must be {string.Join(" or ", levels.Select(l => $"\"{l}\""))}");
            return;
        }
        requests.Add(new PermissionRequest(name, risk, optional, [level]));
    }

    /// <summary><c>"theme": true</c> or <c>{"optional": true}</c>.</summary>
    private static void Flag(ManifestObject permissions, string name, PermissionRisk risk, List<PermissionRequest> requests)
    {
        if (permissions.Raw(name) is not { } value || value.ValueKind == JsonValueKind.False) return;
        var optional = false;
        if (value.ValueKind == JsonValueKind.Object && permissions.Object(name) is { } obj)
        {
            obj.WarnUnknown("optional");
            optional = obj.Bool("optional");
        }
        else if (value.ValueKind != JsonValueKind.True)
        {
            permissions.Log.Error(permissions.At(name), "must be true or an object");
            return;
        }
        requests.Add(new PermissionRequest(name, risk, optional, []));
    }

    /// <summary><c>"terminal": ["read", "write"]</c>: one request per listed capability.</summary>
    private static void Each(ManifestObject permissions, string name, Dictionary<string, PermissionRisk> capabilities, List<PermissionRequest> requests)
    {
        foreach (var capability in permissions.Strings(name).Distinct())
        {
            if (capabilities.TryGetValue(capability, out var risk)) requests.Add(new PermissionRequest($"{name}.{capability}", risk, false, []));
            else permissions.Log.Error(permissions.At(name), $"\"{capability}\" is not one of {string.Join(", ", capabilities.Keys)}");
        }
    }

    /// <summary><c>"net": {"hosts": [...]}</c>: one request carrying the list.</summary>
    private static void List(ManifestObject permissions, string name, string listName, PermissionRisk risk, List<PermissionRequest> requests, string? key = null)
    {
        if (permissions.Object(name) is not { } obj) return;
        obj.WarnUnknown(listName, "optional");
        var items = obj.Strings(listName).Where(s => s.Trim().Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (items.Count == 0)
        {
            obj.Log.Error(obj.At(listName), "must list at least one entry");
            return;
        }
        requests.Add(new PermissionRequest(key ?? name, risk, obj.Bool("optional"), items));
    }

    private static void ReadNet(ManifestObject permissions, List<PermissionRequest> requests)
    {
        if (permissions.Object("net") is not { } obj) return;
        obj.WarnUnknown("hosts", "optional");
        var items = obj.Strings("hosts").Where(s => s.Trim().Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (items.Count == 0)
        {
            obj.Log.Error(obj.At("hosts"), "must list at least one entry");
            return;
        }
        var risk = items.Any(IsWildcardHostPattern) ? PermissionRisk.High : PermissionRisk.Medium;
        requests.Add(new PermissionRequest("net", risk, obj.Bool("optional"), items));
    }

    internal static bool IsWildcardHostPattern(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        var trimmed = pattern.Trim();
        return trimmed.StartsWith("*.", StringComparison.Ordinal)
            && trimmed.Length > 2
            && trimmed[2] != '.'
            && !trimmed[2..].Contains('*', StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- files

    private static FsProjectRequest? ReadProject(ManifestObject fs, List<PermissionRequest> requests)
    {
        if (fs.Object("project") is not { } project) return null;
        project.WarnUnknown("access", "include", "exclude", "optional");
        var access = Access(project, "access", FileAccessKind.Read);
        var risk = access == FileAccessKind.ReadWrite ? PermissionRisk.High : PermissionRisk.Medium;
        var request = new FsProjectRequest(access, project.Strings("include"), project.Strings("exclude"), project.Bool("optional"));
        requests.Add(new PermissionRequest("fs.project", risk, request.Optional, [AccessText(access), .. request.Include], Exclude: request.Exclude));
        return request;
    }

    private static void ReadExternal(ManifestObject fs, List<FsPathRequest> external, List<PermissionRequest> requests)
    {
        foreach (var item in fs.Objects("external"))
        {
            item.WarnUnknown("path", "access", "reason", "optional", "localized");
            var path = item.String("path", required: true);
            var reason = item.String("reason", required: true);
            if (path is null || reason is null) continue;
            if (!(path.StartsWith('/') || path.StartsWith("~/", StringComparison.Ordinal) || Path.IsPathRooted(path)))
            {
                item.Log.Error(item.At("path"), "must be absolute or start with ~/");
                continue;
            }
            var request = new FsPathRequest(path, Access(item, "access", FileAccessKind.Read), reason, item.Bool("optional"), item.Localized());
            external.Add(request);
            requests.Add(new PermissionRequest("fs.external", PermissionRisk.High, request.Optional, [path, AccessText(request.Access)], reason, request.Localized));
        }
    }

    private static void ReadAgentData(ManifestObject fs, List<FsAgentDataRequest> agentData, List<PermissionRequest> requests)
    {
        foreach (var item in fs.Objects("agentData"))
        {
            item.WarnUnknown("agent", "include", "access", "reason", "optional", "localized");
            var agent = item.String("agent", required: true);
            if (agent is null) continue;
            var include = item.Strings("include");
            if (include.Count == 0)
            {
                item.Log.Error(item.At("include"), "must list the files the plugin reads, e.g. \"projects/**/*.jsonl\"");
                continue;
            }
            var access = Access(item, "access", FileAccessKind.Read);
            // Writing into another program's data is a different kind of request from reading its logs.
            var risk = access == FileAccessKind.ReadWrite ? PermissionRisk.High : PermissionRisk.Medium;
            var request = new FsAgentDataRequest(agent, include, access, item.String("reason"), item.Bool("optional"), item.Localized());
            agentData.Add(request);
            requests.Add(new PermissionRequest($"fs.agentData.{agent}", risk, request.Optional, [AccessText(access), .. include], request.Reason, request.Localized));
        }
    }

    private static FileAccessKind Access(ManifestObject obj, string name, FileAccessKind fallback) => obj.String(name) switch
    {
        null => fallback,
        "read" => FileAccessKind.Read,
        "readwrite" => FileAccessKind.ReadWrite,
        var other => Invalid(obj, name, other, fallback),
    };

    private static FileAccessKind Invalid(ManifestObject obj, string name, string value, FileAccessKind fallback)
    {
        obj.Log.Error(obj.At(name), $"\"{value}\" must be \"read\" or \"readwrite\"");
        return fallback;
    }

    private static string AccessText(FileAccessKind access) => access == FileAccessKind.ReadWrite ? "readwrite" : "read";

    // ---------------------------------------------------------------- system & process

    /// <summary>
    /// <c>"system": ["metrics", "process.signal"]</c> or
    /// <c>"system": {"metrics": true, "processSignal": {"optional": true}}</c>.
    /// </summary>
    private static void ReadSystem(ManifestObject permissions, List<PermissionRequest> requests)
    {
        if (permissions.Raw("system") is not { } value) return;
        if (value.ValueKind is JsonValueKind.Array or JsonValueKind.String)
        {
            Each(permissions, "system", new()
            {
                ["metrics"] = PermissionRisk.Medium,
                ["process.signal"] = PermissionRisk.High,
            }, requests);
            return;
        }
        if (permissions.Object("system") is not { } system) return;
        system.WarnUnknown("metrics", "processSignal", "process.signal");
        ReadSystemFlag(system, "metrics", "system.metrics", PermissionRisk.Medium, requests);
        ReadSystemFlag(system, "processSignal", "system.process.signal", PermissionRisk.High, requests);
        ReadSystemFlag(system, "process.signal", "system.process.signal", PermissionRisk.High, requests);
    }

    private static void ReadSystemFlag(ManifestObject system, string property, string key, PermissionRisk risk, List<PermissionRequest> requests)
    {
        if (system.Raw(property) is not { } value || value.ValueKind == JsonValueKind.False) return;
        if (requests.Any(r => r.Key == key)) return;
        var optional = false;
        if (value.ValueKind == JsonValueKind.Object && system.Object(property) is { } obj)
        {
            obj.WarnUnknown("optional");
            optional = obj.Bool("optional");
        }
        else if (value.ValueKind != JsonValueKind.True)
        {
            system.Log.Error(system.At(property), "must be true or an object");
            return;
        }
        requests.Add(new PermissionRequest(key, risk, optional, []));
    }

    /// <summary>
    /// <c>"process": {"run": ["docker", "ps"], "devices": ["local", "ssh", "remote"], "optional": false}</c>.
    /// When <c>devices</c> is omitted, execution stays restricted to the local computer.
    /// </summary>
    private static void ReadProcess(ManifestObject permissions, List<PermissionRequest> requests)
    {
        if (permissions.Object("process") is not { } obj) return;
        obj.WarnUnknown("run", "devices", "optional");
        var programs = obj.Strings("run").Where(s => s.Trim().Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (programs.Count == 0)
        {
            obj.Log.Error(obj.At("run"), "must list at least one entry");
            return;
        }
        var optional = obj.Bool("optional");
        requests.Add(new PermissionRequest("process.run", PermissionRisk.High, optional, programs));

        var devices = obj.Strings("devices").Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (devices.Count > 0)
        {
            requests.Add(new PermissionRequest("process.run.devices", PermissionRisk.High, optional, devices));
        }
    }

    // ---------------------------------------------------------------- agents

    /// <summary>
    /// <c>"agents": ["launch"]</c>, or the object form
    /// <c>{"launch": true, "signals": {"claude": ["statusline"]}, "probes": {"codex": ["rateLimits"]}}</c>.
    /// </summary>
    private static void ReadAgents(ManifestObject permissions, List<PermissionRequest> requests)
    {
        if (permissions.Raw("agents") is not { } value) return;
        if (value.ValueKind is JsonValueKind.Array or JsonValueKind.String)
        {
            Each(permissions, "agents", new() { ["launch"] = PermissionRisk.High }, requests);
            return;
        }
        if (permissions.Object("agents") is not { } agents) return;
        agents.WarnUnknown("launch", "signals", "probes");
        if (agents.Bool("launch"))
        {
            requests.Add(new PermissionRequest("agents.launch", PermissionRisk.High, false, []));
        }
        PerAgent(agents, "signals", channel => SensitiveChannels.Contains(channel) ? PermissionRisk.High : PermissionRisk.Medium, requests);
        PerAgent(agents, "probes", _ => PermissionRisk.Medium, requests);
    }

    private static void PerAgent(ManifestObject agents, string name, Func<string, PermissionRisk> risk, List<PermissionRequest> requests)
    {
        if (agents.Object(name) is not { } byAgent) return;
        foreach (var (agent, _) in byAgent.Properties())
        {
            foreach (var item in byAgent.Strings(agent).Distinct(StringComparer.Ordinal))
            {
                requests.Add(new PermissionRequest($"agents.{name}.{agent}.{item}", risk(item), false, [agent, item]));
            }
        }
    }
}

/// <summary>
/// Evaluates the minimum risk level implied by marketplace capability strings so UI risk displays never understate
/// documented high- or medium-risk capabilities.
/// </summary>
public static class PermissionRiskCatalog
{
    public static PermissionRisk EffectiveRisk(string? serverRiskLevel, IReadOnlyList<string> permissions)
    {
        var risk = ParseRisk(serverRiskLevel);
        foreach (var capability in permissions)
        {
            var implied = RiskOfDeclaredCapability(capability);
            if (implied > risk) risk = implied;
        }
        return risk;
    }

    public static PermissionRisk RiskOfDeclaredCapability(string? capability)
    {
        if (string.IsNullOrWhiteSpace(capability)) return PermissionRisk.Low;
        var token = capability.Trim().ToLowerInvariant();

        if (token is "secrets" or "unsafeeval" or "fs.external" or "fs.agentdata" or "terminal" or "process.run" or "process.run.devices" or "system.process.signal" or "agents.launch" or "clipboard.read" or "clipboard:read"
            || token.StartsWith("fs.external:", StringComparison.Ordinal)
            || token.StartsWith("fs.external.", StringComparison.Ordinal)
            || token.StartsWith("fs.agentdata:", StringComparison.Ordinal)
            || token.StartsWith("fs.agentdata.", StringComparison.Ordinal)
            || token.StartsWith("terminal.", StringComparison.Ordinal)
            || token.StartsWith("terminal:", StringComparison.Ordinal)
            || token.StartsWith("process.run.", StringComparison.Ordinal)
            || token.StartsWith("process.run:", StringComparison.Ordinal))
        {
            return PermissionRisk.High;
        }

        if (token.StartsWith("fs.project", StringComparison.Ordinal))
        {
            return token.Contains("readwrite", StringComparison.Ordinal) || token.Contains("write", StringComparison.Ordinal)
                ? PermissionRisk.High
                : PermissionRisk.Medium;
        }

        if (token.StartsWith("agents.signals.", StringComparison.Ordinal)
            && (token.EndsWith(".prompt-submit", StringComparison.Ordinal)
                || token.EndsWith(".permission-request", StringComparison.Ordinal)
                || token.EndsWith(".pre-tool-use", StringComparison.Ordinal)
                || token.EndsWith(".post-tool-use", StringComparison.Ordinal)))
        {
            return PermissionRisk.High;
        }

        if (token is "net" or "net.hosts"
            || token.StartsWith("net:", StringComparison.Ordinal)
            || token.StartsWith("net.hosts:", StringComparison.Ordinal))
        {
            var colon = token.IndexOf(':');
            if (colon >= 0)
            {
                var hosts = token[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (hosts.Any(PermissionReader.IsWildcardHostPattern))
                {
                    return PermissionRisk.High;
                }
            }
            return PermissionRisk.Medium;
        }

        if (token is "git" or "git:read" or "git.read" or "workspace" or "workspace:write" or "workspace.write" or "shell.open" or "system.metrics"
            || token.StartsWith("shell.open:", StringComparison.Ordinal)
            || token.StartsWith("agents.signals", StringComparison.Ordinal)
            || token.StartsWith("agents.probes", StringComparison.Ordinal))
        {
            return PermissionRisk.Medium;
        }

        return PermissionRisk.Low;
    }

    private static PermissionRisk ParseRisk(string? serverRiskLevel) => serverRiskLevel?.Trim().ToLowerInvariant() switch
    {
        "high" => PermissionRisk.High,
        "medium" => PermissionRisk.Medium,
        _ => PermissionRisk.Low,
    };
}
