using System.Text;
using Octopus.App.Files;
using Octopus.App.Files.Documents;
using Xunit;

namespace Octopus.App.Tests.Files;

public class MarkdownFileTextTests
{
    private static MarkdownFileText Decode(byte[] bytes)
    {
        Assert.True(MarkdownFileText.TryDecode(bytes, out var document));
        return document;
    }

    [Fact]
    public void A_plain_file_is_LF_with_a_final_newline_and_no_bom()
    {
        var doc = Decode("# T\n\nbody\n"u8.ToArray());
        Assert.Equal("# T\n\nbody\n", doc.Text);
        Assert.Equal("\n", doc.NewLine);
        Assert.True(doc.EndsWithNewline);
        Assert.False(doc.HasBom);
        Assert.False(doc.HasMixedNewLines);
    }

    [Fact]
    public void A_crlf_file_with_a_bom_is_read_as_LF_and_written_back_the_same_way()
    {
        var original = new byte[] { 0xEF, 0xBB, 0xBF }.Concat("# T\r\n\r\nbody\r\n"u8.ToArray()).ToArray();
        var doc = Decode(original);
        Assert.True(doc.HasBom);
        Assert.Equal("\r\n", doc.NewLine);
        Assert.Equal("# T\n\nbody\n", doc.Text);
        // The editor exports no final newline and LF: the file's own style comes back.
        Assert.Equal(original, doc.Encode("# T\n\nbody"));
    }

    [Fact]
    public void A_file_without_a_final_newline_gets_none_added()
    {
        var doc = Decode("no newline"u8.ToArray());
        Assert.False(doc.EndsWithNewline);
        Assert.Equal("no newline!"u8.ToArray(), doc.Encode("no newline!"));
    }

    [Fact]
    public void The_dominant_line_ending_wins_and_a_mix_is_noticed()
    {
        var doc = Decode("a\r\nb\r\nc\nd\r\n"u8.ToArray());
        Assert.Equal("\r\n", doc.NewLine);
        Assert.True(doc.HasMixedNewLines);
        Assert.Equal("a\nb\nc\nd\n", doc.Text);
    }

    [Fact]
    public void An_empty_file_is_written_with_a_newline_once_it_has_text()
    {
        var doc = Decode([]);
        Assert.Equal("hi\n"u8.ToArray(), doc.Encode("hi"));
        Assert.Empty(doc.Encode(""));
    }

    [Fact]
    public void Bytes_that_are_not_utf8_are_refused_rather_than_damaged()
    {
        Assert.False(MarkdownFileText.TryDecode([0x48, 0x69, 0xFF, 0xFE, 0x41], out _));
        Assert.False(MarkdownFileText.TryDecode(Encoding.Latin1.GetBytes("café"), out _));
    }

    [Fact]
    public void Non_ascii_text_survives_the_round_trip_byte_for_byte()
    {
        var original = "# 标题 ✓\n\n中文，émoji 😀\n"u8.ToArray();
        var doc = Decode(original);
        Assert.Equal(original, doc.Encode(doc.Text));
    }

    [Fact]
    public void An_atomic_write_replaces_the_content_and_leaves_no_temporary_file()
    {
        var dir = Directory.CreateTempSubdirectory("octopus-atomic-").FullName;
        try
        {
            var path = Path.Combine(dir, "a.md");
            File.WriteAllText(path, "old");
            AtomicFileWriter.WriteAllBytes(path, "new content"u8.ToArray());
            Assert.Equal("new content", File.ReadAllText(path));
            Assert.Equal([path], Directory.GetFileSystemEntries(dir));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void An_atomic_write_keeps_the_permission_bits_and_follows_a_link()
    {
        if (OperatingSystem.IsWindows()) return; // permission bits and links are the Unix part of the story
        var dir = Directory.CreateTempSubdirectory("octopus-atomic-").FullName;
        try
        {
            var real = Path.Combine(dir, "real.md");
            File.WriteAllText(real, "old");
            File.SetUnixFileMode(real, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead);
            var link = Path.Combine(dir, "link.md");
            File.CreateSymbolicLink(link, real);
            AtomicFileWriter.WriteAllBytes(link, "through the link"u8.ToArray());
            Assert.Equal("through the link", File.ReadAllText(real));
            Assert.NotNull(new FileInfo(link).LinkTarget);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead, File.GetUnixFileMode(real));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
