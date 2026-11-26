using System.Text;
using System.Text.Json;
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
                Color = bot.Color,
                IsPaused = bot.IsPaused,
                HomeChatId = bot.HomeChatId,
                Connectors = [.. bot.Connectors.Where(c => c.Enabled).Select(c => c.DisplayName)],
                CreatedAt = bot.CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            var json = JsonSerializer.Serialize(manifest, OctopusJsonContext.FileTypeInfo<BotHomeManifest>());
            File.WriteAllText(GetManifestPath(bot), json, Encoding.UTF8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Disk errors on mirror files must not crash UI state transitions.
        }
    }

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
        if (bot.Connectors.Any(c => c.Enabled))
        {
            sb.AppendLine();
            sb.AppendLine("## Connected Apps");
            foreach (var conn in bot.Connectors.Where(c => c.Enabled))
            {
                sb.Append("- ").Append(conn.DisplayName);
                if (!string.IsNullOrWhiteSpace(conn.Url)) sb.Append(" (").Append(conn.Url).Append(')');
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }
}
