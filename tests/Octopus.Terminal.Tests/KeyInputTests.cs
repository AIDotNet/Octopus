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
[Collection(TerminalSessionCollection.Name)]
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

    /// <summary>A key of a touch key bar is sent like the same key on a keyboard.</summary>
    [Theory]
    [InlineData(TerminalKey.Escape, KeyMods.None, "ESC")]
    [InlineData(TerminalKey.Tab, KeyMods.None, "\t")]
    [InlineData(TerminalKey.ArrowUp, KeyMods.None, "ESC[A")]
    [InlineData(TerminalKey.C, KeyMods.Ctrl, "\u0003")]
    [InlineData(TerminalKey.J, KeyMods.Ctrl, "\\n")]
    public async Task SendKey_sends(TerminalKey key, KeyMods mods, string expected) =>
        Assert.Equal(expected, await Press(key, mods, viaSendKey: true));

    private static async Task<string> Press(TerminalKey key, KeyMods mods, bool viaSendKey = false)
    {
        var rec = new Recorder();
        await using var session = new TerminalSession(
            new LaunchSpec("x", [], null, new Dictionary<string, string>()), rec, new TerminalSize(80, 24, 0, 0),
            TerminalPalettes.Dark, ColorScheme.Dark, 100, () => false);
        session.Start();
        using var router = new TerminalInputRouter(session, () => new MacTerminalKeymap(),
            () => Task.FromResult<string?>(null), () => Task.FromResult<string?>(null), _ => Task.CompletedTask, (_, _) => Task.FromResult(true), () => { });
        if (viaSendKey) router.SendKey(key, mods);
        else router.HandleKey(new TerminalKeyEventArgs(new KeyInput(key, KeyAction.Press, mods), global::Avalonia.Input.KeyModifiers.None));
        for (var i = 0; i < 50 && rec.Written.Count == 0; i++) await Task.Delay(20, TestContext.Current.CancellationToken);
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

    /// <summary>Holding Ctrl (or Cmd, Shift, Option) while reading the scrollback keeps it there; typing goes back down.</summary>
    [Theory]
    [InlineData(TerminalKey.ControlLeft, 0)]
    [InlineData(TerminalKey.MetaLeft, 0)]
    [InlineData(TerminalKey.ShiftRight, 0)]
    [InlineData(TerminalKey.AltLeft, 0)]
    [InlineData(TerminalKey.ControlLeft, 31)] // extended keyboard: the program still hears the key, the view stays
    public async Task A_modifier_alone_does_not_scroll_to_the_bottom(TerminalKey modifier, byte kittyFlags)
    {
        var rec = new Recorder();
        await using var session = new TerminalSession(
            new LaunchSpec("x", [], null, new Dictionary<string, string>()), rec, new TerminalSize(80, 24, 0, 0),
            TerminalPalettes.Dark, ColorScheme.Dark, 100, () => false);
        session.Start();
        session.Use(c =>
        {
            for (var i = 0; i < 200; i++) c.Write(Encoding.UTF8.GetBytes($"line {i}\r\n"));
            if (kittyFlags != 0) c.Write(Encoding.UTF8.GetBytes($"\u001b[>{kittyFlags}u"));
        });
        using var router = new TerminalInputRouter(session, () => new MacTerminalKeymap(),
            () => Task.FromResult<string?>(null), () => Task.FromResult<string?>(null), _ => Task.CompletedTask, (_, _) => Task.FromResult(true), () => { });
        var bottom = session.Read(c => c.CaptureFrame(force: true)!.Scrollbar.Offset);
        session.Use(c => c.ScrollViewport(ViewportScroll.Lines(-50)));
        var reading = session.Read(c => c.CaptureFrame(force: true)!.Scrollbar.Offset);
        Assert.NotEqual(bottom, reading);

        router.HandleKey(new TerminalKeyEventArgs(new KeyInput(modifier, KeyAction.Press, KeyMods.None), global::Avalonia.Input.KeyModifiers.None));
        Assert.Equal(reading, session.Read(c => c.CaptureFrame(force: true)!.Scrollbar.Offset));

        router.HandleKey(new TerminalKeyEventArgs(new KeyInput(TerminalKey.ArrowDown, KeyAction.Press, KeyMods.None), global::Avalonia.Input.KeyModifiers.None));
        Assert.Equal(bottom, session.Read(c => c.CaptureFrame(force: true)!.Scrollbar.Offset));
    }

    [Theory]
    [InlineData(0, "03")]
    [InlineData(1, "1B-5B-39-39-3B-35-75")] // ESC [ 99 ; 5 u
    [InlineData(31, "1B-5B-39-39-3B-35-75")]
    public void Ctrl_c_reaches_the_program_with_and_without_the_extended_keyboard(byte kittyFlags, string expected)
    {
        using var encoder = new InputEncoder();
        encoder.Apply(InputModes.Defaults with { KittyFlags = kittyFlags });
        var buffer = new byte[64];
        var key = TerminalInputRouter.WithCodepoint(new KeyInput(TerminalKey.C, KeyAction.Press, KeyMods.Ctrl));
        var n = encoder.EncodeKey(key, buffer);
        Assert.Equal(expected, BitConverter.ToString(buffer, 0, n));
    }

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

    [Fact]
    public void Ctrl_c_is_a_copy_shortcut_on_windows_and_linux_but_not_on_macos()
    {
        var ctrlC = new KeyInput(TerminalKey.C, KeyAction.Press, KeyMods.Ctrl);
        Assert.Equal(TerminalShortcut.Copy, new DefaultTerminalKeymap().Match(ctrlC));
        Assert.Equal(TerminalShortcut.Copy, new DefaultTerminalKeymap().Match(new KeyInput(TerminalKey.C, KeyAction.Press, KeyMods.Ctrl | KeyMods.Shift)));
        Assert.Equal(TerminalShortcut.None, new DefaultTerminalKeymap().Match(new KeyInput(TerminalKey.C, KeyAction.Release, KeyMods.Ctrl)));
        Assert.Equal(TerminalShortcut.None, new DefaultTerminalKeymap().Match(new KeyInput(TerminalKey.C, KeyAction.Press, KeyMods.Ctrl | KeyMods.Alt)));
        Assert.Equal(TerminalShortcut.None, new MacTerminalKeymap().Match(ctrlC));
    }

    [Fact]
    public void Paste_shortcuts_include_ctrl_v_on_windows_and_shift_insert_on_windows_and_linux()
    {
        ITerminalKeymap windows = new DefaultTerminalKeymap(pasteWithoutShift: true);
        ITerminalKeymap linux = new DefaultTerminalKeymap(pasteWithoutShift: false);
        ITerminalKeymap mac = new MacTerminalKeymap();

        var ctrlV = new KeyInput(TerminalKey.V, KeyAction.Press, KeyMods.Ctrl);
        var ctrlShiftV = new KeyInput(TerminalKey.V, KeyAction.Press, KeyMods.Ctrl | KeyMods.Shift);
        var shiftInsert = new KeyInput(TerminalKey.Insert, KeyAction.Press, KeyMods.Shift);

        Assert.True(windows.CopyWithoutShift);
        Assert.True(linux.CopyWithoutShift);
        Assert.False(mac.CopyWithoutShift);

        Assert.Equal(TerminalShortcut.Paste, windows.Match(ctrlV));
        Assert.Equal(TerminalShortcut.Paste, windows.Match(ctrlShiftV));
        Assert.Equal(TerminalShortcut.Paste, windows.Match(shiftInsert));
        Assert.Equal(TerminalShortcut.None, windows.Match(new KeyInput(TerminalKey.V, KeyAction.Release, KeyMods.Ctrl)));
        Assert.Equal(TerminalShortcut.None, windows.Match(new KeyInput(TerminalKey.V, KeyAction.Press, KeyMods.Ctrl | KeyMods.Alt)));
        Assert.Equal(TerminalShortcut.None, windows.Match(new KeyInput(TerminalKey.Insert, KeyAction.Release, KeyMods.Shift)));

        Assert.Equal(TerminalShortcut.None, linux.Match(ctrlV));
        Assert.Equal(TerminalShortcut.Paste, linux.Match(ctrlShiftV));
        Assert.Equal(TerminalShortcut.Paste, linux.Match(shiftInsert));

        Assert.Equal(TerminalShortcut.None, mac.Match(ctrlV));
        Assert.Equal(TerminalShortcut.None, mac.Match(shiftInsert));
    }

    [Fact]
    public async Task Right_click_copies_selection_and_pastes_when_nothing_is_selected_on_default_keymap()
    {
        var rec = new Recorder();
        await using var session = new TerminalSession(
            new LaunchSpec("x", [], null, new Dictionary<string, string>()), rec, new TerminalSize(80, 24, 0, 0),
            TerminalPalettes.Dark, ColorScheme.Dark, 100, () => false);
        session.Start();
        session.Use(c => c.Write("hello world\r\n"u8));

        string? copied = null;
        ITerminalKeymap keymap = new DefaultTerminalKeymap(pasteWithoutShift: true);
        using var router = new TerminalInputRouter(session, () => keymap,
            () => Task.FromResult<string?>("pasted text"), () => Task.FromResult<string?>(null),
            text => { copied = text; return Task.CompletedTask; },
            (_, _) => Task.FromResult(true), () => { });

        var geometry = new MouseGeometry(800, 480, 10, 20, 0, 0, 0, 0);
        TerminalPointerEventArgs RightRelease() => new(
            TerminalPointerKind.Release,
            new ViewportPoint(0, 0),
            new MouseInput(MouseAction.Release, MouseButton.Right, KeyMods.None, 0, 0),
            geometry,
            0);

        // With a selection, right click copies and clears it without pasting.
        session.Use(c => c.Select(SelectionRequest.Range(new ViewportPoint(0, 0), new ViewportPoint(4, 0))));
        var copyArgs = RightRelease();
        router.HandlePointer(copyArgs);
        Assert.True(copyArgs.Handled);
        Assert.Equal("hello", copied);
        Assert.False(session.Read(c => c.HasSelection));

        // With nothing selected, right click pastes clipboard text.
        var pasteArgs = RightRelease();
        router.HandlePointer(pasteArgs);
        Assert.True(pasteArgs.Handled);
        for (var i = 0; i < 50 && rec.Written.Count == 0; i++) await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Equal("pasted text", rec.Text());
    }

    /// <summary>
    /// Dragging a selection past the edges of the view reports cells outside the grid; libghostty rejected them with
    /// InvalidValue and the exception ended the app (crash 2026-11-25 11:28). They are clamped to the grid now.
    /// </summary>
    [Fact]
    public async Task Dragging_a_selection_outside_the_grid_selects_up_to_the_edge()
    {
        var rec = new Recorder();
        await using var session = new TerminalSession(
            new LaunchSpec("x", [], null, new Dictionary<string, string>()), rec, new TerminalSize(80, 24, 0, 0),
            TerminalPalettes.Dark, ColorScheme.Dark, 100, () => false);
        session.Start();
        session.Use(c => c.Write("hello world\r\n"u8));
        using var router = new TerminalInputRouter(session, () => new MacTerminalKeymap(),
            () => Task.FromResult<string?>(null), () => Task.FromResult<string?>(null), _ => Task.CompletedTask,
            (_, _) => Task.FromResult(true), () => { });

        var geometry = new MouseGeometry(800, 480, 10, 20, 0, 0, 0, 0);
        TerminalPointerEventArgs Left(TerminalPointerKind kind, MouseAction action, int column, int row) => new(
            kind, new ViewportPoint(column, row), new MouseInput(action, MouseButton.Left, KeyMods.None, 0, 0), geometry, 0);

        router.HandlePointer(Left(TerminalPointerKind.Press, MouseAction.Press, 0, 0));
        router.HandlePointer(Left(TerminalPointerKind.Move, MouseAction.Motion, 500, 300));
        router.HandlePointer(Left(TerminalPointerKind.Move, MouseAction.Motion, -5, -5));
        router.HandlePointer(Left(TerminalPointerKind.Move, MouseAction.Motion, 200, 0));
        router.HandlePointer(Left(TerminalPointerKind.Release, MouseAction.Release, 200, 0));

        Assert.StartsWith("hello world", session.Read(c => c.GetSelectionText()));
    }

    [Fact]
    public void ClampToGrid_keeps_cells_inside_the_grid()
    {
        var size = new TerminalSize(80, 24, 0, 0);
        Assert.Equal(new ViewportPoint(79, 23), TerminalInputRouter.ClampToGrid(new ViewportPoint(500, 300), size));
        Assert.Equal(new ViewportPoint(0, 0), TerminalInputRouter.ClampToGrid(new ViewportPoint(-3, -1), size));
        Assert.Equal(new ViewportPoint(5, 7), TerminalInputRouter.ClampToGrid(new ViewportPoint(5, 7), size));
    }
}
