using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Octopus.App.Common.Mentions;

/// <summary>
/// Turns <c>@mention</c> tokens inside any <see cref="TextBox"/> (the desktop composer, the phone's <c>TextField</c>)
/// into atomic, highlighted units while the text stays a plain string, so IMEs, soft keyboards, selection, undo and
/// the clipboard keep working as the platform implements them.
/// <list type="bullet">
/// <item>Highlight: a <see cref="MentionHighlightLayer"/> placed under the text presenter paints a pill behind each token.</item>
/// <item>Atomic: after every text change, an edit that removed part of a token removes the rest of it
/// (<see cref="MentionText.RepairEdit"/>); this runs on the text, not on keys, because Android and iOS keyboards often
/// delete through the input connection without a key event.</item>
/// <item>Caret: a caret that lands inside a token moves to its edge (<see cref="MentionText.SnapCaret"/>).</item>
/// </list>
/// Nothing is rewritten while an IME is composing (pre-edit text showing).
/// </summary>
public static class MentionEditing
{
    /// <summary>The tokens of the current draft. Setting it on a TextBox switches the behaviour on.</summary>
    public static readonly AttachedProperty<IReadOnlyList<MentionToken>?> TokensProperty =
        AvaloniaProperty.RegisterAttached<TextBox, IReadOnlyList<MentionToken>?>("Tokens", typeof(MentionEditing));

    /// <summary>Fill of the pill drawn behind each token.</summary>
    public static readonly AttachedProperty<IBrush?> HighlightBrushProperty =
        AvaloniaProperty.RegisterAttached<TextBox, IBrush?>("HighlightBrush", typeof(MentionEditing),
            new SolidColorBrush(Color.FromArgb(0x33, 0x4C, 0x8D, 0xFF)));

    private static readonly AttachedProperty<State?> StateProperty =
        AvaloniaProperty.RegisterAttached<TextBox, State?>("State", typeof(MentionEditing));

    static MentionEditing()
    {
        TokensProperty.Changed.AddClassHandler<TextBox>(static (box, _) => EnsureState(box).OnTokensChanged());
        HighlightBrushProperty.Changed.AddClassHandler<TextBox>(static (box, e) =>
        {
            if (box.GetValue(StateProperty) is { } state) state.SetBrush(e.NewValue as IBrush);
        });
    }

    public static IReadOnlyList<MentionToken>? GetTokens(TextBox box) => box.GetValue(TokensProperty);

    public static void SetTokens(TextBox box, IReadOnlyList<MentionToken>? value) => box.SetValue(TokensProperty, value);

    public static IBrush? GetHighlightBrush(TextBox box) => box.GetValue(HighlightBrushProperty);

    public static void SetHighlightBrush(TextBox box, IBrush? value) => box.SetValue(HighlightBrushProperty, value);

    /// <summary>The token spans currently in the box's text (empty when the behaviour is off).</summary>
    public static IReadOnlyList<MentionSpan> GetSpans(TextBox box) => box.GetValue(StateProperty)?.Spans ?? [];

    private static State EnsureState(TextBox box)
    {
        if (box.GetValue(StateProperty) is { } existing) return existing;
        var state = new State(box);
        box.SetValue(StateProperty, state);
        return state;
    }

    private sealed class State
    {
        private readonly TextBox _box;
        private readonly MentionHighlightLayer _layer = new();
        private TextPresenter? _presenter;
        private AdornerLayer? _adornerHost;
        private string _text;
        private int _caret;
        private bool _repairPending;

        public State(TextBox box)
        {
            _box = box;
            _text = box.Text ?? "";
            _caret = box.CaretIndex;
            _layer.Brush = box.GetValue(HighlightBrushProperty);
            box.PropertyChanged += OnBoxPropertyChanged;
            box.TemplateApplied += OnTemplateApplied;
            if (box.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault() is { } presenter) Attach(presenter);
        }

        public IReadOnlyList<MentionSpan> Spans { get; private set; } = [];

        private IReadOnlyList<MentionToken> Tokens => _box.GetValue(TokensProperty) ?? [];

        public void OnTokensChanged()
        {
            Spans = MentionText.Scan(_box.Text, Tokens);
            _layer.Update(Spans);
        }

        public void SetBrush(IBrush? brush)
        {
            _layer.Brush = brush;
            _layer.InvalidateVisual();
        }

        private void OnTemplateApplied(object? sender, TemplateAppliedEventArgs e)
        {
            Detach();
            if (e.NameScope.Find<TextPresenter>("PART_TextPresenter") is { } presenter) Attach(presenter);
        }

