using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Octopus.Agents;
using Octopus.Git;

namespace Octopus.Plugins.Runtime.Modules;

/// <summary><c>octopus.projects</c>: the current project for everyone, all projects with the <c>projects</c> permission.</summary>
public sealed class ProjectsModule : IPluginApiModule
{
    private readonly IPluginProjectContext _projects;
    private readonly ConcurrentDictionary<PluginHost, byte> _listeners = new();

    public ProjectsModule(IPluginProjectContext projects)
    {
        _projects = projects;
        projects.CurrentChanged += (_, _) =>
        {
            var current = Json(projects.Current);
            foreach (var plugin in _listeners.Keys) plugin.Emit("projects.current", current?.DeepClone());
        };
    }

    public static JsonObject? Json(PluginProjectInfo? project) => project is null ? null : new JsonObject
    {
        ["id"] = project.Id,
        ["name"] = project.Name,
        ["path"] = project.Path,
        ["color"] = project.Color,
    };

    public void Register(PluginApiRouter router)
    {
        router.Sync("projects.current", _ => Json(_projects.Current)?.ToJsonString() ?? "");
        router.Request("projects.all", (call, _) =>
        {
            call.Plugin.Grants.Require("projects");
            var all = new JsonArray();
            foreach (var project in _projects.All) all.Add((JsonNode?)Json(project));
            return Task.FromResult(PluginReply.Of(all));
        });
    }

    public void Subscribed(PluginHost plugin, PluginSubscription subscription)
    {
        if (subscription.Event == "projects.current") _listeners[plugin] = 0;
    }

    public void Reset(PluginHost plugin) => _listeners.TryRemove(plugin, out _);
}

/// <summary><c>octopus.git</c>: read-only state of the current project's repository (the <c>git</c> permission).</summary>
public sealed class GitModule(IGitService git, IPluginProjectContext projects) : IPluginApiModule
{
    public void Register(PluginApiRouter router)
    {
        router.Sync("git.state", call =>
        {
            // A synchronous read must not run git: it answers from what the last status call learned.
            return call.Plugin.Grants.Has("git") && _last.TryGetValue(projects.Current?.Path ?? "", out var state) ? state.ToJsonString() : "";
        });
        router.Request("git.status", async (call, ct) =>
        {
            var root = await RootAsync(call, ct).ConfigureAwait(false);
            var status = await git.GetStatusAsync(root, ct).ConfigureAwait(false) ?? throw new PluginApiException("Io", "git status failed.");
            _last[projects.Current!.Path] = new JsonObject { ["root"] = root, ["branch"] = status.Branch };
            var changes = new JsonArray();
            foreach (var change in status.Changes)
            {
                changes.Add((JsonNode)new JsonObject { ["path"] = change.FullPath, ["status"] = change.Kind.ToString().ToLowerInvariant(), ["staged"] = change.IsStaged });
            }
            return PluginReply.Of(changes);
        });
        router.Request("git.log", async (call, ct) =>
        {
            var root = await RootAsync(call, ct).ConfigureAwait(false);
            var limit = (int)Math.Clamp(call.Long("limit") ?? 50, 1, 500);
            var log = new JsonArray();
            foreach (var commit in await git.GetLogAsync(root, limit, ct).ConfigureAwait(false))
            {
                log.Add((JsonNode)new JsonObject { ["hash"] = commit.Hash, ["subject"] = commit.Subject, ["author"] = commit.Author, ["time"] = commit.Date.ToUnixTimeMilliseconds() });
            }
            return PluginReply.Of(log);
        });
        router.Request("git.diff", async (call, ct) =>
        {
            var root = await RootAsync(call, ct).ConfigureAwait(false);
            var diff = await git.GetFileDiffAsync(root, call.RequiredString("path"), call.Bool("staged"), null, ct).ConfigureAwait(false)
                ?? throw new PluginApiException("Io", "git diff failed.");
            if (diff.Kind != GitFileDiffKind.Text)
            {
                return PluginReply.OfText("");
            }
            // Unified diff text, the form plugins and agents understand.
            var text = new System.Text.StringBuilder();
            foreach (var line in diff.Lines)
            {
                text.Append(line.Kind switch
                {
                    GitDiffLineKind.Added => "+",
                    GitDiffLineKind.Removed => "-",
                    GitDiffLineKind.Context => " ",
                    GitDiffLineKind.Note => "\\ ",
                    _ => "",
                }).Append(line.Text).Append('\n');
            }
            return PluginReply.OfText(text.ToString());
        });
    }

    private readonly ConcurrentDictionary<string, JsonObject> _last = new(StringComparer.Ordinal);

    private async Task<string> RootAsync(PluginCall call, CancellationToken ct)
    {
        call.Plugin.Grants.Require("git");
        var project = projects.Current ?? throw new PluginApiException("NotFound", "There is no current project.");
        return await git.FindRepositoryRootAsync(project.Path, ct).ConfigureAwait(false)
            ?? throw new PluginApiException("NotFound", "The current project is not in a git repository.");
    }
}

/// <summary><c>agents.list</c>: which agents Octopus knows and which are installed (no profiles, no environment variables).</summary>
public sealed class AgentsListModule(IAgentCatalog catalog, IAgentScanner scanner) : IPluginApiModule
{
    public void Register(PluginApiRouter router)
    {
        router.Request("agents.list", (call, _) =>
        {
            var list = new JsonArray();
            foreach (var definition in catalog.Definitions)
            {
                var installed = scanner.Find(definition.Id);
                list.Add((JsonNode)new JsonObject
                {
                    ["id"] = definition.Id,
                    ["name"] = definition.Name,
                    ["installed"] = installed is not null,
                    ["version"] = installed?.Version,
                });
            }
            return Task.FromResult(PluginReply.Of(list));
        });
    }
}
