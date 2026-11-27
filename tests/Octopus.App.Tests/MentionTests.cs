using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Octopus.App.AgentGui;
using Octopus.App.Collab;
using Octopus.App.Common.Mentions;
using Octopus.App.Composition;
using Octopus.App.Services;
using Octopus.App.Workspace;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>Inline @mention tokens: the plain-text rules (<see cref="MentionText"/>) and the TextBox behaviour (<see cref="MentionEditing"/>).</summary>
public sealed class MentionTests
{
    private static readonly MentionToken Codex = new("session", "item-codex", "@Codex（重构登录）", "codex");
    private static readonly MentionToken Claude = new("agent", "claude", "@Claude Code", "claude");
    private static readonly MentionToken[] Tokens = [Codex, Claude];

    private const string Sentence = "请 @Codex（重构登录） 和 @Claude Code 帮我处理";

    [Fact]
    public void Scan_finds_every_token_in_order_on_word_boundaries()
    {
        var spans = MentionText.Scan(Sentence, Tokens);

        Assert.Equal([Codex, Claude], spans.Select(s => s.Token).ToArray());
        Assert.Equal(Sentence.IndexOf("@Codex", StringComparison.Ordinal), spans[0].Start);
        Assert.Equal(Codex.Text.Length, spans[0].Length);

        // CJK text may touch a token; ASCII word characters may not (e-mails, longer names).
        Assert.Single(MentionText.Scan("让@Claude Code处理", Tokens));
        Assert.Empty(MentionText.Scan("me@Claude Code", Tokens));
        Assert.Empty(MentionText.Scan("@Claude Coder", Tokens));
        Assert.Single(MentionText.Scan("@Claude Code.", Tokens));
    }

    [Fact]
    public void Longer_tokens_win_overlaps()
    {
        var shortToken = new MentionToken("agent", "claude-short", "@Claude");
        var spans = MentionText.Scan("@Claude Code and @Claude", [shortToken, Claude]);

        Assert.Equal(2, spans.Count);
        Assert.Same(Claude, spans[0].Token);
        Assert.Same(shortToken, spans[1].Token);
    }

    [Fact]
    public void Deleting_into_a_token_removes_the_whole_token()
    {
        var spans = MentionText.Scan(Sentence, Tokens);
        var codexEnd = spans[0].End;

        // Backspace on the token's last character, as a soft keyboard's deleteSurroundingText would do it.
        var afterBackspace = Sentence.Remove(codexEnd - 1, 1);
        var repair = MentionText.RepairEdit(Sentence, spans, afterBackspace);

        Assert.NotNull(repair);
        Assert.Equal("请  和 @Claude Code 帮我处理", repair.Value.Text);
        Assert.Equal(spans[0].Start, repair.Value.Caret);
    }

    [Fact]
    public void Replacing_a_selection_that_cuts_a_token_keeps_the_typed_text_and_drops_the_token()
    {
        var spans = MentionText.Scan(Sentence, Tokens);
        var claudeStart = spans[1].Start;
        // Select "和 @Cla" and type "X".
        var selectionStart = Sentence.IndexOf('和');
        var edited = string.Concat(Sentence.AsSpan(0, selectionStart), "X", Sentence.AsSpan(claudeStart + 4));

        var repair = MentionText.RepairEdit(Sentence, spans, edited);

        Assert.NotNull(repair);
        Assert.Equal("请 @Codex（重构登录） X 帮我处理", repair.Value.Text);
        Assert.Equal(selectionStart + 1, repair.Value.Caret);
    }

    [Fact]
    public void Edits_that_leave_tokens_whole_are_not_touched()
    {
        var spans = MentionText.Scan(Sentence, Tokens);

        Assert.Null(MentionText.RepairEdit(Sentence, spans, Sentence + "吧"));                   // typing at the end
        Assert.Null(MentionText.RepairEdit(Sentence, spans, Sentence.Remove(spans[0].End, 1)));   // the space after a token
        Assert.Null(MentionText.RepairEdit(Sentence, spans, Sentence.Remove(spans[0].Start, spans[0].Length))); // whole token
        Assert.Null(MentionText.RepairEdit(Sentence, spans, Sentence.Insert(0, "嗨，")));           // before every token
    }

    [Fact]
    public void Caret_inside_a_token_snaps_to_an_edge()
    {
        var spans = MentionText.Scan(Sentence, Tokens);
        var codex = spans[0];

        Assert.Equal(codex.End, MentionText.SnapCaret(codex.Start + 1, codex.Start, spans));    // → from the start
        Assert.Equal(codex.Start, MentionText.SnapCaret(codex.End - 1, codex.End, spans));      // ← from the end
        Assert.Equal(codex.Start, MentionText.SnapCaret(codex.Start + 2, 0, spans));            // click near the start
        Assert.Equal(codex.End, MentionText.SnapCaret(codex.End - 2, Sentence.Length, spans));  // tap near the end
        Assert.Equal(codex.End, MentionText.SnapCaret(codex.End, 0, spans));                    // edges stay
    }

