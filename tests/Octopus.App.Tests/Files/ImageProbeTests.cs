using Octopus.App.Files.Images;
using Xunit;

namespace Octopus.App.Tests.Files;

public class ImageProbeTests
{
    [Fact]
    public void The_header_of_each_format_gives_its_size_without_decoding()
    {
        Check(TestImages.Png(37, 21), ImageFormat.Png, 37, 21);
        Check(TestImages.Jpeg(64, 48), ImageFormat.Jpeg, 64, 48);
        Check(TestImages.WebP(50, 30), ImageFormat.WebP, 50, 30);
        Check(TestImages.Bmp(13, 9), ImageFormat.Bmp, 13, 9);
        Check(TestImages.Gif(), ImageFormat.Gif, 1, 1);
        Check(TestImages.Ico(48), ImageFormat.Ico, 48, 48);
        Check(TestImages.Ico(256), ImageFormat.Ico, 256, 256);
    }

    private static void Check(byte[] bytes, ImageFormat format, int width, int height)
    {
        var info = ImageProbe.TryRead(bytes);
        Assert.NotNull(info);
        Assert.Equal(format, info.Format);
        Assert.Equal(width, info.Width);
        Assert.Equal(height, info.Height);
        Assert.Equal((long)width * height, info.Pixels);
    }

    [Fact]
    public void A_huge_claimed_size_is_read_from_the_header_alone()
    {
        var info = ImageProbe.TryRead(TestImages.PngHeaderOnly(60000, 60000));
        Assert.NotNull(info);
        Assert.Equal(3_600_000_000, info.Pixels);
    }

    [Fact]
    public void Text_and_cut_off_headers_are_not_pictures()
    {
        Assert.Null(ImageProbe.TryRead("hello world, this is text"u8));
        Assert.Null(ImageProbe.TryRead(TestImages.Png(10, 10)[..12]));
        Assert.Null(ImageProbe.TryRead([]));
        Assert.Null(ImageProbe.Sniff("BM text"u8));
    }
}
