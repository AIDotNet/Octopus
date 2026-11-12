using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Avalonia.Media.TextFormatting.Unicode;
using Avalonia.Threading;
using Avalonia.Utilities;
using Avalonia.VisualTree;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;

namespace Octopus.App.Files;

/// <summary>How a line is tinted across the whole row.</summary>
public enum CodeLineTint : byte { None, Added, Removed, Hunk }

/// <summary>
/// Per-line extras of a <see cref="CodeView"/> that shows a diff: two number columns, a marker, a tint, and which sides of
/// the change the line belongs to for syntax coloring (bit 1 = the old file, bit 2 = the new file; 0 = not code).
/// </summary>
public readonly record struct CodeLineMark(string Number1, string Number2, string Marker, CodeLineTint Tint, byte Sides);

/// <summary>
/// A read-only code viewer: only the rows in view are laid out and drawn, the line-number gutter stays at the left while
/// the text scrolls, text can be selected with the mouse and copied, long lines can wrap. Syntax colors come from
/// <see cref="CodeSyntax"/> and are computed on a background thread in growing chunks, so a large file shows plain text
/// first and fills in. It is an <see cref="ILogicalScrollable"/>: put it in an <c>arc:ScrollArea</c> for the scrollbars.
/// </summary>
public sealed class CodeView : Control, ILogicalScrollable
{
    private const int TabSize = 4;
    private const int LongLine = 600;
    private const int HighlightLineLimit = 10_000;

    public static readonly StyledProperty<string> TextProperty = AvaloniaProperty.Register<CodeView, string>(nameof(Text), "");
    public static readonly StyledProperty<string?> LanguageProperty = AvaloniaProperty.Register<CodeView, string?>(nameof(Language));
    public static readonly StyledProperty<bool> WrapLinesProperty = AvaloniaProperty.Register<CodeView, bool>(nameof(WrapLines));
    public static readonly StyledProperty<IReadOnlyList<CodeLineMark>?> MarksProperty = AvaloniaProperty.Register<CodeView, IReadOnlyList<CodeLineMark>?>(nameof(Marks));
    public static readonly StyledProperty<FontFamily> FontFamilyProperty = Avalonia.Controls.Documents.TextElement.FontFamilyProperty.AddOwner<CodeView>();
    public static readonly StyledProperty<double> FontSizeProperty = Avalonia.Controls.Documents.TextElement.FontSizeProperty.AddOwner<CodeView>();
    public static readonly StyledProperty<IBrush?> ForegroundProperty = Avalonia.Controls.Documents.TextElement.ForegroundProperty.AddOwner<CodeView>();
    public static readonly StyledProperty<IBrush?> BackgroundProperty = AvaloniaProperty.Register<CodeView, IBrush?>(nameof(Background));
    public static readonly StyledProperty<IBrush?> MutedForegroundProperty = AvaloniaProperty.Register<CodeView, IBrush?>(nameof(MutedForeground));
    public static readonly StyledProperty<IBrush?> GutterForegroundProperty = AvaloniaProperty.Register<CodeView, IBrush?>(nameof(GutterForeground));
    public static readonly StyledProperty<IBrush?> HoverBrushProperty = AvaloniaProperty.Register<CodeView, IBrush?>(nameof(HoverBrush));
    public static readonly StyledProperty<IBrush?> SelectionBrushProperty = AvaloniaProperty.Register<CodeView, IBrush?>(nameof(SelectionBrush));
    public static readonly StyledProperty<IBrush?> DividerBrushProperty = AvaloniaProperty.Register<CodeView, IBrush?>(nameof(DividerBrush));
    public static readonly StyledProperty<IBrush?> AddedBrushProperty = AvaloniaProperty.Register<CodeView, IBrush?>(nameof(AddedBrush));
    public static readonly StyledProperty<IBrush?> RemovedBrushProperty = AvaloniaProperty.Register<CodeView, IBrush?>(nameof(RemovedBrush));
    public static readonly StyledProperty<IBrush?> HunkBrushProperty = AvaloniaProperty.Register<CodeView, IBrush?>(nameof(HunkBrush));

    static CodeView()
    {
        AffectsRender<CodeView>(BackgroundProperty, ForegroundProperty, MutedForegroundProperty, GutterForegroundProperty, HoverBrushProperty,
            SelectionBrushProperty, DividerBrushProperty, AddedBrushProperty, RemovedBrushProperty, HunkBrushProperty);
        FocusableProperty.OverrideDefaultValue<CodeView>(true);
        ClipToBoundsProperty.OverrideDefaultValue<CodeView>(true);
        CursorProperty.OverrideDefaultValue<CodeView>(new Cursor(StandardCursorType.Ibeam));
    }

    // Content.
    private string _text = "";
    private int[] _lineStarts = [0];
    private SyntaxSpan[]?[] _spans = [null];
    private int _maxCells;
    private int _lineCount = 1;
    private bool _hasTabs;

    // Layout.
    private double _charWidth = 8;
    private double _rowHeight = 20;
    private Typeface _typeface = Typeface.Default;
    private Typeface _italic = Typeface.Default;
    private double _gutterWidth;
    private double _textLeft;
    private double _rightPad;
    private int _wrapCols = int.MaxValue;
    private int[]? _rowStart;
    private Dictionary<int, int[]>? _breaks;
    private int _totalRows = 1;
    private Size _viewport;
    private Size _extent;
    private Vector _offset;
    private bool _metricsDirty = true;
    private bool _layoutDirty = true;

    // Caches of what is in view.
    private readonly Dictionary<int, RowLayout> _rows = [];
    private readonly Dictionary<int, TextLayout> _numbers = [];
    private GenericTextRunProperties?[] _roleProps = new GenericTextRunProperties?[10];
    private IBrush?[] _roleBrushes = new IBrush?[10];

