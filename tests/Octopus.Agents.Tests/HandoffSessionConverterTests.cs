using Octopus.Agents.Sessions;

namespace Octopus.Agents.Tests;

/// <summary>A session reaches another agent as a handoff note that fits a command line (design/ui/会话管理方案.html).</summary>
public sealed class HandoffSessionConverterTests : IDisposable
{
    private readonly TempDir _root = new();
    private readonly string _project;
    private readonly HandoffSessionConverter _converter = new(new FixedTime(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero)));

    public HandoffSessionConverterTests()
    {
        _project = Path.Combine(_root.Path, "proj");
        Directory.CreateDirectory(_project);
    }

    public void Dispose() => _root.Dispose();

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private SessionTranscript Transcript(params SessionTurn[] turns) => new(
        new AgentSessionInfo("claude", Path.Combine(_root.Path, "acct"), "sess/1", "Fix the bug", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1), turns.Length, null, null, "x.jsonl"),
        turns);

    private static SessionTurn U(string text) => new(SessionTurnRole.User, text);
    private static SessionTurn A(string text) => new(SessionTurnRole.Assistant, text);
    private static SessionTurn T(string name, string text, string? output = null, params string[] files) => new(SessionTurnRole.Tool, text, ToolName: name, Output: output, Files: files);

    private SessionMigrationRequest Request(string? language = null, bool tools = true, int? recent = null, string? dir = null) =>
        new("codex", null, _project, tools, recent, dir, language);

    private async Task<SessionMigrationResult> Run(SessionTranscript t, SessionMigrationRequest r) => await _converter.ConvertAsync(t, r);

    [Fact]
    public void CanConvertAnything() => Assert.True(_converter.CanConvert("claude", "codex"));

    [Fact]
    public async Task HeadingsFollowLanguage()
    {
        var t = Transcript(U("hello"), A("done"));
        var en = (await Run(t, Request())).Prompt!;
        Assert.Contains("## Goal", en);
        Assert.Contains("## Last state", en);
        var zh = (await Run(t, Request("zh-CN"))).Prompt!;
        Assert.Contains("## 目标", zh);
        Assert.Contains("## 最后状态", zh);
        Assert.DoesNotContain("## Goal", zh);
    }

    [Fact]
    public async Task InstructionNamesTheSourceSession()
    {
        var prompt = (await Run(Transcript(U("hello"), A("ok")), Request())).Prompt!;
        Assert.Contains("`claude`", prompt);
        Assert.Contains("Fix the bug", prompt);
        Assert.Contains("sess/1", prompt);
        Assert.Contains("Do NOT redo", prompt);
    }

    [Fact]
    public async Task GoalIsTheFirstPromptInFull()
    {
        var first = "First line\nsecond line of the goal";
        var prompt = (await Run(Transcript(U("  "), U(first), A("ok"), U("later")), Request())).Prompt!;
        var goal = prompt[prompt.IndexOf("## Goal", StringComparison.Ordinal)..prompt.IndexOf("## Conversation", StringComparison.Ordinal)];
        Assert.Contains("> First line", goal);
        Assert.Contains("> second line of the goal", goal);
        Assert.DoesNotContain("later", goal);
    }

    [Fact]
    public async Task FilesAreRelativeSortedAndDeduplicated()
    {
        var a = Path.Combine(_project, "src", "b.cs");
        var b = Path.Combine(_project, "a.cs");
        var outside = Path.Combine(_root.Path, "elsewhere", "c.cs");
        var prompt = (await Run(Transcript(U("go"), T("Edit", "b", null, a, b), T("Write", "a", null, b, outside), A("ok")), Request())).Prompt!;
        var files = prompt[prompt.IndexOf("## Files changed", StringComparison.Ordinal)..prompt.IndexOf("## Conversation", StringComparison.Ordinal)];
        Assert.Contains("- `a.cs`", files);
        Assert.Contains("- `src/b.cs`", files);
        Assert.Equal(1, files.Split("`a.cs`").Length - 1);
        Assert.True(files.IndexOf("`a.cs`", StringComparison.Ordinal) < files.IndexOf("`src/b.cs`", StringComparison.Ordinal));
        Assert.DoesNotContain(_project, files);
    }

    [Fact]
    public async Task RecentTurnsOmitsEarlierOnesButKeepsGoal()
    {
        var t = Transcript(U("the goal"), A("a1"), U("second"), A("a2"), U("third"), A("a3"));
        var prompt = (await Run(t, Request(recent: 1))).Prompt!;
        Assert.Contains("4 earlier turns omitted", prompt);
        Assert.Contains("> the goal", prompt);
        Assert.Contains("> third", prompt);
        Assert.DoesNotContain("> second", prompt);
        Assert.DoesNotContain("> a1", prompt);
    }

    [Fact]
    public async Task ToolTurnsAreOneLinersAndOutputOnlyWhenRequested()
    {
        var t = Transcript(U("go"), T("Bash", "ls -la\nfoo", "OUTPUT-MARKER"), A("ok"));
        var with = (await Run(t, Request(tools: true))).Prompt!;
        Assert.Contains("- Bash: ls -la foo", with);
        Assert.Contains("OUTPUT-MARKER", with);
        var without = (await Run(t, Request(tools: false))).Prompt!;
        Assert.Contains("- Bash: ls -la foo", without);
        Assert.DoesNotContain("OUTPUT-MARKER", without);
    }

    [Fact]
    public async Task OnlyTheLastToolCallsQuoteOutput()
    {
        var turns = new List<SessionTurn> { U("go") };
        for (var i = 0; i < 15; i++) turns.Add(T("Bash", $"cmd{i}", $"OUT{i}END"));
        turns.Add(A("ok"));
        var prompt = (await Run(Transcript([.. turns]), Request())).Prompt!;
        Assert.DoesNotContain("OUT4END", prompt);
        Assert.Contains("OUT5END", prompt);
        Assert.Contains("OUT14END", prompt);
    }

    [Fact]
    public async Task LastStateHasFinalAssistantMessageAndFlagsUnanswered()
    {
        var answered = (await Run(Transcript(U("go"), A("first"), A("the final words")), Request())).Prompt!;
        Assert.Contains("the final words", answered[answered.IndexOf("## Last state", StringComparison.Ordinal)..]);
        Assert.DoesNotContain("not been answered", answered);
        var open = (await Run(Transcript(U("go"), A("reply"), U("and now?")), Request())).Prompt!;
        Assert.Contains("has not been answered", open);
    }

    [Fact]
    public async Task HugeTranscriptKeepsPromptWithinLimitAndNoteHasEverything()
    {
        var dir = Path.Combine(_root.Path, "notes");
        var turns = new List<SessionTurn> { U("the goal " + new string('g', 20_000)) };
        for (var i = 0; i < 300; i++)
        {
            turns.Add(U($"question {i} " + new string('q', 500)));
            turns.Add(A($"answer {i} " + new string('a', 500)));
            turns.Add(T("Bash", $"cmd{i}", new string('o', 10_000)));
        }
        turns.Add(A("FINAL " + new string('f', 20_000)));
        var result = await Run(Transcript([.. turns]), Request(dir: dir));
        Assert.True(result.Prompt!.Length <= 12_000);
        Assert.Contains("## Last state", result.Prompt);
        Assert.Contains("FINAL", result.Prompt);
        Assert.Contains("> the goal", result.Prompt);
        Assert.Contains("earlier turns omitted", result.Prompt);
        Assert.Contains(result.NotePath!, result.Prompt);

        var note = await File.ReadAllTextAsync(result.NotePath!);
        Assert.Contains("question 0 ", note);
        Assert.Contains("answer 299 ", note);
        Assert.Contains(new string('f', 20_000), note);
        Assert.DoesNotContain(new string('o', 4097), note);
        Assert.Contains(new string('o', 4096), note);
        Assert.False(note.StartsWith('﻿'));
    }

    [Fact]
    public async Task NoteFileNameAndLocation()
    {
        var dir = Path.Combine(_root.Path, "deep", "notes");
        var result = await Run(Transcript(U("go"), A("ok")), Request(dir: dir));
        Assert.Equal(Path.Combine(dir, "20260304-050607-sess_1.md"), result.NotePath);
        Assert.True(File.Exists(result.NotePath));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp-*"));
    }

    [Fact]
    public async Task NoNotePathWithoutDirectory()
    {
        var result = await Run(Transcript(U("go"), A("ok")), Request());
        Assert.Null(result.NotePath);
        Assert.Equal(SessionMigrationKind.Handoff, result.Kind);
    }

    [Theory]
    [InlineData("-rf do it")]
    [InlineData("/clear")]
    public async Task PromptNeverStartsWithFlagOrSlash(string first)
    {
        foreach (var language in new[] { null, "zh" })
        {
            var prompt = (await Run(Transcript(U(first), A("ok")), Request(language))).Prompt!;
            Assert.DoesNotContain(prompt[0], "-/");
        }
    }
}
