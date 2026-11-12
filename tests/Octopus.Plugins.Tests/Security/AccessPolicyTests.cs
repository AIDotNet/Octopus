using Octopus.Core.Plugins;
using Octopus.Plugins.Security;
using Xunit;

namespace Octopus.Plugins.Tests.Security;

public sealed class AccessPolicyTests : IDisposable
{
    private readonly SecurityTestTree _t = new();

    public void Dispose() => _t.Dispose();

    private static void Denied(AccessDenial expected, AccessDecision decision)
    {
        Assert.False(decision.Allowed, $"expected {expected}, but it was allowed via {decision.Scope?.Kind}");
        Assert.Equal(expected, decision.Denial);
        Assert.False(string.IsNullOrEmpty(decision.Reason));
    }

    private static void Allowed(FileScopeKind scope, AccessDecision decision)
    {
        Assert.True(decision.Allowed, $"denied: {decision.Denial} {decision.Reason}");
        Assert.Equal(scope, decision.Scope!.Kind);
    }

    // ---------------------------------------------------------------- the plugin's own directories

    [Fact]
    public void Own_data_and_tmp_are_read_write_and_the_package_is_read_only()
    {
        var grants = _t.Grants();
        var data = Path.Combine(_t.Paths.DataDirectoryOf(SecurityTestTree.PluginId), "files", "state.txt");
        Allowed(FileScopeKind.Data, _t.Read(grants, data));
        Allowed(FileScopeKind.Data, _t.Write(grants, data));
        Allowed(FileScopeKind.Tmp, _t.Write(grants, Path.Combine(_t.Paths.TempDirectoryOf(SecurityTestTree.PluginId), "new.txt")));
        var package = Path.Combine(_t.Paths.PackageDirectoryOf(SecurityTestTree.PluginId), "main.js");
        Allowed(FileScopeKind.Plugin, _t.Read(grants, package));
        Denied(AccessDenial.ReadOnlyScope, _t.Write(grants, package));
    }

    [Fact]
    public void Even_a_read_write_grant_on_the_package_stays_read_only()
    {
        var grants = new PluginGrantSet([new FileScope(FileScopeKind.Plugin, _t.Paths.PackageDirectoryOf(SecurityTestTree.PluginId), FileAccessKind.ReadWrite)]);
        Denied(AccessDenial.ReadOnlyScope, _t.Write(grants, Path.Combine(_t.Paths.PackageDirectoryOf(SecurityTestTree.PluginId), "main.js")));
    }

    [Fact]
    public void Plugin_data_outside_files_is_out_of_reach()
    {
        var grants = _t.Grants();
        var own = _t.Paths.DataDirectoryOf(SecurityTestTree.PluginId);
        Denied(AccessDenial.NotInScope, _t.Write(grants, Path.Combine(own, "audit.log")));
        Denied(AccessDenial.NotInScope, _t.Read(grants, Path.Combine(own, "files", "..", "audit.log")));
    }

    // ---------------------------------------------------------------- Octopus and other plugins

    [Fact]
    public void Octopus_data_is_protected_even_inside_a_granted_scope()
    {
        // A project that happens to contain Octopus's data directory (someone opened their whole disk as a project).
        var grants = _t.Grants(new FileScope(FileScopeKind.Project, _t.Root, FileAccessKind.ReadWrite));
        Denied(AccessDenial.Protected, _t.Read(grants, _t.P("data/settings.json")));
        Denied(AccessDenial.Protected, _t.Write(grants, _t.P("data/plugins.json")));
        Denied(AccessDenial.Protected, _t.Read(grants, _t.P($"data/plugin-data/{SecurityTestTree.PluginId}/audit.log")));
        Denied(AccessDenial.Protected, _t.Read(grants, _t.P("data/plugins/other.plugin/main.js")));
        Denied(AccessDenial.Protected, _t.Read(grants, _t.P("data/plugin-data/other.plugin/files/secret.txt")));
        Denied(AccessDenial.Protected, _t.Read(grants, _t.P("cache/plugin-tmp/other.plugin/x.txt")));
        // Its own directories, inside the same protected area, still work through its own scopes.
        Allowed(FileScopeKind.Data, _t.Read(grants, _t.P($"data/plugin-data/{SecurityTestTree.PluginId}/files/state.txt")));
        Allowed(FileScopeKind.Plugin, _t.Read(grants, _t.P($"data/plugins/{SecurityTestTree.PluginId}/main.js")));
    }

