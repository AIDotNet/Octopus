namespace Octopus.FileSystem.Tests;

public sealed class FileAccessServiceTests : IDisposable
{
    private const UnixFileMode AnyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    private readonly TempTree _tree = new();
    private readonly FileAccessService _fs = new();

    public void Dispose() => _tree.Dispose();

    private static FileErrorCode Code(Action action) => Assert.Throws<FileAccessException>(action).Code;

    private string P(string relative) => Path.Combine(_tree.Root, relative);

    // ---------------------------------------------------------------- paths

    [Fact]
    public void Relative_and_nul_paths_are_invalid()
    {
        Assert.Equal(FileErrorCode.InvalidArgument, Code(() => _fs.Stat("relative/path.txt")));
        Assert.Equal(FileErrorCode.InvalidArgument, Code(() => _fs.ReadText(P("a\0b"))));
        Assert.Equal(FileErrorCode.InvalidArgument, Code(() => _fs.ReadText("")));
    }

    // ---------------------------------------------------------------- stat and list

    [Fact]
    public void Stat_describes_files_and_directories()
    {
        var file = _tree.File("dir/a.txt", "hello");
        var stat = _fs.Stat(file);
        Assert.Equal(FileEntryKind.File, stat.Kind);
        Assert.Equal(5, stat.Size);
        Assert.Equal("a.txt", stat.Name);
        Assert.False(stat.IsHidden);
        Assert.False(stat.IsSymbolicLink);
        Assert.True(Math.Abs((stat.Modified - DateTimeOffset.UtcNow).TotalMinutes) < 5);

        var dir = _fs.Stat(P("dir"));
        Assert.True(dir.IsDirectory);
        Assert.Equal(0, dir.Size);
        Assert.True(_fs.Stat(_tree.File(".hidden")).IsHidden);
    }

    [Fact]
    public void Stat_of_a_missing_path()
    {
        Assert.Equal(FileErrorCode.NotFound, Code(() => _fs.Stat(P("missing"))));
        Assert.Null(_fs.TryStat(P("missing")));
    }

    [Fact]
    public void Stat_of_a_link_reports_the_target_and_the_link()
    {
        var target = _tree.File("target.txt", "12345678");
        var link = P("link.txt");
        File.CreateSymbolicLink(link, target);
        var stat = _fs.Stat(link);
        Assert.True(stat.IsSymbolicLink);
        Assert.Equal(FileEntryKind.File, stat.Kind);
        Assert.Equal(8, stat.Size);
        Assert.Equal(link, stat.Path);

        var dirLink = P("dirlink");
        Directory.CreateSymbolicLink(dirLink, _tree.Dir("realdir"));
        Assert.True(_fs.Stat(dirLink).IsDirectory);
        Assert.True(_fs.Stat(dirLink).IsSymbolicLink);
    }

    [Fact]
    public void A_broken_link_is_an_empty_file()
    {
        var link = P("broken");
        File.CreateSymbolicLink(link, P("nowhere"));
        var stat = _fs.Stat(link);
        Assert.True(stat.IsSymbolicLink);
        Assert.Equal(FileEntryKind.File, stat.Kind);
        Assert.Equal(0, stat.Size);
        Assert.Equal(FileErrorCode.NotFound, Code(() => _fs.ReadText(link)));
    }

    [Fact]
    public void StatMany_keeps_the_order_and_marks_missing_paths()
    {
        var a = _tree.File("a.txt", "a");
        var b = _tree.File("b.txt", "bb");
        var result = _fs.StatMany([b, P("missing"), a]);
        Assert.Equal(2, result[0]!.Size);
        Assert.Null(result[1]);
        Assert.Equal(1, result[2]!.Size);
    }

