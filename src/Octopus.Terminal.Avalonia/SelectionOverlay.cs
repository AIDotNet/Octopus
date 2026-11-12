using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using TKCode.Terminal.Avalonia;
using TKCode.Terminal.Core;

namespace Octopus.Terminal.Avalonia;

/// <summary>
/// Draws what a <see cref="TerminalView"/> does not draw itself: the selection, search matches (the current one
/// stronger) and the underline of the link under the pointer. Never takes pointer input.
/// </summary>
public sealed class SelectionOverlay : Control
{
    private static readonly IBrush SelectionFill = new SolidColorBrush(Color.FromArgb(0x70, 0x4C, 0x8D, 0xF6)).ToImmutable();
    private static readonly IBrush MatchFill = new SolidColorBrush(Color.FromArgb(0x5A, 0xF2, 0xC0, 0x2E)).ToImmutable();
    private static readonly IBrush CurrentFill = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0x8A, 0x1F)).ToImmutable();
    private static readonly IPen CurrentPen = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x47)), 1).ToImmutable();
    private static readonly IBrush LinkBrush = new SolidColorBrush(Color.FromRgb(0x5B, 0x9D, 0xFF)).ToImmutable();

    private readonly TerminalView _view;
    private CellSpan[] _spans = [];
    private CellSpan[] _matches = [];
    private CellSpan[] _current = [];
    private IReadOnlyList<CellSpan> _link = [];

    public SelectionOverlay(TerminalView view)
    {
        _view = view;
        IsHitTestVisible = false;
    }

    /// <summary>Shows the selection and search matches of <paramref name="frame"/>.</summary>
    public void Update(FrameSnapshot? frame)
    {
        var spans = frame?.Selection ?? [];
        var matches = frame?.Search?.Matches ?? [];
        var current = frame?.Search?.Selected ?? [];
        if (spans.Length == 0 && _spans.Length == 0 && matches.Length == 0 && _matches.Length == 0
            && current.Length == 0 && _current.Length == 0) return;
        (_spans, _matches, _current) = (spans, matches, current);
        InvalidateVisual();
    }

    /// <summary>Underlines the cells of the link under the pointer; null removes the underline.</summary>
    public void SetLink(LinkHit? link)
    {
        var spans = link?.Spans ?? [];
        if (spans.Count == 0 && _link.Count == 0) return;
        _link = spans;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (_spans.Length + _matches.Length + _current.Length + _link.Count == 0) return;
        var m = _view.Metrics;
        var scale = m.Scaling;
        var pad = Math.Round(_view.Padding * scale);

        Rect RectOf(CellSpan span) => new(
            (pad + span.StartColumn * m.CellWidth) / scale,
            (pad + span.Row * m.CellHeight) / scale,
            (span.EndColumn - span.StartColumn + 1) * m.CellWidth / scale,
            m.CellHeight / scale);

        foreach (var span in _matches) context.FillRectangle(MatchFill, RectOf(span), 2);
        foreach (var span in _current)
        {
            var rect = RectOf(span);
            context.DrawRectangle(CurrentFill, CurrentPen, rect, 2, 2);
        }
        foreach (var span in _spans) context.FillRectangle(SelectionFill, RectOf(span));
        foreach (var span in _link)
        {
            var rect = RectOf(span);
            context.FillRectangle(LinkBrush, new Rect(rect.X, rect.Bottom - 2, rect.Width, 1));
        }
    }
}
