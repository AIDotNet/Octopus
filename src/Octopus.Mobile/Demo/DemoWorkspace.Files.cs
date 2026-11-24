using System.Globalization;
using System.Text;
using Octopus.Mobile.Workspace;

namespace Octopus.Mobile.Demo;

// The made-up projects' files and uncommitted changes: the same small tree in every project folder and branch
// workspace, and a few changes in Octopus only (the other projects have none, which shows the empty page).
public sealed partial class DemoWorkspace
{
    /// <summary>Whether the made-up computers can show files and git; tests turn it off for the "update the computer" path.</summary>
    internal bool FilesSupported { get; set; } = true;

    /// <summary>The changes and diffs asked for, by path, for tests.</summary>
    internal List<string> DiffsRead { get; } = [];

    private const string BigLog = "logs/build.log";
    private const long BigLogLines = 6000;

    // Relative path to text; null for a file that is not text. Folders are what the paths imply.
    private static readonly Dictionary<string, string?> s_files = new(StringComparer.Ordinal)
    {
        ["README.md"] = "# {0}\n\nThe made-up project of the preview.\n\n## Build\n\n```\ndotnet build\ndotnet test\n```\n",
        [".gitignore"] = "bin/\nobj/\n*.user\n",
        ["src/Startup.cs"] = """
            namespace {0};

            public static class Startup
            {
                public static void Configure(IServiceCollection services)
                {
                    services.AddAuth();
                    services.AddSingleton<TokenIssuer>();
                    services.AddSingleton<TokenValidator>();
                    services.AddSingleton<TokenService>();
                }
            }
            """,
        ["src/Auth/TokenIssuer.cs"] = """
            namespace {0}.Auth;

            public sealed class TokenIssuer(
                IClock clock, SigningKeys keys)
            {
                public string Issue(User user)
                {
                    var now = clock.UtcNow;
                    var claims = new[]
                    {
                        new Claim("sub", user.Id),
                        new Claim("name", user.Name),
                    };
                    return keys.Sign(claims,
                        now, now.AddHours(8));
                }
            }
            """,
        ["src/Auth/TokenService.cs"] = """
            namespace {0}.Auth;

            /// <summary>Hands out and checks tokens; the two halves live in TokenIssuer and TokenValidator now.</summary>
            public sealed class TokenService(TokenIssuer issuer, TokenValidator validator)
            {
                public string Issue(User user) => issuer.Issue(user);

                public bool Check(string token) => validator.Validate(token);
            }
            """,
        ["src/Auth/TokenValidator.cs"] = """
            namespace {0}.Auth;

            public sealed class TokenValidator(SigningKeys keys)
            {
                public bool Validate(string token) => keys.Verify(token);
            }
            """,
        ["docs/design.md"] = "# Design\n\n- Tokens are signed with the current key.\n- Old keys stay valid for a day.\n",
        ["tests/TokenTests.cs"] = "namespace {0}.Tests;\n\npublic class TokenTests\n{\n    [Fact]\n    public void Issued_tokens_validate() { }\n}\n",
        ["assets/logo.png"] = null,
        [BigLog] = "",
    };