    [Fact]
    public void Other_plugin_directories_elsewhere_are_protected()
    {
        var grants = _t.Grants(new FileScope(FileScopeKind.External, _t.P("dev"), FileAccessKind.ReadWrite));
        Denied(AccessDenial.Protected, _t.Read(grants, _t.P("dev/other.dev/main.js")));
        Denied(AccessDenial.Protected, _t.Write(grants, _t.P("dev/other.dev/new.js")));
    }

    // ---------------------------------------------------------------- project scope, .., links, case

    [Fact]
    public void Project_scope_follows_its_access()
    {
        var read = _t.Grants(_t.ProjectScope());
        Allowed(FileScopeKind.Project, _t.Read(read, Path.Combine(_t.Project, "src", "a.md")));
        Denied(AccessDenial.ReadOnlyScope, _t.Write(read, Path.Combine(_t.Project, "src", "a.md")));
        var write = _t.Grants(_t.ProjectScope(FileAccessKind.ReadWrite));
        Allowed(FileScopeKind.Project, _t.Write(write, Path.Combine(_t.Project, "src", "a.md")));
        // A file that does not exist yet: checked through its deepest existing ancestor.
        var created = _t.Write(write, Path.Combine(_t.Project, "src", "new", "deeper", "c.md"));
        Allowed(FileScopeKind.Project, created);
        Assert.Equal(Path.Combine(_t.Project, "src", "new", "deeper", "c.md"), created.RealPath);
        Allowed(FileScopeKind.Project, _t.Read(write, _t.Project)); // the root itself
    }

    [Fact]
    public void Dot_dot_cannot_climb_out_of_a_project()
    {
        var grants = _t.Grants(_t.ProjectScope(FileAccessKind.ReadWrite));
        var decision = _t.Read(grants, Path.Combine(_t.Project, "src", "..", "..", "outside", "notes.txt"));
        Denied(AccessDenial.NotInScope, decision);
        Assert.Equal(Path.Combine(_t.Outside, "notes.txt"), decision.RealPath);
        Allowed(FileScopeKind.Project, _t.Read(grants, Path.Combine(_t.Project, "docs", "..", "src", ".", "a.md")));
        Allowed(FileScopeKind.Project, _t.Read(grants, _t.Project + "//src///a.md"));
    }

    [Fact]
    public void A_link_inside_a_project_does_not_extend_the_scope()
    {
        if (OperatingSystem.IsWindows()) return;
        var grants = _t.Grants(true, _t.ProjectScope(FileAccessKind.ReadWrite));
        var key = _t.Read(grants, Path.Combine(_t.Project, "ssh-link", "id_rsa"));
        Denied(AccessDenial.NotInScope, key);
        Assert.Equal(Path.Combine(_t.Home, ".ssh", "id_rsa"), key.RealPath);
        Denied(AccessDenial.NotInScope, _t.Read(grants, Path.Combine(_t.Project, "secret-link.txt")));
        Denied(AccessDenial.NotInScope, _t.Read(grants, Path.Combine(_t.Project, "outside-link")));
        // Creating a file through a link into somewhere else is refused by where it would land.
        var created = _t.Write(grants, Path.Combine(_t.Project, "outside-link", "new", "planted.txt"));
        Denied(AccessDenial.NotInScope, created);
        Assert.Equal(Path.Combine(_t.Outside, "new", "planted.txt"), created.RealPath);
    }

    [Fact]
    public void A_link_that_stays_inside_the_project_is_fine()
    {
        if (OperatingSystem.IsWindows()) return;
        var grants = _t.Grants(_t.ProjectScope());
        var decision = _t.Read(grants, Path.Combine(_t.Project, "inner-link.md"));
        Allowed(FileScopeKind.Project, decision);
        Assert.Equal(Path.Combine(_t.Project, "src", "a.md"), decision.RealPath);
    }

