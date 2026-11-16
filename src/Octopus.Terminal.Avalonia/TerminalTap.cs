using TKCode.Terminal.Core;

namespace Octopus.Terminal.Avalonia;

/// <summary>
/// A second reader of a session's output (see <see cref="TerminalSession.Tap"/>). Every call is made with the
/// session's lock held, on the thread that reads the process: copy what is needed and return, and never call back into
/// the session. Positions grow by one per byte, and by one for a resize or a reset, so each event has its own place.
/// </summary>
public interface ITerminalTap
{
    /// <summary>The process wrote <paramref name="bytes"/>; the first one is at <paramref name="position"/>. The span is only valid during the call.</summary>
    void OnOutput(ulong position, ReadOnlySpan<byte> bytes);

    /// <summary>The terminal's grid changed, at <paramref name="position"/> in the output.</summary>
    void OnResize(ulong position, TerminalSize size);

    /// <summary>The terminal was cleared for a restart of the program, at <paramref name="position"/>.</summary>
    void OnReset(ulong position);
}

/// <summary>How a terminal's state is handed to another terminal.</summary>
public enum TerminalSnapshotFormat
{
    /// <summary>
    /// The terminal library's own snapshot, restored with <c>TerminalCore.Restore</c>: everything, including the screen a
    /// full-screen program covers and a sequence the program has half written. Only between two builds of the same
    /// library version: the format carries no compatibility promise.
    /// </summary>
    Native,
    /// <summary>
    /// Scrollback and the screen in use as VT text (UTF-8), with the cursor, styles, modes and colors: written to a new
    /// terminal as if it were output. Works across versions and for any terminal, but carries less: while a full-screen
    /// program runs, the shell's screen under it is not included (leaving the program then shows an empty screen), nor
    /// are images or a half-written sequence.
    /// </summary>
    Vt,
}

/// <summary>Where a tap starts.</summary>
/// <param name="Position">The position of the first event the tap will be told.</param>
/// <param name="Snapshot">The terminal as it was just before that position, in <paramref name="Format"/>, for a terminal of size <paramref name="Size"/>.</param>
public readonly record struct TerminalTapStart(ulong Position, TerminalSnapshotFormat Format, byte[] Snapshot, TerminalSize Size, string? Title);