    // Unified diffs of the changes of Octopus, each against what HEAD has.
    private static readonly (string Path, RemoteChangeKind Kind, bool Staged, string Diff)[] s_changes =
    [
        ("src/Auth/TokenService.cs", RemoteChangeKind.Modified, false, """
            @@ -1,14 +1,9 @@
             namespace {0}.Auth;

            -/// <summary>Hands out and checks tokens.</summary>
            -public sealed class TokenService(IClock clock, SigningKeys keys)
            +/// <summary>Hands out and checks tokens; the two halves live in TokenIssuer and TokenValidator now.</summary>
            +public sealed class TokenService(TokenIssuer issuer, TokenValidator validator)
             {
            -    public string Issue(User user)
            -    {
            -        var now = clock.UtcNow;
            -        return keys.Sign([new Claim("sub", user.Id)], now, now.AddHours(8));
            -    }
            +    public string Issue(User user) => issuer.Issue(user);

            -    public bool Check(string token) => keys.Verify(token);
            +    public bool Check(string token) => validator.Validate(token);
             }
            """),
        ("src/Auth/TokenIssuer.cs", RemoteChangeKind.Untracked, false, ""),
        ("src/Auth/TokenValidator.cs", RemoteChangeKind.Added, true, ""),
        ("src/Startup.cs", RemoteChangeKind.Modified, false, """
            @@ -5,7 +5,9 @@ public static class Startup
                 public static void Configure(IServiceCollection services)
                 {
                     services.AddAuth();
            +        services.AddSingleton<TokenIssuer>();
            +        services.AddSingleton<TokenValidator>();
                     services.AddSingleton<TokenService>();
                 }
             }
            \ No newline at end of file
            """),
        ("docs/old-notes.md", RemoteChangeKind.Deleted, false, """
            @@ -1,3 +0,0 @@
            -# Old notes
            -
            -Tokens used to be checked by hand.
            """),
        ("assets/banner.png", RemoteChangeKind.Modified, false, "binary"),
    ];

    public bool SupportsFiles(RemoteHost host) => FilesSupported;

    public bool SupportsGit(RemoteHost host) => FilesSupported;

    public Task<IReadOnlyList<RemoteFileEntry>> ListFilesAsync(RemoteProject project, string folder, CancellationToken cancellationToken = default)
    {
        Supported();
        var relative = RelativeOf(project, folder);
        var prefix = relative.Length == 0 ? "" : relative + "/";
        var folders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in s_files.Keys.Where(path => path.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var rest = path[prefix.Length..];
            if (rest.IndexOf('/') is var slash and >= 0) folders.Add(rest[..slash]);
            else files.Add(rest);
        }
        if (folders.Count == 0 && files.Count == 0 && relative.Length > 0) throw new IOException($"“{folder}”不存在。");
        var entries = folders.Select(name => new RemoteFileEntry(name, Join(folder, name), true, name.StartsWith('.')))
            .Concat(files.Select(name => new RemoteFileEntry(name, Join(folder, name), false, name.StartsWith('.'))))
            .ToList();
        return Task.FromResult<IReadOnlyList<RemoteFileEntry>>(entries);
    }

    public Task<RemoteFileContent> ReadTextFileAsync(RemoteProject project, string path, long maxBytes, CancellationToken cancellationToken = default)
    {
        Supported();
        var relative = RelativeOf(project, path);
        if (!s_files.TryGetValue(relative, out var text)) throw new IOException($"“{path}”不存在。");
        if (text is null) return Task.FromResult(new RemoteFileContent(RemoteFileContentKind.Binary, "", 18_432));
        if (relative == BigLog) text = Log();
        var content = Fill(text, project);
        var size = Encoding.UTF8.GetByteCount(content);
        return Task.FromResult(size > maxBytes
            ? new RemoteFileContent(RemoteFileContentKind.TooLarge, "", size)
            : new RemoteFileContent(RemoteFileContentKind.Text, content, size));
    }

    public Task<RemoteChanges> GetChangesAsync(RemoteProject project, string? workspacePath, CancellationToken cancellationToken = default)
    {
        Supported();
        var root = workspacePath ?? project.Path;
        var branch = workspacePath is null
            ? project.DefaultBranch
            : project.Workspaces.FirstOrDefault(workspace => workspace.Path == workspacePath)?.Branch;
        if (project.Name != "Octopus") return Task.FromResult(new RemoteChanges(true, branch, []));
        var changes = s_changes.Select(change =>
        {
            var diff = DiffOf(project, change.Path);
            return new RemoteChange(change.Path, Join(root, change.Path), change.Kind, change.Staged,
                Added: diff.Kind == RemoteDiffKind.Text ? diff.Added : null,
                Removed: diff.Kind == RemoteDiffKind.Text ? diff.Removed : null);
        }).ToList();
        return Task.FromResult(new RemoteChanges(true, branch, changes));
    }

