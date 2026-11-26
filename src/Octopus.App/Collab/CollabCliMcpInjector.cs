using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.Platform;

namespace Octopus.App.Collab;

/// <summary>
/// Prepares per-session MCP configuration arguments and environment variables so native terminal CLI tabs
/// (Claude Code, Codex CLI, Antigravity CLI, etc.) can invoke Octopus's local collaboration MCP gateway.
/// </summary>
public sealed class CollabCliMcpInjector(CollabMcpServer server, IAppPaths paths)
{
    private const string FolderName = "collab-mcp";

    /// <summary>
    /// Computes extra CLI arguments and environment variables that wire <c>octopus-collab</c> into a terminal agent.
    /// Does not modify any of the user's own config files; per-session JSON files live under the Octopus cache directory.
    /// </summary>
    public CollabCliAttachment? Plan(AgentDefinition definition, ProjectItem item, string? forwarderExecutable)
    {
        if (!server.IsRunning || string.IsNullOrWhiteSpace(server.EndpointUrl))
        {
            return null;
        }
        var token = server.IssueToken(item.Id);
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CollabMcpServer.UrlEnvVariable] = server.EndpointUrl,
            [CollabMcpServer.TokenEnvVariable] = token,
            [CollabMcpServer.ItemIdEnvVariable] = item.Id,
        };

        var hasForwarder = !string.IsNullOrWhiteSpace(forwarderExecutable) && File.Exists(forwarderExecutable);
        var args = new List<string>();
        var id = definition.Id.Trim().ToLowerInvariant();

        if (id == "claude" && hasForwarder)
        {
            var configPath = WriteClaudeMcpConfig(item.Id, forwarderExecutable!, server.EndpointUrl, token);
            if (configPath is not null)
            {
                args.Add("--mcp-config");
                args.Add(configPath);
            }
        }
        else if (id == "codex" && hasForwarder)
        {
            // Codex CLI supports inline TOML config overrides via -c key=value (same pattern as codex.json notify).
            var escapedCmd = EscapeTomlString(forwarderExecutable!);
            var escapedUrl = EscapeTomlString(server.EndpointUrl);
            var escapedToken = EscapeTomlString(token);
            var escapedItem = EscapeTomlString(item.Id);
            args.Add("-c");
            args.Add($"mcp_servers.octopus_collab.command=\"{escapedCmd}\"");
            args.Add("-c");
            args.Add("mcp_servers.octopus_collab.args=[\"--mcp\"]");
            args.Add("-c");
            args.Add($"mcp_servers.octopus_collab.env={{ {CollabMcpServer.UrlEnvVariable} = \"{escapedUrl}\", {CollabMcpServer.TokenEnvVariable} = \"{escapedToken}\", {CollabMcpServer.ItemIdEnvVariable} = \"{escapedItem}\" }}");
        }

        return new CollabCliAttachment(args, env);
    }

    public void CleanupItem(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId)) return;
        server.RevokeToken(itemId);
        try
        {
            var file = Path.Combine(paths.CacheDirectory, FolderName, $"{Sanitize(itemId)}.json");
            if (File.Exists(file)) File.Delete(file);
        }
        catch
        {
        }
    }

    private string? WriteClaudeMcpConfig(string itemId, string forwarderPath, string url, string token)
    {
        try
        {
            var dir = Path.Combine(paths.CacheDirectory, FolderName);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{Sanitize(itemId)}.json");
            var doc = new JsonObject
            {
                ["mcpServers"] = new JsonObject
                {
                    [CollabMcpServer.ServerName] = new JsonObject
                    {
                        ["command"] = forwarderPath,
                        ["args"] = new JsonArray("--mcp"),
                        ["env"] = new JsonObject
                        {
                            [CollabMcpServer.UrlEnvVariable] = url,
                            [CollabMcpServer.TokenEnvVariable] = token,
                            [CollabMcpServer.ItemIdEnvVariable] = itemId,
                        },
                    },
                },
            };
            File.WriteAllText(path, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static string EscapeTomlString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string Sanitize(string itemId)
    {
        var sb = new StringBuilder(itemId.Length);
        foreach (var ch in itemId)
        {
            sb.Append(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_');
        }
        return sb.Length > 0 ? sb.ToString() : "session";
    }
}

public sealed record CollabCliAttachment(
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment);
