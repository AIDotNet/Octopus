using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Octopus.Mobile.Terminals;
using Octopus.Mobile.Workspace;
using Octopus.Remote.Protocol;
using Xunit;

namespace Octopus.Mobile.Tests;

/// <summary>A picture from the phone for a terminal session: readied on the phone, kept on the host, its path typed in.</summary>
public class TerminalImageTests
{
    // ---- readying the picture ----

    /// <summary>A picture of the given size: a red left half and a blue right half, so a turn shows.</summary>
    private static byte[] Picture(int width, int height, bool jpeg = false, byte alpha = 255)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = (y * width + x) * 4;
                var left = x < width / 2;
                // Bgra, premultiplied.
                pixels[at] = left ? (byte)0 : alpha;
                pixels[at + 2] = left ? alpha : (byte)0;
                pixels[at + 3] = alpha;
            }
        }

        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using var bitmap = new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, handle.AddrOfPinnedObject(), new PixelSize(width, height), new Vector(96, 96), width * 4);
            using var output = new MemoryStream();
            if (jpeg) bitmap.Save(output, new JpegBitmapEncoderOptions { Quality = 90 });
            else bitmap.Save(output, PngBitmapEncoderOptions.Default);
            return output.ToArray();
        }
        finally { handle.Free(); }
    }

    /// <summary>The JPEG with an EXIF block that gives its orientation, right after the start marker.</summary>
    private static byte[] WithOrientation(byte[] jpeg, ushort orientation, bool littleEndian = false)
    {
        var tiff = new List<byte>();
        void U16(int v) { if (littleEndian) tiff.AddRange([(byte)v, (byte)(v >> 8)]); else tiff.AddRange([(byte)(v >> 8), (byte)v]); }
        void U32(int v) { if (littleEndian) tiff.AddRange([(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)]); else tiff.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]); }
        tiff.AddRange(littleEndian ? "II"u8.ToArray() : "MM"u8.ToArray());
        U16(42);
        U32(8);
        U16(1);
        U16(0x0112); U16(3); U32(1); U16(orientation); U16(0);
        U32(0);
        var segment = new List<byte>("Exif\0\0"u8.ToArray());
        segment.AddRange(tiff);
        var length = segment.Count + 2;
        return [0xFF, 0xD8, 0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. segment, .. jpeg[2..]];
    }

    private static Bitmap Decode(byte[] data) => new(new MemoryStream(data));

    [AvaloniaFact]
    public void A_small_screenshot_goes_as_it_is()
    {
        var png = Picture(390, 844);
        var prepared = UploadImages.Prepare(new PickedImage("Screenshot.png", png));
        Assert.Equal("Screenshot.png", prepared.Name);
        Assert.Same(png, prepared.Data);
    }

    [AvaloniaFact]
    public void A_large_picture_is_made_smaller_and_sent_as_a_jpeg()
    {
        var prepared = UploadImages.Prepare(new PickedImage("/DCIM/Camera/PXL_1.png", Picture(4000, 1000)));
        Assert.Equal("PXL_1.jpg", prepared.Name);
        using var result = Decode(prepared.Data);
        Assert.Equal(new PixelSize(UploadImages.MaxEdge, 512), result.PixelSize);
        Assert.Equal(0xFF, prepared.Data[0]);
        Assert.Equal(0xD8, prepared.Data[1]);
    }

    [AvaloniaFact]
    public void A_photo_is_turned_upright_by_its_exif_orientation()
    {
        foreach (var littleEndian in new[] { false, true })
        {
            // Stored on its side, landscape; the camera says to turn it a quarter clockwise (6).
            var stored = WithOrientation(Picture(400, 200, jpeg: true), 6, littleEndian);
            Assert.Equal(6, UploadImages.ExifOrientation(stored));
            var prepared = UploadImages.Prepare(new PickedImage("IMG_1.jpg", stored));
            using var result = Decode(prepared.Data);
            Assert.Equal(new PixelSize(200, 400), result.PixelSize);
            // The left half (red) is now the top half.
            var top = Pixel(result, 100, 50);
            var bottom = Pixel(result, 100, 350);
            Assert.True(top.R > 200 && top.B < 60, $"top is {top}");
            Assert.True(bottom.B > 200 && bottom.R < 60, $"bottom is {bottom}");
        }
        Assert.Equal(1, UploadImages.ExifOrientation(Picture(10, 10)));
        Assert.Equal(1, UploadImages.ExifOrientation(Picture(10, 10, jpeg: true)));
    }

    [AvaloniaFact]
    public void See_through_parts_turn_white_not_black()
    {
        var prepared = UploadImages.Prepare(new PickedImage("clear.png", Picture(3000, 100, alpha: 0)));
        using var result = Decode(prepared.Data);
        var pixel = Pixel(result, 10, 10);
        Assert.True(pixel.R > 240 && pixel.G > 240 && pixel.B > 240, $"the pixel is {pixel}");
    }

    [AvaloniaFact]
    public void A_picture_the_phone_cannot_read_goes_as_it_is_only_when_the_host_takes_its_type()
    {
        var unknown = new PickedImage("odd.webp", [1, 2, 3, 4]);
        Assert.Same(unknown, UploadImages.Prepare(unknown));
        Assert.Throws<InvalidDataException>(() => UploadImages.Prepare(new PickedImage("IMG_2.HEIC", [1, 2, 3, 4])));
    }

    private static (byte R, byte G, byte B) Pixel(Bitmap bitmap, int x, int y)
    {
        var pixel = new byte[4];
        var handle = GCHandle.Alloc(pixel, GCHandleType.Pinned);
        try { bitmap.CopyPixels(new PixelRect(x, y, 1, 1), handle.AddrOfPinnedObject(), 4, 4); }
        finally { handle.Free(); }
        return bitmap.Format == PixelFormat.Rgba8888 ? (pixel[0], pixel[1], pixel[2]) : (pixel[2], pixel[1], pixel[0]);
    }

    // ---- the terminal page ----

    private static (TerminalPage Page, TerminalViewModel ViewModel) Open(Phone phone, SessionKind kind)
    {
        var target = phone.Workspace.Hosts[0].Projects.SelectMany(p => p.Sessions).First(s => s.Kind == kind && !s.IsGui);
        phone.Shell.ShowHost(target.Project.Host);
        phone.Shell.ShowSession(target);
        Assert.True(Phone.Until(() => phone.Top<TerminalPage>().ReadScreenText().Trim().Length > 0));
        var page = phone.Top<TerminalPage>();
        var terminal = (TerminalViewModel)page.DataContext!;
        terminal.FitCommand.Execute(null);
        Phone.Pump();
        return (page, terminal);
    }

    private static IDisposable Picking(Func<PickedImage?> pick)
    {
        var before = MobileHost.PickImage;
        MobileHost.PickImage = _ => Task.FromResult(pick());
        return new Restore(() => MobileHost.PickImage = before);
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    [AvaloniaFact]
    public void A_chosen_picture_is_uploaded_and_its_path_goes_after_the_composed_line()
    {
        using var phone = new Phone();
        var (page, terminal) = Open(phone, SessionKind.Agent);
        var screenshot = Picture(300, 600);
        using var _ = Picking(() => new PickedImage("Screenshot.png", screenshot));
        Assert.True(page.FindControl<Control>("ImageButton")!.IsEffectivelyVisible);

        terminal.ComposeText = "看这个报错";
        terminal.SendImageCommand.Execute(null);
        Assert.True(Phone.Until(() => !terminal.IsUploading && phone.Workspace.Uploads.Count == 1));
        Phone.Pump();

        var (session, name, data) = phone.Workspace.Uploads.Single();
        Assert.Same(terminal.Session, session);
        Assert.Equal("Screenshot.png", name);
        Assert.Same(screenshot, data);
        Assert.Equal("看这个报错 /tmp/octopus-paste/paste-demo-1.png ", terminal.ComposeText);
    }

    [AvaloniaFact]
    public void Nothing_chosen_sends_nothing_and_a_host_without_uploads_shows_no_button()
    {
        using var phone = new Phone();
        var (page, terminal) = Open(phone, SessionKind.Agent);
        using (Picking(() => null))
        {
            terminal.SendImageCommand.Execute(null);
            Phone.Settle(300);
            Assert.Empty(phone.Workspace.Uploads);
            Assert.Equal("", terminal.ComposeText);
            Assert.False(terminal.IsUploading);
        }

        phone.Workspace.FilesSupported = false;
        phone.Shell.Back();
        Phone.Pump();
        var (other, _) = Open(phone, SessionKind.Agent);
        Assert.False(other.FindControl<Control>("ImageButton")!.IsVisible);
        Assert.True(RemoteMethods.UploadMaxBytes > UploadImages.KeepBytes);
    }
}
