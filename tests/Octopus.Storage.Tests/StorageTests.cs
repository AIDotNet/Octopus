using System.Text.Json;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.Core.Settings;
using Octopus.Platform;
using Octopus.Storage;
using Xunit;

namespace Octopus.Storage.Tests;

public sealed class TempPaths : IAppPaths, IDisposable
{
    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "octopus-tests-" + Guid.NewGuid().ToString("N"), "data");
    public string CacheDirectory { get; } = Path.Combine(Path.GetTempPath(), "octopus-tests-cache");
    public string ProjectsFile => Path.Combine(DataDirectory, "projects.json");
    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(DataDirectory)!, true); } catch (IOException) { }
    }
}

public class ProjectStoreTests
{
    private static ProjectsDocument Sample()
    {
        var terminal = new ProjectItem { Id = "t1", Kind = ProjectItemKind.Terminal, Title = "zsh", WorkingDirectory = "/Users/张三/代码" };
        var agent = new ProjectItem
        {
            Id = "a1", Kind = ProjectItemKind.Agent, Title = "Claude", CustomTitle = "重构任务", AgentId = "claude",
            Launch = new AgentLaunchOptions { Model = "opus", Effort = "high", Prompt = "SECRET-PROMPT", Resume = true }
        };
        var file = new ProjectItem { Id = "f1", Kind = ProjectItemKind.File, Title = "说明.md", FilePath = "/Users/张三/代码/说明.md" };
        return new ProjectsDocument
        {
            ActiveProjectId = "p1",
            Projects = [new Project { Id = "p1", Name = "我的项目", Path = "/Users/张三/代码", ActiveItemId = "a1", Items = [terminal, agent, file] }]
        };
    }

    [Fact]
    public async Task RoundTrip()
    {
        using var paths = new TempPaths();
        var store = new JsonProjectStore(paths);
        await store.SaveAsync(Sample(), TestContext.Current.CancellationToken);
        var loaded = await new JsonProjectStore(paths).LoadAsync(TestContext.Current.CancellationToken);
        var p = Assert.Single(loaded.Projects);
        Assert.Equal("我的项目", p.Name);
        Assert.Equal("/Users/张三/代码", p.Path);
        Assert.Equal("p1", loaded.ActiveProjectId);
        Assert.Equal(new[] { ProjectItemKind.Terminal, ProjectItemKind.Agent, ProjectItemKind.File }, p.Items.Select(i => i.Kind));
        var agent = p.Items[1];
        Assert.Equal("重构任务", agent.CustomTitle);
        Assert.Equal("重构任务", agent.DisplayTitle);
        Assert.Equal("opus", agent.Launch!.Model);
        Assert.Equal("high", agent.Launch.Effort);
        Assert.Null(agent.Launch.Prompt);
        Assert.Equal("/Users/张三/代码/说明.md", p.Items[2].FilePath);
    }

    [Fact]
    public async Task A_project_on_an_ssh_host_keeps_its_host_and_its_remote_folder()
    {
        using var paths = new TempPaths();
        var document = new ProjectsDocument { Projects = [new Project { Id = "r1", Name = "web", Path = "/srv/my app", SshHost = "prod" }] };
        await new JsonProjectStore(paths).SaveAsync(document, TestContext.Current.CancellationToken);
        var loaded = await new JsonProjectStore(paths).LoadAsync(TestContext.Current.CancellationToken);
        var project = Assert.Single(loaded.Projects);
        Assert.Equal("prod", project.SshHost);
        Assert.Equal("/srv/my app", project.Path);
    }

