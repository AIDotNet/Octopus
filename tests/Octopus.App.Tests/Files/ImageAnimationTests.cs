using System.Text;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Octopus.App.Files;
using Octopus.App.Files.Images;
using SkiaSharp;
using Xunit;

namespace Octopus.App.Tests.Files;

/// <summary>SVG files are drawn as pictures; animated GIFs play.</summary>
public class ImageAnimationTests
{
    private static ImageViewerModel Image(FileTabHarness h) => Assert.IsType<ImageViewerModel>(h.Tab.Viewer);

    private static async Task<FileTabHarness> Open(byte[] bytes, string name)
    {
        var h = await FileTabHarness.OpenAsync(bytes, name);
        await FileTabHarness.WaitUntil(() => Image(h).Image is not null || Image(h).HasNotice);
        h.Settle(100);
        return h;
    }

    /// <summary>The colour of the picture's pixel at (x, y) of its own size.</summary>
    private static SKColor PixelAt(Bitmap bitmap, double x, double y, int width, int height)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        using var decoded = SKBitmap.Decode(stream.ToArray());
        return decoded.GetPixel((int)(x / width * decoded.Width), (int)(y / height * decoded.Height));
    }

    [AvaloniaFact]
    public async Task An_svg_is_drawn_sharp_at_its_own_size()
    {
        const string svg = "<svg xmlns='http://www.w3.org/2000/svg' width='24' height='16' viewBox='0 0 24 16'>"
                           + "<rect width='12' height='16' fill='#ff0000'/><rect x='12' width='12' height='16' fill='#0000ff'/></svg>";
        using var h = await Open(Encoding.UTF8.GetBytes(svg), "logo.svg");
        var model = Image(h);

        Assert.Equal(FileKind.Image, h.Tab.Kind);
        Assert.False(model.HasNotice);
        Assert.Equal(new ImageInfo(ImageFormat.Svg, 24, 16), model.Info);
        Assert.Contains("SVG", model.MetaText);
        Assert.Contains("24 × 16", model.MetaText);
        // Drawn well above its size, so zooming in stays sharp.
        Assert.True(model.Image!.PixelSize.Width >= 24 * 8);
        Assert.Equal(model.Image.PixelSize.Width / 24.0, model.Image.PixelSize.Height / 16.0, 1);
        Assert.True(PixelAt(model.Image, 6, 8, 24, 16).Red > 200);
        Assert.True(PixelAt(model.Image, 18, 8, 24, 16).Blue > 200);
        Assert.False(model.IsAnimated);
    }

    [AvaloniaFact]
    public async Task An_svg_reads_nothing_outside_itself_and_a_broken_one_says_so()
    {
        // A red picture next to the SVG, linked by a file address: it must not be drawn.
        var red = Path.Combine(Directory.CreateTempSubdirectory("octopus-svg-").FullName, "red.png");
        using (var bitmap = new SKBitmap(8, 8))
        {
            bitmap.Erase(SKColors.Red);
            File.WriteAllBytes(red, bitmap.Encode(SKEncodedImageFormat.Png, 100).ToArray());
        }
        var svg = "<svg xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink' width='20' height='20'>"
                  + $"<image width='20' height='20' xlink:href='{new Uri(red).AbsoluteUri}'/></svg>";
        using (var h = await Open(Encoding.UTF8.GetBytes(svg), "linked.svg"))
        {
            var model = Image(h);
            Assert.NotNull(model.Image);
            Assert.True(PixelAt(model.Image!, 10, 10, 20, 20).Red < 200);
        }

        using var broken = await Open("<svg xmlns='http://www.w3.org/2000/svg'><rect"u8.ToArray(), "broken.svg");
        Assert.True(Image(broken).HasNotice);
        Assert.Null(Image(broken).Image);
    }

    [AvaloniaFact]
    public async Task An_svg_switches_between_the_picture_and_its_source()
    {
        const string svg = "<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'><circle r='4' cx='5' cy='5'/></svg>";
        using var h = await Open(Encoding.UTF8.GetBytes(svg), "dot.svg");
        var model = Image(h);
        Assert.True(model.ShowModeSwitch);
        Assert.True(model.ShowCanvas);
        Assert.False(model.ShowSource);

        model.ViewMode = ImageViewerModel.SourceMode;
        Assert.True(model.ShowSource);
        Assert.False(model.ShowCanvas);
        Assert.False(model.ShowZoom);
        Assert.Equal(svg, model.SourceText);

        // A picture that is not an SVG has no source to show.
        using var png = await Open(TestImages.Png(4, 4), "a.png");
        Assert.False(Image(png).ShowModeSwitch);

        // One that cannot be drawn still has its text.
        using var broken = await Open("<svg xmlns='http://www.w3.org/2000/svg'><rect"u8.ToArray(), "half.svg");
        Assert.True(Image(broken).ShowNotice);
        Assert.True(Image(broken).ShowModeSwitch);
        Image(broken).ViewMode = ImageViewerModel.SourceMode;
        Assert.True(Image(broken).ShowSource);
        Assert.False(Image(broken).ShowNotice);
    }

    [AvaloniaFact]
    public async Task An_animated_gif_plays_while_shown_and_can_be_paused()
    {
        using var h = await Open(Gif(loop: true, SKColors.Red, SKColors.Lime, SKColors.Blue), "spin.gif");
        var model = Image(h);

        Assert.True(model.IsAnimated);
        Assert.Equal(3, model.FrameCount);
        Assert.Equal(new PixelSize(4, 4), model.Image!.PixelSize);
        Assert.Contains("3", model.MetaText);
        Assert.True(model.IsPlaying);
        // Each frame shows its own colour (it may already have moved on from the first).
        SKColor[] colors = [SKColors.Red, SKColors.Lime, SKColors.Blue];
        Assert.Equal(colors[model.CurrentFrame], PixelAt(model.Image, 1, 1, 4, 4));

        // Frames of 20 ms: it moves on, and round again.
        var seen = new HashSet<int>();
        await FileTabHarness.WaitUntil(() => { seen.Add(model.CurrentFrame); return seen.Count == 3; });

        model.TogglePlayCommand.Execute(null);
        Assert.False(model.IsPlaying);
        var frame = model.CurrentFrame;
        Assert.Equal(colors[frame], PixelAt(model.Image!, 1, 1, 4, 4));
        h.Settle(200);
        Assert.False(model.IsPlaying);
        Assert.Equal(frame, model.CurrentFrame);

        model.TogglePlayCommand.Execute(null);
        Assert.True(model.IsPlaying);
    }

    [AvaloniaFact]
    public async Task A_gif_without_a_loop_plays_once_and_stops_on_its_last_frame()
    {
        using var h = await Open(Gif(loop: false, SKColors.Red, SKColors.Lime, SKColors.Blue), "once.gif");
        var model = Image(h);
        await FileTabHarness.WaitUntil(() => !model.IsPlaying);
        Assert.Equal(2, model.CurrentFrame);
        Assert.True(PixelAt(model.Image!, 1, 1, 4, 4).Blue > 200);

        // Play again starts from the beginning.
        model.TogglePlayCommand.Execute(null);
        Assert.Equal(0, model.CurrentFrame);
        Assert.True(model.IsPlaying);
    }

    [Fact]
    public void A_still_gif_is_not_an_animation()
    {
        Assert.Null(ImageDecoders.DecodeAnimation(TestImages.Gif()));
        Assert.NotNull(ImageDecoders.DecodeAnimation(Gif(loop: true, SKColors.Red, SKColors.Blue)));
    }

    /// <summary>
    /// A 4 × 4 GIF with one solid frame per colour, 20 ms each. The pixels are written as literal LZW codes with a clear
    /// code every two, so the code width never grows past three bits.
    /// </summary>
    private static byte[] Gif(bool loop, params SKColor[] colors)
    {
        var palette = colors.Concat(Enumerable.Repeat(SKColors.Black, 4)).Take(4).ToArray();
        var gif = new List<byte>();
        gif.AddRange("GIF89a"u8.ToArray());
        gif.AddRange([4, 0, 4, 0, 0x91, 0, 0]);                     // 4 × 4, a global table of 4 colours
        foreach (var c in palette) gif.AddRange([c.Red, c.Green, c.Blue]);
        if (loop) gif.AddRange([0x21, 0xFF, 0x0B, .. "NETSCAPE2.0"u8.ToArray(), 3, 1, 0, 0, 0]);
        for (var frame = 0; frame < colors.Length; frame++)
        {
            gif.AddRange([0x21, 0xF9, 4, 0x04, 2, 0, 0, 0]);       // keep the frame, 20 ms
            gif.AddRange([0x2C, 0, 0, 0, 0, 4, 0, 4, 0, 0]);       // the whole picture
            gif.Add(2);                                            // minimum code size: 2 bits
            var codes = new List<int>();
            for (var pixel = 0; pixel < 16; pixel++)
            {
                if (pixel % 2 == 0) codes.Add(4);                  // clear
                codes.Add(frame);
            }
            codes.Add(5);                                          // end
            var bytes = new List<byte>();
            int buffer = 0, bits = 0;
            foreach (var code in codes)
            {
                buffer |= code << bits;
                bits += 3;
                while (bits >= 8) { bytes.Add((byte)buffer); buffer >>= 8; bits -= 8; }
            }
            if (bits > 0) bytes.Add((byte)buffer);
            gif.Add((byte)bytes.Count);
            gif.AddRange(bytes);
            gif.Add(0);
        }
        gif.Add(0x3B);
        return gif.ToArray();
    }
}
