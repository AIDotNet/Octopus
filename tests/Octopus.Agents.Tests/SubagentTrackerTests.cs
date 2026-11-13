using System.Text.Json;
using Octopus.Agents.Subagents;
using Xunit;

namespace Octopus.Agents.Tests;

/// <summary>Payload shapes as Claude Code 2.1.323 sent them (captured with a dump hook, trimmed).</summary>
public class SubagentTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 13, 8, 0, 0, TimeSpan.Zero);
    private const string Transcript = "/home/u/.claude/projects/-w/20f97ccc.jsonl";

    private static string Launch(string description, string toolUseId, string type = "Explore", bool background = false) =>
        JsonSerializer.Serialize(new
        {
            hook_event_name = "PreToolUse",
            tool_name = "Agent",
            tool_input = new { description, prompt = "Read the file…", subagent_type = type, run_in_background = background },
            tool_use_id = toolUseId,
            transcript_path = Transcript,
        });

    private static string Start(string id, string type = "Explore") =>
        JsonSerializer.Serialize(new { hook_event_name = "SubagentStart", agent_id = id, agent_type = type, transcript_path = Transcript });

    private static string Stop(string id) =>
        JsonSerializer.Serialize(new { hook_event_name = "SubagentStop", agent_id = id, agent_type = "Explore", stop_hook_active = false });

    [Fact]
    public void Two_parallel_subagents_get_their_descriptions_and_finish()
    {
        var tracker = new SubagentTracker();
        Assert.False(tracker.Apply("subagent-launch", Launch("Read file a", "toolu_a"), T0));
        Assert.True(tracker.Apply("subagent-start", Start("ac26a7257da0fe8ee"), T0));
        tracker.Apply("subagent-launch", Launch("Read file b", "toolu_b"), T0);
        tracker.Apply("subagent-start", Start("a44e9a62206ef73c5"), T0.AddSeconds(1));

        Assert.Equal(["Read file a", "Read file b"], tracker.Runs.Select(r => r.Description));
        Assert.Equal(["toolu_a", "toolu_b"], tracker.Runs.Select(r => r.ToolUseId));
        Assert.Equal("/home/u/.claude/projects/-w/20f97ccc/subagents/agent-ac26a7257da0fe8ee.jsonl".Replace('/', Path.DirectorySeparatorChar),
            tracker.Runs[0].TranscriptPath!.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(tracker.HasRunning);

        Assert.True(tracker.Apply("subagent-stop", Stop("ac26a7257da0fe8ee"), T0.AddSeconds(4)));
        Assert.Equal(SubagentState.Done, tracker.Runs[0].State);
        Assert.Equal(T0.AddSeconds(4), tracker.Runs[0].EndedAt);
        Assert.Equal(SubagentState.Running, tracker.Runs[1].State);
        // The same stop again is no news.
        Assert.False(tracker.Apply("subagent-stop", Stop("ac26a7257da0fe8ee"), T0.AddSeconds(5)));
    }

    [Fact]
    public void A_start_that_overtakes_its_launch_still_gets_the_description()
    {
        var tracker = new SubagentTracker();
        tracker.Apply("subagent-start", Start("a1", "Plan"), T0);
        Assert.Null(tracker.Runs[0].Description);
        Assert.True(tracker.Apply("subagent-launch", Launch("Design it", "toolu_1", "Plan"), T0));
        Assert.Equal("Design it", tracker.Runs[0].Description);
    }

    [Fact]
    public void A_failed_agent_call_marks_its_run()
    {
        var tracker = new SubagentTracker();
        tracker.Apply("subagent-launch", Launch("Build it", "toolu_x", "general-purpose"), T0);
        tracker.Apply("subagent-start", Start("a1", "general-purpose"), T0);
        Assert.True(tracker.Apply("subagent-failed", """{"tool_name":"Agent","tool_use_id":"toolu_x","error":"boom"}""", T0.AddSeconds(2)));
        Assert.Equal(SubagentState.Failed, tracker.Runs[0].State);
        // A failure before any start only forgets the launch.
        Assert.False(tracker.Apply("subagent-failed", """{"tool_use_id":"toolu_none"}""", T0));
    }

    [Fact]
    public void The_turn_end_interrupts_foreground_runs_and_the_next_prompt_clears_finished_ones()
    {
        var tracker = new SubagentTracker();
        tracker.Apply("subagent-launch", Launch("Foreground", "t1"), T0);
        tracker.Apply("subagent-start", Start("a1"), T0);
        tracker.Apply("subagent-launch", Launch("Background", "t2", background: true), T0);
        tracker.Apply("subagent-start", Start("a2"), T0);

        Assert.True(tracker.Apply("stop", "{}", T0.AddSeconds(3)));
        Assert.Equal(SubagentState.Interrupted, tracker.Runs[0].State);
        Assert.Equal(SubagentState.Running, tracker.Runs[1].State);

        // Its SubagentStop came in after the turn end: it did finish.
        Assert.True(tracker.Apply("subagent-stop", Stop("a1"), T0.AddSeconds(3)));
        Assert.Equal(SubagentState.Done, tracker.Runs[0].State);

        Assert.True(tracker.Apply("prompt-submit", """{"prompt":"next"}""", T0.AddSeconds(10)));
        Assert.Equal(["a2"], tracker.Runs.Select(r => r.Id));

        Assert.True(tracker.Apply("session-end", "{}", T0.AddSeconds(20)));
        Assert.Equal(SubagentState.Interrupted, tracker.Runs[0].State);
    }

    [Fact]
    public void A_compaction_keeps_the_list_and_a_new_session_clears_it()
    {
        var tracker = new SubagentTracker();
        tracker.Apply("subagent-start", Start("a1"), T0);
        Assert.False(tracker.Apply("session-start", """{"source":"compact"}""", T0));
        Assert.Single(tracker.Runs);
        Assert.True(tracker.Apply("session-start", """{"source":"clear"}""", T0));
        Assert.Empty(tracker.Runs);
    }

    [Theory]
    [InlineData("""{"agent_id":"../../etc","agent_type":"Explore"}""")]
    [InlineData("""{"agent_type":"Explore"}""")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void Unusable_starts_are_ignored(string payload)
    {
        var tracker = new SubagentTracker();
        Assert.False(tracker.Apply("subagent-start", payload, T0));
        Assert.Empty(tracker.Runs);
    }

    [Fact]
    public void Other_channels_change_nothing()
    {
        var tracker = new SubagentTracker();
        tracker.Apply("subagent-start", Start("a1"), T0);
        Assert.False(tracker.Apply("statusline", "{}", T0));
        Assert.False(tracker.Apply("notification", "{}", T0));
    }
}

public class SubagentTranscriptTests
{
    private static string ToolUse(string name, string inputJson) =>
        $$$"""{"type":"assistant","agentId":"a1","message":{"role":"assistant","content":[{"type":"tool_use","id":"t","name":"{{{name}}}","input":{{{inputJson}}}}]}}""" + "\n";

    [Fact]
    public void Reads_tool_calls_as_they_are_appended_and_only_whole_lines()
    {
        var dir = Directory.CreateTempSubdirectory("octopus-subagent-").FullName;
        var path = Path.Combine(dir, "agent-a1.jsonl");
        File.WriteAllText(path,
            """{"type":"user","message":{"role":"user","content":"Read the file"}}""" + "\n"
            + """{"type":"attachment","attachment":{"type":"deferred_tools_delta"}}""" + "\n"
            + ToolUse("Read", """{"file_path":"/w/src/SignalHub.cs"}"""));

        var first = SubagentTranscript.Read(path, 0, withMeta: false);
        Assert.Equal(1, first.Tools);
        Assert.Equal("Read SignalHub.cs", first.LastAction);
        Assert.Equal(new FileInfo(path).Length, first.ToOffset);

        // Nothing new: no work.
        var idle = SubagentTranscript.Read(path, first.ToOffset, withMeta: false);
        Assert.Equal((0, first.ToOffset), (idle.Tools, idle.ToOffset));

        // A line still being written is left for the next read.
        var half = ToolUse("Bash", """{"command":"dotnet build\nmore"}""");
        File.AppendAllText(path, ToolUse("Grep", """{"pattern":"TemplateEngine"}""") + half[..20]);
        var second = SubagentTranscript.Read(path, first.ToOffset, withMeta: false);
        Assert.Equal(1, second.Tools);
        Assert.Equal("Grep \"TemplateEngine\"", second.LastAction);
        File.AppendAllText(path, half[20..]);
        var third = SubagentTranscript.Read(path, second.ToOffset, withMeta: false);
        Assert.Equal("Bash dotnet build", third.LastAction);
        Assert.Equal(new FileInfo(path).Length, third.ToOffset);
    }

    [Fact]
    public void Reads_the_meta_file_and_tolerates_missing_files()
    {
        var dir = Directory.CreateTempSubdirectory("octopus-subagent-").FullName;
        var path = Path.Combine(dir, "agent-a1.jsonl");
        Assert.Equal(new SubagentProgress(0, 0, 0, null, null), SubagentTranscript.Read(path, 0, withMeta: true));

        File.WriteAllText(Path.Combine(dir, "agent-a1.meta.json"),
            """{"agentType":"Explore","isBuiltIn":true,"description":"Read file b","toolUseId":"toolu_01X","spawnDepth":1,"requestShape":"foreground","finished":true}""");
        var meta = SubagentTranscript.Read(path, 0, withMeta: true).Meta!;
        Assert.Equal(new SubagentMeta("Read file b", "toolu_01X", false), meta);
    }

    [Fact]
    public void Progress_moves_a_run_once_and_a_final_read_settles_it()
    {
        var dir = Directory.CreateTempSubdirectory("octopus-subagent-").FullName;
        var transcript = Path.Combine(dir, "s.jsonl");
        var tracker = new SubagentTracker();
        tracker.Apply("subagent-start", JsonSerializer.Serialize(new { agent_id = "a1", agent_type = "Explore", transcript_path = transcript }), DateTimeOffset.Now);
        var run = tracker.Runs.Single();
        Directory.CreateDirectory(Path.GetDirectoryName(run.TranscriptPath)!);
        File.WriteAllText(run.TranscriptPath!, ToolUse("Read", """{"file_path":"a.txt"}"""));
        File.WriteAllText(Path.ChangeExtension(run.TranscriptPath!, ".meta.json"), """{"description":"Read file a","toolUseId":"toolu_a"}""");

        var progress = SubagentTranscript.Read(run.TranscriptPath!, run.TranscriptOffset, !run.MetaRead);
        Assert.True(tracker.ApplyProgress(run, progress));
        Assert.Equal((1, "Read a.txt", "Read file a"), (run.ToolCount, run.LastAction, run.Description));
        // The same read again is stale.
        Assert.False(tracker.ApplyProgress(run, progress));
        Assert.True(run.NeedsRead);

        tracker.Apply("subagent-stop", JsonSerializer.Serialize(new { agent_id = "a1" }), DateTimeOffset.Now);
        tracker.ApplyProgress(run, SubagentTranscript.Read(run.TranscriptPath!, run.TranscriptOffset, false), final: true);
        Assert.False(run.NeedsRead);
    }

    [Theory]
    [InlineData("Read", """{"file_path":"C:\\w\\Program.cs"}""", "Read Program.cs")]
    [InlineData("Glob", """{"pattern":"**/*.axaml"}""", "Glob \"**/*.axaml\"")]
    [InlineData("WebFetch", """{"url":"https://example.com/a/b"}""", "WebFetch example.com")]
    [InlineData("mcp__x__y", """{}""", "mcp__x__y")]
    public void Tool_calls_are_described_in_a_few_words(string tool, string input, string expected)
    {
        using var doc = JsonDocument.Parse(input);
        Assert.Equal(expected, SubagentTranscript.Describe(tool, doc.RootElement));
    }
}
