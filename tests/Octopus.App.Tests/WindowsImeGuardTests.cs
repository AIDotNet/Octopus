using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Octopus.App.Shell;
using Xunit;

namespace Octopus.App.Tests;

public sealed class WindowsImeGuardTests
{
    [Fact]
    public void Shift_commit_suppresses_duplicate_chars_then_allows_next_char()
    {
        var nowMs = 1_000L;
        var filter = new ImeCommitDuplicateFilter(() => nowMs);

        filter.OnImeResultCommitted("mcp");
        filter.OnKeyDown(ImeCommitDuplicateFilter.VkShift);

        nowMs += 5;
        Assert.True(filter.ShouldSuppressChar('m'));
        Assert.True(filter.ShouldSuppressChar('c'));
        Assert.True(filter.ShouldSuppressChar('p'));

        // Once the committed sequence has been drained, subsequent typing passes through.
        nowMs += 1;
        Assert.False(filter.ShouldSuppressChar('m'));
    }

    [Fact]
    public void Genuine_key_down_cancels_suppression_for_same_letter()
    {
        var nowMs = 1_000L;
        var filter = new ImeCommitDuplicateFilter(() => nowMs);

        filter.OnImeResultCommitted("mcp");
        filter.OnKeyDown('M');

        nowMs += 5;
        Assert.False(filter.ShouldSuppressChar('m'));
    }

    [Fact]
    public void Process_key_down_does_not_cancel_suppression()
    {
        var nowMs = 1_000L;
        var filter = new ImeCommitDuplicateFilter(() => nowMs);

        filter.OnImeResultCommitted("mcp");
        filter.OnKeyDown(ImeCommitDuplicateFilter.VkProcessKey);

        nowMs += 10;
        Assert.True(filter.ShouldSuppressChar('m'));
        Assert.True(filter.ShouldSuppressChar('c'));
        Assert.True(filter.ShouldSuppressChar('p'));
        Assert.False(filter.ShouldSuppressChar('m'));
    }

    [Fact]
    public void Modifier_keys_do_not_cancel_suppression()
    {
        var nowMs = 1_000L;
        var filter = new ImeCommitDuplicateFilter(() => nowMs);

        filter.OnImeResultCommitted("ab");
        filter.OnKeyDown(ImeCommitDuplicateFilter.VkLShift);
        filter.OnKeyDown(ImeCommitDuplicateFilter.VkRShift);
        filter.OnKeyDown(ImeCommitDuplicateFilter.VkControl);
        filter.OnKeyDown(ImeCommitDuplicateFilter.VkMenu);

        Assert.True(filter.ShouldSuppressChar('a'));
        Assert.True(filter.ShouldSuppressChar('b'));
        Assert.False(filter.ShouldSuppressChar('a'));
    }

    [Fact]
    public void Expired_window_does_not_suppress_chars()
    {
        var nowMs = 1_000L;
        var filter = new ImeCommitDuplicateFilter(() => nowMs);

        filter.OnImeResultCommitted("mcp");

        nowMs += ImeCommitDuplicateFilter.DefaultWindowMs + 1;
        Assert.False(filter.ShouldSuppressChar('m'));
    }

    [Fact]
    public void Mismatched_char_cancels_remaining_suppression()
    {
        var nowMs = 1_000L;
        var filter = new ImeCommitDuplicateFilter(() => nowMs);

        filter.OnImeResultCommitted("mcp");

        Assert.False(filter.ShouldSuppressChar('x'));
        Assert.False(filter.ShouldSuppressChar('m'));
    }

    [Fact]
    public void Chinese_commit_suppresses_matching_chars_then_allows_next_char()
    {
        var nowMs = 1_000L;
        var filter = new ImeCommitDuplicateFilter(() => nowMs);

        filter.OnImeResultCommitted("你好");

        Assert.True(filter.ShouldSuppressChar('你'));
        Assert.True(filter.ShouldSuppressChar('好'));
        Assert.False(filter.ShouldSuppressChar('你'));
    }

    [Fact]
    public void Empty_or_null_commit_does_not_suppress()
    {
        var nowMs = 1_000L;
        var filter = new ImeCommitDuplicateFilter(() => nowMs);

        filter.OnImeResultCommitted(null);
        filter.OnImeResultCommitted(string.Empty);

        Assert.False(filter.ShouldSuppressChar('a'));
    }

    [AvaloniaFact]
    public void Attach_is_a_safe_noop_on_non_windows()
    {
        var window = new Window();

        WindowsImeGuard.Attach(window);

        window.Show();
        window.Close();
    }
}
