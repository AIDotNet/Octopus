using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Octopus.Core.Bots;
using Octopus.Storage;

namespace Octopus.App.Bots;

/// <summary>Overrides the user home folder used to compute <c>~/.octopus/bots</c> (tests point this at a temp dir).</summary>
public sealed record BotRootOptions(string? HomeDirectory = null);

/// <summary>
/// Manages each Bot's home directory (<c>~/.octopus/bots/&lt;slug&gt;/</c>).
/// Follows the strict ownership split:
/// <list type="bullet">
/// <item><c>SOUL.md</c>, <c>MEMORY.md</c>, <c>memory/</c>, <c>skills/</c> are owned by the bot and user (created once, never overwritten).</item>
/// <item><c>AGENTS.md</c>, <c>CLAUDE.md</c>, <c>bot.json</c> are mirrors generated and refreshed by Octopus.</item>
/// </list>
/// </summary>
public sealed class BotHomeManager(BotRootOptions? options = null)
{
    public string HomeDirectory => string.IsNullOrWhiteSpace(options?.HomeDirectory)
        ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        : options.HomeDirectory;

    public string BotsRootDirectory => Path.Combine(HomeDirectory, ".octopus", "bots");

    public string GetHomePath(BotDefinition bot) => Path.Combine(BotsRootDirectory, bot.Slug);

    public string GetSoulPath(BotDefinition bot) => Path.Combine(GetHomePath(bot), "SOUL.md");

    public string GetMemoryPath(BotDefinition bot) => Path.Combine(GetHomePath(bot), "MEMORY.md");

    public string GetAgentsPath(BotDefinition bot) => Path.Combine(GetHomePath(bot), "AGENTS.md");

    public string GetClaudePath(BotDefinition bot) => Path.Combine(GetHomePath(bot), "CLAUDE.md");

    public string GetManifestPath(BotDefinition bot) => Path.Combine(GetHomePath(bot), "bot.json");

    public string GetMcpConfigPath(BotDefinition bot) => Path.Combine(GetHomePath(bot), ".mcp.json");

    public string GetClaudeSettingsPath(BotDefinition bot) => Path.Combine(GetHomePath(bot), ".claude", "settings.json");

