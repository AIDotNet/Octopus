using Octopus.Core.Plugins;
using Octopus.Plugins.Security;

namespace Octopus.Plugins.Tests.Security;

/// <summary>
/// A small machine on disk for the access policy: an Octopus data and cache directory with this plugin's and another
/// plugin's files, a fake home with agent data and secrets, a project with links that try to escape, and a folder
/// outside everything. Deleted with the test.
/// </summary>
public sealed class SecurityTestTree : IDisposable
{
    public const string PluginId = "acme.sample";

    public SecurityTestTree()
    {
        // The temp directory itself may sit behind a link (/var → /private/var on macOS): resolve once so expected
        // real paths compare as they should.
        Root = PolicyContext.Resolve(App.Root);
        Paths = new PluginPaths(App);
        Home = Dir("home");
        Project = Dir("project");
        Outside = Dir("outside");

        File("project/src/a.md");
        File("project/src/b.cs");
        File("project/docs/guide.txt");
        File("project/.env", "SECRET=1");
        File("project/config/.env.local");
        File("project/keys/server.pem");
        File("project/.git/config");
        File("project/node_modules/lib/readme.md");
        File("project/build/out/notes.md");
        File("project/auth.json");
        File("home/.ssh/id_rsa");
        File("home/.ssh/known_hosts");
        File("home/.claude/.credentials.json");
        File("home/.claude/settings.json");
        File("home/.claude/projects/p1/session.jsonl");
        File("home/.codex/auth.json");
        File("home/.codex/sessions/rollout-1.jsonl");
        File("home/.custom-agent/token.json");
        File("outside/notes.txt");
        File("outside/secret.txt");
        File("data/settings.json");
        File("data/plugins.json");
        File($"data/plugins/{PluginId}/main.js");
        File("data/plugins/other.plugin/main.js");
        File($"data/plugin-data/{PluginId}/files/state.txt");
        File($"data/plugin-data/{PluginId}/audit.log");
        File("data/plugin-data/other.plugin/files/secret.txt");
        File("cache/plugin-tmp/other.plugin/x.txt");
        File("dev/other.dev/main.js");
        Dir($"cache/plugin-tmp/{PluginId}");

        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(Path.Combine(Project, "ssh-link"), Path.Combine(Home, ".ssh"));
            Directory.CreateSymbolicLink(Path.Combine(Project, "outside-link"), Outside);
            System.IO.File.CreateSymbolicLink(Path.Combine(Project, "secret-link.txt"), Path.Combine(Outside, "secret.txt"));
            System.IO.File.CreateSymbolicLink(Path.Combine(Project, "inner-link.md"), Path.Combine(Project, "src", "a.md"));
            System.IO.File.CreateSymbolicLink(Path.Combine(Project, "creds-link.json"), Path.Combine(Home, ".claude", ".credentials.json"));
        }

        Context = new PolicyContext
        {
            ProtectedDirectories = [App.DataDirectory, App.CacheDirectory],
            OtherPluginDirectories = [Path.Combine(Root, "dev", "other.dev")],
            AgentCredentials =
            [
                new AgentCredentialLocation(Path.Combine(Home, ".claude"), [".credentials.json"]),
                new AgentCredentialLocation(Path.Combine(Home, ".custom-agent"), ["token.json"]),
            ],
        };
    }

    public TempAppPaths App { get; } = new();
    public PluginPaths Paths { get; }
    public string Root { get; }
    public string Home { get; }
    public string Project { get; }
    public string Outside { get; }
    public PolicyContext Context { get; }

    public PluginAccessPolicy Policy { get; } = new();

    public string P(string relative) => Path.Combine(Root, relative);

    /// <summary>The plugin's always-held scopes plus <paramref name="granted"/>.</summary>
    public PluginGrantSet Grants(bool includeSensitiveFiles, params FileScope[] granted) =>
        PluginGrantSet.ForPlugin(Paths, PluginId, Paths.PackageDirectoryOf(PluginId), granted, includeSensitiveFiles);

    public PluginGrantSet Grants(params FileScope[] granted) => Grants(false, granted);

    public FileScope ProjectScope(FileAccessKind access = FileAccessKind.Read) => new(FileScopeKind.Project, Project, access);

    public AccessDecision Read(PluginGrantSet grants, string path, PolicyContext? context = null) =>
        Policy.Check(grants, path, FileAccessKind.Read, context ?? Context);

    public AccessDecision Write(PluginGrantSet grants, string path, PolicyContext? context = null) =>
        Policy.Check(grants, path, FileAccessKind.ReadWrite, context ?? Context);

    public void Dispose() => App.Dispose();

    private string Dir(string relative) => Directory.CreateDirectory(Path.Combine(Root, relative)).FullName;

    private void File(string relative, string content = "x")
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
    }
}
