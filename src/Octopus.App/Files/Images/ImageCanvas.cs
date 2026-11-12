using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Octopus.App.Files.Images;

/// <summary>
/// Draws the picture of an <see cref="ImageViewerModel"/> on a neutral ground, with a checkerboard behind it so
/// transparency shows. Input becomes calls on the model: the platform's command key with the wheel, a touchpad
/// magnify or a pinch zooms, a plain wheel or a drag moves the picture, a double click toggles fit and 100%.
/// </summary>
public sealed class ImageCanvas : Control
{
    public static readonly StyledProperty<IBrush?> GroundProperty = AvaloniaProperty.Register<ImageCanvas, IBrush?>(nameof(Ground));
    public static readonly StyledProperty<IBrush?> CheckerLightProperty = AvaloniaProperty.Register<ImageCanvas, IBrush?>(nameof(CheckerLight));
    public static readonly StyledProperty<IBrush?> CheckerDarkProperty = AvaloniaProperty.Register<ImageCanvas, IBrush?>(nameof(CheckerDark));
    public static readonly StyledProperty<IBrush?> EdgeProperty = AvaloniaProperty.Register<ImageCanvas, IBrush?>(nameof(Edge));

    private const double CheckerSize = 8;
    private const double WheelPanStep = 48;

    private ImageViewerModel? _model;
    private Point? _dragFrom;
    private double _pinchStartZoom;
    private DrawingBrush? _checker;

