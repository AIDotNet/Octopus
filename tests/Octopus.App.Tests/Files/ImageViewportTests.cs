using Avalonia;
using Octopus.App.Files.Images;
using Xunit;

namespace Octopus.App.Tests.Files;

public class ImageViewportTests
{
    private static ImageViewport Viewport(double imageWidth, double imageHeight, double viewWidth = 800, double viewHeight = 600)
    {
        var viewport = new ImageViewport(imageWidth, imageHeight);
        viewport.SetView(new Size(viewWidth, viewHeight));
        return viewport;
    }

    [Fact]
    public void A_small_picture_is_fitted_at_100_percent_and_centered_never_enlarged()
    {
        var v = Viewport(64, 32);
        Assert.True(v.IsFit);
        Assert.Equal(1, v.Zoom);
        Assert.Equal(100, v.Percent);
        Assert.Equal(new Rect(368, 284, 64, 32), v.ImageRect);
    }

    [Fact]
    public void A_large_picture_is_fitted_inside_the_window_with_a_margin()
    {
        var v = Viewport(4000, 3000);
        var expected = (600 - 2 * ImageViewport.FitMargin) / 3000;
        Assert.Equal(expected, v.Zoom, 6);
        Assert.True(v.ImageRect.Width <= 800 - 2 * ImageViewport.FitMargin + 1e-6);
        Assert.Equal(300, v.ImageRect.Center.Y, 3);
        Assert.Equal(400, v.ImageRect.Center.X, 3);
    }

    [Fact]
    public void Fit_follows_the_window_when_it_is_resized()
    {
        var v = Viewport(2000, 1000);
        var before = v.Zoom;
        v.SetView(new Size(400, 300));
        Assert.True(v.IsFit);
        Assert.True(v.Zoom < before);
        Assert.Equal((400 - 2 * ImageViewport.FitMargin) / 2000, v.Zoom, 6);
        v.SetView(new Size(5000, 5000));
        Assert.Equal(1, v.Zoom);
    }

    [Fact]
    public void Actual_size_is_100_percent_and_leaves_fit_mode()
    {
        var v = Viewport(4000, 3000);
        v.ActualSize();
        Assert.False(v.IsFit);
        Assert.Equal(1, v.Zoom);
        Assert.Equal(100, v.Percent);
        // About the middle of the window: the picture's middle is still in the window's middle.
        Assert.Equal(400, v.ImageRect.Center.X, 3);
        Assert.Equal(300, v.ImageRect.Center.Y, 3);
    }

    [Fact]
    public void Zoom_in_and_out_move_through_the_steps_from_wherever_the_zoom_is()
    {
        var v = Viewport(4000, 3000); // fit is 0.188
        v.ZoomIn();
        Assert.Equal(0.25, v.Zoom);
        v.ZoomIn();
        Assert.Equal(0.5, v.Zoom);
        v.ZoomOut();
        v.ZoomOut();
        Assert.Equal(0.1, v.Zoom);
        for (var i = 0; i < 40; i++) v.ZoomIn();
        Assert.Equal(32, v.Zoom);
        for (var i = 0; i < 40; i++) v.ZoomOut();
        Assert.Equal(0.05, v.Zoom);
        Assert.Equal(2, ImageViewport.NextStep(1.5, +1));
        Assert.Equal(1, ImageViewport.NextStep(1.2, -1));
    }

    [Fact]
    public void Zooming_about_a_point_keeps_the_picture_point_under_it()
    {
        var v = Viewport(2000, 2000);
        v.ActualSize();
        v.Pan(new Vector(-300, -200));
        var anchor = new Point(250, 150);
        var before = new Point((anchor.X - v.Offset.X) / v.Zoom, (anchor.Y - v.Offset.Y) / v.Zoom);
        v.ZoomByFactor(2, anchor);
        var after = new Point((anchor.X - v.Offset.X) / v.Zoom, (anchor.Y - v.Offset.Y) / v.Zoom);
        Assert.Equal(2, v.Zoom, 6);
        Assert.Equal(before.X, after.X, 3);
        Assert.Equal(before.Y, after.Y, 3);
    }

    [Fact]
    public void A_wheel_notch_is_ten_percent_and_the_zoom_stays_in_its_limits()
    {
        var v = Viewport(2000, 2000);
        v.ActualSize();
        v.ZoomByWheel(1, new Point(400, 300));
        Assert.Equal(1.1, v.Zoom, 6);
        v.ZoomByWheel(-1, new Point(400, 300));
        Assert.Equal(1, v.Zoom, 6);
        v.ZoomByWheel(500, new Point(0, 0));
        Assert.Equal(ImageViewport.MaxZoom, v.Zoom);
        v.ZoomByWheel(-5000, new Point(0, 0));
        Assert.Equal(ImageViewport.MinZoom, v.Zoom);
    }

    [Fact]
    public void A_picture_cannot_be_dragged_past_its_edges_and_a_small_one_stays_centered()
    {
        var v = Viewport(2000, 1500);
        v.ActualSize();
        v.Pan(new Vector(100000, 100000));
        Assert.Equal(0, v.ImageRect.Left);
        Assert.Equal(0, v.ImageRect.Top);
        v.Pan(new Vector(-100000, -100000));
        Assert.Equal(800, v.ImageRect.Right, 6);
        Assert.Equal(600, v.ImageRect.Bottom, 6);
        Assert.True(v.CanPan);

        var small = Viewport(100, 100);
        small.Pan(new Vector(300, 300));
        Assert.Equal(new Rect(350, 250, 100, 100), small.ImageRect);
        Assert.False(small.CanPan);
    }

    [Fact]
    public void A_double_click_toggles_between_fit_and_100_percent()
    {
        var v = Viewport(4000, 3000);
        v.ToggleFitActual();
        Assert.Equal(1, v.Zoom);
        Assert.False(v.IsFit);
        v.ToggleFitActual();
        Assert.True(v.IsFit);
        Assert.True(v.Zoom < 1);
        // A picture that already fits at 100% stays as it is.
        var small = Viewport(32, 32);
        small.ToggleFitActual();
        Assert.True(small.IsFit);
        Assert.Equal(1, small.Zoom);
    }

    [Fact]
    public void A_chosen_zoom_keeps_the_middle_of_the_view_when_the_window_changes()
    {
        var v = Viewport(2000, 2000);
        v.ActualSize();
        v.Pan(new Vector(-400, -300));
        var middle = new Point((400 - v.Offset.X) / v.Zoom, (300 - v.Offset.Y) / v.Zoom);
        v.SetView(new Size(600, 500));
        var after = new Point((300 - v.Offset.X) / v.Zoom, (250 - v.Offset.Y) / v.Zoom);
        Assert.Equal(middle.X, after.X, 3);
        Assert.Equal(middle.Y, after.Y, 3);
        Assert.False(v.IsFit);
    }
}
