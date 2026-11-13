using System.Text.Json;
using Octopus.Agents.Sessions;
using Xunit;

namespace Octopus.Agents.Tests;

/// <summary>Rollouts built by hand in the shape Codex 0.167 writes (trimmed to the records the source reads).</summary>
public sealed class CodexSessionSourceTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "octopus-codex-" + Guid.NewGuid().ToString("N"));
    private readonly string _project = Path.Combine(Path.GetTempPath(), "proj-" + Guid.NewGuid().ToString("N"));
    private readonly CodexSessionSource _source = new();

    public CodexSessionSourceTests() => Directory.CreateDirectory(_home);

    public void Dispose()
    {
        try { Directory.Delete(_home, true); } catch (IOException) { }
    }

    private static string Json(object o) => JsonSerializer.Serialize(o);

    private static string Meta(string id, string cwd, string time = "2026-11-01T10:00:00Z", string? branch = null) =>
        Json(new Dictionary<string, object?>
        {
            ["timestamp"] = time,
            ["type"] = "session_meta",
            ["payload"] = new Dictionary<string, object?>
            {
                ["id"] = id, ["timestamp"] = time, ["cwd"] = cwd, ["cli_version"] = "0.167.0",
                ["git"] = branch is null ? null : new { branch, commit_hash = "abc" },
            },
        });

    private static string Msg(string role, string text, string time, bool output = false) =>
        Json(new { timestamp = time, type = "response_item", payload = new { type = "message", role, content = new[] { new { type = output ? "output_text" : "input_text", text } } } });

    private static string Event(string kind, string text, string time) =>
        Json(new { timestamp = time, type = "event_msg", payload = new Dictionary<string, string> { ["type"] = kind, ["message"] = text } });

    private static string Context(string model, string time) =>
        Json(new { timestamp = time, type = "turn_context", payload = new { model, cwd = "x" } });

    private string Write(string day, string id, IEnumerable<string> lines, string? root = null)
    {
        var dir = Path.Combine(root ?? Path.Combine(_home, "sessions"), day.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"rollout-2026-11-01T10-00-00-{id}.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    private const string Id1 = "11111111-1111-1111-1111-111111111111";
    private const string Id2 = "22222222-2222-2222-2222-222222222222";
    private const string Id3 = "33333333-3333-3333-3333-333333333333";

    [Fact]
    public async Task Lists_only_sessions_of_the_project_across_date_folders_and_ignores_archives()
    {
        Write("2026/10/31", Id1, [Meta(Id1, _project), Msg("user", "a", "2026-11-01T10:00:01Z")]);
        Write("2026/11/01", Id2, [Meta(Id2, _project + Path.DirectorySeparatorChar), Msg("user", "b", "2026-11-01T10:00:01Z")]);
        Write("2026/11/01", Id3, [Meta(Id3, _project + "-other"), Msg("user", "c", "2026-11-01T10:00:01Z")]);
        Write("x", "44444444-4444-4444-4444-444444444444", [Meta("44444444-4444-4444-4444-444444444444", _project), Msg("user", "d", "2026-11-01T10:00:01Z")],
            Path.Combine(_home, "archived_sessions"));

        var list = await _source.ListAsync(_home, _project);

        Assert.Equal([Id1, Id2], list.Select(s => s.SessionId).Order());
        Assert.All(list, s => Assert.Equal("codex", s.AgentId));
    }

    [Fact]
    public async Task Missing_folder_gives_an_empty_list()
    {
        Assert.Empty(await _source.ListAsync(Path.Combine(_home, "nope"), _project));
    }

    [Fact]
    public async Task Title_is_the_thread_name_from_the_index_else_the_first_prompt()
    {
        Write("2026/11/01", Id1, [Meta(Id1, _project), Msg("user", "\n  fix   the   build  \nmore", "2026-11-01T10:00:01Z")]);
        Write("2026/11/01", Id2, [Meta(Id2, _project), Msg("user", new string('x', 200), "2026-11-01T10:00:02Z")]);
        File.WriteAllLines(Path.Combine(_home, "session_index.jsonl"),
        [
            Json(new { id = Id1, thread_name = "old name", updated_at = "2026-11-01T10:00:00Z" }),
            "garbage",
            Json(new { id = Id1, thread_name = "Renamed", updated_at = "2026-11-01T11:00:00Z" }),
        ]);

        var list = await _source.ListAsync(_home, _project);

        Assert.Equal("Renamed", list.Single(s => s.SessionId == Id1).Title);
        var cut = list.Single(s => s.SessionId == Id2).Title;
        Assert.Equal(80 + 1, cut.Length);
        Assert.EndsWith("…", cut);

        File.Delete(Path.Combine(_home, "session_index.jsonl"));
        Assert.Equal("fix the build", (await _source.ListAsync(_home, _project)).Single(s => s.SessionId == Id1).Title);
    }

    [Fact]
    public async Task Injected_messages_are_not_prompts_and_sessions_without_content_are_skipped()
    {
        Write("2026/11/01", Id1,
        [
            Meta(Id1, _project),
            Msg("developer", "rules", "2026-11-01T10:00:01Z"),
            Msg("user", "<environment_context>cwd</environment_context>", "2026-11-01T10:00:01Z"),
            Msg("user", "# AGENTS.md instructions for /x", "2026-11-01T10:00:01Z"),
            Msg("user", "<user_instructions>x</user_instructions>", "2026-11-01T10:00:01Z"),
            Msg("user", "<turn_aborted>", "2026-11-01T10:00:01Z"),
            Msg("user", "real question", "2026-11-01T10:00:02Z"),
            Msg("assistant", "answer", "2026-11-01T10:00:03Z", output: true),
        ]);
        Write("2026/11/01", Id2, [Meta(Id2, _project), Msg("user", "<environment_context>x</environment_context>", "2026-11-01T10:00:01Z")]);

        var list = await _source.ListAsync(_home, _project);

        var s = Assert.Single(list);
        Assert.Equal("real question", s.Title);
        Assert.Equal(2, s.MessageCount);
    }

    [Fact]
    public async Task Messages_present_as_response_items_and_events_count_once_and_events_alone_still_count()
    {
        Write("2026/11/01", Id1,
        [
            Meta(Id1, _project),
            Msg("user", "q", "2026-11-01T10:00:01Z"), Event("user_message", "q", "2026-11-01T10:00:01Z"),
            Msg("assistant", "a", "2026-11-01T10:00:02Z", true), Event("agent_message", "a", "2026-11-01T10:00:02Z"),
        ]);
        Write("2026/11/01", Id2,
        [
            Meta(Id2, _project),
            Event("user_message", "old q", "2026-11-01T10:00:01Z"), Event("agent_message", "old a", "2026-11-01T10:00:02Z"),
        ]);

        var list = await _source.ListAsync(_home, _project);

        Assert.Equal(2, list.Single(s => s.SessionId == Id1).MessageCount);
        var old = list.Single(s => s.SessionId == Id2);
        Assert.Equal(2, old.MessageCount);
        Assert.Equal("old q", old.Title);
        var turns = (await _source.ReadAsync(list.Single(s => s.SessionId == Id1))).Turns;
        Assert.Equal(2, turns.Count);
    }

    [Fact]
    public async Task Model_branch_and_times_come_from_the_records_and_newest_comes_first()
    {
        Write("2026/11/01", Id1,
        [
            Meta(Id1, _project, "2026-11-01T10:00:00Z", "feature/x"),
            Context("gpt-a", "2026-11-01T10:00:01Z"), Msg("user", "q", "2026-11-01T10:00:01Z"),
            Context("gpt-b", "2026-11-01T10:05:00Z"), Msg("assistant", "a", "2026-11-01T10:05:00Z", true),
        ]);
        Write("2026/11/02", Id2, [Meta(Id2, _project, "2026-11-02T10:00:00Z"), Msg("user", "q", "2026-11-02T10:00:01Z")]);

        var list = await _source.ListAsync(_home, _project);

        Assert.Equal([Id2, Id1], list.Select(s => s.SessionId));
        var first = list[1];
        Assert.Equal("gpt-b", first.Model);
        Assert.Equal("feature/x", first.GitBranch);
        Assert.Null(list[0].Model);
        Assert.Null(list[0].GitBranch);
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T10:00:00Z"), first.StartedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T10:05:00Z"), first.UpdatedAt);
    }

    [Fact]
    public async Task Model_falls_back_to_thread_settings()
    {
        Write("2026/11/01", Id1,
        [
            Meta(Id1, _project), Msg("user", "q", "2026-11-01T10:00:01Z"),
            """{"timestamp":"2026-11-01T10:00:02Z","type":"event_msg","payload":{"type":"thread_settings_applied","thread_settings":{"model":"gpt-s"}}}""",
        ]);

        Assert.Equal("gpt-s", Assert.Single(await _source.ListAsync(_home, _project)).Model);
    }

    [Fact]
    public async Task Malformed_lines_and_files_are_ignored()
    {
        Write("2026/11/01", Id1, [Meta(Id1, _project), "{not json", "", Msg("user", "q", "2026-11-01T10:00:01Z"), "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\""]);
        Write("2026/11/01", Id2, ["garbage"]);
        Write("2026/11/01", Id3, []);

        var s = Assert.Single(await _source.ListAsync(_home, _project));
        Assert.Equal(Id1, s.SessionId);
        Assert.Equal(1, s.MessageCount);
    }

    [Fact]
    public async Task Session_id_falls_back_to_the_file_name()
    {
        Write("2026/11/01", Id1, [Json(new { timestamp = "2026-11-01T10:00:00Z", type = "session_meta", payload = new { cwd = _project } }), Msg("user", "q", "2026-11-01T10:00:01Z")]);

        Assert.Equal(Id1, Assert.Single(await _source.ListAsync(_home, _project)).SessionId);
    }

    [Fact]
    public async Task Unchanged_files_are_not_read_again_and_changed_ones_are()
    {
        var path = Write("2026/11/01", Id1, [Meta(Id1, _project), Msg("user", "q", "2026-11-01T10:00:01Z")]);
        Write("2026/11/01", Id2, [Meta(Id2, "/elsewhere"), Msg("user", "q", "2026-11-01T10:00:01Z")]);

        await _source.ListAsync(_home, _project);
        Assert.Equal(1, _source.FullScans);
        await _source.ListAsync(_home, _project);
        Assert.Equal(1, _source.FullScans);

        File.AppendAllLines(path, [Msg("assistant", "a", "2026-11-01T10:00:09Z", true)]);
        var list = await _source.ListAsync(_home, _project);
        Assert.Equal(2, _source.FullScans);
        Assert.Equal(2, list.Single().MessageCount);
    }

    [Fact]
    public async Task Cancelled_listing_throws()
    {
        Write("2026/11/01", Id1, [Meta(Id1, _project), Msg("user", "q", "2026-11-01T10:00:01Z")]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _source.ListAsync(_home, _project, cts.Token));
    }

    [Fact]
    public async Task Read_gives_turns_with_tool_outputs_paired_by_call_id_and_patched_files()
    {
        const string patch = "*** Begin Patch\n*** Update File: src/a.cs\n@@\n-x\n+y\n*** Add File: src/b.cs\n+z\n*** Delete File: old.txt\n*** End Patch";
        var path = Write("2026/11/01", Id1,
        [
            Meta(Id1, _project),
            Msg("user", "<environment_context>x</environment_context>", "2026-11-01T10:00:00Z"),
            Msg("user", "do it", "2026-11-01T10:00:01Z"),
            """{"timestamp":"2026-11-01T10:00:02Z","type":"response_item","payload":{"type":"reasoning","summary":[],"encrypted_content":"zzz"}}""",
            Json(new { timestamp = "2026-11-01T10:00:03Z", type = "response_item", payload = new { type = "function_call", name = "shell", arguments = Json(new { command = new[] { "bash", "-lc", "ls   -la" } }), call_id = "c1" } }),
            Json(new { timestamp = "2026-11-01T10:00:04Z", type = "response_item", payload = new { type = "custom_tool_call", name = "apply_patch", input = patch, call_id = "c2" } }),
            Json(new { timestamp = "2026-11-01T10:00:05Z", type = "response_item", payload = new { type = "function_call", name = "read", arguments = Json(new { path = "/x/y.txt", extra = 1 }), call_id = "c3" } }),
            Json(new { timestamp = "2026-11-01T10:00:06Z", type = "response_item", payload = new { type = "function_call_output", call_id = "c3", output = "contents" } }),
            Json(new { timestamp = "2026-11-01T10:00:07Z", type = "response_item", payload = new { type = "function_call_output", call_id = "c1", output = new { output = new string('o', 5000), metadata = 1 } } }),
            Json(new { timestamp = "2026-11-01T10:00:08Z", type = "response_item", payload = new { type = "custom_tool_call_output", call_id = "c2", output = "Done" } }),
            Msg("assistant", "finished", "2026-11-01T10:00:09Z", true),
        ]);
        var info = Assert.Single(await _source.ListAsync(_home, _project));

        var turns = (await _source.ReadAsync(info)).Turns;

        Assert.Equal([SessionTurnRole.User, SessionTurnRole.Tool, SessionTurnRole.Tool, SessionTurnRole.Tool, SessionTurnRole.Assistant], turns.Select(t => t.Role));
        Assert.Equal("do it", turns[0].Text);
        Assert.Equal("shell", turns[1].ToolName);
        Assert.Equal("bash -lc ls -la", turns[1].Text);
        Assert.Equal(4096 + 1, turns[1].Output!.Length);
        Assert.Equal("apply_patch", turns[2].ToolName);
        Assert.Equal(["src/a.cs", "src/b.cs", "old.txt"], turns[2].Files);
        Assert.Equal("Done", turns[2].Output);
        Assert.Equal("/x/y.txt", turns[3].Text);
        Assert.Equal("contents", turns[3].Output);
        Assert.Null(turns[3].Files);
        Assert.Equal("finished", turns[4].Text);
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T10:00:09Z"), turns[4].Timestamp);
        Assert.Equal(path, info.FilePath);
    }

    [Fact]
    public async Task Tool_without_known_keys_is_summarised_as_cut_json_and_a_missing_file_throws()
    {
        var path = Write("2026/11/01", Id1,
        [
            Meta(Id1, _project), Msg("user", "q", "2026-11-01T10:00:01Z"),
            Json(new { timestamp = "2026-11-01T10:00:03Z", type = "response_item", payload = new { type = "function_call", name = "t", arguments = Json(new { blob = new string('b', 500) }), call_id = "c" } }),
        ]);
        var info = Assert.Single(await _source.ListAsync(_home, _project));

        var tool = (await _source.ReadAsync(info)).Turns.Single(t => t.Role == SessionTurnRole.Tool);
        Assert.Equal(200 + 1, tool.Text.Length);
        Assert.Null(tool.Output);

        File.Delete(path);
        await Assert.ThrowsAnyAsync<IOException>(() => _source.ReadAsync(info));
    }
}
