using System.Text.Json;
using Octopus.Agents.Sessions;

namespace Octopus.Agents.Tests;

public sealed class ClaudeSessionSourceTests : IDisposable
{
    private readonly string _account = Path.Combine(Path.GetTempPath(), "octopus-claude-sessions-" + Guid.NewGuid().ToString("N"));
    private readonly string _project = Path.Combine(Path.GetTempPath(), "proj-a");
    private readonly ClaudeSessionSource _source = new();

    public void Dispose()
    {
        try { Directory.Delete(_account, recursive: true); } catch (IOException) { }
    }

    private string Folder(string? project = null)
    {
        var folder = Path.Combine(_account, "projects", ClaudeProjectFolder.Encode(project ?? _project));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private string Write(string id, IEnumerable<string> lines, string? project = null)
    {
        var path = Path.Combine(Folder(project), id + ".jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    private static string J(object? value) => JsonSerializer.Serialize(value);

    private string Rec(string type, string time, object message, string? cwd = null, string branch = "main", bool? sidechain = null, bool? meta = null) =>
        "{\"parentUuid\":null,\"isSidechain\":" + (sidechain == true ? "true" : "false")
        + (meta == true ? ",\"isMeta\":true" : "")
        + ",\"type\":\"" + type + "\",\"message\":" + J(message)
        + ",\"timestamp\":\"" + time + "\",\"cwd\":" + J(cwd ?? _project) + ",\"gitBranch\":" + J(branch) + "}";

    private string User(string time, object content, string? cwd = null, bool? meta = null, bool? sidechain = null) =>
        Rec("user", time, new { role = "user", content }, cwd, meta: meta, sidechain: sidechain);

    private string Assistant(string time, string messageId, object[] blocks, string? model = "claude-x", string branch = "main") =>
        Rec("assistant", time, new { id = messageId, role = "assistant", model, content = blocks }, branch: branch);

    private static object Text(string text) => new { type = "text", text };

    private async Task<IReadOnlyList<AgentSessionInfo>> List() => await _source.ListAsync(_account, _project);

    [Fact]
    public async Task Lists_only_sessions_whose_cwd_is_the_project()
    {
        Write("mine", [User("2026-01-01T00:00:00Z", "hello")]);
        // Same encoded folder name, different directory.
        var other = _project.Replace("proj-a", "proj.a");
        Assert.Equal(ClaudeProjectFolder.Encode(_project), ClaudeProjectFolder.Encode(other));
        Write("theirs", [User("2026-01-01T00:00:00Z", "hello", cwd: other)]);
        File.WriteAllText(Path.Combine(Folder(), "notes.txt"), "{}");
        Directory.CreateDirectory(Path.Combine(Folder(), "mine"));

        var sessions = await List();

        var session = Assert.Single(sessions);
        Assert.Equal("mine", session.SessionId);
        Assert.Equal("claude", session.AgentId);
        Assert.Equal(_account, session.AccountDirectory);
    }

    [Fact]
    public async Task Trailing_separator_in_the_project_path_is_ignored()
    {
        Write("s", [User("2026-01-01T00:00:00Z", "hello")]);

        var sessions = await _source.ListAsync(_account, _project + Path.DirectorySeparatorChar);

        Assert.Single(sessions);
    }

    [Fact]
    public async Task Missing_folder_lists_nothing()
    {
        Assert.Empty(await _source.ListAsync(_account, _project));
    }

    [Fact]
    public async Task Custom_title_beats_ai_title_beats_first_prompt()
    {
        var prompt = User("2026-01-01T00:00:00Z", "  first   prompt\nsecond line ");
        Write("a", [prompt]);
        Write("b", [prompt, """{"type":"ai-title","aiTitle":"Old"}""", """{"type":"ai-title","aiTitle":"New"}"""]);
        Write("c", [prompt, """{"type":"ai-title","aiTitle":"AI"}""", """{"type":"custom-title","customTitle":"Mine"}""", """{"type":"ai-title","aiTitle":"Later AI"}"""]);

        var titles = (await List()).ToDictionary(s => s.SessionId, s => s.Title);

        Assert.Equal("first prompt", titles["a"]);
        Assert.Equal("New", titles["b"]);
        Assert.Equal("Mine", titles["c"]);
    }

    [Fact]
    public async Task Long_prompt_titles_are_cut_with_an_ellipsis()
    {
        Write("a", [User("2026-01-01T00:00:00Z", new string('x', 200))]);

        var title = Assert.Single(await List()).Title;

        Assert.Equal(new string('x', 80) + "…", title);
    }

    [Fact]
    public async Task Meta_sidechain_reminder_and_tool_result_records_are_not_messages()
    {
        Write("a",
        [
            User("2026-01-01T00:00:00Z", "<system-reminder>ignore me</system-reminder>"),
            User("2026-01-01T00:00:01Z", "caveat", meta: true),
            User("2026-01-01T00:00:02Z", "side", sidechain: true),
            User("2026-01-01T00:00:03Z", "<command-name>/clear</command-name>"),
            User("2026-01-01T00:00:04Z", "[Request interrupted by user]"),
            User("2026-01-01T00:00:05Z", new object[] { new { type = "tool_result", tool_use_id = "t1", content = "out" } }),
            User("2026-01-01T00:00:06Z", "<system-reminder>noise</system-reminder>Real question"),
        ]);

        var session = Assert.Single(await List());

        Assert.Equal("Real question", session.Title);
        Assert.Equal(1, session.MessageCount);
    }

    [Fact]
    public async Task Split_assistant_records_count_once_and_only_text_counts()
    {
        Write("a",
        [
            User("2026-01-01T00:00:00Z", "go"),
            Assistant("2026-01-01T00:00:01Z", "m1", [new { type = "thinking", thinking = "hmm" }]),
            Assistant("2026-01-01T00:00:02Z", "m1", [Text("one")]),
            Assistant("2026-01-01T00:00:03Z", "m1", [Text("two")]),
            Assistant("2026-01-01T00:00:04Z", "m2", [new { type = "tool_use", id = "t", name = "Bash", input = new { command = "ls" } }]),
            Assistant("2026-01-01T00:00:05Z", "m3", [Text("three")]),
        ]);

        var session = Assert.Single(await List());

        Assert.Equal(3, session.MessageCount);
    }

    [Fact]
    public async Task Model_branch_and_times_come_from_the_records()
    {
        Write("a",
        [
            User("2026-01-01T00:00:00Z", "go", cwd: null),
            Assistant("2026-01-01T00:00:01Z", "m1", [Text("a")], model: "claude-old", branch: "feature"),
            Assistant("2026-01-01T00:00:02Z", "m2", [Text("b")], model: "claude-new", branch: "feature"),
            Assistant("2026-01-01T00:00:03Z", "m3", [Text("c")], model: "<synthetic>", branch: ""),
        ]);

        var session = Assert.Single(await List());

        Assert.Equal("claude-new", session.Model);
        Assert.Equal("feature", session.GitBranch);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:00:00Z"), session.StartedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:00:03Z"), session.UpdatedAt);
        Assert.EndsWith("a.jsonl", session.FilePath);
    }

    [Fact]
    public async Task Newest_session_comes_first_and_files_without_messages_are_skipped()
    {
        Write("old", [User("2026-01-01T00:00:00Z", "old")]);
        Write("new", [User("2026-03-01T00:00:00Z", "new")]);
        Write("empty", ["""{"type":"ai-title","aiTitle":"x"}""", """{"type":"mode","mode":"normal"}"""]);

        var ids = (await List()).Select(s => s.SessionId).ToArray();

        Assert.Equal(["new", "old"], ids);
    }

    [Fact]
    public async Task Malformed_lines_and_huge_ignored_records_do_not_matter()
    {
        var blob = "{\"type\":\"api-request-blob\",\"hash\":\"h\",\"message\":\"" + new string('z', 300_000) + "\"}";
        Write("a",
        [
            "not json",
            "{\"type\":\"user\",\"message\":",
            blob,
            User("2026-01-01T00:00:00Z", "hello"),
            blob,
            Assistant("2026-01-01T00:00:01Z", "m1", [Text("hi")]),
            "{\"type\":\"user\"",
        ]);

        var session = Assert.Single(await List());

        Assert.Equal("hello", session.Title);
        Assert.Equal(2, session.MessageCount);
        var transcript = await _source.ReadAsync(session);
        Assert.Equal(2, transcript.Turns.Count);
    }

    [Fact]
    public async Task A_file_without_a_final_newline_is_read_to_its_end()
    {
        var path = Path.Combine(Folder(), "a.jsonl");
        File.WriteAllText(path, User("2026-01-01T00:00:00Z", "hello") + "\n" + Assistant("2026-01-01T00:00:01Z", "m1", [Text("hi")]));

        Assert.Equal(2, Assert.Single(await List()).MessageCount);
    }

    [Fact]
    public async Task Unchanged_files_come_from_the_cache_and_changed_files_are_read_again()
    {
        var path = Write("a", [User("2026-01-01T00:00:00Z", "hello")]);
        var first = await List();
        var second = await List();
        Assert.Equal(first, second);

        File.AppendAllText(path, Assistant("2026-01-01T00:00:09Z", "m1", [Text("hi")]) + "\n");
        var third = await List();

        Assert.Equal(1, Assert.Single(first).MessageCount);
        Assert.Equal(2, Assert.Single(third).MessageCount);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:00:09Z"), third[0].UpdatedAt);
    }

    [Fact]
    public async Task Cancelled_listing_throws()
    {
        Write("a", [User("2026-01-01T00:00:00Z", "hello")]);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _source.ListAsync(_account, _project, cts.Token));
    }

