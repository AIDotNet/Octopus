using System.Text.RegularExpressions;
using Octopus.Platform.Linux;
using Octopus.Platform.MacOS;
using Octopus.Platform.Unix;
using Octopus.Platform.Windows;

namespace Octopus.Platform.Tests;

/// <summary>Trash implementations against a temporary home: the real trash is never touched.</summary>
public sealed class TrashServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("octopus-trash-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string File(string relative, string content = "x")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    // ---------------------------------------------------------------- shared

    [Theory]
    [InlineData("notes.txt", 1, " ", "notes.txt")]
    [InlineData("notes.txt", 2, " ", "notes 2.txt")]
    [InlineData("notes.txt", 3, ".", "notes.3.txt")]
    [InlineData(".bashrc", 2, " ", ".bashrc 2")]
    [InlineData("folder", 2, ".", "folder.2")]
    public void Numbered_names(string name, int number, string separator, string expected)
    {
        Assert.Equal(expected, UnixTrash.NumberedName(name, number, separator));
    }

    [Fact]
    public void Mount_of_a_path_is_the_longest_containing_mount_point()
    {
        string[] mounts = ["/", "/home", "/home/user/media", "/homework"];
        Assert.Equal("/home", UnixTrash.MountOf("/home/user/doc.txt", mounts));
        Assert.Equal("/home/user/media", UnixTrash.MountOf("/home/user/media/a", mounts));
        Assert.Equal("/", UnixTrash.MountOf("/tmp/x", mounts));
        Assert.Equal("/homework", UnixTrash.MountOf("/homework/a", mounts));
    }

    [Fact]
    public void Same_volume_on_this_machine()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.True(UnixTrash.SameVolume(File("a.txt"), Path.Combine(_root, "trash")));
        Assert.False(UnixTrash.SameVolume("/dev/null", _root)); // devfs / devtmpfs is its own mount
    }

    // ---------------------------------------------------------------- macOS

    [Fact]
    public void Mac_moves_into_the_trash_under_a_free_name()
    {
        if (OperatingSystem.IsWindows()) return;
        var trash = Path.Combine(_root, ".Trash");
        var service = new MacTrashService(trash);
        Assert.True(service.MoveToTrash(File("a/notes.txt", "one")));
        Assert.True(service.MoveToTrash(File("b/notes.txt", "two")));
        Assert.False(System.IO.File.Exists(Path.Combine(_root, "a/notes.txt")));
        Assert.Equal("one", System.IO.File.ReadAllText(Path.Combine(trash, "notes.txt")));
        Assert.Equal("two", System.IO.File.ReadAllText(Path.Combine(trash, "notes 2.txt")));

        File("dir/inner.txt");
        Assert.True(service.MoveToTrash(Path.Combine(_root, "dir")));
        Assert.True(System.IO.File.Exists(Path.Combine(trash, "dir", "inner.txt")));
    }

    [Fact]
    public void Mac_trashes_a_link_not_its_target()
    {
        if (OperatingSystem.IsWindows()) return;
        var target = File("target.txt", "keep");
        var link = Path.Combine(_root, "link.txt");
        System.IO.File.CreateSymbolicLink(link, target);
        var trash = Path.Combine(_root, ".Trash");
        Assert.True(new MacTrashService(trash).MoveToTrash(link));
        Assert.Equal("keep", System.IO.File.ReadAllText(target));
        Assert.NotNull(new FileInfo(Path.Combine(trash, "link.txt")).LinkTarget);
    }

    [Fact]
    public void Mac_refuses_other_volumes_and_missing_items()
    {
        if (OperatingSystem.IsWindows()) return;
        var service = new MacTrashService(Path.Combine(_root, ".Trash"), sameVolume: (_, _) => false);
        var file = File("elsewhere.txt");
        Assert.False(service.MoveToTrash(file));
        Assert.True(System.IO.File.Exists(file));
        Assert.Throws<FileNotFoundException>(() => service.MoveToTrash(Path.Combine(_root, "missing")));
    }

    // ---------------------------------------------------------------- Linux (freedesktop.org Trash spec)

    [Fact]
    public void Linux_moves_into_files_and_writes_trashinfo()
    {
        if (OperatingSystem.IsWindows()) return;
        var service = new LinuxTrashService(home: Path.Combine(_root, "home"), xdgDataHome: Path.Combine(_root, "data"));
        Assert.Equal(Path.Combine(_root, "data", "Trash"), service.TrashDirectory);
        var item = File("project/my notes.txt", "content");
        Assert.True(service.MoveToTrash(item));

        Assert.False(System.IO.File.Exists(item));
        Assert.Equal("content", System.IO.File.ReadAllText(Path.Combine(service.TrashDirectory, "files", "my notes.txt")));
        var info = System.IO.File.ReadAllText(Path.Combine(service.TrashDirectory, "info", "my notes.txt.trashinfo"));
        var lines = info.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("[Trash Info]", lines[0]);
        Assert.Equal("Path=" + LinuxTrashService.EscapePath(item), lines[1]);
        Assert.Contains("%20", lines[1]);
        Assert.Matches(new Regex(@"^DeletionDate=\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}$"), lines[2]);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            System.IO.File.GetUnixFileMode(Path.Combine(service.TrashDirectory, "files")));
    }

    [Fact]
    public void Linux_numbers_colliding_names_in_files_and_info()
    {
        if (OperatingSystem.IsWindows()) return;
        var service = new LinuxTrashService(home: Path.Combine(_root, "home"), xdgDataHome: Path.Combine(_root, "data"));
        // A stale .trashinfo without its file still reserves the name.
        Directory.CreateDirectory(Path.Combine(service.TrashDirectory, "info"));
        System.IO.File.WriteAllText(Path.Combine(service.TrashDirectory, "info", "a.txt.trashinfo"), "");
        Assert.True(service.MoveToTrash(File("1/a.txt", "first")));
        Assert.True(service.MoveToTrash(File("2/a.txt", "second")));
        Assert.Equal("first", System.IO.File.ReadAllText(Path.Combine(service.TrashDirectory, "files", "a.2.txt")));
        Assert.Equal("second", System.IO.File.ReadAllText(Path.Combine(service.TrashDirectory, "files", "a.3.txt")));
        Assert.True(System.IO.File.Exists(Path.Combine(service.TrashDirectory, "info", "a.3.txt.trashinfo")));
    }

    [Fact]
    public void Linux_ignores_a_relative_xdg_data_home()
    {
        var home = Path.Combine(_root, "home");
        var service = new LinuxTrashService(home: home, xdgDataHome: "relative/data");
        Assert.Equal(Path.Combine(home, ".local", "share", "Trash"), service.TrashDirectory);
    }

    [Fact]
    public void Linux_refuses_other_volumes_without_leaving_an_info_file()
    {
        if (OperatingSystem.IsWindows()) return;
        var service = new LinuxTrashService(home: Path.Combine(_root, "home"), xdgDataHome: Path.Combine(_root, "data"), sameVolume: (_, _) => false);
        var item = File("other-volume.txt");
        Assert.False(service.MoveToTrash(item));
        Assert.True(System.IO.File.Exists(item));
        Assert.Empty(Directory.GetFiles(Path.Combine(service.TrashDirectory, "info")));
    }

    [Fact]
    public void Linux_escapes_paths_like_urls()
    {
        Assert.Equal("/home/u/a%20b/%E6%97%A5.txt", LinuxTrashService.EscapePath("/home/u/a b/日.txt"));
        Assert.Equal("/x/100%25-(ok)!~*'_.md", LinuxTrashService.EscapePath("/x/100%-(ok)!~*'_.md"));
        var info = LinuxTrashService.InfoFile("/tmp/a#b", new DateTime(2026, 1, 2, 3, 4, 5));
        Assert.Equal("[Trash Info]\nPath=/tmp/a%23b\nDeletionDate=2026-01-02T03:04:05\n", info);
    }

    // ---------------------------------------------------------------- Windows and fallback

    [Fact]
    public void Windows_trash_is_unsupported_elsewhere()
    {
        if (OperatingSystem.IsWindows()) return;
        var file = File("win.txt");
        Assert.False(new WindowsTrashService().MoveToTrash(file));
        Assert.True(System.IO.File.Exists(file));
    }

    [Fact]
    public void No_trash_says_unsupported()
    {
        Assert.False(new NoTrashService().MoveToTrash(File("any.txt")));
    }
}
