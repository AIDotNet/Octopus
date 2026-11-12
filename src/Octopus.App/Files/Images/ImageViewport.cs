using Avalonia;

namespace Octopus.App.Files.Images;

/// <summary>
/// The zoom and position of a picture inside a window, as plain numbers: where the picture is drawn, what "fit" means,
/// how a wheel or a pinch changes the zoom about a point. No controls are involved, so the arithmetic is tested alone.
/// </summary>
/// <remarks>
/// <see cref="Zoom"/> 1 is one picture pixel per device-independent pixel. In fit mode the zoom follows the window and
/// never goes above 1 (a small picture is not blown up). A picture smaller than the window in one direction is centered
/// in it; larger, it can be moved but never beyond its edges.
/// </remarks>
public sealed class ImageViewport(double imageWidth, double imageHeight)
{
    /// <summary>The empty room kept around the picture in fit mode.</summary>
    public const double FitMargin = 12;

    public const double MinZoom = 0.02;
    public const double MaxZoom = 64;

    /// <summary>The zoom the buttons and keys step through (a fraction of 1 is a percent over 100).</summary>
    public static readonly double[] Steps = [0.05, 0.1, 0.25, 0.5, 0.75, 1, 1.5, 2, 3, 4, 6, 8, 12, 16, 24, 32];

    private Size _view;
    private double _zoom = 1;
    private Vector _offset;

    public double ImageWidth { get; } = imageWidth;
    public double ImageHeight { get; } = imageHeight;

    /// <summary>The window (the area the picture is shown in).</summary>
    public Size View => _view;

    /// <summary>True while the zoom follows the window ("fit").</summary>
    public bool IsFit { get; private set; } = true;

    public double Zoom => _zoom;

    /// <summary>Where the picture's top-left corner is, in the window's own coordinates.</summary>
    public Vector Offset => _offset;

    /// <summary>The whole picture, as drawn.</summary>
    public Rect ImageRect => new(_offset.X, _offset.Y, ImageWidth * _zoom, ImageHeight * _zoom);

    public int Percent => (int)Math.Round(_zoom * 100);

    /// <summary>The zoom that shows the whole picture in the window, at most 1.</summary>
    public double FitZoom
    {
        get
        {
            var availableWidth = Math.Max(1, _view.Width - 2 * FitMargin);
            var availableHeight = Math.Max(1, _view.Height - 2 * FitMargin);
            return Math.Clamp(Math.Min(1, Math.Min(availableWidth / ImageWidth, availableHeight / ImageHeight)), MinZoom, 1);
        }
    }

    /// <summary>The window changed size (also its first size): fit follows it, a chosen zoom keeps its place.</summary>
    public void SetView(Size view)
    {
        var first = _view == default;
        var center = first ? default : CenterPoint;
        _view = view;
        if (IsFit) ApplyFit();
        else
        {
            // Keep the picture point that was at the middle of the window at the middle.
            if (!first) _offset = new Vector(view.Width / 2 - center.X * _zoom, view.Height / 2 - center.Y * _zoom);
            Clamp();
        }
    }

    /// <summary>The picture point (in picture pixels) at the middle of the window.</summary>
    private Point CenterPoint => new((_view.Width / 2 - _offset.X) / _zoom, (_view.Height / 2 - _offset.Y) / _zoom);

    /// <summary>Back to fitting the window.</summary>
    public void Fit()
    {
        IsFit = true;
        ApplyFit();
    }

    /// <summary>100%: one picture pixel per pixel, about the middle of the window.</summary>
    public void ActualSize() => SetZoom(1, Center);

    /// <summary>Between fit and 100%, as a double click does.</summary>
    public void ToggleFitActual()
    {
        if (IsFit && FitZoom < 1) ActualSize();
        else Fit();
    }

    public void ZoomIn() => SetZoom(NextStep(_zoom, +1), Center);
    public void ZoomOut() => SetZoom(NextStep(_zoom, -1), Center);

    /// <summary>The wheel: each notch (delta 1) is 10%; a finer delta from a touchpad scales along.</summary>
    public void ZoomByWheel(double delta, Point anchor) => SetZoom(_zoom * Math.Pow(1.1, delta), anchor);

    /// <summary>A pinch or a magnify gesture: <paramref name="factor"/> times the zoom, about the fingers.</summary>
    public void ZoomByFactor(double factor, Point anchor) => SetZoom(_zoom * factor, anchor);

    /// <summary>Sets the zoom so the picture point under <paramref name="anchor"/> (window coordinates) stays under it.</summary>
    public void SetZoom(double zoom, Point anchor)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        if (_view == default)
        {
            _zoom = zoom;
            IsFit = false;
            return;
        }
        var picturePoint = new Point((anchor.X - _offset.X) / _zoom, (anchor.Y - _offset.Y) / _zoom);
        _zoom = zoom;
        IsFit = false;
        _offset = new Vector(anchor.X - picturePoint.X * zoom, anchor.Y - picturePoint.Y * zoom);
        Clamp();
    }

    /// <summary>Moves the picture by a drag or a scroll (window pixels).</summary>
    public void Pan(Vector delta)
    {
        _offset += delta;
        Clamp();
    }

    /// <summary>True when the picture is larger than the window in some direction (a drag moves it).</summary>
    public bool CanPan => ImageWidth * _zoom > _view.Width + 0.5 || ImageHeight * _zoom > _view.Height + 0.5;

    private Point Center => new(_view.Width / 2, _view.Height / 2);

    private void ApplyFit()
    {
        _zoom = FitZoom;
        _offset = new Vector((_view.Width - ImageWidth * _zoom) / 2, (_view.Height - ImageHeight * _zoom) / 2);
    }

    private void Clamp()
    {
        _offset = new Vector(ClampAxis(_offset.X, ImageWidth * _zoom, _view.Width), ClampAxis(_offset.Y, ImageHeight * _zoom, _view.Height));
    }

    private static double ClampAxis(double offset, double size, double view) =>
        size <= view ? (view - size) / 2 : Math.Clamp(offset, view - size, 0);

    /// <summary>The next zoom in a direction from the list of steps (a zoom between two steps goes to the nearer step in that direction).</summary>
    public static double NextStep(double zoom, int direction)
    {
        const double tolerance = 1e-6;
        if (direction > 0)
        {
            foreach (var step in Steps) if (step > zoom + tolerance) return step;
            return Steps[^1];
        }
        for (var i = Steps.Length - 1; i >= 0; i--) if (Steps[i] < zoom - tolerance) return Steps[i];
        return Steps[0];
    }
}