    [Fact]
    public async Task Read_gives_user_assistant_and_tool_turns_with_outputs_and_files()
    {
        var edit = Path.Combine(_project, "a.cs");
        var longOutput = new string('o', 5000);
        Write("s",
        [
            User("2026-01-01T00:00:00Z", "<system-reminder>x</system-reminder>fix it"),
            Assistant("2026-01-01T00:00:01Z", "m1", [new { type = "thinking", thinking = "private" }]),
            Assistant("2026-01-01T00:00:02Z", "m1", [Text("looking")]),
            Assistant("2026-01-01T00:00:03Z", "m1", [new { type = "tool_use", id = "t1", name = "Bash", input = new { command = "ls\n  -la" } }]),
            Assistant("2026-01-01T00:00:03Z", "m1", [new { type = "tool_use", id = "t2", name = "Edit", input = new { file_path = edit, old_string = "a", new_string = "b" } }]),
            Assistant("2026-01-01T00:00:03Z", "m1", [new { type = "tool_use", id = "t3", name = "Weird", input = new { a = 1 } }]),
            User("2026-01-01T00:00:04Z", new object[]
            {
                new { type = "tool_result", tool_use_id = "t1", content = longOutput },
                new { type = "tool_result", tool_use_id = "t2", content = new object[] { Text("edited"), Text("ok") } },
            }),
            User("2026-01-01T00:00:05Z", "side", sidechain: true),
            Assistant("2026-01-01T00:00:06Z", "m2", [Text("done")]),
        ]);
        var session = Assert.Single(await List());

        var transcript = await _source.ReadAsync(session);

        Assert.Equal(session, transcript.Session);
        var turns = transcript.Turns;
        Assert.Equal(
            [SessionTurnRole.User, SessionTurnRole.Assistant, SessionTurnRole.Tool, SessionTurnRole.Tool, SessionTurnRole.Tool, SessionTurnRole.Assistant],
            turns.Select(t => t.Role).ToArray());
        Assert.Equal("fix it", turns[0].Text);
        Assert.Equal("looking", turns[1].Text);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:00:02Z"), turns[1].Timestamp);
        Assert.Equal("Bash", turns[2].ToolName);
        Assert.Equal("ls -la", turns[2].Text);
        Assert.StartsWith("ooo", turns[2].Output);
        Assert.True(turns[2].Output!.Length <= 4097);
        Assert.Null(turns[2].Files);
        Assert.Equal("Edit", turns[3].ToolName);
        Assert.Equal(edit, turns[3].Text);
        Assert.Equal("edited\nok", turns[3].Output);
        Assert.Equal([edit], turns[3].Files);
        Assert.Equal("{\"a\":1}", turns[4].Text);
        Assert.Null(turns[4].Output);
        Assert.Equal("done", turns[5].Text);
    }

    [Fact]
    public async Task Read_joins_text_blocks_of_one_message()
    {
        Write("s",
        [
            User("2026-01-01T00:00:00Z", "go"),
            Assistant("2026-01-01T00:00:01Z", "m1", [Text("one")]),
            Assistant("2026-01-01T00:00:02Z", "m1", [Text("two")]),
        ]);

        var transcript = await _source.ReadAsync(Assert.Single(await List()));

        Assert.Equal("one\ntwo", transcript.Turns[1].Text);
        Assert.Equal(2, transcript.Turns.Count);
    }

    [Fact]
    public async Task Read_of_a_vanished_file_throws_io()
    {
        var path = Write("s", [User("2026-01-01T00:00:00Z", "go")]);
        var session = Assert.Single(await List());
        File.Delete(path);

        await Assert.ThrowsAnyAsync<IOException>(() => _source.ReadAsync(session));
    }
}
