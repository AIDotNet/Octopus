using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Octopus.Agents;
using Octopus.Agents.Signals;
using Octopus.Core.Plugins;
using Octopus.FileSystem;
using Octopus.Platform;
using Octopus.Plugins.Catalog;
using Octopus.Plugins.Security;

namespace Octopus.Plugins.Runtime.Modules;

/// <summary>
/// <c>octopus.fs</c> (design/plugins/04-权限与文件系统.md, 11-补充能力.md §4–§5). Every call asks the access policy
/// first and then works on the resolved real path, so a link cannot carry a granted scope elsewhere. Writes, deletes
/// and reads outside the plugin's own directories go to its audit log.
/// </summary>
public sealed class FsModule : IPluginApiModule
{
    private readonly IFileAccessService _files;
    private readonly IPluginAccessPolicy _policy;
    private readonly ITrashService _trash;
    private readonly PluginPaths _paths;
    private readonly IAppPaths _app;
    private readonly IPluginProjectContext _projects;
    private readonly IAgentCatalog _agents;
    private readonly ISignalHub? _hub;
    private readonly IPluginCatalog _catalog;
    private readonly IFileOpener? _opener;
    private readonly ConcurrentDictionary<(string Plugin, string Key), FileSystemWatcher> _watchers = new();

    /// <summary>The <c>fs.searchText</c> listeners, by plugin and search, waiting for the request they belong to.</summary>
    private readonly ConcurrentDictionary<(string Plugin, string Search), ConcurrentQueue<string>> _searchListeners = new();

    /// <summary>A text search reads at most this many files, each at most <see cref="MaxSearchFileBytes"/>, and reports at most <see cref="MaxSearchMatches"/> lines.</summary>
    public const int MaxSearchFiles = 20_000;
    public const long MaxSearchFileBytes = 2 * 1024 * 1024;
    public const int MaxSearchMatches = 10_000;
    private const int SearchBatch = 200;
    private readonly ConcurrentDictionary<string, CachedGrants> _grants = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AuditLog> _audit = new(StringComparer.Ordinal);
    private readonly string _home;

    public FsModule(
        IFileAccessService files,
        IPluginAccessPolicy policy,
        ITrashService trash,
        PluginPaths paths,
        IAppPaths app,
        IPluginProjectContext projects,
        IAgentCatalog agents,
        IPluginCatalog catalog,
        ISignalHub? hub = null,
        IFileOpener? opener = null,
        UserHome? home = null)
    {
        _opener = opener;
        _home = (home ?? UserHome.Current).Path;
        _files = files;
        _policy = policy;
        _trash = trash;
        _paths = paths;
        _app = app;
        _projects = projects;
        _agents = agents;
        _catalog = catalog;
        _hub = hub;
    }