    public Task<RemoteDiff> GetDiffAsync(RemoteProject project, string? workspacePath, RemoteChange change, CancellationToken cancellationToken = default)
    {
        Supported();
        DiffsRead.Add(change.Path);
        return Task.FromResult(DiffOf(project, change.Path));
    }

    private void Supported()
    {
        if (!FilesSupported) throw new IOException("请更新电脑上的 Octopus。");
    }

    private static RemoteDiff DiffOf(RemoteProject project, string path)
    {
        var change = s_changes.FirstOrDefault(known => known.Path == path);
        if (change.Path is null) return RemoteDiff.Of(RemoteDiffKind.Empty);
        if (change.Diff == "binary") return RemoteDiff.Of(RemoteDiffKind.Binary);
        // A new file: every line of it added.
        var unified = change.Diff.Length > 0
            ? change.Diff
            : AllAdded(Fill(s_files.GetValueOrDefault(path) ?? "", project));
        return Parse(Fill(unified, project));
    }

    // {0} in the texts above is the project's name (not string.Format: the code has braces of its own).
    private static string Fill(string text, RemoteProject project) => text.Replace("{0}", project.Name, StringComparison.Ordinal);

    private static string AllAdded(string text)
    {
        var lines = text.Split('\n');
        return $"@@ -0,0 +1,{lines.Length} @@\n" + string.Join('\n', lines.Select(line => "+" + line));
    }

    // Enough of a unified diff reader for the texts above.
    private static RemoteDiff Parse(string unified)
    {
        var lines = new List<RemoteDiffLine>();
        int oldNumber = 0, newNumber = 0, added = 0, removed = 0;
        foreach (var line in unified.Split('\n'))
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                var parts = line.Split(' ');
                oldNumber = int.Parse(parts[1].TrimStart('-').Split(',')[0], CultureInfo.InvariantCulture);
                newNumber = int.Parse(parts[2].TrimStart('+').Split(',')[0], CultureInfo.InvariantCulture);
                lines.Add(new RemoteDiffLine(RemoteDiffLineKind.Hunk, null, null, line));
            }
            else if (line.StartsWith('+'))
            {
                added++;
                lines.Add(new RemoteDiffLine(RemoteDiffLineKind.Added, null, newNumber++, line[1..]));
            }
            else if (line.StartsWith('-'))
            {
                removed++;
                lines.Add(new RemoteDiffLine(RemoteDiffLineKind.Removed, oldNumber++, null, line[1..]));
            }
            else if (line.StartsWith('\\'))
            {
                lines.Add(new RemoteDiffLine(RemoteDiffLineKind.Note, null, null, line));
            }
            else
            {
                lines.Add(new RemoteDiffLine(RemoteDiffLineKind.Context, oldNumber++, newNumber++, line.Length > 0 ? line[1..] : ""));
            }
        }
        return new RemoteDiff(RemoteDiffKind.Text, lines, lines.Count, added, removed);
    }

    // A long build log: more lines than the viewer shows.
    private static string Log()
    {
        var log = new StringBuilder();
        for (var i = 1; i <= BigLogLines; i++) log.Append("[build] step ").Append(i.ToString(CultureInfo.InvariantCulture)).Append(" ok\n");
        return log.ToString();
    }

    // Where a path lies in the made-up tree: under the project's folder or one of its branch workspaces.
    private static string RelativeOf(RemoteProject project, string path)
    {
        var trimmed = path.TrimEnd('/');
        foreach (var root in project.Workspaces.Select(workspace => workspace.Path).Prepend(project.Path))
        {
            if (trimmed == root) return "";
            if (trimmed.StartsWith(root + "/", StringComparison.Ordinal)) return trimmed[(root.Length + 1)..];
        }
        throw new IOException("只能查看项目文件夹里的文件。");
    }

    private static string Join(string folder, string name) => folder.TrimEnd('/') + "/" + name;
}