    [Fact]
    public void StatMany_with_a_glob_walks_only_where_matches_can_be()
    {
        _tree.File("projects/p1/s1.jsonl", "{}");
        _tree.File("projects/p1/deep/s2.jsonl", "{}");
        _tree.File("projects/p1/notes.txt", "");
        _tree.File("other/s3.jsonl", "{}");
        var visited = new List<string>();
        var listing = _fs.StatMany(_tree.Root, "projects/**/*.jsonl", dir =>
        {
            visited.Add(Path.GetFileName(dir));
            return true;
        });
        Assert.False(listing.Truncated);
        Assert.Equal(["s1.jsonl", "s2.jsonl"], listing.Entries.Select(e => e.Name).Order());
        Assert.DoesNotContain("other", visited);
    }

    [Fact]
    public void StatMany_with_a_glob_honors_the_descend_veto_and_never_enters_links()
    {
        _tree.File("keep/a.md", "");
        _tree.File("skip/b.md", "");
        var outside = Directory.CreateTempSubdirectory("octopus-outside-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(outside, "secret.md"), "");
            Directory.CreateSymbolicLink(P("keep/link"), outside);
            var listing = _fs.StatMany(_tree.Root, "**/*.md", dir => Path.GetFileName(dir) != "skip");
            Assert.Equal(["a.md"], listing.Entries.Select(e => e.Name));
        }
        finally
        {
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public void StatMany_with_a_glob_stops_at_the_limit()
    {
        for (var i = 0; i < 5; i++) _tree.File($"f{i}.txt");
        var listing = new FileAccessService(new FileAccessLimits { MaxListEntries = 3 }).StatMany(_tree.Root, "*.txt");
        Assert.True(listing.Truncated);
        Assert.Equal(3, listing.Entries.Count);
    }

    [Fact]
    public void List_sorts_by_name_and_truncates()
    {
        _tree.File("b.txt");
        _tree.File("a.txt");
        _tree.Dir("c");
        var listing = _fs.List(_tree.Root);
        Assert.Equal(["a.txt", "b.txt", "c"], listing.Entries.Select(e => e.Name));
        Assert.True(listing.Entries[2].IsDirectory);
        Assert.False(listing.Truncated);

        var limited = new FileAccessService(new FileAccessLimits { MaxListEntries = 2 }).List(_tree.Root);
        Assert.True(limited.Truncated);
        Assert.Equal(2, limited.Entries.Count);
    }

    [Fact]
    public void List_needs_an_existing_directory()
    {
        Assert.Equal(FileErrorCode.NotFound, Code(() => _fs.List(P("missing"))));
        Assert.Equal(FileErrorCode.NotDirectory, Code(() => _fs.List(_tree.File("file.txt"))));
    }

    // ---------------------------------------------------------------- read

    [Theory]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i' }, "utf-8", true)]
    [InlineData(new byte[] { 0xFF, 0xFE, (byte)'h', 0, (byte)'i', 0 }, "utf-16le", true)]
    [InlineData(new byte[] { 0xFE, 0xFF, 0, (byte)'h', 0, (byte)'i' }, "utf-16be", true)]
    [InlineData(new byte[] { (byte)'h', (byte)'i' }, "utf-8", false)]
    public void ReadText_detects_the_byte_order_mark(byte[] bytes, string encoding, bool bom)
    {
        var path = P("bom.txt");
        File.WriteAllBytes(path, bytes);
        var text = _fs.ReadText(path);
        Assert.Equal("hi", text.Text);
        Assert.Equal(encoding, text.Encoding);
        Assert.Equal(bom, text.HasBom);
    }

    [Fact]
    public void ReadText_with_a_named_encoding()
    {
        var path = P("latin1.txt");
        File.WriteAllBytes(path, [0x63, 0x61, 0x66, 0xE9]); // "café" in Latin-1
        Assert.Equal("café", _fs.ReadText(path, "latin1").Text);
        Assert.Equal("caf�", _fs.ReadText(path).Text); // not valid UTF-8
        Assert.Equal(FileErrorCode.InvalidArgument, Code(() => _fs.ReadText(path, "no-such-encoding")));
    }

    [Fact]
    public void ReadText_refuses_big_files_and_directories()
    {
        var small = new FileAccessService(new FileAccessLimits { MaxReadBytes = 10 });
        var path = _tree.File("big.txt", new string('x', 11));
        Assert.Equal(FileErrorCode.TooLarge, Code(() => small.ReadText(path)));
        Assert.Equal("xxxxxxxxxx", small.ReadText(_tree.File("ok.txt", new string('x', 10))).Text);
        Assert.Equal(FileErrorCode.IsDirectory, Code(() => _fs.ReadText(_tree.Dir("d"))));
        Assert.Equal(FileErrorCode.NotFound, Code(() => _fs.ReadText(P("missing.txt"))));
    }

    [Fact]
    public void ReadBytes_reads_a_range()
    {
        var path = _tree.File("bytes.bin", "0123456789");
        Assert.Equal("345"u8.ToArray(), _fs.ReadBytes(path, 3, 3));
        Assert.Equal("789"u8.ToArray(), _fs.ReadBytes(path, 7));
        Assert.Equal("89"u8.ToArray(), _fs.ReadBytes(path, 8, 100));
        Assert.Empty(_fs.ReadBytes(path, 50, 5));
        Assert.Equal(FileErrorCode.InvalidArgument, Code(() => _fs.ReadBytes(path, -1)));
        var small = new FileAccessService(new FileAccessLimits { MaxReadBytes = 4 });
        Assert.Equal(FileErrorCode.TooLarge, Code(() => small.ReadBytes(path)));
        Assert.Equal(4, small.ReadBytes(path, 0, 4).Length);
    }

    [Fact]
    public void IsBinary_looks_for_nul_bytes_but_knows_utf16()
    {
        var binary = P("bin");
        File.WriteAllBytes(binary, [1, 2, 0, 3]);
        Assert.True(_fs.IsBinary(binary));
        Assert.False(_fs.IsBinary(_tree.File("text.txt", "plain text")));
        var utf16 = P("utf16.txt");
        File.WriteAllBytes(utf16, [0xFF, 0xFE, (byte)'a', 0, (byte)'b', 0]);
        Assert.False(_fs.IsBinary(utf16));
        Assert.False(_fs.IsBinary(_tree.File("empty.txt")));
    }

    // ---------------------------------------------------------------- write

    [Fact]
    public void WriteText_creates_a_file_atomically_without_leftovers()
    {
        var path = P("new.txt");
        _fs.WriteText(path, "héllo");
        Assert.Equal("héllo", File.ReadAllText(path));
        Assert.Equal([0x68, 0xC3, 0xA9], File.ReadAllBytes(path)[..3]); // UTF-8, no BOM
        Assert.Single(Directory.GetFiles(_tree.Root)); // the temporary file is gone

        _fs.WriteText(path, "replaced");
        Assert.Equal("replaced", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_tree.Root));
    }

    [Fact]
    public void WriteText_honors_create_and_overwrite()
    {
        var existing = _tree.File("exists.txt", "old");
        Assert.Equal(FileErrorCode.Exists, Code(() => _fs.WriteText(existing, "new", new WriteOptions { Overwrite = false })));
        Assert.Equal("old", File.ReadAllText(existing));
        Assert.Equal(FileErrorCode.NotFound, Code(() => _fs.WriteText(P("missing.txt"), "x", new WriteOptions { Create = false })));
        Assert.False(File.Exists(P("missing.txt")));
        _fs.WriteText(existing, "new", new WriteOptions { Create = false });
        Assert.Equal("new", File.ReadAllText(existing));
        _fs.WriteText(P("fresh.txt"), "x", new WriteOptions { Overwrite = false });
        Assert.Equal("x", File.ReadAllText(P("fresh.txt")));
    }

    [Fact]
    public void WriteText_needs_an_existing_parent_directory()
    {
        Assert.Equal(FileErrorCode.NotFound, Code(() => _fs.WriteText(P("no/such/dir/a.txt"), "x")));
        var file = _tree.File("plain.txt");
        Assert.Equal(FileErrorCode.NotDirectory, Code(() => _fs.WriteText(Path.Combine(file, "child.txt"), "x")));
        Assert.Equal(FileErrorCode.IsDirectory, Code(() => _fs.WriteText(_tree.Dir("adir"), "x")));
    }

    [Fact]
    public void WriteText_with_an_encoding_and_a_byte_order_mark()
    {
        var path = P("utf16.txt");
        _fs.WriteText(path, "hi", encoding: "utf-16le", byteOrderMark: true);
        Assert.Equal(new byte[] { 0xFF, 0xFE, (byte)'h', 0, (byte)'i', 0 }, File.ReadAllBytes(path));
        var read = _fs.ReadText(path);
        Assert.Equal(("hi", "utf-16le", true), (read.Text, read.Encoding, read.HasBom));
    }

    [Fact]
    public void Writes_over_the_limit_are_refused()
    {
        var small = new FileAccessService(new FileAccessLimits { MaxWriteBytes = 4 });
        Assert.Equal(FileErrorCode.TooLarge, Code(() => small.WriteText(P("a.txt"), "12345")));
        Assert.Equal(FileErrorCode.TooLarge, Code(() => small.WriteBytes(P("b.bin"), new byte[5])));
        Assert.Equal(FileErrorCode.TooLarge, Code(() => small.AppendText(P("c.txt"), "12345")));
        Assert.Empty(Directory.GetFiles(_tree.Root));
    }

    [Fact]
    public void A_write_through_a_link_changes_the_target_and_keeps_the_link()
    {
        var target = _tree.File("real.txt", "old");
        var link = P("link.txt");
        File.CreateSymbolicLink(link, target);
        _fs.WriteText(link, "new");
        Assert.Equal("new", File.ReadAllText(target));
        Assert.NotNull(new FileInfo(link).LinkTarget);
    }

    [Fact]
    public void New_files_are_never_executable()
    {
        if (OperatingSystem.IsWindows()) return;
        _fs.WriteText(P("script.sh"), "#!/bin/sh\necho hi\n");
        _fs.WriteBytes(P("tool"), [1, 2, 3], new WriteOptions { Atomic = false });
        _fs.AppendText(P("log.txt"), "line\n");
        foreach (var name in new[] { "script.sh", "tool", "log.txt" })
        {
            Assert.Equal((UnixFileMode)0, File.GetUnixFileMode(P(name)) & AnyExecute);
        }
    }

    [Fact]
    public void Replacing_a_file_keeps_its_mode()
    {
        if (OperatingSystem.IsWindows()) return;
        var script = _tree.File("run.sh", "old");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _fs.WriteText(script, "new");
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(script));
    }