    // Interaction.
    private (int Line, int Col) _anchor;
    private (int Line, int Col) _caret;
    private bool _hasSelection;
    private bool _dragging;
    private int _hoverLine = -1;
    private int _renderedRows;
    private int _layoutsCreated;

    // Highlighting.
    private int _highlightVersion;
    private CancellationTokenSource? _highlightCts;
    private bool _highlightQueued;
    private TaskCompletionSource _highlightDone = Completed();
    private bool _unsubscribeTheme;

    public CodeView()
    {
        AddHandler(KeyDownEvent, OnViewKeyDown, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
    }

    public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }

    /// <summary>The language identifier of <see cref="FileLanguages"/>; null or an unknown one shows plain text.</summary>
    public string? Language { get => GetValue(LanguageProperty); set => SetValue(LanguageProperty, value); }

    public bool WrapLines { get => GetValue(WrapLinesProperty); set => SetValue(WrapLinesProperty, value); }

    /// <summary>Diff extras per line; null for a plain file (the gutter then has line numbers).</summary>
    public IReadOnlyList<CodeLineMark>? Marks { get => GetValue(MarksProperty); set => SetValue(MarksProperty, value); }

    public FontFamily FontFamily { get => GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public double FontSize { get => GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public IBrush? MutedForeground { get => GetValue(MutedForegroundProperty); set => SetValue(MutedForegroundProperty, value); }
    public IBrush? GutterForeground { get => GetValue(GutterForegroundProperty); set => SetValue(GutterForegroundProperty, value); }
    public IBrush? HoverBrush { get => GetValue(HoverBrushProperty); set => SetValue(HoverBrushProperty, value); }
    public IBrush? SelectionBrush { get => GetValue(SelectionBrushProperty); set => SetValue(SelectionBrushProperty, value); }
    public IBrush? DividerBrush { get => GetValue(DividerBrushProperty); set => SetValue(DividerBrushProperty, value); }
    public IBrush? AddedBrush { get => GetValue(AddedBrushProperty); set => SetValue(AddedBrushProperty, value); }
    public IBrush? RemovedBrush { get => GetValue(RemovedBrushProperty); set => SetValue(RemovedBrushProperty, value); }
    public IBrush? HunkBrush { get => GetValue(HunkBrushProperty); set => SetValue(HunkBrushProperty, value); }

    // ---- Diagnostics and test hooks ----

    public int LineCount => _lineCount;
    public int TotalRows => _totalRows;
    public double RowHeight => _rowHeight;
    public double CharWidth => _charWidth;
    public double GutterWidth => _gutterWidth;

    /// <summary>The rows drawn in the last frame.</summary>
    public int RenderedRows => _renderedRows;

    /// <summary>How many row layouts were created since the text was set (rows in view, not rows in the file).</summary>
    public int LayoutsCreated => _layoutsCreated;

    /// <summary>The line the pointer is on, or -1.</summary>
    public int HoverLine => _hoverLine;

    /// <summary>Completes when the colors of the current text are all in.</summary>
    public Task WhenHighlighted => _highlightDone.Task;

    /// <summary>The syntax roles found in the highlighted lines (plain excluded).</summary>
    public IReadOnlySet<SyntaxRole> SyntaxRolesInUse
    {
        get
        {
            var roles = new HashSet<SyntaxRole>();
            foreach (var spans in _spans)
            {
                if (spans is null) continue;
                foreach (var span in spans) roles.Add(span.Role);
            }
            return roles;
        }
    }

    /// <summary>The brush a role is drawn with (null for plain text); for tests.</summary>
    public IBrush? BrushOf(SyntaxRole role) => EnsurePalette()[(int)role];

    public string GetLine(int line) => line < 0 || line >= _lineCount ? "" : _text.Substring(_lineStarts[line], LineEnd(line) - _lineStarts[line]);

    // ---- Selection ----

    public bool HasSelection => _hasSelection && _anchor != _caret;

    public string SelectedText
    {
        get
        {
            if (!HasSelection) return "";
            var (start, end) = Ordered();
            var sb = new StringBuilder();
            for (var line = start.Line; line <= end.Line; line++)
            {
                var from = line == start.Line ? start.Col : 0;
                var to = line == end.Line ? end.Col : LineEnd(line) - _lineStarts[line];
                sb.Append(_text, _lineStarts[line] + from, Math.Max(0, to - from));
                if (line < end.Line) sb.Append('\n');
            }
            return sb.ToString();
        }
    }

    public void SelectAll()
    {
        _anchor = (0, 0);
        _caret = (_lineCount - 1, LineEnd(_lineCount - 1) - _lineStarts[_lineCount - 1]);
        _hasSelection = true;
        InvalidateVisual();
    }

    /// <summary>Selects from one position to another (zero-based line and column of the raw text).</summary>
    public void Select(int startLine, int startColumn, int endLine, int endColumn)
    {
        _anchor = ClampPosition(startLine, startColumn);
        _caret = ClampPosition(endLine, endColumn);
        _hasSelection = true;
        InvalidateVisual();
    }

    public void ClearSelection()
    {
        _hasSelection = false;
        InvalidateVisual();
    }

    /// <summary>Puts the selected text on the clipboard; false when nothing is selected or there is no clipboard.</summary>
    public async Task<bool> CopyAsync()
    {
        var text = SelectedText;
        if (text.Length == 0 || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return false;
        await clipboard.SetTextAsync(text);
        return true;
    }

    private ((int Line, int Col) Start, (int Line, int Col) End) Ordered() =>
        _anchor.Line < _caret.Line || (_anchor.Line == _caret.Line && _anchor.Col <= _caret.Col) ? (_anchor, _caret) : (_caret, _anchor);

    private (int, int) ClampPosition(int line, int column)
    {
        line = Math.Clamp(line, 0, _lineCount - 1);
        return (line, Math.Clamp(column, 0, LineEnd(line) - _lineStarts[line]));
    }

    // ---- Property changes ----

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        var property = change.Property;
        if (property == TextProperty)
        {
            SetText(change.GetNewValue<string?>() ?? "");
        }
        else if (property == LanguageProperty)
        {
            QueueHighlight();
        }
        else if (property == MarksProperty)
        {
            _metricsDirty = true;
            _layoutDirty = true;
            _rows.Clear();
            _numbers.Clear();
            QueueHighlight();
            InvalidateMeasure();
            InvalidateVisual();
        }
        else if (property == WrapLinesProperty)
        {
            _layoutDirty = true;
            _rows.Clear();
            SetOffset(new Vector(0, _offset.Y), false);
            InvalidateMeasure();
        }
        else if (property == FontFamilyProperty || property == FontSizeProperty)
        {
            _metricsDirty = true;
            _layoutDirty = true;
            _rows.Clear();
            _numbers.Clear();
            InvalidateMeasure();
            InvalidateVisual();
        }
        else if (property == ForegroundProperty || property == MutedForegroundProperty)
        {
            _roleProps = new GenericTextRunProperties?[10];
            _roleBrushes = new IBrush?[10];
            _rows.Clear();
            InvalidateVisual();
        }
        else if (property == GutterForegroundProperty)
        {
            _numbers.Clear();
        }
        else if (property.Name == "ActualThemeVariant")
        {
            RefreshPalette();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ArcTheme.Manager.StateChanged += OnArcThemeChanged;
        _unsubscribeTheme = true;
        RefreshPalette();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_unsubscribeTheme) ArcTheme.Manager.StateChanged -= OnArcThemeChanged;
        _unsubscribeTheme = false;
        _highlightCts?.Cancel();
    }

    private void OnArcThemeChanged(object? sender, ArcThemeChangedEventArgs e) => Dispatcher.UIThread.Post(RefreshPalette, DispatcherPriority.Background);

    private void RefreshPalette()
    {
        _roleProps = new GenericTextRunProperties?[10];
        _roleBrushes = new IBrush?[10];
        _rows.Clear();
        InvalidateVisual();
    }

    // ---- Text and lines ----

    private void SetText(string text)
    {
        var previousText = _text;
        var previousStarts = _lineStarts;
        var previousSpans = _spans;
        var previousCount = _lineCount;

        _text = text;
        var starts = new List<int>(Math.Max(16, text.Length / 32)) { 0 };
        for (var i = text.IndexOf('\n'); i >= 0; i = text.IndexOf('\n', i + 1))
        {
            if (i + 1 < text.Length) starts.Add(i + 1);
        }
        _lineStarts = starts.ToArray();
        _lineCount = _lineStarts.Length;
        _hasTabs = text.Contains('\t');

        // Lines that did not change keep their colors until the new ones arrive, so a reload does not flash.
        _spans = new SyntaxSpan[]?[_lineCount];
        var shared = Math.Min(previousCount, _lineCount);
        for (var i = 0; i < shared; i++)
        {
            if (previousSpans[i] is not null && SameLine(previousText, previousStarts, previousCount, i)) _spans[i] = previousSpans[i];
        }

        _hasSelection = false;
        _hoverLine = -1;
        _rows.Clear();
        _numbers.Clear();
        _layoutsCreated = 0;
        _metricsDirty = true;
        _layoutDirty = true;
        QueueHighlight();
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>Whether line <paramref name="line"/> of the previous text equals the same line of the current one.</summary>
    private bool SameLine(string previousText, int[] previousStarts, int previousCount, int line)
    {
        var previousEnd = LineEndIn(previousText, previousStarts, previousCount, line);
        var end = LineEnd(line);
        return previousEnd - previousStarts[line] == end - _lineStarts[line] &&
               previousText.AsSpan(previousStarts[line], previousEnd - previousStarts[line]).SequenceEqual(_text.AsSpan(_lineStarts[line], end - _lineStarts[line]));
    }

    private static int LineEndIn(string text, int[] starts, int count, int line)
    {
        var start = starts[line];
        var end = line + 1 < count ? starts[line + 1] : text.Length;
        if (end > start && text[end - 1] == '\n') end--;
        if (end > start && text[end - 1] == '\r') end--;
        return end;
    }

    private int LineEnd(int line) => LineEndIn(_text, _lineStarts, _lineCount, line);

    // ---- Cells (a monospace grid: tabs to the next stop, wide characters take two) ----

    private static int CellAdvance(char c, int cell)
    {
        if (c == '\t') return TabSize - cell % TabSize;
        if (c < 0x1100) return 1;
        if (char.IsHighSurrogate(c)) return 2;
        if (char.IsLowSurrogate(c)) return 0;
        if ((c >= 0x1100 && c <= 0x115F) || (c >= 0x2E80 && c <= 0xA4CF) || (c >= 0xAC00 && c <= 0xD7A3) ||
            (c >= 0xF900 && c <= 0xFAFF) || (c >= 0xFE30 && c <= 0xFE6F) || (c >= 0xFF00 && c <= 0xFF60) || (c >= 0xFFE0 && c <= 0xFFE6))
        {
            return 2;
        }
        return 1;
    }

    private int CellsOf(int start, int end)
    {
        var cell = 0;
        for (var i = start; i < end; i++) cell += CellAdvance(_text[i], cell);
        return cell;
    }

    private int NextRowEnd(int start, int lineEnd, int cols)
    {
        var cell = 0;
        var lastBreak = -1;
        for (var i = start; i < lineEnd; i++)
        {
            var c = _text[i];
            var advance = CellAdvance(c, cell);
            if (cell + advance > cols && i > start && advance > 0)
            {
                return lastBreak > start && lastBreak - start >= (i - start) / 2 ? lastBreak : i;
            }
            cell += advance;
            if (c is ' ' or '\t' or ',' or ';' or ')' or ']' or '}' or '>' or '/' or '-') lastBreak = i + 1;
        }
        return lineEnd;
    }

    // ---- Metrics and layout ----

    private void UpdateMetrics()
    {
        if (!_metricsDirty) return;
        _metricsDirty = false;
        var family = FontFamily;
        var size = FontSize is > 0 and < 200 ? FontSize : 13;
        _typeface = new Typeface(family);
        _italic = new Typeface(family, FontStyle.Italic);
        using var probe = new TextLayout("0000000000", _typeface, size, Brushes.Black);
        _charWidth = Math.Max(1, probe.Width / 10);
        _rowHeight = Math.Ceiling(Math.Max(probe.Height, size * 1.55));
        _roleProps = new GenericTextRunProperties?[10];

        var marks = Marks;
        var digits = Math.Max(2, _lineCount.ToString(CultureInfo.InvariantCulture).Length);
        var pad = Math.Round(_charWidth * 1.5);
        if (marks is null)
        {
            _gutterWidth = Math.Round(pad + digits * _charWidth + pad);
        }
        else
        {
            var wide = 1;
            foreach (var mark in marks) wide = Math.Max(wide, Math.Max(mark.Number1.Length, mark.Number2.Length));
            _gutterWidth = Math.Round(pad + wide * _charWidth + _charWidth * 1.5 + wide * _charWidth + _charWidth * 1.5 + _charWidth + _charWidth);
        }
        _textLeft = _gutterWidth + Math.Round(_charWidth * 0.5);
        _rightPad = Math.Round(_charWidth * 2);
    }

    private void RebuildLayout()
    {
        UpdateMetrics();
        var wrap = WrapLines;
        var cols = wrap ? Math.Max(8, (int)((_viewport.Width - _textLeft - _rightPad) / _charWidth)) : int.MaxValue;
        if (!_layoutDirty && cols == _wrapCols) return;
        _layoutDirty = false;
        _wrapCols = cols;
        _rows.Clear();
        _maxCells = 0;
        if (!wrap)
        {
            _rowStart = null;
            _breaks = null;
            _totalRows = _lineCount;
            for (var line = 0; line < _lineCount; line++)
            {
                var start = _lineStarts[line];
                var end = LineEnd(line);
                _maxCells = Math.Max(_maxCells, CellsOf(start, end));
            }
        }
        else
        {
            var rowStart = new int[_lineCount + 1];
            var breaks = new Dictionary<int, int[]>();
            var rows = 0;
            var list = new List<int>();
            for (var line = 0; line < _lineCount; line++)
            {
                rowStart[line] = rows;
                var start = _lineStarts[line];
                var end = LineEnd(line);
                if (end - start <= cols / 2)
                {
                    rows++;
                    continue;
                }
                list.Clear();
                var position = start;
                do
                {
                    list.Add(position);
                    position = NextRowEnd(position, end, cols);
                }
                while (position < end);
                if (list.Count > 1) breaks[line] = list.ToArray();
                rows += list.Count;
            }
            rowStart[_lineCount] = rows;
            _rowStart = rowStart;
            _breaks = breaks;
            _totalRows = rows;
        }
        UpdateExtent();
    }

    private void UpdateExtent()
    {
        var width = WrapLines ? _viewport.Width : _textLeft + _maxCells * _charWidth + _rightPad;
        var height = _totalRows * _rowHeight + Math.Round(_rowHeight * 0.5);
        var extent = new Size(Math.Max(width, _viewport.Width), height);
        _extent = extent;
        var clamped = Clamp(_offset);
        if (clamped != _offset) _offset = clamped;
        NotifyScroll();
    }

    private (Size Extent, Size Viewport, Vector Offset) _notified;

    /// <summary>Tells the scroll viewer only when something it shows changed: a notice on every measure would loop layout.</summary>
    private void NotifyScroll()
    {
        var state = (_extent, _viewport, _offset);
        if (state == _notified) return;
        _notified = state;
        RaiseScrollInvalidated(EventArgs.Empty);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? 600 : availableSize.Height;
        var viewport = new Size(width, height);
        var widthChanged = viewport.Width != _viewport.Width;
        _viewport = viewport;
        if (widthChanged && WrapLines) _layoutDirty = true;
        RebuildLayout();
        UpdateExtent();
        return viewport;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (finalSize != _viewport)
        {
            var widthChanged = finalSize.Width != _viewport.Width;
            _viewport = finalSize;
            if (widthChanged && WrapLines) _layoutDirty = true;
            RebuildLayout();
            UpdateExtent();
        }
        return finalSize;
    }

    // ---- Rows ----

    private sealed class RowLayout
    {
        public required TextLayout Layout { get; init; }
        public required int RawStart { get; init; }
        public required int RawEnd { get; init; }
        public required double X0 { get; init; }
        public int[]? Map { get; init; }
        public bool Partial { get; init; }
        public int DisplayLength { get; init; }
        public int CellStart { get; init; }
        public int CellEnd { get; init; }
    }

    private int LineOfRow(int row)
    {
        if (_rowStart is null) return row;
        int lo = 0, hi = _lineCount - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (_rowStart[mid] <= row) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    private int FirstRowOf(int line) => _rowStart is null ? line : _rowStart[line];

    private (int Start, int End) RawRange(int line, int rowInLine)
    {
        var lineEnd = LineEnd(line);
        if (_breaks is not null && _breaks.TryGetValue(line, out var breaks))
        {
            return (breaks[rowInLine], rowInLine + 1 < breaks.Length ? breaks[rowInLine + 1] : lineEnd);
        }
        return (_lineStarts[line], lineEnd);
    }

    private RowLayout GetRow(int row)
    {
        var wanted = VisibleCells();
        if (_rows.TryGetValue(row, out var cached) && (!cached.Partial || (cached.CellStart <= wanted.First && cached.CellEnd >= wanted.Last)))
        {
            return cached;
        }
        var line = LineOfRow(row);
        var (rawStart, rawEnd) = RawRange(line, row - FirstRowOf(line));
        var partial = false;
        var cellStart = 0;
        var cell = 0;
        var cellEnd = 0;
        if (!WrapLines && rawEnd - rawStart > LongLine)
        {
            // Only the columns in view (and a margin) are laid out: a minified line does not cost its whole length.
            partial = true;
            var first = Math.Max(0, wanted.First - 64);
            var last = wanted.Last + 64;
            var i = rawStart;
            while (i < rawEnd)
            {
                var advance = CellAdvance(_text[i], cell);
                if (cell + advance > first && !char.IsLowSurrogate(_text[i])) break;
                cell += advance;
                i++;
            }
            cellStart = cell;
            var j = i;
            while (j < rawEnd && cell <= last) cell += CellAdvance(_text[j++], cell);
            while (j < rawEnd && char.IsLowSurrogate(_text[j])) j++;
            cellEnd = cell;
            rawStart = i;
            rawEnd = j;
        }

        var raw = _text.AsSpan(rawStart, rawEnd - rawStart);
        string display;
        int[]? map = null;
        if (raw.Contains('\t'))
        {
            var sb = new StringBuilder(raw.Length + 16);
            map = new int[raw.Length + 1];
            var c = cellStart;
            for (var k = 0; k < raw.Length; k++)
            {
                map[k] = sb.Length;
                if (raw[k] == '\t')
                {
                    var n = CellAdvance('\t', c);
                    sb.Append(' ', n);
                    c += n;
                }
                else
                {
                    sb.Append(raw[k]);
                    c += CellAdvance(raw[k], c);
                }
            }
            map[raw.Length] = sb.Length;
            display = sb.ToString();
        }
        else
        {
            display = raw.ToString();
        }

        var mark = Marks is { } marks && line < marks.Count ? marks[line] : default;
        var overrides = BuildOverrides(line, rawStart - _lineStarts[line], rawEnd - _lineStarts[line], map, mark.Tint == CodeLineTint.Hunk);
        var layout = new TextLayout(display, _typeface, FontSize is > 0 and < 200 ? FontSize : 13,
            mark.Tint == CodeLineTint.Hunk ? MutedForeground ?? Foreground : Foreground,
            TextAlignment.Left, TextWrapping.NoWrap, TextTrimming.None, null, FlowDirection.LeftToRight,
            double.PositiveInfinity, double.PositiveInfinity, _rowHeight, 0, 0, null, overrides);
        _layoutsCreated++;
        var result = new RowLayout
        {
            Layout = layout,
            RawStart = rawStart,
            RawEnd = rawEnd,
            X0 = cellStart * _charWidth,
            Map = map,
            Partial = partial,
            DisplayLength = display.Length,
            CellStart = cellStart,
            CellEnd = cellEnd,
        };
        if (_rows.Count > 600) _rows.Clear();
        _rows[row] = result;
        return result;
    }

    private (int First, int Last) VisibleCells()
    {
        var first = (int)Math.Floor(_offset.X / _charWidth);
        var last = (int)Math.Ceiling((_offset.X + _viewport.Width) / _charWidth);
        return (first, last);
    }

    private List<ValueSpan<TextRunProperties>>? BuildOverrides(int line, int fromCol, int toCol, int[]? map, bool plain)
    {
        if (plain || _spans[line] is not { Length: > 0 } spans) return null;
        var result = new List<ValueSpan<TextRunProperties>>();
        foreach (var span in spans)
        {
            var start = Math.Max(span.Start, fromCol);
            var end = Math.Min(span.Start + span.Length, toCol);
            if (end <= start) continue;
            var displayStart = start - fromCol;
            var displayEnd = end - fromCol;
            if (map is not null)
            {
                displayStart = map[displayStart];
                displayEnd = map[displayEnd];
            }
            if (displayEnd <= displayStart) continue;
            result.Add(new ValueSpan<TextRunProperties>(displayStart, displayEnd - displayStart, PropertiesOf(span.Role)));
        }
        return result.Count == 0 ? null : result;
    }

    private IBrush?[] EnsurePalette()
    {
        if (_roleBrushes[(int)SyntaxRole.Keyword] is not null || _roleBrushes[(int)SyntaxRole.Comment] is not null) return _roleBrushes;
        for (var i = 1; i < _roleBrushes.Length; i++)
        {
            _roleBrushes[i] = ResolveRoleBrush((SyntaxRole)i);
        }
        return _roleBrushes;
    }

    private IBrush? ResolveRoleBrush(SyntaxRole role)
    {
        var kind = role switch
        {
            SyntaxRole.Comment => CodeBlockTokenKind.SyntaxComment,
            SyntaxRole.String => CodeBlockTokenKind.SyntaxString,
            SyntaxRole.Number => CodeBlockTokenKind.SyntaxNumber,
            SyntaxRole.Keyword => CodeBlockTokenKind.SyntaxKeyword,
            SyntaxRole.Type => CodeBlockTokenKind.SyntaxType,
            SyntaxRole.Function => CodeBlockTokenKind.SyntaxFunction,
            SyntaxRole.Property => CodeBlockTokenKind.SyntaxProperty,
            SyntaxRole.Tag => CodeBlockTokenKind.SyntaxTag,
            _ => CodeBlockTokenKind.SyntaxPunctuation,
        };
        if (this.TryFindResource(CodeBlockTokens.Key(kind), ActualThemeVariant, out var value))
        {
            if (value is Color color) return new ImmutableSolidColorBrush(color);
            if (value is IBrush brush) return brush;
        }
        return Foreground;
    }

    private GenericTextRunProperties PropertiesOf(SyntaxRole role)
    {
        var index = (int)role;
        if (_roleProps[index] is { } existing) return existing;
        var size = FontSize is > 0 and < 200 ? FontSize : 13;
        var made = new GenericTextRunProperties(role == SyntaxRole.Comment ? _italic : _typeface, size, null, EnsurePalette()[index] ?? Foreground);
        _roleProps[index] = made;
        return made;
    }

    // ---- Drawing ----

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        RebuildLayout();
        _renderedRows = 0;
        var bounds = new Rect(Bounds.Size);
        if (Background is { } page) context.FillRectangle(page, bounds);
        var firstRow = Math.Max(0, (int)Math.Floor(_offset.Y / _rowHeight));
        var lastRow = Math.Min(_totalRows - 1, (int)Math.Ceiling((_offset.Y + bounds.Height) / _rowHeight));
        var marks = Marks;
        var (selStart, selEnd) = Ordered();
        var selecting = HasSelection;
        var scrollX = WrapLines ? 0 : _offset.X;
        var textOriginX = _textLeft - scrollX;
        var textClip = new Rect(_gutterWidth, 0, Math.Max(0, bounds.Width - _gutterWidth), bounds.Height);

        // Row backgrounds: the diff tint and the line under the pointer.
        for (var row = firstRow; row <= lastRow; row++)
        {
            var line = LineOfRow(row);
            var y = row * _rowHeight - _offset.Y;
            var rowRect = new Rect(0, y, bounds.Width, _rowHeight);
            if (marks is not null && line < marks.Count && marks[line].Tint != CodeLineTint.None)
            {
                var brush = marks[line].Tint switch { CodeLineTint.Added => AddedBrush, CodeLineTint.Removed => RemovedBrush, _ => HunkBrush };
                if (brush is not null)
                {
                    using (context.PushOpacity(marks[line].Tint == CodeLineTint.Hunk ? 0.10 : 0.16))
                    {
                        context.FillRectangle(brush, rowRect);
                    }
                }
            }
            if (line == _hoverLine && HoverBrush is { } hover)
            {
                context.FillRectangle(hover, rowRect);
            }
        }

        using (context.PushClip(textClip))
        {
            for (var row = firstRow; row <= lastRow; row++)
            {
                var line = LineOfRow(row);
                var y = row * _rowHeight - _offset.Y;
                var layout = GetRow(row);
                _renderedRows++;
                if (selecting && line >= selStart.Line && line <= selEnd.Line && SelectionBrush is { } selection)
                {
                    var rect = SelectionRect(layout, line, selStart, selEnd, row);
                    if (rect is { } r)
                    {
                        context.FillRectangle(selection, new Rect(textOriginX + layout.X0 + r.X, y, r.Width, _rowHeight));
                    }
                }
                layout.Layout.Draw(context, new Point(textOriginX + layout.X0, y + (_rowHeight - layout.Layout.Height) / 2));
            }
        }

        // The gutter covers the text that scrolled under it.
        DrawGutter(context, bounds, firstRow, lastRow, marks);
    }

    private Rect? SelectionRect(RowLayout row, int line, (int Line, int Col) start, (int Line, int Col) end, int rowIndex)
    {
        var lineStart = _lineStarts[line];
        var from = line == start.Line ? lineStart + start.Col : lineStart;
        var to = line == end.Line ? lineStart + end.Col : int.MaxValue;
        var segmentStart = Math.Max(from, row.RawStart);
        var segmentEnd = Math.Min(to, row.RawEnd);
        var continues = line < end.Line && rowIndex == FirstRowOf(line) + RowsOfLine(line) - 1;
        if (segmentEnd < segmentStart || (segmentEnd == segmentStart && !continues)) return null;
        if (row.Partial && segmentEnd == segmentStart && !continues) return null;
        var x0 = DisplayX(row, segmentStart);
        var x1 = DisplayX(row, segmentEnd);
        if (continues && segmentEnd >= row.RawEnd) x1 += _charWidth * 0.6;
        return x1 > x0 ? new Rect(x0, 0, x1 - x0, _rowHeight) : null;
    }

    private int RowsOfLine(int line) => _breaks is not null && _breaks.TryGetValue(line, out var breaks) ? breaks.Length : 1;

    private static double DisplayX(RowLayout row, int raw)
    {
        var index = Math.Clamp(raw - row.RawStart, 0, row.RawEnd - row.RawStart);
        var display = row.Map is null ? index : row.Map[index];
        return row.Layout.HitTestTextPosition(display).X;
    }

    private void DrawGutter(DrawingContext context, Rect bounds, int firstRow, int lastRow, IReadOnlyList<CodeLineMark>? marks)
    {
        var gutter = new Rect(0, 0, _gutterWidth, bounds.Height);
        // The page color under the numbers keeps text that scrolled sideways from showing through.
        if (Background is { } background) context.FillRectangle(background, gutter);

        var pad = Math.Round(_charWidth * 1.5);
        for (var row = firstRow; row <= lastRow; row++)
        {
            var line = LineOfRow(row);
            if (row != FirstRowOf(line)) continue;
            var y = row * _rowHeight - _offset.Y;
            if (marks is null)
            {
                var number = NumberLayout(line + 1);
                number.Draw(context, new Point(_gutterWidth - pad - number.Width, y + (_rowHeight - number.Height) / 2));
                continue;
            }
            if (line >= marks.Count) continue;
            var mark = marks[line];
            if (mark.Tint == CodeLineTint.Hunk || (mark.Number1.Length == 0 && mark.Number2.Length == 0 && mark.Marker.Length == 0)) continue;
            var wide = 1;
            foreach (var m in marks) wide = Math.Max(wide, Math.Max(m.Number1.Length, m.Number2.Length));
            var columnWidth = wide * _charWidth;
            DrawNumber(context, mark.Number1, pad + columnWidth, y);
            DrawNumber(context, mark.Number2, pad + columnWidth + _charWidth * 1.5 + columnWidth, y);
            if (mark.Marker.Length > 0)
            {
                var markerBrush = mark.Tint switch { CodeLineTint.Added => AddedBrush, CodeLineTint.Removed => RemovedBrush, _ => GutterForeground };
                var layout = new TextLayout(mark.Marker, _typeface, FontSize, markerBrush ?? GutterForeground);
                layout.Draw(context, new Point(_gutterWidth - _charWidth * 1.8, y + (_rowHeight - layout.Height) / 2));
            }
        }

        if (_offset.X > 0.5 && !WrapLines && DividerBrush is { } divider)
        {
            context.FillRectangle(divider, new Rect(_gutterWidth - 1, 0, 1, bounds.Height));
        }
    }

    private void DrawNumber(DrawingContext context, string text, double right, double y)
    {
        if (text.Length == 0) return;
        var layout = NumberLayoutFor(text);
        layout.Draw(context, new Point(right - layout.Width, y + (_rowHeight - layout.Height) / 2));
    }

    private TextLayout NumberLayout(int number)
    {
        if (_numbers.TryGetValue(number, out var layout)) return layout;
        if (_numbers.Count > 2000) _numbers.Clear();
        layout = new TextLayout(number.ToString(CultureInfo.InvariantCulture), _typeface, FontSize is > 0 and < 200 ? FontSize : 13, GutterForeground ?? Foreground);
        _numbers[number] = layout;
        return layout;
    }

    private TextLayout NumberLayoutFor(string text) => new(text, _typeface, FontSize is > 0 and < 200 ? FontSize : 13, GutterForeground ?? Foreground);

    // ---- Pointer and keyboard ----

    private (int Line, int Col) HitTest(Point point)
    {
        var row = (int)Math.Floor((point.Y + _offset.Y) / _rowHeight);
        row = Math.Clamp(row, 0, _totalRows - 1);
        var line = LineOfRow(row);
        var layout = GetRow(row);
        var scrollX = WrapLines ? 0 : _offset.X;
        var x = point.X - (_textLeft - scrollX) - layout.X0;
        var hit = layout.Layout.HitTestPoint(new Point(Math.Max(0, x), layout.Layout.Height / 2));
        var display = Math.Clamp(hit.TextPosition, 0, layout.DisplayLength);
        var raw = layout.RawStart + RawOffsetOf(layout, display);
        return (line, Math.Clamp(raw - _lineStarts[line], 0, LineEnd(line) - _lineStarts[line]));
    }

    private static int RawOffsetOf(RowLayout row, int display)
    {
        if (row.Map is null) return Math.Min(display, row.RawEnd - row.RawStart);
        var length = row.RawEnd - row.RawStart;
        for (var k = 0; k < length; k++)
        {
            if (row.Map[k + 1] > display) return display - row.Map[k] > (row.Map[k + 1] - row.Map[k]) / 2 ? k + 1 : k;
        }
        return length;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;
        var position = HitTest(point.Position);
        if (e.ClickCount == 2)
        {
            SelectWord(position);
        }
        else if (e.ClickCount >= 3)
        {
            _anchor = (position.Line, 0);
            _caret = (position.Line, LineEnd(position.Line) - _lineStarts[position.Line]);
            _hasSelection = true;
        }
        else
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift) || !_hasSelection) _anchor = position;
            _caret = position;
            _hasSelection = true;
            _dragging = true;
            e.Pointer.Capture(this);
        }
        InvalidateVisual();
        e.Handled = true;
    }