    [Fact]
    public async Task The_tunnels_of_a_project_on_an_ssh_host_round_trip_and_are_written_by_name()
    {
        using var paths = new TempPaths();
        var local = new TunnelSpec { Id = "t1", Name = "前端", Kind = TunnelKind.Local, ListenPort = 5173, TargetHost = "localhost", TargetPort = 5173, AutoStart = true };
        var remote = new TunnelSpec
        {
            Id = "t2", Kind = TunnelKind.Remote, BindAddress = "0.0.0.0", ListenPort = 9000, TargetHost = "127.0.0.1", TargetPort = 3000, AutoReconnect = false,
        };
        var document = new ProjectsDocument { Projects = [new Project { Id = "r1", Name = "web", Path = "/srv/app", SshHost = "prod", Tunnels = [local, remote] }] };

        await new JsonProjectStore(paths).SaveAsync(document, TestContext.Current.CancellationToken);

        var text = File.ReadAllText(paths.ProjectsFile);
        Assert.Contains("\"kind\": \"local\"", text);
        Assert.Contains("\"kind\": \"remote\"", text);
        var loaded = Assert.Single((await new JsonProjectStore(paths).LoadAsync(TestContext.Current.CancellationToken)).Projects);
        Assert.Equal(2, loaded.Tunnels.Count);
        var first = loaded.Tunnels[0];
        Assert.Equal(("t1", "前端", TunnelKind.Local, "127.0.0.1", 5173, "localhost", 5173, true, true),
            (first.Id, first.Name, first.Kind, first.BindAddress, first.ListenPort, first.TargetHost, first.TargetPort, first.AutoStart, first.AutoReconnect));
        var second = loaded.Tunnels[1];
        Assert.Equal(("t2", null, TunnelKind.Remote, "0.0.0.0", 9000, "127.0.0.1", 3000, false, false),
            (second.Id, second.Name, second.Kind, second.BindAddress, second.ListenPort, second.TargetHost, second.TargetPort, second.AutoStart, second.AutoReconnect));
    }

    [Fact]
    public async Task A_file_without_tunnels_loads_with_none()
    {
        using var paths = new TempPaths();
        Directory.CreateDirectory(paths.DataDirectory);
        File.WriteAllText(paths.ProjectsFile, """{ "version": 1, "projects": [ { "id": "p1", "name": "A", "sshHost": "prod", "path": "/srv" } ] }""");

        var doc = await new JsonProjectStore(paths).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Empty(Assert.Single(doc.Projects).Tunnels);
    }

