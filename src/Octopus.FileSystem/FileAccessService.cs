using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Octopus.FileSystem;

/// <summary>
/// <see cref="IFileAccessService"/> on <see cref="System.IO"/>. Reads never lock a file (the person's editor may be
/// writing it); every read is bounded by <see cref="FileAccessLimits"/> no matter what size the file claims.
/// </summary>
public sealed class FileAccessService(FileAccessLimits? limits = null) : IFileAccessService
{
    private const int BinaryProbeSize = 8192;

    /// <summary>rw-rw-rw- before the umask: new files never get an execute bit (design/plugins/04 §3.3).</summary>
    private const UnixFileMode NewFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite;

    private const UnixFileMode ExecuteAndSpecialBits =
        UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute |
        UnixFileMode.SetUser | UnixFileMode.SetGroup | UnixFileMode.StickyBit;

    private static readonly EnumerationOptions DirectoryOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
    };

    private static readonly JsonWriterOptions RowWriterOptions = new()
    {
        // Rows go to JSON.parse, not into HTML: keep non-ASCII text as is instead of \u-escaping it.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly FileAccessLimits _limits = limits ?? FileAccessLimits.Default;

    // ---------------------------------------------------------------- query

    public FileStat Stat(string path) =>
        TryStat(path) ?? throw new FileAccessException(FileErrorCode.NotFound, "Nothing exists at the path.", path);

    public FileStat? TryStat(string path)
    {
        var full = Full(path);
        try
        {
            if (Directory.Exists(full)) return Describe(new DirectoryInfo(full), full);
            var file = new FileInfo(full);
            // Also true for a link whose target is missing: the link itself is an entry.
            return file.Exists ? Describe(file, full) : null;
        }
        catch (Exception e) when (IsMappable(e))
        {
            throw Map(e, full);
        }
    }

    public IReadOnlyList<FileStat?> StatMany(IReadOnlyList<string> paths)
    {
        var result = new FileStat?[paths.Count];
        for (var i = 0; i < paths.Count; i++) result[i] = TryStat(paths[i]);
        return result;
    }

    public FileListing StatMany(string root, string glob, Func<string, bool>? descend = null, CancellationToken cancellationToken = default)
    {
        var full = RequireDirectory(root);
        if (string.IsNullOrWhiteSpace(glob)) throw new FileAccessException(FileErrorCode.InvalidArgument, "The glob is empty.", full);
        var matcher = new PathGlob(glob, PathGlob.PlatformIgnoresCase);
        var found = new List<FileStat>();
        var queue = new Queue<(string Directory, string Relative)>();
        queue.Enqueue((full, ""));
        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, relative) = queue.Dequeue();
            foreach (var entry in EnumerateQuietly(directory))
            {
                var childRelative = relative.Length == 0 ? entry.Name : relative + "/" + entry.Name;
                FileAttributes attributes;
                try
                {
                    attributes = entry.Attributes;
                }
                catch (Exception e) when (IsMappable(e))
                {
                    continue; // vanished while walking
                }
                if (matcher.IsMatch(childRelative))
                {
                    if (found.Count >= _limits.MaxListEntries) return new FileListing(found, Truncated: true);
                    if (TryDescribe(entry) is { } stat) found.Add(stat);
                }
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                var isLink = (attributes & FileAttributes.ReparsePoint) != 0;
                // A link is reported but not entered: it may lead out of the root or around in a circle.
                if (isDirectory && !isLink && matcher.CouldMatchInside(childRelative) && (descend?.Invoke(entry.FullName) ?? true))
                {
                    queue.Enqueue((entry.FullName, childRelative));
                }
            }
        }
        return new FileListing(found, Truncated: false);
    }

    public FileListing List(string directory)
    {
        var full = RequireDirectory(directory);
        try
        {
            var entries = new List<FileStat>();
            var truncated = false;
            foreach (var entry in new DirectoryInfo(full).EnumerateFileSystemInfos("*", DirectoryOptions))
            {
                if (entries.Count >= _limits.MaxListEntries)
                {
                    truncated = true;
                    break;
                }
                if (TryDescribe(entry) is { } stat) entries.Add(stat);
            }
            entries.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
            return new FileListing(entries, truncated);
        }
        catch (Exception e) when (IsMappable(e))
        {
            throw Map(e, full);
        }
    }

    // ---------------------------------------------------------------- read

    public FileText ReadText(string path, string? encoding = null)
    {
        var full = Full(path);
        try
        {
            using var stream = OpenRead(full);
            return TextCodec.Decode(ReadAllBounded(stream, full), encoding);
        }
        catch (Exception e) when (IsMappable(e))
        {
            throw Map(e, full);
        }
    }

    public byte[] ReadBytes(string path, long offset = 0, long? length = null)
    {
        var full = Full(path);
        if (offset < 0 || length < 0) throw new FileAccessException(FileErrorCode.InvalidArgument, "Offset and length must not be negative.", full);
        try
        {
            using var stream = OpenRead(full);
            var available = Math.Max(0, stream.Length - offset);
            var count = length is { } wanted ? Math.Min(wanted, available) : available;
            if (count > _limits.MaxReadBytes) throw TooLarge(count, _limits.MaxReadBytes, full);
            if (count == 0) return [];
            var bytes = new byte[count];
            stream.Seek(offset, SeekOrigin.Begin);
            var read = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            return read == bytes.Length ? bytes : bytes[..read];
        }
        catch (Exception e) when (IsMappable(e))
        {
            throw Map(e, full);
        }
    }

    public bool IsBinary(string path)
    {
        var full = Full(path);
        try
        {
            using var stream = OpenRead(full);
            Span<byte> probe = stackalloc byte[BinaryProbeSize];
            var read = stream.ReadAtLeast(probe, probe.Length, throwOnEndOfStream: false);
            probe = probe[..read];
            // UTF-16 text is full of NUL bytes; its byte order mark says what it is.
            return !TextCodec.StartsWithUtf16Bom(probe) && probe.Contains((byte)0);
        }
        catch (Exception e) when (IsMappable(e))
        {
            throw Map(e, full);
        }
    }

    // ---------------------------------------------------------------- write

    public void WriteText(string path, string text, WriteOptions? options = null, string? encoding = null, bool byteOrderMark = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        var full = Full(path);
        try
        {
            var bytes = TextCodec.Encode(text, encoding, byteOrderMark, _limits.MaxWriteBytes, full);
            WriteCore(full, bytes, options ?? WriteOptions.Default);
        }
        catch (Exception e) when (IsMappable(e))
        {
            throw Map(e, full);
        }
    }

    public void WriteBytes(string path, ReadOnlySpan<byte> bytes, WriteOptions? options = null)
    {
        var full = Full(path);
        if (bytes.Length > _limits.MaxWriteBytes) throw TooLarge(bytes.Length, _limits.MaxWriteBytes, full);
        try
        {
            WriteCore(full, bytes, options ?? WriteOptions.Default);
        }
        catch (Exception e) when (IsMappable(e))
        {
            throw Map(e, full);
        }
    }

    public void AppendText(string path, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var full = Full(path);
        try
        {
            var bytes = TextCodec.Encode(text, null, byteOrderMark: false, _limits.MaxWriteBytes, full);
            var target = WriteTarget(full);
            if (Directory.Exists(target)) throw new FileAccessException(FileErrorCode.IsDirectory, "Cannot append to a directory.", target);
            RequireParent(target);
            using var stream = new FileStream(target, NewFileOptions(FileMode.Append, FileShare.ReadWrite | FileShare.Delete));
            stream.Write(bytes);
        }
        catch (Exception e) when (IsMappable(e))
        {
            throw Map(e, full);
        }
    }

    public void CreateDirectory(string path, bool recursive = false)
    {
        var full = Full(path);
        try
        {
            if (Directory.Exists(full)) return;
            if (Exists(full)) throw new FileAccessException(FileErrorCode.Exists, "A file is in the way.", full);
            if (recursive) RequireNoFileAncestor(full);
            else RequireParent(full);
            Directory.CreateDirectory(full);
        }
        catch (Exception e) when (IsMappable(e))
        {
            throw Map(e, full);
        }
    }

    // ---------------------------------------------------------------- change

    public void Move(string source, string destination, bool overwrite = false)
    {
        var from = Full(source);
        var to = Full(destination);
        try
        {
            if (!Exists(from)) throw new FileAccessException(FileErrorCode.NotFound, "Nothing exists at the source.", from);
            RefuseRoot(from);
            RefuseRoot(to);
            if (string.Equals(from, to, StringComparison.Ordinal)) return;
            var isLink = IsLink(from);
            var isDirectory = !isLink && Directory.Exists(from);
            // On a case-insensitive file system "a.txt" → "A.txt" names the same entry: just rename it.
            var caseOnly = PathGlob.PlatformIgnoresCase && string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly)
            {
                if (isDirectory && IsInsideOrEqual(from, to)) throw new FileAccessException(FileErrorCode.InvalidArgument, "Cannot move a directory into itself.", to);
                if (Exists(to))
                {
                    if (!overwrite) throw new FileAccessException(FileErrorCode.Exists, "The destination exists.", to);
                    if (isDirectory) throw new FileAccessException(FileErrorCode.Exists, "A directory cannot replace an existing entry.", to);
                    if (Directory.Exists(to) && !IsLink(to)) throw new FileAccessException(FileErrorCode.IsDirectory, "A file cannot replace a directory.", to);
                }
                RequireParent(to);
            }
            if (isDirectory || isLink)
            {
                // Directory.Move is a plain rename: it moves a link itself, whatever it points to.
                if (!caseOnly && overwrite && Exists(to)) File.Delete(to);
                Directory.Move(from, to);
            }
            else
            {
                File.Move(from, to, overwrite);
            }
        }
        catch (Exception e) when (IsMappable(e))
        {
            throw Map(e, from);
        }
    }

    public void Copy(string source, string destination, bool recursive = false, bool overwrite = false, Func<string, bool>? filter = null)
    {
        var from = Full(source);
        var to = Full(destination);
        try
        {
            if (!Exists(from)) throw new FileAccessException(FileErrorCode.NotFound, "Nothing exists at the source.", from);
            if (Directory.Exists(from))
            {
                if (!recursive) throw new FileAccessException(FileErrorCode.IsDirectory, "Copying a directory needs recursive.", from);
                if (IsInsideOrEqual(from, to)) throw new FileAccessException(FileErrorCode.InvalidArgument, "Cannot copy a directory into itself.", to);
                if (Directory.Exists(to))
                {
                    if (!overwrite) throw new FileAccessException(FileErrorCode.Exists, "The destination exists.", to);
                }
                else
                {
                    if (Exists(to)) throw new FileAccessException(FileErrorCode.Exists, "A file is in the way.", to);
                    RequireParent(to);
                }
                CopyTree(from, to, overwrite, filter);
                return;
            }
            if (Directory.Exists(to)) throw new FileAccessException(FileErrorCode.IsDirectory, "A file cannot replace a directory.", to);
            if (Exists(to) && !overwrite) throw new FileAccessException(FileErrorCode.Exists, "The destination exists.", to);
            RequireParent(to);
            CopyFile(from, to, overwrite);
        }
        catch (Exception e) when (IsMappable(e))
        {
            throw Map(e, from);
        }
    }

    public void Delete(string path, bool recursive = false)
    {
        var full = Full(path);
        try
        {
            if (!Exists(full)) throw new FileAccessException(FileErrorCode.NotFound, "Nothing exists at the path.", full);
            RefuseRoot(full);
            if (IsLink(full))
            {
                // Only the link goes. Windows keeps directory links as directories; Unix unlinks any link.
                if (OperatingSystem.IsWindows() && Directory.Exists(full)) Directory.Delete(full);
                else File.Delete(full);
                return;
            }
            if (Directory.Exists(full))
            {
                if (recursive)
                {
                    Directory.Delete(full, recursive: true); // does not follow links inside
                }
                else
                {
                    if (Directory.EnumerateFileSystemEntries(full).Any())
                    {
                        throw new FileAccessException(FileErrorCode.IsDirectory, "The directory is not empty; delete it recursively.", full);
                    }
                    Directory.Delete(full);
                }
                return;
            }
            File.Delete(full);
        }
        catch (Exception e) when (IsMappable(e))
        {
            throw Map(e, full);
        }
    }

    // ---------------------------------------------------------------- lines

    public LineBatch ReadLines(string path, LineReadOptions? options = null, CancellationToken cancellationToken = default)
    {
        var o = options ?? new LineReadOptions();
        var full = Full(path);
        if (o.FromOffset < 0 || o.Limit <= 0 || o.MaxLineLength <= 0)
        {
            throw new FileAccessException(FileErrorCode.InvalidArgument, "fromOffset must not be negative; limit and maxLineLength must be positive.", full);
        }
        try
        {
            using var stream = OpenRead(full);
            var length = stream.Length;
            RequireOffsetInFile(o.FromOffset, length, full);
            var lines = new List<string>();
            var bytes = 0L;
            var budget = _limits.MaxReadBytes;
            var result = LineScanner.Scan(stream, o.FromOffset, Math.Min(o.MaxLineLength, budget), (line, terminated) =>
            {
                if (!terminated && !o.IncludeUnterminatedLine) return LineAction.StopBefore;
                if (lines.Count > 0 && bytes + line.Length > budget) return LineAction.StopBefore;
                lines.Add(TextCodec.Utf8NoBom.GetString(line));
                bytes += line.Length;
                return lines.Count >= o.Limit ? LineAction.Stop : LineAction.Continue;
            }, cancellationToken);
            return new LineBatch(lines, result.NextOffset, result.ReachedEnd || result.NextOffset >= length, result.Overlong);
        }
        catch (Exception e) when (IsMappable(e))
        {
            throw Map(e, full);
        }
    }

    public IReadOnlyList<string> Tail(string path, int lines)
    {
        var full = Full(path);
        if (lines <= 0) throw new FileAccessException(FileErrorCode.InvalidArgument, "lines must be positive.", full);
        try
        {
            using var stream = OpenRead(full);
            var length = stream.Length;
            if (length == 0) return [];
            var end = length;
            stream.Seek(length - 1, SeekOrigin.Begin);
            if (stream.ReadByte() == '\n') end--; // a final line break does not start another line

            var budget = _limits.MaxReadBytes;
            var chunk = ArrayPool<byte>.Shared.Rent(64 * 1024);
            long start = 0, earliestBreak = -1;
            try
            {
                var found = 0;
                var position = end;
                var complete = false;
                while (position > 0 && !complete)
                {
                    var size = (int)Math.Min(chunk.Length, Math.Min(position, budget - (end - position)));
                    if (size <= 0) break; // window full
                    position -= size;
                    stream.Seek(position, SeekOrigin.Begin);
                    stream.ReadExactly(chunk, 0, size);
                    for (var i = size - 1; i >= 0; i--)
                    {
                        if (chunk[i] != '\n') continue;
                        earliestBreak = position + i;
                        if (++found == lines)
                        {
                            start = position + i + 1;
                            complete = true;
                            break;
                        }
                    }
                }
                if (!complete && position > 0)
                {
                    // The window ran out before the beginning of the file: keep only whole lines inside it.
                    if (earliestBreak < 0) throw TooLarge(end, budget, full);
                    start = earliestBreak + 1;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunk);
            }

            var window = new byte[end - start];
            stream.Seek(start, SeekOrigin.Begin);
            stream.ReadExactly(window);
            var span = start == 0 && window.AsSpan().StartsWith(TextCodec.Utf8Preamble) ? window.AsSpan(3) : window.AsSpan();
            var text = TextCodec.Utf8NoBom.GetString(span);
            var result = text.Split('\n');
            for (var i = 0; i < result.Length; i++)
            {
                if (result[i].EndsWith('\r')) result[i] = result[i][..^1];
            }
            return result;
        }
        catch (Exception e) when (IsMappable(e))
        {
            throw Map(e, full);
        }
    }

    public JsonLinesBatch ScanJsonLines(string path, JsonLinesQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var full = Full(path);
        if (query.FromOffset < 0 || query.Limit <= 0 || query.MaxLineLength <= 0 || query.MaxResultBytes <= 0)
        {
            throw new FileAccessException(FileErrorCode.InvalidArgument, "fromOffset must not be negative; limit, maxLineLength and maxResultBytes must be positive.", full);
        }
        var projector = new JsonLineProjector(query.Select, query.Where);
        try
        {
            using var stream = OpenRead(full);
            var length = stream.Length;
            RequireOffsetInFile(query.FromOffset, length, full);
            var buffer = new ArrayBufferWriter<byte>(4096);
            using var writer = new Utf8JsonWriter(buffer, RowWriterOptions);
            writer.WriteStartArray();
            var rows = 0;
            var skipped = 0;
            var result = LineScanner.Scan(stream, query.FromOffset, query.MaxLineLength, (line, terminated) =>
            {
                if (line.IsEmpty || line.IndexOfAnyExcept((byte)' ', (byte)'\t', (byte)'\r') < 0)
                {
                    return terminated ? LineAction.Continue : LineAction.StopBefore;
                }
                if (!projector.TryRead(line, out var matches))
                {
                    // A last line that does not parse yet is probably still being written: leave it for next time.
                    if (!terminated) return LineAction.StopBefore;
                    skipped++;
                    return LineAction.Continue;
                }
                if (!matches) return LineAction.Continue;
                if (rows > 0 && writer.BytesCommitted + writer.BytesPending + projector.RowSize() > query.MaxResultBytes)
                {
                    return LineAction.StopBefore;
                }
                projector.WriteRow(writer, line);
                rows++;
                return rows >= query.Limit ? LineAction.Stop : LineAction.Continue;
            }, cancellationToken);
            writer.WriteEndArray();
            writer.Flush();
            var json = TextCodec.Utf8NoBom.GetString(buffer.WrittenSpan);
            return new JsonLinesBatch(json, rows, result.NextOffset, result.ReachedEnd || result.NextOffset >= length, skipped + result.Overlong);
        }
        catch (Exception e) when (IsMappable(e))
        {
            throw Map(e, full);
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Validates and normalizes a path: absolute, no NUL, <c>.</c> and <c>..</c> collapsed, no trailing separator.</summary>
    private static string Full(string path)
    {
        if (string.IsNullOrEmpty(path)) throw new FileAccessException(FileErrorCode.InvalidArgument, "The path is empty.");
        if (path.Contains('\0')) throw new FileAccessException(FileErrorCode.InvalidArgument, "The path contains a NUL character.");
        if (!Path.IsPathFullyQualified(path)) throw new FileAccessException(FileErrorCode.InvalidArgument, "The path must be absolute.", path);
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new FileAccessException(FileErrorCode.InvalidArgument, "The path is not valid.", path, e);
        }
    }

    private static string RequireDirectory(string path)
    {
        var full = Full(path);
        if (Directory.Exists(full)) return full;
        throw Exists(full)
            ? new FileAccessException(FileErrorCode.NotDirectory, "The path is a file, not a directory.", full)
            : new FileAccessException(FileErrorCode.NotFound, "The directory does not exist.", full);
    }

    /// <summary>An entry exists: file, directory, or a link (even one whose target is missing).</summary>
    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static bool IsLink(string path) => new FileInfo(path).LinkTarget is not null;

    private static void RefuseRoot(string path)
    {
        if (Path.GetPathRoot(path) is { } root &&
            string.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase))
        {
            throw new FileAccessException(FileErrorCode.InvalidArgument, "Refusing to change the root of a volume.", path);
        }
    }

    private static bool IsInsideOrEqual(string root, string path)
    {
        var comparison = PathGlob.PlatformIgnoresCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(root, path, comparison)) return true;
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, comparison);
    }

    /// <summary>The parent must be an existing directory: <see cref="FileErrorCode.NotFound"/> or <see cref="FileErrorCode.NotDirectory"/> otherwise.</summary>
    private static void RequireParent(string path)
    {
        var parent = Path.GetDirectoryName(path) ?? throw new FileAccessException(FileErrorCode.InvalidArgument, "The path has no parent directory.", path);
        if (Directory.Exists(parent)) return;
        RequireNoFileAncestor(parent);
        throw new FileAccessException(FileErrorCode.NotFound, "The parent directory does not exist.", parent);
    }

    /// <summary>Walks up to the nearest existing ancestor; a file there means the path can never be a directory.</summary>
    private static void RequireNoFileAncestor(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if (Directory.Exists(current)) return;
            if (File.Exists(current)) throw new FileAccessException(FileErrorCode.NotDirectory, "A file is in the way of the path.", current);
        }
    }

    private static void RequireOffsetInFile(long offset, long length, string path)
    {
        if (offset > length)
        {
            throw new FileAccessException(FileErrorCode.InvalidArgument,
                "fromOffset is past the end of the file: it was truncated or replaced; read again from 0.", path);
        }
    }

    private static FileStream OpenRead(string path)
    {
        if (Directory.Exists(path)) throw new FileAccessException(FileErrorCode.IsDirectory, "The path is a directory.", path);
        // No buffering of our own: every reader here reads in large chunks. Share everything so an editor that is
        // writing or replacing the file is never blocked by us.
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 0);
    }

    /// <summary>The whole stream, refusing anything over the read limit even when the reported size is wrong (special files report 0).</summary>
    private byte[] ReadAllBounded(FileStream stream, string path)
    {
        var max = _limits.MaxReadBytes;
        var length = stream.Length;
        if (length > max) throw TooLarge(length, max, path);
        if (length > 0)
        {
            var bytes = new byte[length];
            var read = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            return read == bytes.Length ? bytes : bytes[..read];
        }
        using var memory = new MemoryStream();
        var chunk = new byte[81920];
        int count;
        while ((count = stream.Read(chunk)) > 0)
        {
            if (memory.Length + count > max) throw TooLarge(memory.Length + count, max, path);
            memory.Write(chunk, 0, count);
        }
        return memory.ToArray();
    }

    /// <summary>A write through a link lands on the link's final target, as editors do; the link stays a link.</summary>
    private static string WriteTarget(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is null) return path;
        return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
    }

    private static void WriteCore(string path, ReadOnlySpan<byte> bytes, WriteOptions options)
    {
        var target = WriteTarget(path);
        if (Directory.Exists(target)) throw new FileAccessException(FileErrorCode.IsDirectory, "The path is a directory.", target);
        var exists = File.Exists(target);
        if (exists && !options.Overwrite) throw new FileAccessException(FileErrorCode.Exists, "The file exists.", target);
        if (!exists && !options.Create) throw new FileAccessException(FileErrorCode.NotFound, "The file does not exist.", target);
        RequireParent(target);

        if (!options.Atomic)
        {
            var mode = !options.Overwrite ? FileMode.CreateNew : options.Create ? FileMode.Create : FileMode.Truncate;
            using var stream = new FileStream(target, NewFileOptions(mode, FileShare.Read));
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            return;
        }

        // Same directory, so the final rename never crosses a volume and replaces the target in one step.
        var temp = Path.Combine(Path.GetDirectoryName(target)!, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, NewFileOptions(FileMode.CreateNew, FileShare.None)))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (exists) KeepMode(target, temp);
            File.Move(temp, target, options.Overwrite);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private static FileStreamOptions NewFileOptions(FileMode mode, FileShare share)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.Write, Share = share, BufferSize = 0 };
        // The mode only applies when a file is created, and FileStream refuses it for modes that cannot create one.
        if (!OperatingSystem.IsWindows() && mode is not (FileMode.Open or FileMode.Truncate)) options.UnixCreateMode = NewFileMode;
        return options;
    }

    /// <summary>
    /// The replacement keeps the permissions of the file it replaces (an edited script stays runnable, a private file
    /// stays private), minus set-id bits. That is not "setting" a bit: the file had it already.
    /// </summary>
    private static void KeepMode(string original, string replacement)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            var mode = File.GetUnixFileMode(original) & ~(UnixFileMode.SetUser | UnixFileMode.SetGroup);
            File.SetUnixFileMode(replacement, mode);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The new file keeps the default mode, which is never executable.
        }
    }

    private static void CopyFile(string from, string to, bool overwrite)
    {
        File.Copy(from, to, overwrite);
        if (OperatingSystem.IsWindows()) return;
        // File.Copy carries the source's mode over; a copy is a new file and gets no execute or set-id bits.
        var mode = File.GetUnixFileMode(to);
        if ((mode & ExecuteAndSpecialBits) != 0) File.SetUnixFileMode(to, mode & ~ExecuteAndSpecialBits);
    }

    private static void CopyTree(string from, string to, bool overwrite, Func<string, bool>? filter)
    {
        Directory.CreateDirectory(to);
        foreach (var entry in new DirectoryInfo(from).EnumerateFileSystemInfos("*", DirectoryOptions))
        {
            var attributes = entry.Attributes;
            if ((attributes & FileAttributes.ReparsePoint) != 0 && entry.LinkTarget is not null) continue;
            if (filter is not null && !filter(entry.FullName)) continue;
            var target = Path.Combine(to, entry.Name);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if (Exists(target) && !Directory.Exists(target)) throw new FileAccessException(FileErrorCode.Exists, "A file is in the way.", target);
                CopyTree(entry.FullName, target, overwrite, filter);
            }
            else
            {
                if (Directory.Exists(target)) throw new FileAccessException(FileErrorCode.IsDirectory, "A file cannot replace a directory.", target);
                if (!overwrite && Exists(target)) throw new FileAccessException(FileErrorCode.Exists, "The destination exists.", target);
                CopyFile(entry.FullName, target, overwrite);
            }
        }
    }

    private static IEnumerable<FileSystemInfo> EnumerateQuietly(string directory)
    {
        List<FileSystemInfo> entries;
        try
        {
            entries = [.. new DirectoryInfo(directory).EnumerateFileSystemInfos("*", DirectoryOptions)];
        }
        catch (Exception e) when (IsMappable(e))
        {
            return []; // unreadable or vanished: the walk goes on without it
        }
        return entries;
    }

    private static FileStat? TryDescribe(FileSystemInfo info)
    {
        try
        {
            return Describe(info, info.FullName);
        }
        catch (Exception e) when (IsMappable(e))
        {
            return null; // vanished between listing and looking
        }
    }

    private static FileStat Describe(FileSystemInfo info, string path)
    {
        var name = Path.GetFileName(path);
        if (name.Length == 0) name = path;
        var attributes = info.Attributes;
        var hidden = name.StartsWith('.') || (attributes & FileAttributes.Hidden) != 0;
        if ((attributes & FileAttributes.ReparsePoint) != 0 && info.LinkTarget is not null)
        {
            // A link reports its target (as stat(2) does), and that it is a link. FileInfo.Length of a link is the
            // length of the link itself, so look at the final target explicitly.
            string? final = null;
            try
            {
                final = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            }
            catch (IOException)
            {
                // a cycle of links: reported as a broken link
            }
            if (final is not null)
            {
                if (Directory.Exists(final)) return new FileStat(path, name, FileEntryKind.Directory, 0, Utc(new DirectoryInfo(final).LastWriteTimeUtc), hidden, true);
                var file = new FileInfo(final);
                if (file.Exists && file.LinkTarget is null) return new FileStat(path, name, FileEntryKind.File, file.Length, Utc(file.LastWriteTimeUtc), hidden, true);
            }
            return new FileStat(path, name, FileEntryKind.File, 0, Utc(info.LastWriteTimeUtc), hidden, true);
        }
        if ((attributes & FileAttributes.Directory) != 0) return new FileStat(path, name, FileEntryKind.Directory, 0, Utc(info.LastWriteTimeUtc), hidden, false);
        return new FileStat(path, name, FileEntryKind.File, info is FileInfo f ? f.Length : 0, Utc(info.LastWriteTimeUtc), hidden, false);
    }

    private static DateTimeOffset Utc(DateTime utc) => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeSpan.Zero);

    private static FileAccessException TooLarge(long size, int max, string path) =>
        new(FileErrorCode.TooLarge, $"{size} bytes is over the {max} byte limit of one call; read it in pieces.", path);

    private static bool IsMappable(Exception e) =>
        e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    /// <summary>Turns a <see cref="System.IO"/> exception into the error code a plugin can act on.</summary>
    private static FileAccessException Map(Exception e, string path) => e switch
    {
        FileNotFoundException or DirectoryNotFoundException => new(FileErrorCode.NotFound, e.Message, path, e),
        UnauthorizedAccessException => new(FileErrorCode.PermissionDenied, e.Message, path, e),
        PathTooLongException => new(FileErrorCode.InvalidArgument, e.Message, path, e),
        IOException io when IoError.Is(io, IoError.Kind.Exists) => new(FileErrorCode.Exists, e.Message, path, e),
        IOException io when IoError.Is(io, IoError.Kind.NotEmpty) => new(FileErrorCode.IsDirectory, e.Message, path, e),
        IOException io when IoError.Is(io, IoError.Kind.IsDirectory) => new(FileErrorCode.IsDirectory, e.Message, path, e),
        IOException io when IoError.Is(io, IoError.Kind.NotDirectory) => new(FileErrorCode.NotDirectory, e.Message, path, e),
        IOException => new(FileErrorCode.Io, e.Message, path, e),
        NotSupportedException => new(FileErrorCode.Unsupported, e.Message, path, e),
        _ => new(FileErrorCode.InvalidArgument, e.Message, path, e),
    };

    /// <summary>
    /// The error behind an <see cref="IOException"/>. On Unix the runtime puts the errno into <c>HResult</c>; on
    /// Windows it is an HRESULT wrapping the Win32 error.
    /// </summary>
    private static class IoError
    {
        public enum Kind { Exists, NotEmpty, IsDirectory, NotDirectory }

        public static bool Is(IOException e, Kind kind)
        {
            if (OperatingSystem.IsWindows())
            {
                var win32 = e.HResult & 0xFFFF;
                return kind switch
                {
                    Kind.Exists => win32 is 80 or 183,  // ERROR_FILE_EXISTS, ERROR_ALREADY_EXISTS
                    Kind.NotEmpty => win32 is 145,      // ERROR_DIR_NOT_EMPTY
                    Kind.NotDirectory => win32 is 267,  // ERROR_DIRECTORY
                    _ => false,
                };
            }
            return kind switch
            {
                Kind.Exists => e.HResult == 17,                                   // EEXIST
                Kind.NotEmpty => e.HResult == (OperatingSystem.IsLinux() ? 39 : 66), // ENOTEMPTY
                Kind.NotDirectory => e.HResult == 20,                             // ENOTDIR
                Kind.IsDirectory => e.HResult == 21,                              // EISDIR
                _ => false,
            };
        }
    }
}
