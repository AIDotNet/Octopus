using System.Buffers;
using TKCode.Terminal.Avalonia;
using TKCode.Terminal.Core;

namespace Octopus.Terminal.Avalonia;

/// <summary>
/// Turns what the view reports (keys, text, pointer, focus) into bytes for the process or into selection and
/// scrolling on the core. Used from the UI thread only.
/// </summary>
public sealed class TerminalInputRouter(
    TerminalSession session,
    Func<ITerminalKeymap> keymap,
    Func<Task<string?>> getClipboardText,
    Func<Task<string?>> saveClipboardImage,
    Func<string, Task> setClipboardText,
    Func<string, bool, Task<bool>> allowPaste,
    Action openSearch) : IDisposable
{
    private const KeyMods Relevant = KeyMods.Shift | KeyMods.Ctrl | KeyMods.Alt | KeyMods.Super;
    private const int WheelLinesPerNotch = 3;
    private static readonly TimeSpan MultiClickInterval = TimeSpan.FromMilliseconds(500);

    private readonly InputEncoder _encoder = new() { OptionAsAlt = OptionAsAlt.Both };
    private ViewportPoint _lastClick;
    private long _lastClickAt;
    private int _clickCount;
    private ViewportPoint _dragStart;
    private bool _dragging;
    private bool _pasting;

    /// <summary>A non-text key. Sets <see cref="TerminalKeyEventArgs.Handled"/> when it was consumed.</summary>
    public void HandleKey(TerminalKeyEventArgs e)
    {
        var input = e.Input;
        var shortcut = keymap().Match(input);
        if (shortcut != TerminalShortcut.None && RunShortcut(shortcut))
        {
            // A bare Ctrl+C that copied lets go of the selection: the next one is the interrupt.
            if (shortcut == TerminalShortcut.Copy && (input.Mods & Relevant) == KeyMods.Ctrl) session.Use(c => c.ClearSelection());
            e.Handled = true;
            return;
        }

        var modes = session.ReadInputModes();
        if (input.Action == KeyAction.Press && Translate(input, modes) is { } text)
        {
            session.Use(c => c.ScrollViewport(ViewportScroll.Bottom));
            session.Send(text);
            e.Handled = true;
            return;
        }

        if (input.Action != KeyAction.Release) session.Use(c => c.ScrollViewport(ViewportScroll.Bottom));
        _encoder.Apply(modes);
        Span<byte> buffer = stackalloc byte[128];
        var n = _encoder.EncodeKey(input, buffer);
        if (n > 0)
        {
            session.Send(buffer[..n]);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Keys that other terminals turn into the plain bytes shells and agent CLIs understand, where the encoder would
    /// send a CSI sequence they ignore. Null when the key goes through the encoder.
    /// </summary>
    public static string? Translate(in KeyInput input, in InputModes modes)
    {
        var mods = input.Mods & Relevant;
        // Ctrl+J is the line feed. Agent CLIs such as Claude Code read it as "insert a newline" while Enter (0x0D)
        // submits. Always plain, the program cannot tell it from Enter otherwise.
        if (input.Key == TerminalKey.J && mods == KeyMods.Ctrl) return "\n";

        // A program that asked for the extended keyboard protocol, or a full-screen one (vim, less), gets the
        // real key and decides itself.
        if (modes.KittyFlags != 0 || modes.AlternateScreen) return null;
        return (input.Key, mods) switch
        {
            // Shift+Enter: a newline without submitting, like Ctrl+J.
            (TerminalKey.Enter, KeyMods.Shift) => "\n",
            // Command+arrows / Backspace edit the line (readline: start, end, kill to start).
            (TerminalKey.ArrowLeft, KeyMods.Super) => "\u0001",
            (TerminalKey.ArrowRight, KeyMods.Super) => "\u0005",
            (TerminalKey.Backspace, KeyMods.Super) => "\u0015",
            // Option+arrows move by word (readline: backward-word, forward-word).
            (TerminalKey.ArrowLeft, KeyMods.Alt) => "\u001bb",
            (TerminalKey.ArrowRight, KeyMods.Alt) => "\u001bf",
            _ => null,
        };
    }

    /// <summary>Printable text, including what an input method committed.</summary>
    public void HandleText(string text)
    {
        if (text.Length == 0) return;
        session.Use(c =>
        {
            c.ScrollViewport(ViewportScroll.Bottom);
            if (c.HasSelection) c.ClearSelection();
        });
        session.Send(text);
    }

    /// <summary>Focus in/out, reported to programs that asked for it (mode 1004).</summary>
    public void HandleFocus(bool focused)
    {
        if (!session.ReadInputModes().FocusEvents) return;
        Span<byte> buffer = stackalloc byte[16];
        var n = InputEncoder.EncodeFocus(focused, buffer);
        if (n > 0) session.Send(buffer[..n]);
    }

    /// <summary>Mouse and wheel.</summary>
    public void HandlePointer(TerminalPointerEventArgs e)
    {
        var modes = session.ReadInputModes();
        var reporting = modes.MouseTracking != MouseTracking.None && !e.Mouse.Mods.HasFlag(KeyMods.Shift);

        if (e.Kind == TerminalPointerKind.Wheel)
        {
            HandleWheel(e, modes);
            e.Handled = true;
            return;
        }
        if (reporting)
        {
            if (e.Kind == TerminalPointerKind.Press) _dragging = true;
            else if (e.Kind == TerminalPointerKind.Release) _dragging = false;
            _encoder.Apply(modes);
            Span<byte> buffer = stackalloc byte[64];
            var n = _encoder.EncodeMouse(e.Mouse, e.Geometry, anyButtonPressed: _dragging, buffer);
            if (n > 0) session.Send(buffer[..n]);
            return;
        }

        switch (e.Kind)
        {
            case TerminalPointerKind.Press when e.Mouse.Button == MouseButton.Left:
                var now = Environment.TickCount64;
                _clickCount = now - _lastClickAt < MultiClickInterval.TotalMilliseconds && _lastClick == e.Cell
                    ? Math.Min(_clickCount + 1, 3)
                    : 1;
                (_lastClick, _lastClickAt) = (e.Cell, now);
                _dragStart = e.Cell;
                _dragging = true;
                var cell = e.Cell;
                var count = _clickCount;
                session.Use(c =>
                {
                    switch (count)
                    {
                        case 2: c.Select(SelectionRequest.Word(cell)); break;
                        case 3: c.Select(SelectionRequest.Line(cell)); break;
                        default: c.ClearSelection(); break;
                    }
                });
                break;
            case TerminalPointerKind.Move when _dragging && _clickCount == 1:
                var (start, end) = (_dragStart, e.Cell);
                session.Use(c => c.Select(SelectionRequest.Range(start, end)));
                break;
            case TerminalPointerKind.Release when e.Mouse.Button == MouseButton.Left:
                _dragging = false;
                break;
            case TerminalPointerKind.Release when e.Mouse.Button == MouseButton.Right && keymap().CopyWithoutShift:
                // Selected text is copied and released; with nothing selected the click pastes.
                var selected = session.Read(c => c.GetSelectionText());
                if (!string.IsNullOrEmpty(selected))
                {
                    _ = setClipboardText(selected);
                    session.Use(c => c.ClearSelection());
                }
                else
                {
                    _ = PasteAsync();
                }
                e.Handled = true;
                break;
        }
    }

    private void HandleWheel(TerminalPointerEventArgs e, InputModes modes)
    {
        var notches = Math.Max(1, (int)Math.Round(Math.Abs(e.WheelDelta)));
        if (modes.MouseTracking != MouseTracking.None && !e.Mouse.Mods.HasFlag(KeyMods.Shift))
        {
            _encoder.Apply(modes);
            Span<byte> buffer = stackalloc byte[64];
            for (var i = 0; i < notches; i++)
            {
                var n = _encoder.EncodeMouse(e.Mouse, e.Geometry, anyButtonPressed: false, buffer);
                if (n > 0) session.Send(buffer[..n]);
            }
            return;
        }
        if (modes.AlternateScreen && modes.AlternateScroll)
        {
            // Full-screen program without mouse reporting: the wheel becomes arrow keys.
            var key = e.WheelDelta > 0 ? TerminalKey.ArrowUp : TerminalKey.ArrowDown;
            _encoder.Apply(modes);
            Span<byte> buffer = stackalloc byte[32];
            for (var i = 0; i < notches * WheelLinesPerNotch; i++)
            {
                var n = _encoder.EncodeKey(new KeyInput(key, KeyAction.Press, KeyMods.None), buffer);
                if (n > 0) session.Send(buffer[..n]);
            }
            return;
        }
        var lines = (long)-Math.Sign(e.WheelDelta) * notches * WheelLinesPerNotch;
        session.Use(c => c.ScrollViewport(ViewportScroll.Lines(lines)));
    }

    private bool RunShortcut(TerminalShortcut shortcut)
    {
        switch (shortcut)
        {
            case TerminalShortcut.Copy:
                // Only taken when something is selected; otherwise the key goes on to the program.
                var text = session.Read(c => c.GetSelectionText());
                if (string.IsNullOrEmpty(text)) return false;
                _ = setClipboardText(text);
                return true;
            case TerminalShortcut.Paste:
                _ = PasteAsync();
                return true;
            case TerminalShortcut.Clear:
                session.Use(c => { if (c.IsParserGround) c.Clear(ClearTarget.All); });
                return true;
            case TerminalShortcut.Find:
                openSearch();
                return true;
            case TerminalShortcut.SelectAll:
                session.Use(c => c.Select(SelectionRequest.All()));
                return true;
            default:
                return false;
        }
    }

    /// <summary>Pastes the clipboard text, wrapped the way the program asked for.</summary>
    public async Task PasteAsync()
    {
        var text = await getClipboardText().ConfigureAwait(true);
        // No text: an image on the clipboard is saved to a file and its path pasted, which is what agent CLIs
        // (Claude Code) and shells can both use; a terminal cannot carry the pixels itself.
        if (string.IsNullOrEmpty(text)) text = await saveClipboardImage().ConfigureAwait(true);
        if (string.IsNullOrEmpty(text)) return;
        if (_pasting) return;
        _pasting = true;
        try
        {
            // Programs that enabled bracketed paste know it is a paste; anything else gets asked about.
            if (!await allowPaste(text, session.ReadInputModes().BracketedPaste).ConfigureAwait(true)) return;
        }
        finally { _pasting = false; }
        var writer = new ArrayBufferWriter<byte>();
        var outcome = session.Read(c => c.EncodePaste(text, allowUnsafe: true, writer));
        if (outcome == PasteOutcome.Written)
        {
            session.Use(c => c.ScrollViewport(ViewportScroll.Bottom));
            session.Send(writer.WrittenSpan);
        }
    }

    public void Dispose() => _encoder.Dispose();
}