    [Fact]
    public void Query_is_the_at_word_before_the_caret()
    {
        var spans = MentionText.Scan(Sentence, Tokens);

        Assert.Equal(new MentionQuery(0, 3, "co"), MentionText.FindQuery("@co 后面", 3, []));
        Assert.Equal("cl", MentionText.FindQuery("请让@cl", 5, [])?.Query);           // CJK right before @
        Assert.Null(MentionText.FindQuery("mail me@host", 12, []));                 // e-mail
        Assert.Null(MentionText.FindQuery("@co x", 5, []));                         // a space ended it
        Assert.Null(MentionText.FindQuery(Sentence, spans[0].Start + 3, spans));    // inside an existing token
        Assert.Equal("", MentionText.FindQuery(Sentence + " @", Sentence.Length + 2, spans)?.Query);
    }

    [Fact]
    public void Labels_are_unique_per_target_and_reused_for_the_same_target()
    {
        List<MentionToken> existing = [Codex];

        Assert.Equal(Codex.Text, MentionText.BuildLabel("Codex", "whatever", "session", Codex.TargetId, existing));
        Assert.Equal("@Codex（重构登录）·2", MentionText.BuildLabel("Codex", "重构登录", "session", "item-other", existing));
        Assert.Equal("@Claude Code", MentionText.BuildLabel(" Claude  Code ", "Claude Code", "session", "x", existing));
        Assert.Equal("@Codex（一个非常非常非常长的会…）", MentionText.BuildLabel("Codex", "一个非常非常非常长的会话标题", "session", "y", existing));
    }

    [Fact]
    public void Inserting_a_token_replaces_the_query_and_spaces_it_from_words()
    {
        var inserted = MentionText.InsertToken("让 @co 处理", 2, 5, "@Codex");
        Assert.Equal("让 @Codex 处理", inserted.Text);
        Assert.Equal("让 @Codex ".Length, inserted.Caret);

        var glued = MentionText.InsertToken("ask", 3, 3, "@Codex");
        Assert.Equal("ask @Codex ", glued.Text);
        Assert.Equal(glued.Text.Length, glued.Caret);
    }

    [Fact]
    public void Replayed_turn_hides_the_mention_block_and_keeps_the_mentions_as_chips()
    {
        const string replayed = Sentence + """

            <octopus-mentions>
              <session order="1" id="item-codex" label="@Codex（重构登录）" agent="codex" surface="gui" status="idle" />
              <agent order="2" id="claude" label="@Claude Code" action="delegate_or_spawn" />
            </octopus-mentions>
            """;

        var turn = new UserTurnItem(replayed, onReuse: _ => { });

        Assert.Equal(Sentence, turn.PromptText);
        Assert.Equal(Sentence, turn.DisplayText);
        Assert.Equal(["item-codex", "claude"], turn.Attachments.Select(c => c.Attachment.Path).ToArray());
        Assert.Equal("@Claude Code", turn.Attachments[1].Attachment.DisplayName);
    }

    [AvaloniaFact]
    public void Composer_shows_inline_tokens_and_repairs_soft_keyboard_deletes()
    {
        var dir = Directory.CreateTempSubdirectory("octopus-mentions-").FullName;
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(dir);
        var lead = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Lead", Surface = AgentSurface.Gui };
        var peer = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "codex", Title = "重构登录", Surface = AgentSurface.Gui };
        projects.AddItem(project, lead);
        projects.AddItem(project, peer, activate: false);
        AppHost.Get<ICollabCoordinator>().Server.Start();

        var view = AppHost.Get<ITabContentRegistry>().GetOrCreate(project, lead);
        var vm = Assert.IsType<AgentGuiTabViewModel>(view.DataContext);
        var window = new Window { Width = 900, Height = 640, Content = view };
        window.Show();
        try
        {
            Frames.Pump();
            var box = view.GetVisualDescendants().OfType<TextBox>().Single(b => b.Name == "ComposerInput");
            Assert.Single(box.GetVisualDescendants().OfType<MentionHighlightLayer>());

            // Two targets in one sentence, both picked from the @ list.
            vm.InputText = "请 @重构";
            vm.ComposerCaretIndex = vm.InputText.Length;
            Frames.Pump();
            vm.InsertFileMention(vm.FilteredFiles.First(m => m.IsSession && m.TargetId == peer.Id));
            vm.InputText += "和 @claude";
            vm.ComposerCaretIndex = vm.InputText.Length;
            Frames.Pump();
            vm.InsertFileMention(vm.FilteredFiles.First(m => m.IsAgent && m.TargetId == "claude"));
            vm.InputText += "帮我处理";
            Frames.Pump();

            Assert.Equal(vm.InputText, box.Text);
            var spans = MentionEditing.GetSpans(box);
            Assert.Equal(2, spans.Count);
            Assert.Equal(peer.Id, spans[0].Token.TargetId);
            Assert.Equal("claude", spans[1].Token.TargetId);

            // A delete that arrives as a text change (no key event), as Android / iOS keyboards send it.
            var before = box.Text!;
            box.Text = before.Remove(spans[0].End - 1, 1);
            Frames.Pump();

            Assert.Equal(before.Remove(spans[0].Start, spans[0].Length), box.Text);
            Assert.Equal(box.Text, vm.InputText);
            Assert.Equal(spans[0].Start, box.CaretIndex);
            Assert.False(vm.HasSessionMention);
            Assert.Single(MentionEditing.GetSpans(box));

            // A caret placed inside the remaining token moves to its nearer edge.
            var claude = MentionEditing.GetSpans(box)[0];
            box.CaretIndex = claude.End - 2;
            Frames.Pump();
            Assert.Equal(claude.End, box.CaretIndex);
        }
        finally
        {
            window.Close();
        }
    }
}