    public void Register(PluginApiRouter router)
    {
        router.Request("fs.stat", (call, _) => Run(call, () => PluginReply.Of(Stat(_files.Stat(Read(call, "path"))))));
        router.Request("fs.exists", (call, _) => Run(call, () =>
        {
            var decision = Decide(call, call.RequiredString("path"), FileAccessKind.Read);
            return PluginReply.Of(decision.Allowed && _files.TryStat(decision.RealPath!) is not null);
        }));
        router.Request("fs.list", (call, _) => Run(call, () =>
        {
            var directory = Read(call, "directory", isDirectory: true);
            var listing = _files.List(directory);
            var entries = new JsonArray();
            // Entries the plugin may not see (a credential file, an excluded folder) are left out, not shown as denied.
            foreach (var entry in listing.Entries)
            {
                if (Decide(call, entry.Path, FileAccessKind.Read, entry.IsDirectory).Allowed) entries.Add((JsonNode)Stat(entry));
            }
            return PluginReply.Of(new JsonObject { ["entries"] = entries, ["truncated"] = listing.Truncated });
        }));
        router.Request("fs.readText", (call, _) => Run(call, () =>
        {
            var text = _files.ReadText(Read(call, "path"), call.String("encoding"));
            // An empty file is "" (a reply with empty text would read as undefined).
            return text.Text.Length == 0 ? PluginReply.Of("") : PluginReply.OfText(text.Text);
        }));
        router.Request("fs.readBytes", (call, _) => Run(call, () =>
        {
            var bytes = _files.ReadBytes(Read(call, "path"), call.Long("offset") ?? 0, call.Long("length"));
            return PluginReply.Of(Convert.ToBase64String(bytes));
        }));
        router.Request("fs.isBinary", (call, _) => Run(call, () => PluginReply.Of(_files.IsBinary(Read(call, "path")))));
        router.Request("fs.writeText", (call, _) => Run(call, () =>
        {
            var path = Write(call, "path", "writeText");
            _files.WriteText(path, call.Text, Options(call));
            return PluginReply.Empty;
        }));
        router.Request("fs.writeBytes", (call, _) => Run(call, () =>
        {
            var path = Write(call, "path", "writeBytes");
            _files.WriteBytes(path, Convert.FromBase64String(call.Text), Options(call));
            return PluginReply.Empty;
        }));
        router.Request("fs.appendText", (call, _) => Run(call, () =>
        {
            _files.AppendText(Write(call, "path", "appendText"), call.Text);
            return PluginReply.Empty;
        }));
        router.Request("fs.createDirectory", (call, _) => Run(call, () =>
        {
            _files.CreateDirectory(Write(call, "path", "createDirectory", isDirectory: true), call.Bool("recursive", true));
            return PluginReply.Empty;
        }));
        router.Request("fs.rename", (call, _) => Run(call, () =>
        {
            var from = Write(call, "from", "rename");
            CheckTree(call, from, FileAccessKind.ReadWrite);
            var to = Write(call, "to", "rename", isDirectory: Directory.Exists(from));
            _files.Move(from, to, call.Bool("overwrite"));
            return PluginReply.Empty;
        }));
        router.Request("fs.copy", (call, _) => Run(call, () =>
        {
            var from = Read(call, "from");
            var to = Write(call, "to", "copy", isDirectory: Directory.Exists(from));
            // Recursive copies check every entry: a granted folder may hold a credential file or an excluded folder.
            _files.Copy(from, to, call.Bool("recursive"), call.Bool("overwrite"),
                filter: entry => Decide(call, entry, FileAccessKind.Read).Allowed);
            return PluginReply.Empty;
        }));
        router.Request("fs.delete", (call, _) => Run(call, () =>
        {
            var path = Write(call, "path", call.Bool("permanent") ? "delete" : "trash");
            if (call.Bool("recursive")) CheckTree(call, path, FileAccessKind.ReadWrite);
            // To the trash unless the plugin asks otherwise; a plugin's own data and temporary files go away for good.
            var own = path.StartsWith(_paths.DataDirectoryOf(call.Plugin.Id), StringComparison.Ordinal)
                || path.StartsWith(_paths.TempDirectoryOf(call.Plugin.Id), StringComparison.Ordinal);
            if (!call.Bool("permanent") && !own && _trash.MoveToTrash(path)) return PluginReply.Of(new JsonObject { ["trashed"] = true });
            _files.Delete(path, call.Bool("recursive"));
            return PluginReply.Of(new JsonObject { ["trashed"] = false });
        }));
        router.Request("fs.readLines", (call, _) => Run(call, () =>
        {
            var batch = _files.ReadLines(Read(call, "path"), new LineReadOptions
            {
                FromOffset = call.Long("fromOffset") ?? 0,
                Limit = (int)Math.Clamp(call.Long("limit") ?? FileAccessLimits.DefaultLineLimit, 1, 100_000),
                MaxLineLength = (int)Math.Clamp(call.Long("maxLineLength") ?? FileAccessLimits.DefaultMaxLineLength, 64, 4 * 1024 * 1024),
                IncludeUnterminatedLine = call.Bool("includeUnterminatedLine"),
            });
            var lines = new JsonArray();
            foreach (var line in batch.Lines) lines.Add((JsonNode)JsonValue.Create(line));
            return PluginReply.Of(new JsonObject { ["lines"] = lines, ["nextOffset"] = batch.NextOffset, ["done"] = batch.Done, ["skipped"] = batch.Skipped });
        }));
        router.Request("fs.tail", (call, _) => Run(call, () =>
        {
            var lines = new JsonArray();
            foreach (var line in _files.Tail(Read(call, "path"), (int)Math.Clamp(call.Long("lines") ?? 100, 1, 10_000))) lines.Add((JsonNode)JsonValue.Create(line));
            return PluginReply.Of(lines);
        }));
        router.Request("fs.scanJsonLines", (call, ct) => Run(call, () =>
        {
            Dictionary<string, JsonElement>? where = null;
            if (call["where"] is JsonObject filter)
            {
                where = [];
                foreach (var (name, value) in filter)
                {
                    using var document = JsonDocument.Parse(value?.ToJsonString() ?? "null");
                    where[name] = document.RootElement.Clone();
                }
            }
            var batch = _files.ScanJsonLines(Read(call, "path"), new JsonLinesQuery
            {
                FromOffset = call.Long("fromOffset") ?? 0,
                Where = where,
                Select = call.Strings("select"),
                Limit = (int)Math.Clamp(call.Long("limit") ?? FileAccessLimits.DefaultLineLimit, 1, 100_000),
            }, ct);
            // The rows are already JSON copied from the file: pasted in, not parsed and written again.
            var json = new StringBuilder(batch.RowsJson.Length + 96)
                .Append("{\"rows\":").Append(batch.RowsJson)
                .Append(",\"nextOffset\":").Append(batch.NextOffset)
                .Append(",\"done\":").Append(batch.Done ? "true" : "false")
                .Append(",\"skipped\":").Append(batch.Skipped)
                .Append('}').ToString();
            return new PluginReply(json);
        }));
        router.Request("fs.statMany", (call, ct) => Run(call, () =>
        {
            if (call["paths"] is JsonArray list)
            {
                var result = new JsonArray();
                foreach (var node in list.Take(10_000))
                {
                    var path = node?.GetValue<string>() ?? "";
                    var decision = Decide(call, path, FileAccessKind.Read);
                    result.Add(decision.Allowed && _files.TryStat(decision.RealPath!) is { } stat ? (JsonNode)Stat(stat) : null);
                }
                return PluginReply.Of(result);
            }
            var root = Read(call, "root", isDirectory: true);
            var glob = call.RequiredString("glob");
            var listing = _files.StatMany(root, glob, descend: d => Decide(call, d, FileAccessKind.Read, isDirectory: true).Allowed, ct);
            var entries = new JsonArray();
            foreach (var entry in listing.Entries)
            {
                if (Decide(call, entry.Path, FileAccessKind.Read, entry.IsDirectory).Allowed) entries.Add((JsonNode)Stat(entry));
            }
            return PluginReply.Of(new JsonObject { ["entries"] = entries, ["truncated"] = listing.Truncated });
        }));
        router.Request("fs.findFiles", (call, ct) => Run(call, () => PluginReply.Of(Find(call, call.RequiredString("glob"), ct))));
        router.Request("fs.searchNames", (call, ct) => Run(call, () =>
        {
            var query = call.RequiredString("query").Replace("*", "").Replace("?", "").Trim();
            if (query.Length == 0) throw PluginApiException.Invalid("The query is empty.");
            return PluginReply.Of(Find(call, $"**/*{query}*", ct));
        }));
        router.Request("fs.searchText", (call, ct) => Run(call, () => SearchText(call, ct)));
        router.Request("shell.reveal", (call, _) => Run(call, () =>
        {
            var path = Read(call, "path");
            if (_opener is null) throw PluginApiException.Unsupported("Revealing files");
            _opener.Reveal(path);
            return PluginReply.Empty;
        }));
        router.Sync("fs.scopes", call =>
        {
            var list = new JsonArray();
            foreach (var scope in GrantsOf(call.Plugin).Set.Scopes)
            {
                list.Add((JsonNode)new JsonObject
                {
                    ["kind"] = scope.Kind.ToString().ToLowerInvariant(),
                    ["root"] = scope.Root,
                    ["access"] = scope.Access == FileAccessKind.ReadWrite ? "readwrite" : "read",
                    ["agent"] = scope.Agent,
                    ["account"] = scope.Account,
                });
            }
            return list.ToJsonString();
        });
    }