    public ImageCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        // A finger can pinch: the recognizer turns two touches into Pinch events.
        GestureRecognizers.Add(new PinchGestureRecognizer());
        AddHandler(PinchEvent, OnPinch);
        AddHandler(PinchEndedEvent, (_, _) => _pinchStartZoom = 0);
        AddHandler(PointerTouchPadGestureMagnifyEvent, OnMagnify);
    }

    /// <summary>The ground the picture lies on.</summary>
    public IBrush? Ground { get => GetValue(GroundProperty); set => SetValue(GroundProperty, value); }
    public IBrush? CheckerLight { get => GetValue(CheckerLightProperty); set => SetValue(CheckerLightProperty, value); }
    public IBrush? CheckerDark { get => GetValue(CheckerDarkProperty); set => SetValue(CheckerDarkProperty, value); }

    /// <summary>The hairline around the picture.</summary>
    public IBrush? Edge { get => GetValue(EdgeProperty); set => SetValue(EdgeProperty, value); }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null) _model.ViewChanged -= OnViewChanged;
        _model = DataContext as ImageViewerModel;
        if (_model is not null)
        {
            _model.ViewChanged += OnViewChanged;
            if (Bounds.Size != default) _model.SetViewSize(Bounds.Size);
        }
        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_model is not null) _model.ViewChanged -= OnViewChanged;
        _model = null;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_model is null && DataContext is ImageViewerModel model)
        {
            _model = model;
            model.ViewChanged += OnViewChanged;
        }
    }

    private void OnViewChanged(object? sender, EventArgs e)
    {
        Cursor = _model?.Viewport is { CanPan: true } ? new Cursor(StandardCursorType.SizeAll) : null;
        InvalidateVisual();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _model?.SetViewSize(finalSize);
        return base.ArrangeOverride(finalSize);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CheckerLightProperty || change.Property == CheckerDarkProperty) _checker = null;
        if (change.Property == GroundProperty || change.Property == CheckerLightProperty || change.Property == CheckerDarkProperty || change.Property == EdgeProperty) InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        context.DrawRectangle(Ground, null, new Rect(Bounds.Size));
        if (_model is not { Image: { } image, Viewport: { } viewport }) return;
        var target = viewport.ImageRect;
        var scaling = (VisualRoot as TopLevel)?.RenderScaling ?? 1;
        target = Snap(target, scaling);
        // Behind the picture: the checkerboard (the picture's own pixels, transparent or not, lie over it).
        context.DrawRectangle(CheckerBrush(), null, target);
        var interpolation = viewport.Zoom >= 2 ? BitmapInterpolationMode.None : viewport.Zoom >= 1 ? BitmapInterpolationMode.MediumQuality : BitmapInterpolationMode.HighQuality;
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = interpolation }))
        {
            context.DrawImage(image, new Rect(0, 0, image.PixelSize.Width, image.PixelSize.Height), target);
        }
        if (Edge is { } edge) context.DrawRectangle(null, new Pen(edge, 1), target.Inflate(0.5));
    }

    /// <summary>The corners on whole device pixels, so a small picture shows sharp edges instead of a blurred rim.</summary>
    private static Rect Snap(Rect rect, double scaling)
    {
        double S(double v) => Math.Round(v * scaling) / scaling;
        var left = S(rect.Left);
        var top = S(rect.Top);
        return new Rect(left, top, Math.Max(1 / scaling, S(rect.Right) - left), Math.Max(1 / scaling, S(rect.Bottom) - top));
    }

    private IBrush CheckerBrush()
    {
        if (_checker is not null) return _checker;
        var light = CheckerLight ?? Brushes.White;
        var dark = CheckerDark ?? Brushes.LightGray;
        var drawing = new DrawingGroup
        {
            Children =
            {
                new GeometryDrawing { Brush = light, Geometry = new RectangleGeometry(new Rect(0, 0, CheckerSize * 2, CheckerSize * 2)) },
                new GeometryDrawing { Brush = dark, Geometry = new RectangleGeometry(new Rect(0, 0, CheckerSize, CheckerSize)) },
                new GeometryDrawing { Brush = dark, Geometry = new RectangleGeometry(new Rect(CheckerSize, CheckerSize, CheckerSize, CheckerSize)) },
            },
        };
        return _checker = new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.None,
            DestinationRect = new RelativeRect(0, 0, CheckerSize * 2, CheckerSize * 2, RelativeUnit.Absolute),
            SourceRect = new RelativeRect(0, 0, CheckerSize * 2, CheckerSize * 2, RelativeUnit.Absolute),
        };
    }

    private static KeyModifiers CommandModifiers => Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_model is null) return;
        if ((e.KeyModifiers & CommandModifiers) != 0)
        {
            _model.ZoomByWheel(e.Delta.Y, e.GetPosition(this));
        }
        else
        {
            _model.Pan(new Vector(e.Delta.X * WheelPanStep, e.Delta.Y * WheelPanStep));
        }
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (_model is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2)
        {
            _model.ToggleFitCommand.Execute(null);
            _dragFrom = null;
            e.Handled = true;
            return;
        }
        _dragFrom = e.GetPosition(this);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_model is null || _dragFrom is not { } from) return;
        var now = e.GetPosition(this);
        _model.Pan(now - from);
        _dragFrom = now;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragFrom is null) return;
        _dragFrom = null;
        e.Pointer.Capture(null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_model is null || (e.KeyModifiers & ~(CommandModifiers | KeyModifiers.Shift)) != 0) return;
        switch (e.Key)
        {
            case Key.OemPlus or Key.Add:
                _model.ZoomInCommand.Execute(null);
                break;
            case Key.OemMinus or Key.Subtract:
                _model.ZoomOutCommand.Execute(null);
                break;
            case Key.D0 or Key.NumPad0:
                _model.ActualSizeCommand.Execute(null);
                break;
            case Key.D9 or Key.NumPad9:
                _model.FitCommand.Execute(null);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void OnPinch(object? sender, PinchEventArgs e)
    {
        if (_model?.Viewport is not { } viewport) return;
        if (_pinchStartZoom == 0) _pinchStartZoom = viewport.Zoom;
        _model.ZoomByFactor(_pinchStartZoom * e.Scale / viewport.Zoom, e.ScaleOrigin);
        e.Handled = true;
    }

    private void OnMagnify(object? sender, PointerDeltaEventArgs e)
    {
        // The touchpad's magnify gesture: Delta.X is the change of scale since the last event.
        _model?.ZoomByFactor(1 + e.Delta.X, e.GetPosition(this));
        e.Handled = true;
    }
}