    [Fact]
    public void WriteBytes_without_atomic_replaces_in_place()
    {
        var path = _tree.File("data.bin", "0123456789");
        _fs.WriteBytes(path, [65, 66], new WriteOptions { Atomic = false });
        Assert.Equal("AB", File.ReadAllText(path));
        Assert.Equal(FileErrorCode.Exists, Code(() => _fs.WriteBytes(path, [1], new WriteOptions { Atomic = false, Overwrite = false })));
    }

    [Fact]
    public void AppendText_creates_and_appends()
    {
        var path = P("log.txt");
        _fs.AppendText(path, "one\n");
        _fs.AppendText(path, "two\n");
        Assert.Equal("one\ntwo\n", File.ReadAllText(path));
        Assert.Equal(FileErrorCode.IsDirectory, Code(() => _fs.AppendText(_tree.Dir("d"), "x")));
    }

    [Fact]
    public void CreateDirectory_with_and_without_recursive()
    {
        Assert.Equal(FileErrorCode.NotFound, Code(() => _fs.CreateDirectory(P("a/b/c"))));
        _fs.CreateDirectory(P("a/b/c"), recursive: true);
        Assert.True(Directory.Exists(P("a/b/c")));
        _fs.CreateDirectory(P("a/b/c")); // already there
        _fs.CreateDirectory(P("a/d"));
        Assert.True(Directory.Exists(P("a/d")));
        var file = _tree.File("a/file");
        Assert.Equal(FileErrorCode.Exists, Code(() => _fs.CreateDirectory(file)));
        Assert.Equal(FileErrorCode.NotDirectory, Code(() => _fs.CreateDirectory(Path.Combine(file, "x", "y"), recursive: true)));
    }

