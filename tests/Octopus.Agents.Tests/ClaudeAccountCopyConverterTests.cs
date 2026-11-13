using System.Text.Json;
using Octopus.Agents.Sessions;

namespace Octopus.Agents.Tests;

/// <summary>A Claude session moves to another account by copying its transcript into that account's project folder.</summary>
public sealed class ClaudeAccountCopyConverterTests : IDisposable
{
    private readonly TempDir _root = new();
    private readonly string _home;
    private readonly string _source;
    private readonly string _project;
    private readonly string _file;
    private const string Id = "11111111-2222-3333-4444-555555555555";

    public ClaudeAccountCopyConverterTests()
    {
        _home = Path.Combine(_root.Path, "home");
        _source = Path.Combine(_root.Path, "acct-a");
        _project = Path.Combine(_root.Path, "work", "app");
        var folder = ClaudeProjectFolder.For(_source, _project);
        Directory.CreateDirectory(folder);
        _file = Path.Combine(folder, Id + ".jsonl");
        File.WriteAllText(_file, $"{{\"type\":\"user\",\"sessionId\":\"{Id}\",\"message\":\"héllo\",\"n\":1.50}}\nnot json\n[1,2]\n{{\"type\":\"assistant\",\"sessionId\":\"{Id}\",\"nested\":{{\"sessionId\":\"keep\"}}}}\n");
    }

    public void Dispose() => _root.Dispose();

    private SessionTranscript Transcript() => new(
        new AgentSessionInfo("claude", _source, Id, "t", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1, null, null, _file), []);

    private SessionMigrationRequest Request(string? target) => new("claude", target, _project);

    private ClaudeAccountCopyConverter Converter() => new(_home);

    [Fact]
    public void OnlyClaudeToClaude()
    {
        var c = Converter();
        Assert.Equal(SessionMigrationKind.Native, c.Kind);
        Assert.True(c.CanConvert("claude", "claude"));
        Assert.False(c.CanConvert("claude", "codex"));
        Assert.False(c.CanConvert("codex", "claude"));
    }

    [Fact]
    public async Task CopiesIntoNewAccountWithSubfolder()
    {
        var sub = Path.Combine(Path.GetDirectoryName(_file)!, Id, "subagents");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "agent-1.jsonl"), "{}\n");
        var target = Path.Combine(_root.Path, "acct-b");

        var result = await Converter().ConvertAsync(Transcript(), Request(target));

        Assert.Equal(SessionMigrationKind.Native, result.Kind);
        Assert.Equal(Id, result.ResumeSessionId);
        var folder = ClaudeProjectFolder.For(target, _project);
        Assert.Equal(File.ReadAllBytes(_file), File.ReadAllBytes(Path.Combine(folder, Id + ".jsonl")));
        Assert.True(File.Exists(Path.Combine(folder, Id, "subagents", "agent-1.jsonl")));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp-*"));
    }

    [Fact]
    public async Task NullTargetMeansDotClaudeInHome()
    {
        var result = await Converter().ConvertAsync(Transcript(), Request(null));
        Assert.Equal(Id, result.ResumeSessionId);
        Assert.True(File.Exists(Path.Combine(ClaudeProjectFolder.For(Path.Combine(_home, ".claude"), _project), Id + ".jsonl")));
    }

    [Fact]
    public async Task IdenticalExistingFileIsReused()
    {
        var target = Path.Combine(_root.Path, "acct-b");
        var folder = ClaudeProjectFolder.For(target, _project);
        Directory.CreateDirectory(folder);
        File.Copy(_file, Path.Combine(folder, Id + ".jsonl"));

        var result = await Converter().ConvertAsync(Transcript(), Request(target));

        Assert.Equal(Id, result.ResumeSessionId);
        Assert.Single(Directory.GetFiles(folder, "*.jsonl"));
    }

    [Fact]
    public async Task DifferingExistingFileGetsNewIdWithSessionIdRewritten()
    {
        var target = Path.Combine(_root.Path, "acct-b");
        var folder = ClaudeProjectFolder.For(target, _project);
        Directory.CreateDirectory(folder);
        var existing = Path.Combine(folder, Id + ".jsonl");
        File.WriteAllText(existing, "{\"other\":true}\n");

        var result = await Converter().ConvertAsync(Transcript(), Request(target));

        var newId = result.ResumeSessionId!;
        Assert.NotEqual(Id, newId);
        Assert.True(Guid.TryParse(newId, out _));
        Assert.Equal("{\"other\":true}\n", File.ReadAllText(existing));
        var lines = File.ReadAllLines(Path.Combine(folder, newId + ".jsonl"));
        Assert.Equal(4, lines.Length);
        foreach (var i in new[] { 0, 3 })
        {
            using var doc = JsonDocument.Parse(lines[i]);
            Assert.Equal(newId, doc.RootElement.GetProperty("sessionId").GetString());
        }
        using (var doc = JsonDocument.Parse(lines[0]))
        {
            Assert.Equal("héllo", doc.RootElement.GetProperty("message").GetString());
            Assert.Equal("1.50", doc.RootElement.GetProperty("n").GetRawText());
        }
        using (var doc = JsonDocument.Parse(lines[3]))
        {
            Assert.Equal("keep", doc.RootElement.GetProperty("nested").GetProperty("sessionId").GetString());
        }
        Assert.Equal("not json", lines[1]);
        Assert.Equal("[1,2]", lines[2]);
    }

    [Fact]
    public async Task SubfolderFollowsTheNewId()
    {
        var sub = Path.Combine(Path.GetDirectoryName(_file)!, Id);
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "x.txt"), "x");
        var target = Path.Combine(_root.Path, "acct-b");
        var folder = ClaudeProjectFolder.For(target, _project);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, Id + ".jsonl"), "different\n");

        var result = await Converter().ConvertAsync(Transcript(), Request(target));

        Assert.True(File.Exists(Path.Combine(folder, result.ResumeSessionId!, "x.txt")));
    }

    [Fact]
    public async Task SameAccountThrows()
    {
        var same = _source + Path.DirectorySeparatorChar;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Converter().ConvertAsync(Transcript(), Request(same)));
    }
}