        private void Attach(TextPresenter presenter)
        {
            _presenter = presenter;
            _layer.Presenter = presenter;
            presenter.LayoutUpdated += OnPresenterLayoutUpdated;
            if (presenter.Parent is Panel panel)
            {
                // Under the text, inside the scroll content, so pills scroll and wrap with the text.
                _layer.IsHitTestVisible = false;
                panel.Children.Insert(panel.Children.IndexOf(presenter), _layer);
            }
            else if (AdornerLayer.GetAdornerLayer(presenter) is { } adorners)
            {
                // Templates without a panel around the presenter: draw on the adorner layer, over the text.
                _layer.IsHitTestVisible = false;
                _layer.Opacity = 0.6;
                AdornerLayer.SetAdornedElement(_layer, presenter);
                adorners.Children.Add(_layer);
                _adornerHost = adorners;
            }
        }

        private void Detach()
        {
            _presenter?.LayoutUpdated -= OnPresenterLayoutUpdated;
            if (_layer.Parent is Panel panel) panel.Children.Remove(_layer);
            _adornerHost?.Children.Remove(_layer);
            _adornerHost = null;
            _presenter = null;
            _layer.Presenter = null;
        }

        private void OnPresenterLayoutUpdated(object? sender, EventArgs e)
        {
            if (Spans.Count > 0) _layer.InvalidateVisual();
        }

        private bool IsComposing => !string.IsNullOrEmpty(_presenter?.PreeditText);

        private void OnBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == TextBox.TextProperty) OnTextChanged(_box.Text ?? "");
            else if (e.Property == TextBox.CaretIndexProperty) OnCaretChanged(_box.CaretIndex);
        }

        private void OnTextChanged(string text)
        {
            var oldText = _text;
            var oldSpans = Spans;
            _text = text;
            Spans = MentionText.Scan(text, Tokens);
            _layer.Update(Spans);

            if (IsComposing || _repairPending) return;
            if (MentionText.RepairEdit(oldText, oldSpans, text) is not { } repair) return;

            // Rewrite after the TextBox (and the IME bridge) finished handling this change; Normal runs before the next
            // render, so the half-deleted token never shows.
            _repairPending = true;
            Dispatcher.UIThread.Post(() =>
            {
                _repairPending = false;
                if (!string.Equals(_box.Text, text, StringComparison.Ordinal)) return; // something else changed it meanwhile
                _box.Text = repair.Text;
                _box.ClearSelection();
                _box.CaretIndex = repair.Caret;
            }, DispatcherPriority.Normal);
        }

        private void OnCaretChanged(int caret)
        {
            var previous = _caret;
            _caret = caret;
            if (Spans.Count == 0 || IsComposing || _repairPending) return;
            if (_box.SelectionStart != _box.SelectionEnd) return; // selections may cover tokens partly; deleting one repairs it
            var snapped = MentionText.SnapCaret(caret, previous, Spans);
            if (snapped == caret) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (_box.CaretIndex != caret || _box.SelectionStart != _box.SelectionEnd) return;
                _box.CaretIndex = snapped;
            }, DispatcherPriority.Normal);
        }
    }
}

/// <summary>Paints a rounded pill behind each mention token of a <see cref="TextPresenter"/>.</summary>
public sealed class MentionHighlightLayer : Control
{
    private IReadOnlyList<MentionSpan> _spans = [];

    internal TextPresenter? Presenter { get; set; }

    internal IBrush? Brush { get; set; }

    internal void Update(IReadOnlyList<MentionSpan> spans)
    {
        if (_spans.Count == 0 && spans.Count == 0) return;
        _spans = spans;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (_spans.Count == 0 || Presenter is not { } presenter || Brush is not { } brush) return;
        if (!string.IsNullOrEmpty(presenter.PreeditText)) return; // the layout shows the pre-edit text; offsets differ

        var textLength = presenter.Text?.Length ?? 0;
        var origin = presenter.TranslatePoint(default, this) ?? default;
        var layout = presenter.TextLayout;
        foreach (var span in _spans)
        {
            if (span.End > textLength) continue;
            foreach (var rect in layout.HitTestTextRange(span.Start, span.Length))
            {
                var pill = new Rect(origin.X + rect.X - 2, origin.Y + rect.Y + 1, rect.Width + 4, Math.Max(0, rect.Height - 2));
                context.DrawRectangle(brush, null, pill, 4, 4);
            }
        }
    }
}