    [Fact]
    public async Task A_tunnel_without_an_id_or_with_a_repeated_one_is_dropped()
    {
        using var paths = new TempPaths();
        Directory.CreateDirectory(paths.DataDirectory);
        File.WriteAllText(paths.ProjectsFile, """
            { "version": 1, "projects": [ { "id": "p1", "name": "A", "sshHost": "prod", "path": "/srv", "tunnels": [
                { "id": "t1", "kind": "local", "listenPort": 1000, "targetPort": 1000 },
                { "id": "t1", "kind": "local", "listenPort": 2000, "targetPort": 2000 },
                { "id": "", "kind": "remote", "listenPort": 3000, "targetPort": 3000 },
                null,
                { "id": "t2", "kind": "remote", "listenPort": 4000, "targetPort": 4000 } ] } ] }
            """);

        var doc = await new JsonProjectStore(paths).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["t1", "t2"], Assert.Single(doc.Projects).Tunnels.Select(t => t.Id));
        Assert.Equal(1000, doc.Projects[0].Tunnels[0].ListenPort);
    }

    [Fact]
    public async Task JsonShape()
    {
        using var paths = new TempPaths();
        await new JsonProjectStore(paths).SaveAsync(Sample(), TestContext.Current.CancellationToken);
        var text = File.ReadAllText(paths.ProjectsFile);
        Assert.DoesNotContain("SECRET-PROMPT", text);
        Assert.DoesNotContain("prompt", text);
        Assert.DoesNotContain("resume", text);
        Assert.DoesNotContain("displayTitle", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"kind\": \"agent\"", text);
        Assert.Contains("\"activeProjectId\"", text);
        Assert.Contains("\"customTitle\"", text);
        Assert.Contains("我的项目", text);
        using var doc = JsonDocument.Parse(text);
        Assert.All(doc.RootElement.EnumerateObject(), x => Assert.True(char.IsLower(x.Name[0])));
    }

    [Fact]
    public async Task MissingFileGivesEmptyDocument()
    {
        using var paths = new TempPaths();
        var doc = await new JsonProjectStore(paths).LoadAsync(TestContext.Current.CancellationToken);
        Assert.Empty(doc.Projects);
        Assert.Null(doc.ActiveProjectId);
    }

    [Fact]
    public async Task CorruptFileIsQuarantined()
    {
        using var paths = new TempPaths();
        Directory.CreateDirectory(paths.DataDirectory);
        File.WriteAllText(paths.ProjectsFile, "{ not json");
        var doc = await new JsonProjectStore(paths).LoadAsync(TestContext.Current.CancellationToken);
        Assert.Empty(doc.Projects);
        Assert.False(File.Exists(paths.ProjectsFile));
        var kept = Assert.Single(Directory.GetFiles(paths.DataDirectory, "projects.json.corrupt-*"));
        Assert.Equal("{ not json", File.ReadAllText(kept));
    }

    [Fact]
    public async Task ConcurrentSavesKeepFileValid()
    {
        using var paths = new TempPaths();
        var store = new JsonProjectStore(paths);
        var ct = TestContext.Current.CancellationToken;
        await Task.WhenAll(Enumerable.Range(0, 40).Select(n =>
            store.SaveAsync(new ProjectsDocument { Projects = [new Project { Id = $"p{n}", Name = new string('x', 5000) }] }, ct)));
        var loaded = await store.LoadAsync(ct);
        Assert.Single(loaded.Projects);
        Assert.Empty(Directory.GetFiles(paths.DataDirectory, "*.tmp"));
        Assert.Empty(Directory.GetFiles(paths.DataDirectory, "*.corrupt-*"));
    }

    [Fact]
    public async Task NormalizationRules()
    {
        using var paths = new TempPaths();
        Directory.CreateDirectory(paths.DataDirectory);
        File.WriteAllText(paths.ProjectsFile, """
        {
          // comment
          "activeProjectId": "ghost",
          "projects": [
            { "id": "p1", "name": "A", "activeItemId": "nope", "items": [
                { "id": "i1", "kind": "terminal", "title": "t", },
                { "id": "i1", "kind": "file", "title": "dup" } ] },
            { "id": "p1", "name": "dup project" },
            { "id": "p2", "name": "B", "items": null, "activeItemId": "i1" },
            { "id": "p3", "name": "C", "activeItemId": "i1", "items": [ { "id": "i1", "kind": "terminal", "title": "x" } ] }
          ]
        }
        """);
        var doc = await new JsonProjectStore(paths).LoadAsync(TestContext.Current.CancellationToken);
        Assert.Null(doc.ActiveProjectId);
        Assert.Equal(new[] { "p1", "p2", "p3" }, doc.Projects.Select(p => p.Id));
        Assert.Single(doc.Projects[0].Items);
        Assert.Null(doc.Projects[0].ActiveItemId);
        Assert.NotNull(doc.Projects[1].Items);
        Assert.Null(doc.Projects[1].ActiveItemId);
        Assert.Empty(doc.Projects[2].Items); // i1 already used by p1
        Assert.Null(doc.Projects[2].ActiveItemId);
    }

    [Fact]
    public async Task Groups_and_the_open_group_round_trip()
    {
        using var paths = new TempPaths();
        var document = new ProjectsDocument
        {
            Projects = [new Project { Id = "p1", Name = "A" }, new Project { Id = "p2", Name = "B", GroupId = "g1" }],
            Groups = [new ProjectGroup { Id = "g1", Name = "工作", Color = "#0EA5E9" }, new ProjectGroup { Id = "g2", Name = "开源" }],
            ExpandedGroupId = "g1",
        };
        await new JsonProjectStore(paths).SaveAsync(document, TestContext.Current.CancellationToken);
        var loaded = await new JsonProjectStore(paths).LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "g1", "g2" }, loaded.Groups.Select(g => g.Id));
        Assert.Equal("工作", loaded.Groups[0].Name);
        Assert.Equal("#0EA5E9", loaded.Groups[0].Color);
        Assert.Null(loaded.Projects[0].GroupId);
        Assert.Equal("g1", loaded.Projects[1].GroupId);
        Assert.Equal("g1", loaded.ExpandedGroupId);
    }

    [Fact]
    public async Task A_file_without_groups_loads_with_every_project_in_none()
    {
        using var paths = new TempPaths();
        Directory.CreateDirectory(paths.DataDirectory);
        File.WriteAllText(paths.ProjectsFile, """{ "version": 1, "projects": [ { "id": "p1", "name": "A" } ] }""");
        var doc = await new JsonProjectStore(paths).LoadAsync(TestContext.Current.CancellationToken);
        Assert.Empty(doc.Groups);
        Assert.Null(doc.ExpandedGroupId);
        Assert.Null(Assert.Single(doc.Projects).GroupId);
    }

    [Fact]
    public async Task References_to_missing_groups_are_dropped()
    {
        using var paths = new TempPaths();
        Directory.CreateDirectory(paths.DataDirectory);
        File.WriteAllText(paths.ProjectsFile, """
        {
          "expandedGroupId": "ghost",
          "groups": [ { "id": "g1", "name": "A" }, { "id": "g1", "name": "dup" }, null ],
          "projects": [ { "id": "p1", "name": "A", "groupId": "g1" }, { "id": "p2", "name": "B", "groupId": "ghost" } ]
        }
        """);
        var doc = await new JsonProjectStore(paths).LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("A", Assert.Single(doc.Groups).Name);
        Assert.Null(doc.ExpandedGroupId);
        Assert.Equal("g1", doc.Projects[0].GroupId);
        Assert.Null(doc.Projects[1].GroupId);
    }

    [Fact]
    public async Task UnknownFieldsAreIgnored()
    {
        using var paths = new TempPaths();
        Directory.CreateDirectory(paths.DataDirectory);
        File.WriteAllText(paths.ProjectsFile, """{ "future": 1, "projects": [ { "id": "p", "name": "N", "extra": { "a": [1] } } ] }""");
        var doc = await new JsonProjectStore(paths).LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("N", Assert.Single(doc.Projects).Name);
    }
}

