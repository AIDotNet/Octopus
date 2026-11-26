using TKCode.Terminal.Core;

namespace Octopus.Terminal.Avalonia;

/// <summary>
/// A second reader of a session's output (see <see cref="TerminalSession.Tap(ITerminalTap, TerminalSnapshotFormat, ulong?, out TerminalTapStart)"/>). Every call is made with the
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
public readonly record struct TerminalTapStart(ulong Position, TerminalSnapshotFormat Format, byte[] Snapshot, TerminalSize Size, string? Title, bool Resumed = false);

/// <summary>Kind of event stored in a session's 1 MiB history ring buffer for delta reconnection.</summary>
public enum TerminalHistoryKind : byte
{
    Output = 0,
    Resize = 1,
    Reset = 2,
}

/// <summary>One output chunk, resize, or reset recorded in <see cref="TerminalHistoryRing"/>.</summary>
public readonly record struct TerminalHistoryEvent(TerminalHistoryKind Kind, ulong Position, byte[] Bytes, TerminalSize Size = default)
{
    public ulong NextPosition => Kind == TerminalHistoryKind.Output ? Position + (ulong)Bytes.Length : Position + 1;
}

/// <summary>
/// A bounded 1 MiB ring buffer of recent terminal output and control records (design/12-远程操作.md §4.2, §6.3).
/// Allocated on demand when a session first gets a remote viewer so local-only sessions pay zero memory.
/// </summary>
public sealed class TerminalHistoryRing(int maxBytes = TerminalHistoryRing.DefaultMaxBytes)
{
    public const int DefaultMaxBytes = 1024 * 1024;
    private const int RecordWeight = 16;

    private readonly Queue<TerminalHistoryEvent> _events = new();
    private readonly int _maxBytes = Math.Max(1024, maxBytes);
    private int _usedBytes;
    private bool _initialized;

    /// <summary>The earliest position still present in the ring buffer.</summary>
    public ulong TailPosition { get; private set; }

    /// <summary>The next expected position at the head of the ring buffer.</summary>
    public ulong HeadPosition { get; private set; }

    public void ResetTo(ulong position)
    {
        _events.Clear();
        _usedBytes = 0;
        TailPosition = position;
        HeadPosition = position;
        _initialized = true;
    }

    public void AppendOutput(ulong position, ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return;
        EnsureContiguous(position);
        if (bytes.Length >= _maxBytes)
        {
            _events.Clear();
            _usedBytes = 0;
            var offset = bytes.Length - _maxBytes;
            var slice = bytes[offset..].ToArray();
            var startPos = position + (ulong)offset;
            _events.Enqueue(new TerminalHistoryEvent(TerminalHistoryKind.Output, startPos, slice));
            _usedBytes = slice.Length;
            TailPosition = startPos;
            HeadPosition = position + (ulong)bytes.Length;
            return;
        }

        var copy = bytes.ToArray();
        _events.Enqueue(new TerminalHistoryEvent(TerminalHistoryKind.Output, position, copy));
        _usedBytes += copy.Length;
        HeadPosition = position + (ulong)copy.Length;
        Trim();
    }

    public void AppendResize(ulong position, TerminalSize size)
    {
        EnsureContiguous(position);
        _events.Enqueue(new TerminalHistoryEvent(TerminalHistoryKind.Resize, position, [], size));
        _usedBytes += RecordWeight;
        HeadPosition = position + 1;
        Trim();
    }

    public void AppendReset(ulong position)
    {
        EnsureContiguous(position);
        _events.Enqueue(new TerminalHistoryEvent(TerminalHistoryKind.Reset, position, []));
        _usedBytes += RecordWeight;
        HeadPosition = position + 1;
        Trim();
    }

    /// <summary>
    /// Attempts to extract every event from <paramref name="resumeFrom"/> up to <paramref name="currentHead"/>.
    /// Returns false when <paramref name="resumeFrom"/> is older than <see cref="TailPosition"/> or ahead of <paramref name="currentHead"/>.
    /// </summary>
    public bool TryReadFrom(ulong resumeFrom, ulong currentHead, out IReadOnlyList<TerminalHistoryEvent> delta)
    {
        if (resumeFrom == currentHead)
        {
            delta = [];
            return true;
        }
        if (!_initialized || HeadPosition != currentHead || resumeFrom < TailPosition || resumeFrom > currentHead)
        {
            delta = [];
            return false;
        }

        var list = new List<TerminalHistoryEvent>();
        foreach (var item in _events)
        {
            if (item.NextPosition <= resumeFrom) continue;
            if (item.Kind == TerminalHistoryKind.Output && item.Position < resumeFrom)
            {
                var skip = (int)(resumeFrom - item.Position);
                list.Add(new TerminalHistoryEvent(TerminalHistoryKind.Output, resumeFrom, item.Bytes[skip..]));
            }
            else
            {
                list.Add(item);
            }
        }
        delta = list;
        return true;
    }

    private void EnsureContiguous(ulong position)
    {
        if (!_initialized || HeadPosition != position)
        {
            ResetTo(position);
        }
    }

    private void Trim()
    {
        while (_usedBytes > _maxBytes && _events.Count > 0)
        {
            var removed = _events.Dequeue();
            _usedBytes -= removed.Kind == TerminalHistoryKind.Output ? removed.Bytes.Length : RecordWeight;
            TailPosition = _events.TryPeek(out var next) ? next.Position : HeadPosition;
        }
    }
}