    [Fact]
    public void A_project_that_is_reached_through_a_link_still_matches()
    {
        if (OperatingSystem.IsWindows()) return;
        var alias = _t.P("project-alias");
        Directory.CreateSymbolicLink(alias, _t.Project);
        var grants = _t.Grants(new FileScope(FileScopeKind.Project, alias, FileAccessKind.Read));
        Allowed(FileScopeKind.Project, _t.Read(grants, Path.Combine(_t.Project, "src", "a.md")));
        Allowed(FileScopeKind.Project, _t.Read(grants, Path.Combine(alias, "src", "a.md")));
    }

    [Fact]
    public void Case_rules_follow_the_platform_setting()
    {
        var grants = _t.Grants(_t.ProjectScope(FileAccessKind.ReadWrite));
        var upper = Path.Combine(_t.Project.ToUpperInvariant(), "SRC", "A.MD");
        var insensitive = new PolicyContext { ProtectedDirectories = _t.Context.ProtectedDirectories, IgnoreCase = true };
        var sensitive = new PolicyContext { ProtectedDirectories = _t.Context.ProtectedDirectories, IgnoreCase = false };
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows())
        {
            // The same file under another spelling (the disk ignores case): inside the project.
            Allowed(FileScopeKind.Project, _t.Read(grants, upper, insensitive));
            // .GIT is .git here.
            Denied(AccessDenial.Protected, _t.Read(grants, Path.Combine(_t.Project, ".GIT", "config"), insensitive));
        }
        // On a case-sensitive file system /PROJECT is a different directory: outside the project.
        Denied(AccessDenial.NotInScope, _t.Read(grants, upper, sensitive));
        Allowed(FileScopeKind.Project, _t.Read(grants, Path.Combine(_t.Project, "src", "a.md"), sensitive));
    }

    // ---------------------------------------------------------------- .git

    [Fact]
    public void Git_directory_is_not_part_of_the_project()
    {
        var grants = _t.Grants(_t.ProjectScope(FileAccessKind.ReadWrite));
        Denied(AccessDenial.Protected, _t.Read(grants, Path.Combine(_t.Project, ".git", "config")));
        Denied(AccessDenial.Protected, _t.Write(grants, Path.Combine(_t.Project, ".git", "hooks", "pre-commit")));
        Denied(AccessDenial.Protected, _t.Read(grants, Path.Combine(_t.Project, ".git")));
        // Elsewhere a .git can be read, but never written.
        var external = _t.Grants(new FileScope(FileScopeKind.External, _t.Project, FileAccessKind.ReadWrite));
        Allowed(FileScopeKind.External, _t.Read(external, Path.Combine(_t.Project, ".git", "config")));
        Denied(AccessDenial.Protected, _t.Write(external, Path.Combine(_t.Project, ".git", "config")));
    }

    // ---------------------------------------------------------------- sensitive files

    [Theory]
    [InlineData(".env")]
    [InlineData("config/.env.local")]
    [InlineData("keys/server.pem")]
    [InlineData("keys/deploy.KEY")]
    [InlineData("id_rsa.pub")]
    [InlineData("id_ed25519")]
    [InlineData(".npmrc")]
    [InlineData("deploy/credentials")]
    [InlineData("login.keychain-db")]
    [InlineData(".aws/config")]
    [InlineData("sub/.kube/config")]
    public void Sensitive_files_need_the_switch(string relative)
    {
        var path = Path.Combine(_t.Project, relative);
        Denied(AccessDenial.Sensitive, _t.Read(_t.Grants(_t.ProjectScope(FileAccessKind.ReadWrite)), path));
        Denied(AccessDenial.Sensitive, _t.Write(_t.Grants(_t.ProjectScope(FileAccessKind.ReadWrite)), path));
        Allowed(FileScopeKind.Project, _t.Read(_t.Grants(true, _t.ProjectScope(FileAccessKind.ReadWrite)), path));
    }

    [Theory]
    [InlineData("src/a.md")]
    [InlineData("environment.txt")]
    [InlineData("keyboard.json")]
    [InlineData("docs/credentials.md")]
    public void Ordinary_files_are_not_sensitive(string relative)
    {
        Allowed(FileScopeKind.Project, _t.Read(_t.Grants(_t.ProjectScope()), Path.Combine(_t.Project, relative)));
    }

    [Fact]
    public void Sensitive_files_the_person_chose_are_allowed()
    {
        var env = Path.Combine(_t.Project, ".env");
        Allowed(FileScopeKind.Document, _t.Read(_t.Grants(new FileScope(FileScopeKind.Document, env, FileAccessKind.ReadWrite)), env));
        Allowed(FileScopeKind.Picked, _t.Read(_t.Grants(new FileScope(FileScopeKind.Picked, env, FileAccessKind.Read)), env));
        // A picked folder of keys: the person pointed at it.
        var ssh = Path.Combine(_t.Home, ".ssh");
        Allowed(FileScopeKind.Picked, _t.Read(_t.Grants(new FileScope(FileScopeKind.Picked, ssh, FileAccessKind.Read)), Path.Combine(ssh, "known_hosts")));
        // But an external grant over the same folder still needs the switch.
        Denied(AccessDenial.Sensitive, _t.Read(_t.Grants(new FileScope(FileScopeKind.External, ssh, FileAccessKind.Read)), Path.Combine(ssh, "known_hosts")));
    }

    // ---------------------------------------------------------------- credentials

    [Fact]
    public void Credentials_are_never_readable()
    {
        var claude = Path.Combine(_t.Home, ".claude");
        var credentials = Path.Combine(claude, ".credentials.json");
        foreach (var scope in new[]
        {
            new FileScope(FileScopeKind.External, claude, FileAccessKind.ReadWrite),
            new FileScope(FileScopeKind.Picked, credentials, FileAccessKind.Read),
            new FileScope(FileScopeKind.Document, credentials, FileAccessKind.Read),
            new FileScope(FileScopeKind.AgentData, claude, FileAccessKind.Read) { Agent = "claude" },
        })
        {
            Denied(AccessDenial.Credential, _t.Read(_t.Grants(true, scope), credentials));
            Denied(AccessDenial.Credential, _t.Write(_t.Grants(true, scope), credentials));
        }
    }

    [Fact]
    public void Agent_data_scope_reads_records_but_not_logins()
    {
        var claude = Path.Combine(_t.Home, ".claude");
        var codex = Path.Combine(_t.Home, ".codex");
        var grants = _t.Grants(true,
            new FileScope(FileScopeKind.AgentData, claude, FileAccessKind.Read) { Agent = "claude", Include = ["projects/**/*.jsonl"] },
            new FileScope(FileScopeKind.AgentData, codex, FileAccessKind.Read) { Agent = "codex", Include = ["sessions/**/*.jsonl", "*.json"] });
        Allowed(FileScopeKind.AgentData, _t.Read(grants, Path.Combine(claude, "projects", "p1", "session.jsonl")));
        Allowed(FileScopeKind.AgentData, _t.Read(grants, Path.Combine(claude, "projects"))); // a directory on the way
        Denied(AccessDenial.NotInScope, _t.Read(grants, Path.Combine(claude, "settings.json")));
        // auth.json directly in an agent directory is a login, even when the include globs would let it through.
        Denied(AccessDenial.Credential, _t.Read(grants, Path.Combine(codex, "auth.json")));
        Denied(AccessDenial.ReadOnlyScope, _t.Write(grants, Path.Combine(claude, "projects", "p1", "session.jsonl")));
    }

    [Fact]
    public void Credential_names_are_refused_anywhere_and_through_links()
    {
        var grants = _t.Grants(true, _t.ProjectScope(FileAccessKind.ReadWrite));
        Denied(AccessDenial.Credential, _t.Read(grants, Path.Combine(_t.Project, "backup", ".credentials.json")));
        Denied(AccessDenial.Credential, _t.Write(grants, Path.Combine(_t.Project, ".Credentials.JSON")));
        Denied(AccessDenial.Credential, _t.Read(grants, Path.Combine(_t.Project, "oauth_creds.json")));
        // auth.json is only a login inside an agent directory; in a project it is an ordinary file.
        Allowed(FileScopeKind.Project, _t.Read(grants, Path.Combine(_t.Project, "auth.json")));
        if (!OperatingSystem.IsWindows())
        {
            Denied(AccessDenial.Credential, _t.Read(grants, Path.Combine(_t.Project, "creds-link.json")));
        }
    }

    [Fact]
    public void Credential_files_listed_by_an_agent_definition_are_refused()
    {
        var custom = Path.Combine(_t.Home, ".custom-agent");
        var grants = _t.Grants(true, new FileScope(FileScopeKind.External, custom, FileAccessKind.Read));
        Denied(AccessDenial.Credential, _t.Read(grants, Path.Combine(custom, "token.json")));
        Denied(AccessDenial.Credential, _t.Read(grants, Path.Combine(custom, "auth.json")));
    }

    [Theory]
    [InlineData(".codex/auth.json")]
    [InlineData(".grok/auth.json")]
    [InlineData(".local/share/opencode/auth.json")]
    [InlineData(".config/github-copilot/hosts.json")]
    public void Known_agent_logins_are_refused_by_their_default_place(string relative)
    {
        var grants = _t.Grants(true, new FileScope(FileScopeKind.External, _t.Home, FileAccessKind.Read));
        Denied(AccessDenial.Credential, _t.Read(grants, Path.Combine(_t.Home, relative)));
    }

    // ---------------------------------------------------------------- system locations

    [Fact]
    public void System_locations_are_never_writable()
    {
        if (OperatingSystem.IsWindows()) return;
        var grants = _t.Grants(
            new FileScope(FileScopeKind.External, "/usr", FileAccessKind.ReadWrite),
            new FileScope(FileScopeKind.External, "/etc", FileAccessKind.ReadWrite),
            new FileScope(FileScopeKind.External, "/System", FileAccessKind.ReadWrite));
        Denied(AccessDenial.SystemLocation, _t.Write(grants, "/usr/local/bin/new-tool"));
        Denied(AccessDenial.SystemLocation, _t.Write(grants, "/usr/bin/env"));
        Denied(AccessDenial.SystemLocation, _t.Write(grants, "/etc/hosts"));
        Denied(AccessDenial.SystemLocation, _t.Write(grants, "/System/Library/new"));
        Allowed(FileScopeKind.External, _t.Read(grants, "/usr/bin/env"));
    }

    [Fact]
    public void Octopus_itself_is_not_writable()
    {
        var context = PolicyContext.For(_t.App);
        var install = AppContext.BaseDirectory;
        var grants = _t.Grants(new FileScope(FileScopeKind.External, install, FileAccessKind.ReadWrite));
        Denied(AccessDenial.SystemLocation, _t.Write(grants, Path.Combine(install, "Octopus.Plugins.dll"), context));
    }

    // ---------------------------------------------------------------- picked and document

    [Fact]
    public void A_picked_file_outside_the_project_is_allowed_alone()
    {
        var notes = Path.Combine(_t.Outside, "notes.txt");
        var grants = _t.Grants(_t.ProjectScope(), new FileScope(FileScopeKind.Picked, notes, FileAccessKind.ReadWrite));
        Allowed(FileScopeKind.Picked, _t.Read(grants, notes));
        Allowed(FileScopeKind.Picked, _t.Write(grants, notes));
        Denied(AccessDenial.NotInScope, _t.Read(grants, Path.Combine(_t.Outside, "secret.txt")));
        Denied(AccessDenial.NotInScope, _t.Read(grants, _t.Outside));
    }

    [Fact]
    public void A_picked_folder_covers_its_content()
    {
        var grants = _t.Grants(new FileScope(FileScopeKind.Picked, _t.Outside, FileAccessKind.Read));
        Allowed(FileScopeKind.Picked, _t.Read(grants, Path.Combine(_t.Outside, "secret.txt")));
        Allowed(FileScopeKind.Picked, _t.Read(grants, _t.Outside));
        Denied(AccessDenial.ReadOnlyScope, _t.Write(grants, Path.Combine(_t.Outside, "new.txt")));
    }

    [Fact]
    public void A_read_only_document_cannot_be_saved()
    {
        var doc = Path.Combine(_t.Project, "docs", "guide.txt");
        Denied(AccessDenial.ReadOnlyScope, _t.Write(_t.Grants(new FileScope(FileScopeKind.Document, doc, FileAccessKind.Read)), doc));
        Allowed(FileScopeKind.Document, _t.Write(_t.Grants(new FileScope(FileScopeKind.Document, doc, FileAccessKind.ReadWrite)), doc));
    }

    [Fact]
    public void The_least_audited_scope_that_allows_wins()
    {
        var doc = Path.Combine(_t.Project, "docs", "guide.txt");
        var grants = _t.Grants(
            new FileScope(FileScopeKind.Picked, doc, FileAccessKind.ReadWrite),
            _t.ProjectScope());
        Allowed(FileScopeKind.Project, _t.Read(grants, doc));  // reads: the project, not recorded
        Allowed(FileScopeKind.Picked, _t.Write(grants, doc));  // writes: only the picked grant allows them
    }

    // ---------------------------------------------------------------- globs

    [Fact]
    public void Include_and_exclude_globs_narrow_a_scope()
    {
        var grants = _t.Grants(_t.ProjectScope(FileAccessKind.ReadWrite) with
        {
            Include = ["**/*.md", "docs/**"],
            Exclude = ["**/node_modules/**", "build"],
        });
        Allowed(FileScopeKind.Project, _t.Read(grants, Path.Combine(_t.Project, "src", "a.md")));
        Allowed(FileScopeKind.Project, _t.Read(grants, Path.Combine(_t.Project, "docs", "guide.txt")));
        Allowed(FileScopeKind.Project, _t.Write(grants, Path.Combine(_t.Project, "new.md")));
        Denied(AccessDenial.NotInScope, _t.Read(grants, Path.Combine(_t.Project, "src", "b.cs")));
        Denied(AccessDenial.NotInScope, _t.Read(grants, Path.Combine(_t.Project, "node_modules", "lib", "readme.md")));
        // "build" has no slash: it matches the folder anywhere, and everything below it is out with it.
        Denied(AccessDenial.NotInScope, _t.Read(grants, Path.Combine(_t.Project, "build", "out", "notes.md")));
        // Directories on the way to matching files can be listed.
        Allowed(FileScopeKind.Project, _t.Read(grants, Path.Combine(_t.Project, "src")));
        Allowed(FileScopeKind.Project, _t.Read(grants, _t.Project));
        // A directory about to be created counts as a directory when the caller says so.
        Assert.True(_t.Policy.Check(grants, Path.Combine(_t.Project, "notes"), FileAccessKind.ReadWrite, _t.Context, isDirectory: true).Allowed);
        Assert.False(_t.Policy.Check(grants, Path.Combine(_t.Project, "notes"), FileAccessKind.ReadWrite, _t.Context).Allowed);
    }

    [Fact]
    public void Globs_with_a_directory_do_not_open_other_directories()
    {
        var grants = _t.Grants(_t.ProjectScope() with { Include = ["docs/*.txt"] });
        Allowed(FileScopeKind.Project, _t.Read(grants, Path.Combine(_t.Project, "docs", "guide.txt")));
        Denied(AccessDenial.NotInScope, _t.Read(grants, Path.Combine(_t.Project, "src")));
        Denied(AccessDenial.NotInScope, _t.Read(grants, Path.Combine(_t.Project, "src", "a.md")));
    }

    // ---------------------------------------------------------------- malformed paths

    [Theory]
    [InlineData("relative/path.txt")]
    [InlineData("")]
    [InlineData("/tmp/a\0b")]
    public void Malformed_paths_are_refused(string path)
    {
        var decision = _t.Read(_t.Grants(_t.ProjectScope()), path);
        Denied(AccessDenial.InvalidPath, decision);
        Assert.Null(decision.RealPath);
    }

    [Theory]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\?\C:\Users\me\file.txt")]
    [InlineData(@"\??\C:\Users\me\file.txt")]
    [InlineData(@"C:\Users\me\file.txt:hidden")]
    [InlineData(@"C:\Users\me\CON")]
    [InlineData(@"C:\Users\me\nul.txt")]
    [InlineData(@"C:\Users\me\COM1.log")]
    [InlineData(@"C:\Users\me\.env.")]
    [InlineData(@"C:\Users\me\file.txt ")]
    [InlineData(@"C:\PROGRA~1\App\app.exe")]
    [InlineData(@"C:\Users\me\a|b.txt")]
    [InlineData(@"C:relative.txt")]
    [InlineData(@"\Users\me\no-drive.txt")]
    [InlineData(@"\\server\share\file.txt")]
    public void Windows_special_paths_are_refused(string path)
    {
        var windows = new PolicyContext { Windows = true, IgnoreCase = true, SystemLocations = [] };
        Denied(AccessDenial.InvalidPath, _t.Read(_t.Grants(_t.ProjectScope()), path, windows));
    }

    [Theory]
    [InlineData(@"C:\Users\me\project\file.txt", false)]
    [InlineData(@"C:/Users/me/project/file.txt", false)]
    [InlineData(@"D:\data\.env", false)]
    [InlineData(@"\\server\share\docs\a.md", true)]
    [InlineData(@"//server/share/docs/a.md", true)]
    public void Windows_paths_that_pass_the_syntax_check(string path, bool unc)
    {
        Assert.Null(PathSyntax.Check(path, windows: true, out var isUnc));
        Assert.Equal(unc, isUnc);
    }

    [Fact]
    public void Unix_syntax_only_asks_for_an_absolute_path_without_nul()
    {
        Assert.Null(PathSyntax.Check("/home/me/CON", windows: false, out _));
        Assert.Null(PathSyntax.Check("/home/me/file.txt:stream", windows: false, out _));
        Assert.NotNull(PathSyntax.Check(@"C:\x", windows: false, out _));
    }

    // ---------------------------------------------------------------- manifest patterns

    [Fact]
    public void External_patterns_split_into_a_root_and_a_glob()
    {
        var home = _t.Home;
        var notes = FileScope.FromPattern(FileScopeKind.External, "~/Documents/Notes/**", FileAccessKind.Read, home);
        Assert.Equal(Path.GetFullPath(Path.Combine(home, "Documents", "Notes")), Path.GetFullPath(notes.Root));
        Assert.Equal(["**"], notes.Include);

        var logDir = Path.Combine(Path.GetPathRoot(home)!, "var", "log");
        var logs = FileScope.FromPattern(FileScopeKind.External, logDir.Replace('\\', '/') + "/*.log", FileAccessKind.Read, home);
        Assert.Equal(Path.GetFullPath(logDir), Path.GetFullPath(logs.Root));
        Assert.Equal(["*.log"], logs.Include);

        var file = FileScope.FromPattern(FileScopeKind.External, "~/todo.txt", FileAccessKind.ReadWrite, home);
        Assert.Equal(Path.Combine(home, "todo.txt"), file.Root);
        Assert.Empty(file.Include);

        if (!OperatingSystem.IsWindows()) Assert.Equal("/", FileScope.FromPattern(FileScopeKind.External, "/*", FileAccessKind.Read, home).Root);
        Assert.Throws<ArgumentException>(() => FileScope.FromPattern(FileScopeKind.External, "notes/**", FileAccessKind.Read, home));
        Assert.Throws<ArgumentException>(() => FileScope.FromPattern(FileScopeKind.External, "$HOME/notes", FileAccessKind.Read, home));
    }

    [Fact]
    public void A_pattern_scope_works_end_to_end()
    {
        var scope = FileScope.FromPattern(FileScopeKind.External, "~/.codex/sessions/**/*.jsonl", FileAccessKind.Read, _t.Home);
        var grants = _t.Grants(scope);
        Allowed(FileScopeKind.External, _t.Read(grants, Path.Combine(_t.Home, ".codex", "sessions", "rollout-1.jsonl")));
        Denied(AccessDenial.NotInScope, _t.Read(grants, Path.Combine(_t.Home, ".codex", "config.toml")));
    }

    [Fact]
    public void Nothing_held_means_nothing_reachable()
    {
        var decision = _t.Policy.Check(new PluginGrantSet([]), Path.Combine(_t.Outside, "notes.txt"), FileAccessKind.Read, _t.Context);
        Denied(AccessDenial.NotInScope, decision);
    }
}
