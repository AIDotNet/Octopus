using System.Text.Json;
using Microsoft.Data.Sqlite;
using Octopus.Agents.Sessions;
using Octopus.Agents.Signals;

namespace Octopus.Agents.Tests;

public sealed class AgySessionSourceTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "octopus-agy-sessions-" + Guid.NewGuid().ToString("N"));
    private readonly string _account;
    private readonly string _project;
    private readonly AgySessionSource _source = new();

    public AgySessionSourceTests()
    {
        _account = Path.Combine(_home, ".gemini", "antigravity-cli");
        _project = Path.Combine(_home, "code", "My App");
        Directory.CreateDirectory(_account);
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }

    private string DbPath => Path.Combine(_account, "conversation_summaries.db");

    private void EnsureDb()
    {
        if (File.Exists(DbPath)) return;
        using var conn = OpenWrite();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE conversation_summaries (
                conversation_id text PRIMARY KEY,
                title text NOT NULL DEFAULT "",
                preview text NOT NULL DEFAULT "",
                step_count integer NOT NULL DEFAULT 0,
                last_modified_time datetime NOT NULL DEFAULT "0001-01-01 00:00:00+00:00",
                workspace_uris text NOT NULL DEFAULT "[]",
                parent_conversation_id text NOT NULL DEFAULT "",
                nesting_depth integer NOT NULL DEFAULT 0,
                killed numeric NOT NULL DEFAULT 0,
                last_user_input_time datetime NOT NULL DEFAULT "0001-01-01 00:00:00+00:00"
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection OpenWrite()
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = DbPath, Pooling = false }.ToString();
        var conn = new SqliteConnection(cs);
        conn.Open();
        return conn;
    }

    private void AddSummary(
        string id,
        string[]? workspaces = null,
        string title = "",
        string preview = "",
        int stepCount = 4,
        string lastModified = "2026-11-23 01:54:34+00:00",
        string lastUserInput = "2026-11-23 01:50:22+00:00",
        string parentId = "",
        int nestingDepth = 0,
        int killed = 0)
    {
        EnsureDb();
        var uris = (workspaces ?? [_project]).Select(p => new Uri(Path.GetFullPath(p)).AbsoluteUri).ToArray();
        using var conn = OpenWrite();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO conversation_summaries
            (conversation_id, title, preview, step_count, last_modified_time, workspace_uris, parent_conversation_id, nesting_depth, killed, last_user_input_time)
            VALUES ($id, $title, $preview, $steps, $mod, $uris, $parent, $depth, $killed, $input);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$preview", preview);
        cmd.Parameters.AddWithValue("$steps", stepCount);
        cmd.Parameters.AddWithValue("$mod", lastModified);
        cmd.Parameters.AddWithValue("$uris", JsonSerializer.Serialize(uris));
        cmd.Parameters.AddWithValue("$parent", parentId);
        cmd.Parameters.AddWithValue("$depth", nestingDepth);
        cmd.Parameters.AddWithValue("$killed", killed);
        cmd.Parameters.AddWithValue("$input", lastUserInput);
        cmd.ExecuteNonQuery();
    }

    private string WriteTranscript(string id, IEnumerable<string> lines)
    {
        var dir = Path.Combine(_account, "brain", id, ".system_generated", "logs");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "transcript.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    private static string J(object value) => JsonSerializer.Serialize(value);

    private static string UserStep(int step, string time, string prompt) => J(new
    {
        step_index = step,
        source = "USER_EXPLICIT",
        type = "USER_INPUT",
        status = "DONE",
        created_at = time,
        content = $"<USER_REQUEST>\n{prompt}\n</USER_REQUEST>\n<ADDITIONAL_METADATA>\nThe current local time is: {time}.\n</ADDITIONAL_METADATA>",
    });

    private static string PlannerStep(int step, string time, string? content = null, string? model = "gemini-4-neon-high", object[]? toolCalls = null) => J(new
    {
        step_index = step,
        source = "MODEL",
        type = "PLANNER_RESPONSE",
        status = "DONE",
        created_at = time,
        model,
        content,
        tool_calls = toolCalls,
    });

    private static string ToolOutputStep(int step, string time, string content) => J(new
    {
        step_index = step,
        source = "MODEL",
        type = "GENERIC",
        status = "DONE",
        created_at = time,
        content,
    });

    private static string SystemStep(int step, string time, string type = "SYSTEM_MESSAGE", string content = "reminder") => J(new
    {
        step_index = step,
        source = "SYSTEM",
        type,
        status = "DONE",
        created_at = time,
        content,
    });

    private static object ToolCall(string name, Dictionary<string, string> rawArgs) =>
        new { name, args = rawArgs.ToDictionary(kv => kv.Key, kv => JsonSerializer.Serialize(kv.Value)) };

    [Fact]
    public async Task Lists_only_top_level_active_sessions_for_the_project()
    {
        var other = Path.Combine(_home, "code", "other");
        AddSummary("mine", title: "Antigravity sessions");
        WriteTranscript("mine", [UserStep(0, "2026-11-23T01:50:22Z", "add agy sessions"), PlannerStep(1, "2026-11-23T01:54:34Z", "done")]);
        AddSummary("other-proj", workspaces: [other], title: "Other");
        AddSummary("subagent", title: "Child", parentId: "mine", nestingDepth: 1);
        AddSummary("killed-one", title: "Killed", killed: 1);
        AddSummary("empty", title: "", preview: "", stepCount: 0);

        var sessions = await _source.ListAsync(_account, _project);

        var session = Assert.Single(sessions);
        Assert.Equal("mine", session.SessionId);
        Assert.Equal("agy", session.AgentId);
        Assert.Equal(_account, session.AccountDirectory);
        Assert.Equal("Antigravity sessions", session.Title);
        Assert.Equal(2, session.MessageCount);
        Assert.Equal("gemini-4-neon-high", session.Model);
    }

    [Fact]
    public async Task Matches_trailing_separator_percent_encoded_uris_and_multi_workspace_entries()
    {
        var extra = Path.Combine(_home, "code", "extra");
        AddSummary("s1", workspaces: [extra, _project], title: "Multi");
        WriteTranscript("s1", [UserStep(0, "2026-11-23T01:50:00Z", "hi")]);

        var sessions = await _source.ListAsync(_account, _project + Path.DirectorySeparatorChar);

        Assert.Equal("s1", Assert.Single(sessions).SessionId);
        Assert.Equal(@"C:\Users\me\repo", AgySessionSource.UriToPath("file:///C:/Users/me/repo"));
        Assert.Equal(@"c:\Users\me\repo", AgySessionSource.UriToPath("file:///c%3A/Users/me/repo"));
    }

    [Fact]
    public async Task Title_prefers_db_title_then_preview_then_first_prompt_and_newest_comes_first()
    {
        AddSummary("a", title: "Explicit title", preview: "preview", lastModified: "2026-11-20 01:00:00+00:00", lastUserInput: "2026-11-20 00:50:00+00:00");
        WriteTranscript("a", [UserStep(0, "2026-11-20T00:50:00Z", "prompt a")]);

        AddSummary("b", title: "", preview: "Preview line 1\nline 2", lastModified: "2026-11-22 01:00:00+00:00", lastUserInput: "2026-11-22 00:50:00+00:00");
        WriteTranscript("b", [UserStep(0, "2026-11-22T00:50:00Z", "prompt b")]);

        AddSummary("c", title: "", preview: "", lastModified: "2026-11-21 01:00:00+00:00", lastUserInput: "0001-01-01 00:00:00+00:00");
        WriteTranscript("c", [UserStep(0, "2026-11-21T00:50:00Z", "prompt from transcript")]);

        var sessions = await _source.ListAsync(_account, _project);

        Assert.Equal(["b", "c", "a"], sessions.Select(s => s.SessionId).ToArray());
        Assert.Equal("Preview line 1", sessions[0].Title);
        Assert.Equal("prompt from transcript", sessions[1].Title);
        Assert.Equal("Explicit title", sessions[2].Title);
    }

    [Fact]
    public async Task Model_skips_internal_enum_placeholders_and_system_steps_are_not_counted()
    {
        AddSummary("s", title: "Work");
        WriteTranscript("s",
        [
            UserStep(0, "2026-11-23T01:00:00Z", "hello"),
            SystemStep(1, "2026-11-23T01:00:01Z", "ERROR_MESSAGE", "retry"),
            PlannerStep(2, "2026-11-23T01:00:02Z", content: null, model: "gemini-4-neon-xhigh", toolCalls: [ToolCall("run_command", new() { ["CommandLine"] = "ls" })]),
            ToolOutputStep(3, "2026-11-23T01:00:03Z", "file.txt"),
            SystemStep(4, "2026-11-23T01:00:04Z", "SYSTEM_MESSAGE", "reminder"),
            PlannerStep(5, "2026-11-23T01:00:05Z", content: "all good", model: "MODEL_GOOGLE_GEMINI_INTERNAL_BYOM"),
        ]);

        var session = Assert.Single(await _source.ListAsync(_account, _project));

        Assert.Equal(2, session.MessageCount);
        Assert.Equal("gemini-4-neon-xhigh", session.Model);
    }

    [Fact]
    public async Task Unchanged_transcript_hits_cache_and_growing_transcript_is_rescanned()
    {
        AddSummary("s", title: "Work");
        var path = WriteTranscript("s", [UserStep(0, "2026-11-23T01:00:00Z", "one")]);

        var first = Assert.Single(await _source.ListAsync(_account, _project));
        var second = Assert.Single(await _source.ListAsync(_account, _project));
        Assert.Equal(1, _source.FullScans);
        Assert.Equal(first, second);

        File.AppendAllText(path, PlannerStep(1, "2026-11-23T01:05:00Z", "two") + "\n");
        var third = Assert.Single(await _source.ListAsync(_account, _project));
        Assert.Equal(2, _source.FullScans);
        Assert.Equal(2, third.MessageCount);
    }

    [Fact]
    public async Task Missing_database_lists_nothing_and_cancelled_listing_throws()
    {
        Assert.Empty(await _source.ListAsync(_account, _project));

        AddSummary("s", title: "Work");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _source.ListAsync(_account, _project, cts.Token));
    }

    [Fact]
    public async Task Read_extracts_user_assistant_tool_calls_outputs_and_changed_files()
    {
        var editedFile = Path.Combine(_project, "Program.cs");
        AddSummary("s", title: "Fix bug");
        WriteTranscript("s",
        [
            UserStep(0, "2026-11-23T01:00:00Z", "fix the bug"),
            PlannerStep(1, "2026-11-23T01:00:01Z", content: "Let me inspect it.", toolCalls:
            [
                ToolCall("run_command", new() { ["CommandLine"] = "git status\n--short" }),
                ToolCall("edit_file", new() { ["Path"] = editedFile, ["Target"] = "a", ["Replacement"] = "b" }),
            ]),
            ToolOutputStep(2, "2026-11-23T01:00:02Z", "Created At: 2026-11-23T09:00:02+08:00\nCompleted At: 2026-11-23T09:00:03+08:00\nM Program.cs"),
            ToolOutputStep(3, "2026-11-23T01:00:03Z", "Created At: 2026-11-23T09:00:03+08:00\nCompleted At: 2026-11-23T09:00:03+08:00\nMade 1 replacement"),
            SystemStep(4, "2026-11-23T01:00:04Z"),
            PlannerStep(5, "2026-11-23T01:00:05Z", content: "Fixed."),
        ]);
        var session = Assert.Single(await _source.ListAsync(_account, _project));

        var transcript = await _source.ReadAsync(session);

        Assert.Equal(session, transcript.Session);
        var turns = transcript.Turns;
        Assert.Equal(
            [SessionTurnRole.User, SessionTurnRole.Assistant, SessionTurnRole.Tool, SessionTurnRole.Tool, SessionTurnRole.Assistant],
            turns.Select(t => t.Role).ToArray());
        Assert.Equal("fix the bug", turns[0].Text);
        Assert.Equal("Let me inspect it.", turns[1].Text);
        Assert.Equal("run_command", turns[2].ToolName);
        Assert.Equal("git status --short", turns[2].Text);
        Assert.Equal("M Program.cs", turns[2].Output);
        Assert.Null(turns[2].Files);
        Assert.Equal("edit_file", turns[3].ToolName);
        Assert.Equal(editedFile, turns[3].Text);
        Assert.Equal("Made 1 replacement", turns[3].Output);
        Assert.Equal([editedFile], turns[3].Files);
        Assert.Equal("Fixed.", turns[4].Text);
    }

    [Fact]
    public async Task Read_of_a_vanished_file_throws_io()
    {
        AddSummary("s", title: "Work");
        var path = WriteTranscript("s", [UserStep(0, "2026-11-23T01:00:00Z", "go")]);
        var session = Assert.Single(await _source.ListAsync(_account, _project));
        File.Delete(path);

        await Assert.ThrowsAnyAsync<IOException>(() => _source.ReadAsync(session));
    }

    [Fact]
    public async Task AgentSessionService_lists_and_migrates_single_account_agy_sessions()
    {
        AddSummary("conv-1", title: "Agy task");
        WriteTranscript("conv-1", [UserStep(0, "2026-11-23T01:00:00Z", "hello"), PlannerStep(1, "2026-11-23T01:00:05Z", "hi")]);

        var service = new AgentSessionService(new EmptyAccounts(), [_source], [new HandoffSessionConverter()], _home);
        var agy = Builtins.Get("agy");
        var claude = Builtins.Get("claude");

        Assert.True(service.CanRead("agy"));
        var group = await service.ListAsync(agy, _project);
        Assert.True(group.IsSupported);
        var account = Assert.Single(group.Accounts);
        Assert.True(account.IsDefault);
        var session = Assert.Single(account.Sessions);
        Assert.Equal("conv-1", session.SessionId);
        Assert.Null(service.AccountEnvironment(agy, account.Directory, null));
        Assert.Equal([SessionMigrationKind.Handoff], service.MigrationKinds("agy", claude));
        Assert.Equal([SessionMigrationKind.Handoff], service.MigrationKinds("claude", agy));
    }

    private sealed class EmptyAccounts : IAgentAccountDiscovery
    {
        public Task<IReadOnlyList<AgentAccount>> DiscoverAsync(string agentId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentAccount>>([]);
    }
}