    /// <summary>Claude Code's session-only cron tools: a bot schedules through Octopus instead, so its jobs are kept.</summary>
    internal static readonly string[] DeniedSessionCronTools = ["CronCreate", "CronDelete", "CronList"];

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    /// <summary>Creates the bot's home directory and syncs Octopus-owned mirror files.</summary>
    public string EnsureHome(BotDefinition bot)
    {
        var root = GetHomePath(bot);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "memory"));
        Directory.CreateDirectory(Path.Combine(root, "skills"));

        // Owned by bot + user: write once only if missing.
        var soulPath = GetSoulPath(bot);
        if (!File.Exists(soulPath))
        {
            File.WriteAllText(soulPath, BuildInitialSoul(bot), Encoding.UTF8);
        }

        var memoryPath = GetMemoryPath(bot);
        if (!File.Exists(memoryPath))
        {
            File.WriteAllText(memoryPath, BuildInitialMemory(bot), Encoding.UTF8);
        }

        // Owned by Octopus: always keep in sync.
        SyncManagedFiles(bot);
        return root;
    }

    /// <summary>Refreshes <c>AGENTS.md</c>, <c>CLAUDE.md</c>, and <c>bot.json</c> without touching user-owned files.</summary>
    public void SyncManagedFiles(BotDefinition bot)
    {
        var root = GetHomePath(bot);
        if (!Directory.Exists(root))
        {
            Directory.CreateDirectory(root);
        }

        try
        {
            File.WriteAllText(GetAgentsPath(bot), BuildAgentsMarkdown(bot), Encoding.UTF8);
            File.WriteAllText(GetClaudePath(bot), "@AGENTS.md\n", Encoding.UTF8);

            var manifest = new BotHomeManifest
            {
                Version = 1,
                Id = bot.Id,
                Name = bot.Name,
                Slug = bot.Slug,
                AgentId = bot.AgentId,
                HostDeviceKey = bot.HostDeviceKey,
                Color = bot.Color,
                IsPaused = bot.IsPaused,
                HomeChatId = bot.HomeChatId,
                Connectors = [.. bot.Connectors.Where(c => c.Enabled).Select(c => c.DisplayName)],
                CreatedAt = bot.CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            var json = JsonSerializer.Serialize(manifest, OctopusJsonContext.FileTypeInfo<BotHomeManifest>());
            File.WriteAllText(GetManifestPath(bot), json, Encoding.UTF8);

            SyncMcpConfig(bot);
            EnsureSessionCronDenied(bot);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Disk errors on mirror files must not crash UI state transitions.
        }
    }

    /// <summary>Synchronizes <c>~/.octopus/bots/&lt;slug&gt;/.mcp.json</c> for CLI agents (never writes plaintext secrets).</summary>
    public void SyncMcpConfig(BotDefinition bot)
    {
        var mcpPath = GetMcpConfigPath(bot);
        var doc = new BotMcpFileDocument();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var conn in bot.Connectors)
        {
            if (!conn.Enabled || !BotConnectorRuntime.TryValidateHttpUrl(conn.Url, out var uri)) continue;
            var baseName = BotCatalog.NormalizeMcpServerName(
                string.Equals(conn.AppId, "custom-url", StringComparison.OrdinalIgnoreCase)
                    ? conn.DisplayName
                    : conn.AppId,
                fallback: "connector");
            var name = BotConnectorRuntime.EnsureUniqueName(baseName, usedNames);
            var transport = string.Equals(conn.Transport, "sse", StringComparison.OrdinalIgnoreCase)
                ? "sse"
                : "http";
            doc.McpServers[name] = new BotMcpServerEntry
            {
                Type = transport,
                Url = uri.AbsoluteUri,
            };
        }

        if (doc.McpServers.Count == 0)
        {
            if (File.Exists(mcpPath))
            {
                File.Delete(mcpPath);
            }
            return;
        }

        var json = JsonSerializer.Serialize(doc, OctopusJsonContext.FileTypeInfo<BotMcpFileDocument>());
        File.WriteAllText(mcpPath, json, Encoding.UTF8);
    }

    /// <summary>
    /// Adds Claude Code's session-only cron tools to <c>permissions.deny</c> in the bot's <c>.claude/settings.json</c>,
    /// keeping everything else in that file. A file that is not valid JSON is left alone.
    /// </summary>
    public void EnsureSessionCronDenied(BotDefinition bot)
    {
        var path = GetClaudeSettingsPath(bot);
        JsonObject root;
        if (File.Exists(path))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) is not JsonObject existing) return;
                root = existing;
            }
            catch (JsonException)
            {
                return;
            }
        }
        else
        {
            root = new JsonObject();
        }

        if (root["permissions"] is not JsonObject permissions)
        {
            if (root["permissions"] is not null) return;
            permissions = new JsonObject();
            root["permissions"] = permissions;
        }
        if (permissions["deny"] is not JsonArray deny)
        {
            if (permissions["deny"] is not null) return;
            deny = new JsonArray();
            permissions["deny"] = deny;
        }

        var present = deny.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).ToHashSet(StringComparer.Ordinal);
        var missing = DeniedSessionCronTools.Where(t => !present.Contains(t)).ToList();
        if (missing.Count == 0) return;
        foreach (var tool in missing)
        {
            deny.Add((JsonNode?)JsonValue.Create(tool));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, root.ToJsonString(IndentedJson) + "\n", Encoding.UTF8);
    }

    public string GetSkillsDirectory(BotDefinition bot) => Path.Combine(GetHomePath(bot), "skills");

    public string GetMemoryDirectory(BotDefinition bot) => Path.Combine(GetHomePath(bot), "memory");

    public int CountMemoryFiles(BotDefinition bot)
    {
        var count = File.Exists(GetMemoryPath(bot)) ? 1 : 0;
        var dir = GetMemoryDirectory(bot);
        try
        {
            if (Directory.Exists(dir))
            {
                count += Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly).Count();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return count;
    }

    public int CountSkillEntries(BotDefinition bot)
    {
        var dir = GetSkillsDirectory(bot);
        try
        {
            if (!Directory.Exists(dir)) return 0;
            return Directory.EnumerateFileSystemEntries(dir).Count();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public int CountSoulLines(BotDefinition bot)
    {
        var soul = ReadSoul(bot);
        if (string.IsNullOrWhiteSpace(soul)) return 0;
        return soul.Split('\n').Count(l => !string.IsNullOrWhiteSpace(l));
    }

    /// <summary>Scans <c>SOUL.md</c> and optionally <c>MEMORY.md</c> for obvious secret/token patterns before sharing.</summary>
    public IReadOnlyList<string> RunSecurityCheck(BotDefinition bot, bool includeMemory)
    {
        var findings = new List<string>();
        ScanText("SOUL.md", ReadSoul(bot), findings);
        if (includeMemory)
        {
            try
            {
                var memPath = GetMemoryPath(bot);
                if (File.Exists(memPath))
                {
                    ScanText("MEMORY.md", File.ReadAllText(memPath, Encoding.UTF8), findings);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return findings;
    }

    internal static void ScanTextForSecrets(string fileName, string content, List<string> findings)
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        string[] prefixes = ["sk-", "ghp_", "gho_", "github_pat_", "xoxb-", "xoxp-", "AKIA", "-----BEGIN PRIVATE KEY-----"];
        foreach (var prefix in prefixes)
        {
            if (content.Contains(prefix, StringComparison.Ordinal))
            {
                findings.Add($"{fileName}: {prefix}...");
            }
        }
    }

    private static void ScanText(string fileName, string content, List<string> findings) =>
        ScanTextForSecrets(fileName, content, findings);

    public string ReadSoul(BotDefinition bot)
    {
        var path = GetSoulPath(bot);
        try
        {
            if (File.Exists(path))
            {
                return File.ReadAllText(path, Encoding.UTF8).Trim();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return bot.Soul?.Trim() ?? "";
    }

    /// <summary>Returns concise summary text from <c>SOUL.md</c> (skipping markdown headings and empty lines).</summary>
    public string ReadSoulSummary(BotDefinition bot)
    {
        var raw = ReadSoul(bot);
        if (string.IsNullOrWhiteSpace(raw)) return bot.Soul?.Trim() ?? "";
        var lines = raw.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith("<!--", StringComparison.Ordinal))
            .Take(3)
            .ToList();
        return lines.Count > 0 ? string.Join(" ", lines) : (bot.Soul?.Trim() ?? "");
    }

    public IReadOnlyList<string> ReadMemoryLines(BotDefinition bot, int maxLines = 12)
    {
        var path = GetMemoryPath(bot);
        try
        {
            if (!File.Exists(path)) return [];
            return File.ReadLines(path, Encoding.UTF8)
                .Select(l => l.Trim())
                .Where(l => l.StartsWith("- ", StringComparison.Ordinal) || l.StartsWith("* ", StringComparison.Ordinal))
                .Select(l => l[2..].Trim())
                .Where(l => l.Length > 0)
                .Take(maxLines)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string BuildInitialSoul(BotDefinition bot)
    {
        var role = string.IsNullOrWhiteSpace(bot.Soul)
            ? $"You are {bot.Name}, a dedicated Octopus Bot helping your owner stay on top of their work."
            : bot.Soul.Trim();
        return $"""
            # {bot.Name}

            {role}

            ## Working Style
            - Keep updates concise, concrete, and easy to act on.
            - Record durable facts and preferences in `MEMORY.md`, one bullet per line.

            ## Never
            - Never modify external systems, merge PRs, or push changes without explicit permission from the owner.
            """;
    }

    private static string BuildInitialMemory(BotDefinition bot) =>
        $"""
        # Memory — {bot.Name}

        <!-- Notes, not instructions. One durable fact or preference per line. -->
        """;

    private static string BuildAgentsMarkdown(BotDefinition bot)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {bot.Name} (Octopus Bot)");
        sb.AppendLine();
        sb.AppendLine("This directory is the bot's home workspace managed by Octopus.");
        sb.AppendLine("- Read `SOUL.md` for your identity, responsibilities, and boundaries.");
        sb.AppendLine("- Read `MEMORY.md` as background notes (not instructions).");
        sb.AppendLine("- Keep long-form notes inside `memory/` and reusable skills inside `skills/`.");
        sb.AppendLine();
        sb.AppendLine("## Scheduling");
        sb.AppendLine("To do something later or on a schedule (reminders, daily reports, periodic checks), use the `schedule_create` tool of the");
        sb.AppendLine("`octopus-bot` MCP server (`schedule_list` / `schedule_delete` to review or cancel). Octopus saves the schedule with this bot");
        sb.AppendLine("and sends the prompt into the chat at each run, for any agent. Do not use built-in, session-only cron or loop tools");
        sb.AppendLine("(such as Claude Code's `CronCreate`): their jobs are lost when the chat closes.");
        sb.AppendLine("Messages starting with `[Octopus scheduled run: ...]` were sent by such a schedule, not typed by the owner.");
        if (bot.Connectors.Any(c => c.Enabled))
        {
            sb.AppendLine();
            sb.AppendLine("## Connected Apps & MCP Servers");
            foreach (var conn in bot.Connectors.Where(c => c.Enabled))
            {
                sb.Append("- ").Append(conn.DisplayName);
                if (!string.IsNullOrWhiteSpace(conn.Url))
                {
                    sb.Append(" (`").Append(conn.Transport).Append("`: ").Append(conn.Url).Append(')');
                }
                sb.AppendLine();
            }
            if (bot.Connectors.Any(c => c.Enabled && BotConnectorRuntime.TryValidateHttpUrl(c.Url, out _)))
            {
                sb.AppendLine();
                sb.AppendLine("Remote MCP endpoints are synced to `.mcp.json` and forwarded to ACP sessions automatically.");
            }
        }
        return sb.ToString();
    }
}