    // ---------------------------------------------------------------- move, copy, delete

    [Fact]
    public void Move_renames_and_respects_overwrite()
    {
        var a = _tree.File("a.txt", "a");
        var b = _tree.File("b.txt", "b");
        Assert.Equal(FileErrorCode.Exists, Code(() => _fs.Move(a, b)));
        _fs.Move(a, b, overwrite: true);
        Assert.False(File.Exists(a));
        Assert.Equal("a", File.ReadAllText(b));
        _fs.Move(b, P("c.txt"));
        Assert.Equal(FileErrorCode.NotFound, Code(() => _fs.Move(P("nothing"), P("x"))));
        Assert.Equal(FileErrorCode.NotFound, Code(() => _fs.Move(P("c.txt"), P("no/dir/c.txt"))));
    }

    [Fact]
    public void Move_handles_directories()
    {
        var dir = _tree.Dir("dir");
        _tree.File("dir/x.txt", "x");
        Assert.Equal(FileErrorCode.InvalidArgument, Code(() => _fs.Move(dir, P("dir/inner"))));
        var other = _tree.Dir("other");
        Assert.Equal(FileErrorCode.Exists, Code(() => _fs.Move(dir, other, overwrite: true)));
        _fs.Move(dir, P("renamed"));
        Assert.True(File.Exists(P("renamed/x.txt")));
        var file = _tree.File("f.txt");
        Assert.Equal(FileErrorCode.IsDirectory, Code(() => _fs.Move(file, other, overwrite: true)));
    }

