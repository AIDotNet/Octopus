using System.Runtime.CompilerServices;
using System.Text;
using Octopus.Core.Terminals;
using Octopus.Terminal;
using Octopus.Terminal.Avalonia;
using TKCode.Terminal.Avalonia;
using TKCode.Terminal.Core;
using Xunit;

namespace Octopus.Terminal.Tests;

/// <summary>The bytes the router sends to the program for keys users expect to behave like in other terminals.</summary>
public class KeyInputTests
{
    private sealed class Recorder : ITerminalConnection, ITerminalConnectionFactory
    {
        public List<byte> Written { get; } = [];
        private readonly TaskCompletionSource<TerminalExit> _exit = new();
        public int ProcessId => 1;
        public Task<TerminalExit> Exited => _exit.Task;
        public ITerminalConnection Start(LaunchSpec spec, int columns, int rows) => this;
        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            yield break;
        }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken ct = default)
        {
            lock (Written) Written.AddRange(input.ToArray());
            return ValueTask.CompletedTask;
        }
        public ValueTask ResizeAsync(int columns, int rows, int pixelWidth, int pixelHeight) => ValueTask.CompletedTask;
        public ValueTask SignalAsync(TerminalSignal signal) => ValueTask.CompletedTask;
        public ValueTask<string?> GetForegroundProcessNameAsync() => ValueTask.FromResult<string?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public string Text() { lock (Written) return Encoding.UTF8.GetString(Written.ToArray()); }
    }

    private static async Task<string> Press(TerminalKey key, KeyMods mods)
    {
        var rec = new Recorder();
        await using var session = new TerminalSession(
            new LaunchSpec("x", [], null, new Dictionary<string, string>()), rec, new TerminalSize(80, 24, 0, 0),
            TerminalPalettes.Dark, ColorScheme.Dark, 100, () => false);
        session.Start();
        using var router = new TerminalInputRouter(session, () => new MacTerminalKeymap(),
            () => Task.FromResult<string?>(null), () => Task.FromResult<string?>(null), _ => Task.CompletedTask, (_, _) => Task.FromResult(true), () => { });
        router.HandleKey(new TerminalKeyEventArgs(new KeyInput(key, KeyAction.Press, mods), global::Avalonia.Input.KeyModifiers.None));
        for (var i = 0; i < 50 && rec.Written.Count == 0; i++) await Task.Delay(20);
        return rec.Text().Replace("\x1b", "ESC").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\x7f", "DEL");
    }

    [Theory]
    [InlineData(TerminalKey.J, KeyMods.Ctrl, "\\n")]
    [InlineData(TerminalKey.Enter, KeyMods.None, "\\r")]
    [InlineData(TerminalKey.Enter, KeyMods.Shift, "\\n")]
    [InlineData(TerminalKey.Enter, KeyMods.Alt, "ESC\\r")]
    [InlineData(TerminalKey.ArrowLeft, KeyMods.Super, "\u0001")]
    [InlineData(TerminalKey.ArrowRight, KeyMods.Super, "\u0005")]
    [InlineData(TerminalKey.Backspace, KeyMods.Super, "\u0015")]
    [InlineData(TerminalKey.ArrowLeft, KeyMods.Alt, "ESCb")]
    [InlineData(TerminalKey.ArrowRight, KeyMods.Alt, "ESCf")]
    [InlineData(TerminalKey.Backspace, KeyMods.Alt, "ESCDEL")]
    public async Task Sends(TerminalKey key, KeyMods mods, string expected) =>
        Assert.Equal(expected, await Press(key, mods));

    [Fact]
    public void Leaves_full_screen_programs_and_extended_keyboards_alone()
    {
        var shiftEnter = new KeyInput(TerminalKey.Enter, KeyAction.Press, KeyMods.Shift);
        var cmdLeft = new KeyInput(TerminalKey.ArrowLeft, KeyAction.Press, KeyMods.Super);
        Assert.Null(TerminalInputRouter.Translate(cmdLeft, InputModes.Defaults with { AlternateScreen = true }));
        Assert.Null(TerminalInputRouter.Translate(shiftEnter, InputModes.Defaults with { KittyFlags = 1 }));
        // Ctrl+J stays a line feed everywhere.
        var ctrlJ = new KeyInput(TerminalKey.J, KeyAction.Press, KeyMods.Ctrl);
        Assert.Equal("\n", TerminalInputRouter.Translate(ctrlJ, InputModes.Defaults with { AlternateScreen = true }));
    }

    [Theory]
    [InlineData("/tmp/octopus-paste/paste-1.png", "ESC[200~/tmp/octopus-paste/paste-1.png ESC[201~", true)]
    [InlineData(null, "", true)]
    public async Task An_image_only_clipboard_pastes_the_path_of_the_saved_file(string? saved, string expectedWithoutSpaceFix, bool bracketed)
    {
        var rec = new Recorder();
        await using var session = new TerminalSession(
            new LaunchSpec("x", [], null, new Dictionary<string, string>()), rec, new TerminalSize(80, 24, 0, 0),
            TerminalPalettes.Dark, ColorScheme.Dark, 100, () => false);
        session.Start();
        if (bracketed) session.Use(c => c.Write("\u001b[?2004h"u8));
        using var router = new TerminalInputRouter(session, () => new MacTerminalKeymap(),
            () => Task.FromResult<string?>(null), () => Task.FromResult(saved), _ => Task.CompletedTask,
            (_, _) => Task.FromResult(true), () => { });
        await router.PasteAsync();
        await Task.Delay(200);
        var sent = rec.Text().Replace("\x1b", "ESC");
        if (saved is null) Assert.Equal("", sent);
        else Assert.Equal($"ESC[200~{saved}ESC[201~", sent);
    }

    [Fact]
    public void Paths_with_spaces_are_quoted_for_the_shell()
    {
        Assert.Equal("/tmp/a.png", ClipboardImages.Quote("/tmp/a.png"));
        Assert.Equal("'/Users/a b/it'\\''s.png'", ClipboardImages.Quote("/Users/a b/it's.png"));
    }
}