public class SettingsStoreTests
{
    [Fact]
    public void Defaults()
    {
        using var paths = new TempPaths();
        var store = new JsonSettingsStore(paths);
        store.Load();
        Assert.Equal("system", store.Current.Appearance.Theme);
        Assert.Equal(260, store.Current.Sidebar.Width);
        Assert.Equal(13, store.Current.Terminal.FontSize);
        Assert.Null(store.Current.Window);
        Assert.False(File.Exists(paths.SettingsFile));
    }

    [Fact]
    public void RoundTrip()
    {
        using var paths = new TempPaths();
        using var store = new JsonSettingsStore(paths);
        store.Load();
        store.Update(s =>
        {
            s.Appearance.Theme = "dark";
            s.Terminal.FontFamily = "JetBrains Mono";
            s.Agents.Profiles["claude"] = new AgentProfile
            {
                Command = "claude --x", Effort = "high", Permission = "plan", Fast = true,
                ExtraArgs = ["--a", "b c"],
                Env = new() { ["K"] = "V" },
                Features = new() { ["web"] = "true" }
            };
        });
        store.Flush();
        Assert.Null(store.LastError);
        var text = File.ReadAllText(paths.SettingsFile);
        Assert.Contains("\"theme\": \"dark\"", text);
        Assert.DoesNotContain("\"window\"", text);

        var second = new JsonSettingsStore(paths);
        second.Load();
        var p = second.Current.Agents.Profiles["claude"];
        Assert.Equal("dark", second.Current.Appearance.Theme);
        Assert.Equal("claude --x", p.Command);
        Assert.Equal("high", p.Effort);
        Assert.Equal("plan", p.Permission);
        Assert.Equal(new[] { "--a", "b c" }, p.ExtraArgs);
        Assert.Equal("V", p.Env["K"]);
        Assert.Equal("true", p.Features["web"]);
    }

    [Fact]
    public void UpdateRaisesChanged()
    {
        using var paths = new TempPaths();
        using var store = new JsonSettingsStore(paths);
        var count = 0;
        store.Changed += (_, _) => count++;
        store.Update(s => s.Sidebar.IsCollapsed = true);
        Assert.Equal(1, count);
        Assert.True(store.Current.Sidebar.IsCollapsed);
    }

