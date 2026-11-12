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
        var store = new JsonSettingsStore(paths);
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
        var store = new JsonSettingsStore(paths);
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
        var store = new JsonSettingsStore(paths);
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
        var store = new JsonSettingsStore(paths);
        store.Update(s => s.Appearance.Theme = "dark");
        Assert.NotNull(store.LastError);
        Assert.Equal("dark", store.Current.Appearance.Theme);
    }
}