    [Fact]
    public void Move_can_change_only_the_case()
    {
        if (!PathGlob.PlatformIgnoresCase) return;
        var file = _tree.File("readme.md", "x");
        _fs.Move(file, P("README.md"));
        Assert.Equal(["README.md"], Directory.GetFiles(_tree.Root).Select(Path.GetFileName));
        var dir = _tree.Dir("docs");
        _fs.Move(dir, P("Docs"));
        Assert.Contains("Docs", Directory.GetDirectories(_tree.Root).Select(Path.GetFileName));
    }

    [Fact]
    public void Move_moves_a_link_not_its_target()
    {
        var target = _tree.Dir("target");
        _tree.File("target/inside.txt");
        var link = P("link");
        Directory.CreateSymbolicLink(link, target);
        _fs.Move(link, P("moved-link"));
        Assert.NotNull(new DirectoryInfo(P("moved-link")).LinkTarget);
        Assert.True(File.Exists(P("target/inside.txt")));
    }

    [Fact]
    public void Copy_a_file_drops_execute_bits()
    {
        var source = _tree.File("tool.sh", "#!/bin/sh");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(source, (UnixFileMode)0b111_101_101);
        _fs.Copy(source, P("copy.sh"));
        Assert.Equal("#!/bin/sh", File.ReadAllText(P("copy.sh")));
        if (!OperatingSystem.IsWindows()) Assert.Equal((UnixFileMode)0, File.GetUnixFileMode(P("copy.sh")) & AnyExecute);
        Assert.Equal(FileErrorCode.Exists, Code(() => _fs.Copy(source, P("copy.sh"))));
        _fs.Copy(source, P("copy.sh"), overwrite: true);
    }