    private void SelectWord((int Line, int Col) position)
    {
        var start = _lineStarts[position.Line];
        var length = LineEnd(position.Line) - start;
        var from = Math.Min(position.Col, length);
        var to = from;
        static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';
        if (from < length && IsWord(_text[start + from]))
        {
            while (from > 0 && IsWord(_text[start + from - 1])) from--;
            while (to < length && IsWord(_text[start + to])) to++;
        }
        else if (from < length)
        {
            to = from + 1;
        }
        _anchor = (position.Line, from);
        _caret = (position.Line, to);
        _hasSelection = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        var row = Math.Clamp((int)Math.Floor((point.Y + _offset.Y) / _rowHeight), 0, _totalRows - 1);
        var hover = point.X >= 0 && point.X <= Bounds.Width && point.Y >= 0 && point.Y <= Bounds.Height ? LineOfRow(row) : -1;
        if (hover != _hoverLine)
        {
            _hoverLine = hover;
            InvalidateVisual();
        }
        if (!_dragging) return;
        // Dragging past the top or bottom keeps scrolling.
        if (point.Y < 0) SetOffset(_offset - new Vector(0, _rowHeight), true);
        else if (point.Y > Bounds.Height) SetOffset(_offset + new Vector(0, _rowHeight), true);
        _caret = HitTest(point);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoverLine < 0) return;
        _hoverLine = -1;
        InvalidateVisual();
    }

    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        // The platform's own shortcuts (Cmd on macOS, Ctrl elsewhere): the app does not decide the modifier.
        var keys = Application.Current?.PlatformSettings?.HotkeyConfiguration;
        if (keys is null) return;
        if (keys.SelectAll.Any(g => g.Matches(e)))
        {
            SelectAll();
            e.Handled = true;
        }
        else if (keys.Copy.Any(g => g.Matches(e)))
        {
            _ = CopyAsync();
            e.Handled = true;
        }
    }

    // ---- Highlighting ----

    private static TaskCompletionSource Completed()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        done.TrySetResult();
        return done;
    }

    private void QueueHighlight()
    {
        if (_highlightQueued) return;
        _highlightQueued = true;
        // Text, language and marks arrive one after the other from bindings: they are read once, after all three.
        Dispatcher.UIThread.Post(() =>
        {
            _highlightQueued = false;
            StartHighlight();
        }, DispatcherPriority.Loaded);
    }

    private void StartHighlight()
    {
        _highlightCts?.Cancel();
        var version = ++_highlightVersion;
        var language = Language;
        if (!_highlightDone.Task.IsCompleted) _highlightDone.TrySetResult();
        _highlightDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var done = _highlightDone;
        if (_lineCount == 0 || _text.Length == 0 || !CodeSyntax.Supports(language))
        {
            done.TrySetResult();
            return;
        }

        var marks = Marks;
        var lines = new string[_lineCount];
        for (var i = 0; i < lines.Length; i++)
        {
            var length = Math.Min(LineEnd(i) - _lineStarts[i], HighlightLineLimit);
            lines[i] = _text.Substring(_lineStarts[i], length);
        }
        var cts = _highlightCts = new CancellationTokenSource();
        var token = cts.Token;
        var streams = new List<(List<int> Lines, bool OnlyOld)>();
        if (marks is null)
        {
            streams.Add((Enumerable.Range(0, _lineCount).ToList(), false));
        }
        else
        {
            var oldSide = new List<int>();
            var newSide = new List<int>();
            for (var i = 0; i < _lineCount && i < marks.Count; i++)
            {
                if ((marks[i].Sides & 1) != 0) oldSide.Add(i);
                if ((marks[i].Sides & 2) != 0) newSide.Add(i);
            }
            streams.Add((newSide, false));
            streams.Add((oldSide, true));
        }

        _ = Task.Run(() =>
        {
            try
            {
                foreach (var (indexes, onlyOld) in streams)
                {
                    RunStream(version, language, lines, indexes, onlyOld ? marks : null, token);
                }
            }
            catch (Exception)
            {
                // A grammar that fails leaves the text plain.
            }
            finally
            {
                Dispatcher.UIThread.Post(() => done.TrySetResult(), DispatcherPriority.Background);
            }
        }, token);
    }

    private void RunStream(int version, string? language, string[] lines, List<int> indexes, IReadOnlyList<CodeLineMark>? onlyOldOf, CancellationToken token)
    {
        if (indexes.Count == 0) return;
        using var session = CodeSyntax.CreateSession(language);
        if (session is null) return;
        var starts = new int[indexes.Count + 1];
        var sb = new StringBuilder();
        for (var k = 0; k < indexes.Count; k++)
        {
            starts[k] = sb.Length;
            sb.Append(lines[indexes[k]]);
            sb.Append('\n');
        }
        starts[indexes.Count] = sb.Length;
        var joined = sb.ToString();
        var done = 0;
        var chunk = 300;
        while (done < indexes.Count)
        {
            token.ThrowIfCancellationRequested();
            var end = Math.Min(indexes.Count, done + chunk);
            var prefix = end == indexes.Count ? joined : joined.Substring(0, starts[end]);
            var tokens = session.Update(prefix);
            var batch = new (int Line, SyntaxSpan[] Spans)[end - done];
            for (var k = done; k < end; k++)
            {
                var line = lines[indexes[k]];
                var lineTokens = k < tokens.Count ? tokens[k] : default;
                batch[k - done] = (indexes[k], CodeSyntax.SpansOf(language, lineTokens, line));
            }
            done = end;
            chunk = Math.Min(chunk * 2, 6000);
            Dispatcher.UIThread.Post(() => ApplySpans(version, batch, onlyOldOf), DispatcherPriority.Background);
        }
    }

    private void ApplySpans(int version, (int Line, SyntaxSpan[] Spans)[] batch, IReadOnlyList<CodeLineMark>? onlyOldOf)
    {
        if (version != _highlightVersion) return;
        foreach (var (line, spans) in batch)
        {
            if (line >= _spans.Length) continue;
            // The old side only colors the removed lines: a context line took its colors from the new side.
            if (onlyOldOf is not null && line < onlyOldOf.Count && (onlyOldOf[line].Sides & 2) != 0) continue;
            _spans[line] = spans;
        }
        _rows.Clear();
        InvalidateVisual();
    }

    // ---- ILogicalScrollable ----

    private Vector Clamp(Vector value) =>
        new(WrapLines ? 0 : Math.Clamp(value.X, 0, Math.Max(0, _extent.Width - _viewport.Width)),
            Math.Clamp(value.Y, 0, Math.Max(0, _extent.Height - _viewport.Height)));

    private void SetOffset(Vector value, bool raise)
    {
        var clamped = Clamp(value);
        if (clamped == _offset) return;
        _offset = clamped;
        InvalidateVisual();
        if (raise) NotifyScroll();
    }

    public bool CanHorizontallyScroll { get; set; } = true;
    public bool CanVerticallyScroll { get; set; } = true;
    public bool IsLogicalScrollEnabled => true;
    public Size ScrollSize => new(_charWidth * 4, _rowHeight * 3);
    public Size PageScrollSize => new(Math.Max(0, _viewport.Width - _gutterWidth), Math.Max(_rowHeight, _viewport.Height - _rowHeight));
    public Size Extent => _extent;
    public Size Viewport => _viewport;

    public Vector Offset
    {
        get => _offset;
        set => SetOffset(value, true);
    }

    public event EventHandler? ScrollInvalidated;

    public bool BringIntoView(Control target, Rect targetRect) => false;

    public Control? GetControlInDirection(NavigationDirection direction, Control? from) => null;

    public void RaiseScrollInvalidated(EventArgs e) => ScrollInvalidated?.Invoke(this, e);
}
