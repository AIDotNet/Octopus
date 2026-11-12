using Octopus.App.Files;
using Xunit;

namespace Octopus.App.Tests.Files;

public class FileKindsTests
{
    [Theory]
    [InlineData("a.md", FileKind.Markdown)]
    [InlineData("README.MARKDOWN", FileKind.Markdown)]
    [InlineData("a.png", FileKind.Image)]
    [InlineData("photo.JPG", FileKind.Image)]
    [InlineData("photo.jpeg", FileKind.Image)]
    [InlineData("anim.gif", FileKind.Image)]
    [InlineData("pic.webp", FileKind.Image)]
    [InlineData("old.bmp", FileKind.Image)]
    [InlineData("favicon.ico", FileKind.Image)]
    [InlineData("logo.svg", FileKind.Image)]
    public void The_extension_decides_for_markdown_and_pictures(string name, FileKind kind) =>
        Assert.Equal(kind, FileKinds.FromExtension(name));

    [Theory]
    [InlineData("Program.cs")]
    [InlineData("data.json")]
    [InlineData("notes.txt")]
    [InlineData("LICENSE")]
    [InlineData("a.mdx")]
    [InlineData("a.pdf")]
    public void Other_names_say_nothing_by_extension(string name) => Assert.Null(FileKinds.FromExtension(name));

    [Fact]
    public void A_name_without_a_known_extension_is_recognized_by_its_first_bytes()
    {
        Assert.Equal(FileKind.Image, FileKinds.Detect("download", TestImages.Png(4, 4)));
        Assert.Equal(FileKind.Image, FileKinds.Detect("download.dat", TestImages.Jpeg(8, 8)));
        Assert.Equal(FileKind.Image, FileKinds.Detect("x.tmp", TestImages.WebP(8, 8)));
        Assert.Equal(FileKind.Image, FileKinds.Detect("x", TestImages.Bmp(4, 4)));
        Assert.Equal(FileKind.Image, FileKinds.Detect("x", TestImages.Gif()));
        Assert.Equal(FileKind.Image, FileKinds.Detect("x", TestImages.Ico(16)));
        Assert.Equal(FileKind.Code, FileKinds.Detect("LICENSE", "MIT License\n\nCopyright"u8));
        Assert.Equal(FileKind.Code, FileKinds.Detect("x", "BM is two ordinary letters at the start of some text"u8));
        Assert.Equal(FileKind.Code, FileKinds.Detect("x", []));
    }

    [Fact]
    public void A_name_with_a_language_is_text_whatever_its_bytes_look_like() =>
        Assert.Equal(FileKind.Code, FileKinds.Detect("odd.cs", TestImages.Png(4, 4)));

    [Fact]
    public void A_picture_extension_wins_over_the_bytes_so_a_damaged_picture_says_so_instead_of_showing_garbage() =>
        Assert.Equal(FileKind.Image, FileKinds.Detect("broken.png", "this is not a png"u8));

    [Fact]
    public void Detect_reads_the_header_from_disk_when_none_is_given()
    {
        var dir = Directory.CreateTempSubdirectory("octopus-kind-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "blob"), TestImages.Png(5, 5));
            File.WriteAllText(Path.Combine(dir, "text"), "plain words");
            Assert.Equal(FileKind.Image, FileKinds.Detect(Path.Combine(dir, "blob")));
            Assert.Equal(FileKind.Code, FileKinds.Detect(Path.Combine(dir, "text")));
            Assert.Equal(FileKind.Code, FileKinds.Detect(Path.Combine(dir, "missing")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