    public void Reset(PluginHost plugin)
    {
        _grants.TryRemove(plugin.Id, out _);
        foreach (var key in _searchListeners.Keys.Where(k => k.Plugin == plugin.Id).ToList()) _searchListeners.TryRemove(key, out _);
        foreach (var key in _watchers.Keys.Where(k => k.Plugin == plugin.Id).ToList())
        {
            if (_watchers.TryRemove(key, out var watcher)) watcher.Dispose();
        }
    }

    private static string SearchKey(string? root, string? query) => (root ?? "") + "\0" + (query ?? "");

    /// <summary>
    /// <c>fs.searchText</c>: every text file below <c>root</c> the plugin may read, line by line, case-insensitively.
    /// Matches go to the plugin's listener in batches as they are found; the request ends when the search does.
    /// Binary files, files above <see cref="MaxSearchFileBytes"/> and what the plugin may not see are skipped.
    /// </summary>
    private PluginReply SearchText(PluginCall call, CancellationToken cancellationToken)
    {
        var query = call.RequiredString("query");
        var root = Read(call, "root", isDirectory: true);
        var listener = _searchListeners.TryGetValue((call.Plugin.Id, SearchKey(call.String("root"), query)), out var queue) && queue.TryDequeue(out var key) ? key : null;
        var listing = _files.StatMany(root, "**/*", descend: d => Decide(call, d, FileAccessKind.Read, isDirectory: true).Allowed, cancellationToken);
        var batch = new JsonArray();
        var matches = 0;
        var files = 0;
        foreach (var entry in listing.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Kind != FileEntryKind.File || entry.Size > MaxSearchFileBytes) continue;
            if (++files > MaxSearchFiles || matches >= MaxSearchMatches) break;
            var decision = Decide(call, entry.Path, FileAccessKind.Read, isDirectory: false);
            if (!decision.Allowed) continue;
            string text;
            try
            {
                if (_files.IsBinary(decision.RealPath!)) continue;
                text = _files.ReadText(decision.RealPath!).Text;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                continue;
            }
            var line = 0;
            foreach (var content in text.AsSpan().EnumerateLines())
            {
                line++;
                var column = content.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                if (column < 0) continue;
                batch.Add((JsonNode)new JsonObject
                {
                    ["path"] = entry.Path,
                    ["line"] = line,
                    ["column"] = column + 1,
                    ["text"] = content.Length > 500 ? content[..500].ToString() : content.ToString(),
                });
                if (++matches >= MaxSearchMatches) break;
                if (batch.Count >= SearchBatch)
                {
                    if (listener is not null) call.Plugin.EmitTo(listener, batch);
                    batch = new JsonArray();
                }
            }
        }
        if (batch.Count > 0 && listener is not null) call.Plugin.EmitTo(listener, batch);
        return PluginReply.Empty;
    }

    /// <summary>The paths below <c>root</c> matching the glob, as far as the plugin may see them; at most <c>limit</c>.</summary>
    private JsonArray Find(PluginCall call, string glob, CancellationToken cancellationToken)
    {
        var root = Read(call, "root", isDirectory: true);
        var limit = (int)Math.Clamp(call.Long("limit") ?? 1000, 1, 50_000);
        var listing = _files.StatMany(root, glob, descend: d => Decide(call, d, FileAccessKind.Read, isDirectory: true).Allowed, cancellationToken);
        var paths = new JsonArray();
        foreach (var entry in listing.Entries)
        {
            if (paths.Count >= limit) break;
            if (!entry.IsDirectory && Decide(call, entry.Path, FileAccessKind.Read, false).Allowed) paths.Add((JsonNode)JsonValue.Create(entry.Path));
        }
        return paths;
    }

    /// <summary>
    /// <c>fs.watch</c>: a watcher per subscription, only on a directory the plugin may read; each change is checked again
    /// before it is reported, and changes arrive at most every 200 ms per path.
    /// </summary>
    public void Subscribed(PluginHost plugin, PluginSubscription subscription)
    {
        if (subscription.Event == "fs.searchText")
        {
            _searchListeners.GetOrAdd((plugin.Id, SearchKey(subscription.Args?["root"]?.GetValue<string>(), subscription.Args?["query"]?.GetValue<string>())), _ => new())
                .Enqueue(subscription.Key);
            return;
        }
        if (subscription.Event != "fs.watch") return;
        var path = subscription.Args?["path"]?.GetValue<string>();
        if (path is null) return;
        var grants = GrantsOf(plugin);
        var decision = _policy.Check(grants.Set, path, FileAccessKind.Read, grants.Context, isDirectory: true);
        if (!decision.Allowed || !Directory.Exists(decision.RealPath))
        {
            plugin.Write(PluginLogLevel.Warn, $"fs.watch(\"{path}\"): {(decision.Allowed ? "not a directory" : decision.Reason)}; nothing is watched.");
            return;
        }
        var recursive = subscription.Args?["recursive"]?.GetValue<bool>() ?? false;
        var last = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        var swept = Environment.TickCount64;
        var watcher = new FileSystemWatcher(decision.RealPath!) { IncludeSubdirectories = recursive, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
        void Report(string type, string changed)
        {
            var now = Environment.TickCount64;
            if (last.TryGetValue(changed, out var before) && now - before < 200 && type == "changed") return;
            last[changed] = now;
            // Only the last 200 ms matter: forget older paths now and then, or a long watch keeps every path it saw.
            if (now - swept > 10_000)
            {
                swept = now;
                foreach (var (path, at) in last)
                {
                    if (now - at >= 200) last.TryRemove(new KeyValuePair<string, long>(path, at));
                }
            }
            var current = GrantsOf(plugin);
            if (!_policy.Check(current.Set, changed, FileAccessKind.Read, current.Context).Allowed) return;
            plugin.EmitTo(subscription.Key, new JsonObject { ["type"] = type, ["path"] = changed });
        }
        watcher.Created += (_, e) => Report("created", e.FullPath);
        watcher.Changed += (_, e) => Report("changed", e.FullPath);
        watcher.Deleted += (_, e) => Report("deleted", e.FullPath);
        watcher.Renamed += (_, e) =>
        {
            Report("deleted", e.OldFullPath);
            Report("created", e.FullPath);
        };
        watcher.EnableRaisingEvents = true;
        if (_watchers.TryRemove((plugin.Id, subscription.Key), out var old)) old.Dispose();
        _watchers[(plugin.Id, subscription.Key)] = watcher;
    }

    public void Unsubscribed(PluginHost plugin, PluginSubscription subscription)
    {
        if (_watchers.TryRemove((plugin.Id, subscription.Key), out var watcher)) watcher.Dispose();
    }

    // ---------------------------------------------------------------- policy

    private sealed record CachedGrants(PluginGrants Source, string? Project, int Accounts, PluginGrantSet Set, PolicyContext Context);

    /// <summary>The plugin's scopes now; rebuilt when its grants, the current project or the known accounts change.</summary>
    private CachedGrants GrantsOf(PluginHost plugin)
    {
        var grants = plugin.Grants;
        var project = _projects.Current?.Path;
        // A version, not the accounts themselves: this runs for every path a call touches (each file of a text
        // search, each watch event), and listing every agent's accounts each time would cost more than the check.
        var accounts = _hub?.AccountsVersion ?? 0;
        if (_grants.TryGetValue(plugin.Id, out var cached) && ReferenceEquals(cached.Source, grants) && cached.Project == project && cached.Accounts == accounts)
        {
            return cached;
        }
        var scopes = new List<FileScope>();
        var permissions = grants.Manifest.Permissions;
        if (permissions.FsProject is { } fsProject && grants.Has("fs.project") && project is not null)
        {
            var access = grants.Level("fs.project") == "readwrite" ? fsProject.Access : FileAccessKind.Read;
            scopes.Add(new FileScope(FileScopeKind.Project, project, access) { Include = fsProject.Include, Exclude = fsProject.Exclude });
        }
        if (grants.Has("fs.external"))
        {
            foreach (var external in permissions.FsExternal)
            {
                try
                {
                    scopes.Add(FileScope.FromPattern(FileScopeKind.External, external.Path, external.Access, _home));
                }
                catch (ArgumentException e)
                {
                    plugin.Write(PluginLogLevel.Warn, $"fs.external \"{external.Path}\": {e.Message}");
                }
            }
        }
        foreach (var request in permissions.FsAgentData)
        {
            if (!grants.Has($"fs.agentData.{request.Agent}")) continue;
            foreach (var (directory, key) in AgentDirectories(request.Agent))
            {
                scopes.Add(new FileScope(FileScopeKind.AgentData, directory, request.Access) { Include = request.Include, Agent = request.Agent, Account = key });
            }
        }
        foreach (var picked in grants.Record?.PathGrants ?? [])
        {
            if (picked.Scope == "picked") scopes.Add(new FileScope(FileScopeKind.Picked, picked.Path, picked.Access));
        }
        var set = PluginGrantSet.ForPlugin(_paths, plugin.Id, plugin.Package.Directory, scopes, grants.Record?.IncludeSensitiveFiles ?? false);
        var others = _catalog.Packages.Where(p => p.Id != plugin.Id)
            .SelectMany(p => new[] { p.Directory, _paths.DataDirectoryOf(p.Id) })
            .Append(_paths.StagingDirectory);
        var credentials = _agents.Definitions
            .Where(d => d.Signals?.CredentialFiles is { Count: > 0 })
            .SelectMany(d => AgentDirectories(d.Id).Select(a => new AgentCredentialLocation(a.Directory, d.Signals!.CredentialFiles)));
        var context = PolicyContext.For(_app, others, credentials);
        var fresh = new CachedGrants(grants, project, accounts, set, context);
        _grants[plugin.Id] = fresh;
        return fresh;
    }

    /// <summary>The agent's default account directory and every other account seen in its signals.</summary>
    private IEnumerable<(string Directory, string? Key)> AgentDirectories(string agentId)
    {
        if (_agents.Find(agentId) is not { } definition) yield break;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (definition.Signals?.Account is not null)
        {
            var directory = SignalAccounts.ResolveDirectory(definition, null, _home);
            if (seen.Add(directory)) yield return (directory, SignalAccounts.KeyOfDirectory(directory));
        }
        foreach (var account in _hub?.Accounts(agentId) ?? [])
        {
            if (seen.Add(account.Directory)) yield return (account.Directory, account.Key);
        }
    }

    private AccessDecision Decide(PluginCall call, string path, FileAccessKind access, bool? isDirectory = null)
    {
        var grants = GrantsOf(call.Plugin);
        return _policy.Check(grants.Set, path, access, grants.Context, isDirectory);
    }

    private AuditLog AuditOf(PluginHost plugin) => _audit.GetOrAdd(plugin.Id, id => new AuditLog(_paths.DataDirectoryOf(id)));

    /// <summary>Checks a read of the path in argument <paramref name="name"/> and returns the real path to use.</summary>
    private string Read(PluginCall call, string name, bool? isDirectory = null) => Checked(call, name, FileAccessKind.Read, call.Method[3..], isDirectory);

    private string Write(PluginCall call, string name, string operation, bool? isDirectory = null) => Checked(call, name, FileAccessKind.ReadWrite, operation, isDirectory);

    private string Checked(PluginCall call, string name, FileAccessKind access, string operation, bool? isDirectory)
    {
        var path = call.RequiredString(name);
        var decision = Decide(call, path, access, isDirectory);
        AuditOf(call.Plugin).RecordAccess(operation, access, decision, path);
        if (!decision.Allowed)
        {
            var canRequest = decision.Denial == AccessDenial.NotInScope;
            throw new PluginApiException("PermissionDenied", decision.Reason.Length > 0 ? decision.Reason : $"{path} is outside what the plugin may access.")
            {
                Path = path,
                Permission = "fs",
                CanRequest = canRequest,
            };
        }
        return decision.RealPath!;
    }

    /// <summary>A recursive change must not take protected entries with it: every entry below is checked first.</summary>
    private void CheckTree(PluginCall call, string root, FileAccessKind access)
    {
        if (!Directory.Exists(root)) return;
        var checkedCount = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false }))
        {
            if (++checkedCount > 200_000) throw new PluginApiException("TooLarge", $"{root} has too many entries to change at once.") { Path = root };
            var decision = Decide(call, entry, access);
            if (!decision.Allowed)
            {
                throw new PluginApiException("PermissionDenied", $"{entry} cannot be changed by this plugin, so {root} is left as it is.") { Path = entry, Permission = "fs", CanRequest = false };
            }
        }
    }

    // ---------------------------------------------------------------- helpers

    private static WriteOptions Options(PluginCall call) => new() { Create = call.Bool("create", true), Overwrite = call.Bool("overwrite", true) };

    private static JsonObject Stat(FileStat stat) => new()
    {
        ["path"] = stat.Path,
        ["name"] = stat.Name,
        ["type"] = stat.IsDirectory ? "directory" : "file",
        ["size"] = stat.Size,
        ["mtime"] = stat.Modified.ToUnixTimeMilliseconds(),
        ["hidden"] = stat.IsHidden,
        ["symlink"] = stat.IsSymbolicLink,
    };

    /// <summary>
    /// Runs on the thread pool (file work never holds up the plugin's thread or the router). File errors become the
    /// API's codes; anything else is the host's bug and propagates.
    /// </summary>
    private static Task<PluginReply> Run(PluginCall call, Func<PluginReply> action) => Task.Run(() => RunCore(action));

    private static PluginReply RunCore(Func<PluginReply> action)
    {
        try
        {
            return action();
        }
        catch (FileAccessException e)
        {
            throw new PluginApiException(e.Code == FileErrorCode.Io ? "Io" : e.Code.ToString(), e.Message) { Path = e.Path };
        }
        catch (FormatException e)
        {
            throw PluginApiException.Invalid(e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new PluginApiException("Io", e.Message);
        }
    }
}