    [Fact]
    public void ClampsValues()
    {
        using var paths = new TempPaths();
        Directory.CreateDirectory(paths.DataDirectory);
        File.WriteAllText(paths.SettingsFile, """{ "sidebar": { "width": 9999 }, "terminal": { "fontSize": 2, "scrollbackLines": 5 }, "appearance": null, "agents": null }""");
        using var store = new JsonSettingsStore(paths);
        store.Load();
        Assert.Equal(480, store.Current.Sidebar.Width);
        Assert.Equal(8, store.Current.Terminal.FontSize);
        Assert.Equal(100, store.Current.Terminal.ScrollbackLines);
        Assert.NotNull(store.Current.Appearance);
        Assert.NotNull(store.Current.Agents.Profiles);
        store.Update(s => { s.Sidebar.Width = 10; s.Terminal.FontSize = 99; s.Terminal.ScrollbackLines = 9_999_999; });
        Assert.Equal(200, store.Current.Sidebar.Width);
        Assert.Equal(32, store.Current.Terminal.FontSize);
        Assert.Equal(100_000, store.Current.Terminal.ScrollbackLines);
    }

    [Fact]
    public void CorruptFileIsQuarantined()
    {
        using var paths = new TempPaths();
        Directory.CreateDirectory(paths.DataDirectory);
        File.WriteAllText(paths.SettingsFile, "garbage");
        var store = new JsonSettingsStore(paths);
        store.Load();
        Assert.Equal("system", store.Current.Appearance.Theme);
        Assert.Single(Directory.GetFiles(paths.DataDirectory, "settings.json.corrupt-*"));
    }

    [Fact]
    public void UnknownFieldsAreIgnored()
    {
        using var paths = new TempPaths();
        Directory.CreateDirectory(paths.DataDirectory);
        File.WriteAllText(paths.SettingsFile, """{ "appearance": { "theme": "light", "newThing": 1 }, "zzz": [] }""");
        var store = new JsonSettingsStore(paths);
        store.Load();
        Assert.Equal("light", store.Current.Appearance.Theme);
    }

    [Fact]
    public void WriteFailureIsRecordedNotThrown()
    {
        using var paths = new TempPaths();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.DataDirectory)!);
        File.WriteAllText(paths.DataDirectory, "i am a file, not a directory");
        using var store = new JsonSettingsStore(paths);
        store.Update(s => s.Appearance.Theme = "dark");
        Assert.Equal("dark", store.Current.Appearance.Theme);
        store.Flush();
        Assert.NotNull(store.LastError);
        Assert.Equal("dark", store.Current.Appearance.Theme);
    }

    [Fact]
    public void UpdateWritesLaterAndFlushWritesNow()
    {
        using var paths = new TempPaths();
        using var store = new JsonSettingsStore(paths);
        store.Update(s => s.Terminal.FontSize = 14);
        store.Update(s => s.Terminal.FontSize = 15);
        // The change is current at once, before the file is written.
        Assert.Equal(15, store.Current.Terminal.FontSize);
        store.Flush();
        var second = new JsonSettingsStore(paths);
        second.Load();
        Assert.Equal(15, second.Current.Terminal.FontSize);
    }

    [Fact]
    public void PendingChangeIsWrittenOnItsOwn()
    {
        using var paths = new TempPaths();
        using var store = new JsonSettingsStore(paths);
        store.Update(s => s.Appearance.Theme = "light");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(paths.SettingsFile) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
        }
        Assert.Contains("\"theme\": \"light\"", File.ReadAllText(paths.SettingsFile));
    }

    [Fact]
    public void DisposeWritesThePendingChange()
    {
        using var paths = new TempPaths();
        var store = new JsonSettingsStore(paths);
        store.Update(s => s.Sidebar.Width = 300);
        store.Dispose();
        var second = new JsonSettingsStore(paths);
        second.Load();
        Assert.Equal(300, second.Current.Sidebar.Width);
    }

    [Fact]
    public void LoadKeepsAChangeNotWrittenYet()
    {
        using var paths = new TempPaths();
        using var store = new JsonSettingsStore(paths);
        store.Update(s => s.Sidebar.IsCollapsed = true);
        store.Load();
        Assert.True(store.Current.Sidebar.IsCollapsed);
    }
}