    [Fact]
    public void Copy_a_directory_needs_recursive_and_skips_links()
    {
        _tree.File("src/a.txt", "a");
        _tree.File("src/sub/b.txt", "b");
        var outside = Directory.CreateTempSubdirectory("octopus-outside-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
            Directory.CreateSymbolicLink(P("src/escape"), outside);
            File.CreateSymbolicLink(P("src/secret-link.txt"), Path.Combine(outside, "secret.txt"));
            Assert.Equal(FileErrorCode.IsDirectory, Code(() => _fs.Copy(P("src"), P("dst"))));
            Assert.Equal(FileErrorCode.InvalidArgument, Code(() => _fs.Copy(P("src"), P("src/inner"), recursive: true)));
            _fs.Copy(P("src"), P("dst"), recursive: true);
            Assert.Equal("a", File.ReadAllText(P("dst/a.txt")));
            Assert.Equal("b", File.ReadAllText(P("dst/sub/b.txt")));
            Assert.False(Directory.Exists(P("dst/escape")));
            Assert.False(File.Exists(P("dst/secret-link.txt")));
        }
        finally
        {
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public void Copy_asks_the_filter_about_every_entry_in_the_tree()
    {
        _tree.File("src/keep.txt", "k");
        _tree.File("src/.credentials.json", "secret");
        _tree.File("src/.git/config", "git");
        _tree.File("src/sub/also.txt", "a");
        var asked = new List<string>();
        _fs.Copy(P("src"), P("dst"), recursive: true, filter: path =>
        {
            asked.Add(Path.GetFileName(path));
            return Path.GetFileName(path) is not (".credentials.json" or ".git");
        });
        Assert.True(File.Exists(P("dst/keep.txt")));
        Assert.True(File.Exists(P("dst/sub/also.txt")));
        Assert.False(File.Exists(P("dst/.credentials.json")));
        Assert.False(Directory.Exists(P("dst/.git")));
        Assert.DoesNotContain("config", asked); // a refused directory is not entered
    }

    [Fact]
    public void Copy_into_an_existing_directory_needs_overwrite()
    {
        _tree.File("src/a.txt", "new");
        _tree.File("dst/a.txt", "old");
        _tree.File("dst/keep.txt", "keep");
        Assert.Equal(FileErrorCode.Exists, Code(() => _fs.Copy(P("src"), P("dst"), recursive: true)));
        _fs.Copy(P("src"), P("dst"), recursive: true, overwrite: true);
        Assert.Equal("new", File.ReadAllText(P("dst/a.txt")));
        Assert.Equal("keep", File.ReadAllText(P("dst/keep.txt")));
    }

    [Fact]
    public void Delete_files_and_directories()
    {
        var file = _tree.File("a.txt");
        _fs.Delete(file);
        Assert.False(File.Exists(file));
        Assert.Equal(FileErrorCode.NotFound, Code(() => _fs.Delete(file)));

        var empty = _tree.Dir("empty");
        _fs.Delete(empty);
        Assert.False(Directory.Exists(empty));

        _tree.File("full/x.txt");
        Assert.Equal(FileErrorCode.IsDirectory, Code(() => _fs.Delete(P("full"))));
        _fs.Delete(P("full"), recursive: true);
        Assert.False(Directory.Exists(P("full")));
    }

    [Fact]
    public void Delete_never_follows_links()
    {
        var outside = Directory.CreateTempSubdirectory("octopus-outside-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
            _tree.File("tree/file.txt");
            Directory.CreateSymbolicLink(P("tree/link"), outside);
            _fs.Delete(P("tree"), recursive: true);
            Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));

            Directory.CreateSymbolicLink(P("dirlink"), outside);
            _fs.Delete(P("dirlink"));
            Assert.False(Directory.Exists(P("dirlink")));
            Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
        }
        finally
        {
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public void The_root_of_a_volume_is_never_deleted_or_moved()
    {
        var root = Path.GetPathRoot(_tree.Root)!;
        Assert.Equal(FileErrorCode.InvalidArgument, Code(() => _fs.Delete(root, recursive: true)));
        Assert.Equal(FileErrorCode.InvalidArgument, Code(() => _fs.Move(root, P("x"))));
    }

    [Fact]
    public void Errors_carry_the_path()
    {
        var error = Assert.Throws<FileAccessException>(() => _fs.ReadText(P("missing.txt")));
        Assert.Equal(P("missing.txt"), error.Path);
        Assert.Equal("NotFound", error.Code.ToString());
    }
}
