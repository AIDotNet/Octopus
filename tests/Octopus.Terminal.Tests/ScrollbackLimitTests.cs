using System.Text;
using TKCode.Terminal.Core;
using Xunit;

namespace Octopus.Terminal.Tests;

/// <summary>The byte limit of the scrollback is what bounds a full-screen program's terminal, not the line limit.</summary>
public class ScrollbackLimitTests(ITestOutputHelper output)
{
    /// <summary>Styled, wide lines like an agent's TUI redraws all the time.</summary>
    private static byte[] Styled(int lines)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < lines; i++)
        {
            sb.Append($"\u001b[38;5;{i % 200}m\u001b[1m{i:D6}\u001b[0m \u001b[48;5;{(i * 7) % 200}m");
            sb.Append(new string((char)('a' + i % 26), 150));
            sb.Append("\u001b[0m\r\n");
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static int LinesKept(long maxBytes)
    {
        using var core = TerminalCore.Create(new TerminalCoreOptions(new TerminalSize(200, 50, 0, 0))
        {
            ScrollbackMaxLines = 100_000,
            ScrollbackMaxBytes = maxBytes,
        });
        for (var batch = 0; batch < 20; batch++) core.Write(Styled(5_000));
        return core.FormatPlainText().Count(c => c == '\n');
    }

    [Fact]
    public void The_byte_limit_bounds_the_scrollback_before_the_line_limit_does()
    {
        const long old = 16L * 1024 * 1024;
        const long current = 8L * 1024 * 1024;
        var before = LinesKept(old);
        var after = LinesKept(current);
        output.WriteLine($"16 MB: {before} lines kept; 8 MB: {after} lines kept");
        Assert.True(before < 100_000, "the byte limit binds first");
        Assert.True(after * 3 < before * 2, "half the bytes keep clearly fewer lines");
        // Still enough to read back (the worst case here is 200 columns, every cell styled).
        Assert.True(after > 3_000);
    }
}
