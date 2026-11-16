using Avalonia.Headless.XUnit;
using Octopus.Mobile.Terminals;
using Octopus.Mobile.Workspace;
using TKCode.Terminal.Core;
using Xunit;

namespace Octopus.Mobile.Tests;

/// <summary>The terminal page: who decides the size, the two ways to type, and the buttons that answer an agent.</summary>
public class TerminalPageTests
{
    private static (TerminalViewModel ViewModel, RemoteSession Session) Open(Phone phone, int project, int session)
    {
        var target = phone.Workspace.Hosts[0].Projects[project].Sessions[session];
        phone.Shell.ShowHost(target.Project.Host);
        phone.Shell.ShowSession(target);
        // The terminal starts once it has a size, then shows the session's screen.
        Assert.True(Phone.Until(() => phone.Top<TerminalPage>().ReadScreenText().Trim().Length > 0));
        return ((TerminalViewModel)phone.Top<TerminalPage>().DataContext!, target);
    }

    [AvaloniaFact]
    public void A_session_opens_for_looking_only_and_fitting_takes_the_size()
    {
        using var phone = new Phone();
        var (terminal, session) = Open(phone, 0, 0);

        Assert.Equal(TerminalInputMode.Viewing, terminal.Mode);
        Assert.Equal("132×40 · 尺寸由 MacBook Pro 决定", terminal.SizeText);
        Assert.False(terminal.HasQuickReplies);

        terminal.FitCommand.Execute(null);
        Phone.Pump();
        // An agent gets the composer; the size is now this phone's grid.
        Assert.Equal(TerminalInputMode.Composing, terminal.Mode);
        Assert.Null(session.SizeOwner);
        var grid = phone.Top<TerminalPage>().GridSize!.Value;
        Assert.Equal((session.Columns, session.Rows), grid);
        Assert.True(grid.Columns is > 20 and < 100);
        Assert.Equal($"{grid.Columns}×{grid.Rows} · 已适应手机", terminal.SizeText);

        terminal.ReleaseCommand.Execute(null);
        Assert.Equal(TerminalInputMode.Viewing, terminal.Mode);
        Assert.Equal("MacBook Pro", session.SizeOwner);
    }

    [AvaloniaFact]
    public void The_choices_of_a_waiting_agent_become_buttons_and_one_answers_it()
    {
        using var phone = new Phone();
        var (terminal, session) = Open(phone, 0, 0);
        terminal.FitCommand.Execute(null);
        terminal.RefreshQuickReplies();

        Assert.True(terminal.HasQuickReplies);
        Assert.Equal(["1. 是", "2. 是，本次会话不再询问", "3. 否，告诉它怎么改", "Esc"], terminal.QuickReplies.Select(reply => reply.Label));

        terminal.ReplyCommand.Execute(terminal.QuickReplies[0]);
        // The made-up agent goes on working: nothing left to answer.
        Assert.True(Phone.Until(() => session.Activity == SessionActivity.Working));
        Assert.Equal("1", phone.Workspace.ConnectionOf(session)!.Received);
        Assert.False(terminal.HasQuickReplies);
        Assert.Empty(terminal.QuickReplies);
    }

    [AvaloniaFact]
    public void The_composer_sends_the_whole_line_with_enter()
    {
        using var phone = new Phone();
        var (terminal, session) = Open(phone, 0, 1);
        terminal.FitCommand.Execute(null);

        terminal.ComposeText = "先别动 TerminalControl";
        terminal.SendCommand.Execute(null);

        Assert.True(Phone.Until(() => phone.Workspace.ConnectionOf(session)!.Received.EndsWith('\r')));
        Assert.Equal("先别动 TerminalControl\r", phone.Workspace.ConnectionOf(session)!.Received);
        Assert.Equal("", terminal.ComposeText);
    }

    [AvaloniaFact]
    public void A_terminal_gets_the_key_row_and_ctrl_is_sticky()
    {
        using var phone = new Phone();
        // The third session of the first project is a shell.
        var (terminal, session) = Open(phone, 0, 2);
        terminal.FitCommand.Execute(null);
        Assert.Equal(TerminalInputMode.Direct, terminal.Mode);
        var connection = phone.Workspace.ConnectionOf(session)!;
        KeyCapViewModel Key(string label) => terminal.Keys.Single(key => key.Label == label);

        Key("Tab").PressCommand.Execute(null);
        Key("↑").PressCommand.Execute(null);
        Key("~").PressCommand.Execute(null);
        Assert.True(Phone.Until(() => connection.Received.EndsWith('~')));
        Assert.Equal("\t\u001b[A~", connection.Received);

        // Ctrl waits for the next key, applies to it, and lets go.
        Key("Ctrl").PressCommand.Execute(null);
        Assert.True(Key("Ctrl").IsArmed);
        Assert.True(terminal.TryTakeTyped("c"));
        Assert.False(Key("Ctrl").IsArmed);
        Assert.True(Phone.Until(() => connection.Received.EndsWith('\u0003')));
        // Unarmed, typed text is the terminal's own business.
        Assert.False(terminal.TryTakeTyped("c"));

        // Back to the composer and to the keys again.
        terminal.ToggleInputCommand.Execute(null);
        Assert.Equal(TerminalInputMode.Composing, terminal.Mode);
        terminal.ToggleInputCommand.Execute(null);
        Assert.Equal(TerminalInputMode.Direct, terminal.Mode);
    }
}

/// <summary>Reading an agent's numbered choices from the text of the screen.</summary>
public class QuickRepliesTests
{
    [Fact]
    public void A_numbered_question_at_the_end_of_the_screen_is_read()
    {
        var replies = QuickReplies.Parse("""
            ● Bash(dotnet test)

             Do you want to proceed?
             ❯ 1. Yes
               2. Yes, and don't ask again for dotnet commands
               3. No, and tell Claude what to do differently (esc)
            """);

        Assert.Equal(["1. Yes", "2. Yes, and don't ask again for dotnet commands", "3. No, and tell Claude what to do differently"], replies.Select(reply => reply.Label));
        Assert.Equal(["1", "2", "3"], replies.Select(reply => reply.Keys));
    }

    [Theory]
    [InlineData("1. only one entry")]
    [InlineData("plain output\nmore output")]
    [InlineData(" 2. starts at two\n 3. and goes on")]
    [InlineData("")]
    public void Text_that_is_not_a_question_gives_nothing(string screen) => Assert.Empty(QuickReplies.Parse(screen));

    [Fact]
    public void Only_the_last_list_counts_and_long_labels_are_cut()
    {
        var replies = QuickReplies.Parse(" 1. old\n 2. old too\nsome output\n > 1. Proceed with the whole migration of every table in the database now\n   2. Stop");

        Assert.Equal(["1. Proceed with the whole migration of every ta…", "2. Stop"], replies.Select(reply => reply.Label));
    }

    [Fact]
    public void A_question_scrolled_far_up_is_not_offered()
    {
        var screen = " 1. Yes\n 2. No\n" + string.Join("\n", Enumerable.Repeat("output", 30));

        Assert.Empty(QuickReplies.Parse(screen));
    }
}
