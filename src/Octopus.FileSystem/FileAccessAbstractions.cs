using System.Text.Json;

namespace Octopus.FileSystem;

/// <summary>
/// Why a <see cref="IFileAccessService"/> call failed. The names are the error codes the plugin SDK throws
/// (design/plugins/02-运行时.md §3.3), so the bridge can pass <c>code.ToString()</c> through unchanged.
/// </summary>
public enum FileErrorCode
{
    NotFound,
    /// <summary>Something is already at the target and the call was told not to replace it.</summary>
    Exists,
    /// <summary>A file was expected; also a non-recursive delete of a directory that is not empty.</summary>
    IsDirectory,
    /// <summary>A directory was expected, or a parent on the way is a file.</summary>
    NotDirectory,
    /// <summary>Over the size limit of a single call (<see cref="FileAccessLimits"/>).</summary>
    TooLarge,
    InvalidArgument,
    /// <summary>The operating system refused (file permissions, read-only volume). Not the plugin permission check.</summary>
    PermissionDenied,
    Unsupported,
    Io,
}

/// <summary>
/// The only exception <see cref="IFileAccessService"/> throws for a file-system problem. <see cref="Exception.Message"/>
/// is for logs; callers that talk to plugins pass on <see cref="Code"/> only (design/plugins/02 §3.3: host exception
/// text is not handed to plugins).
/// </summary>
public sealed class FileAccessException : Exception
{
    public FileAccessException(FileErrorCode code, string message, string? path = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Path = path;
    }

    public FileErrorCode Code { get; }

    /// <summary>The path the failing operation was about, when there is one.</summary>
    public string? Path { get; }
}

public enum FileEntryKind
{
    File,
    Directory,
}

/// <summary>What <c>fs.stat</c> and <c>fs.list</c> report (design/plugins/09 <c>FileStat</c>).</summary>
/// <param name="Path">The absolute path, as asked for (links are not replaced by their target).</param>
/// <param name="Kind">
/// Of the link's target when <paramref name="IsSymbolicLink"/>; a link whose target is missing is reported as an
/// empty <see cref="FileEntryKind.File"/>.
/// </param>
/// <param name="Size">Bytes of the file (of the target for a link); 0 for directories.</param>
/// <param name="IsHidden">The name starts with a dot, or the platform marks the entry hidden.</param>
/// <param name="IsSymbolicLink">The entry itself is a symbolic link (or another kind of reparse point on Windows).</param>
public sealed record FileStat(
    string Path,
    string Name,
    FileEntryKind Kind,
    long Size,
    DateTimeOffset Modified,
    bool IsHidden,
    bool IsSymbolicLink)
{
    public bool IsDirectory => Kind == FileEntryKind.Directory;
}

/// <summary>Entries of a directory listing or a glob walk.</summary>
/// <param name="Truncated">More entries exist than the limit allowed; the list holds the first ones found.</param>
public sealed record FileListing(IReadOnlyList<FileStat> Entries, bool Truncated);

/// <summary>Decoded file content.</summary>
/// <param name="Encoding">The encoding that was used, lower case: <c>utf-8</c>, <c>utf-16le</c>, <c>utf-16be</c>, or the name asked for.</param>
/// <param name="HasBom">The file started with the byte order mark of <paramref name="Encoding"/>; it is not part of <paramref name="Text"/>.</param>
public sealed record FileText(string Text, string Encoding, bool HasBom);

/// <summary>How a write treats the target (design/plugins/09 <c>writeText(path, text, { create, overwrite })</c>).</summary>
public sealed record WriteOptions
{
    public static readonly WriteOptions Default = new();

    /// <summary>A missing file may be created. When false, writing to a missing file fails with <see cref="FileErrorCode.NotFound"/>.</summary>
    public bool Create { get; init; } = true;

    /// <summary>An existing file may be replaced. When false, it fails with <see cref="FileErrorCode.Exists"/>.</summary>
    public bool Overwrite { get; init; } = true;

    /// <summary>
    /// Write a temporary file next to the target and rename it over the target, so a crash never leaves half a file.
    /// Off only for the rare file whose identity (inode, hard links) must survive the write.
    /// </summary>
    public bool Atomic { get; init; } = true;
}

/// <summary>Options of <see cref="IFileAccessService.ReadLines"/>.</summary>
public sealed record LineReadOptions
{
    /// <summary>Byte offset to start at; a value from <see cref="LineBatch.NextOffset"/> reads only what was added since.</summary>
    public long FromOffset { get; init; }

    /// <summary>At most this many lines.</summary>
    public int Limit { get; init; } = FileAccessLimits.DefaultLineLimit;

    /// <summary>Lines longer than this many bytes are skipped and counted in <see cref="LineBatch.Skipped"/>.</summary>
    public int MaxLineLength { get; init; } = FileAccessLimits.DefaultMaxLineLength;

    /// <summary>
    /// Also return a last line that has no line break yet, and move past it. Off by default: a log that is being
    /// written may end in half a line, and reading it now would split it in two.
    /// </summary>
    public bool IncludeUnterminatedLine { get; init; }
}

/// <summary>A page of lines.</summary>
/// <param name="Lines">UTF-8 decoded, without the line break (<c>\n</c> or <c>\r\n</c>). No line contains <c>\n</c>, so a bridge can pass the page as one <c>\n</c>-joined string instead of a JSON array.</param>
/// <param name="NextOffset">Where the next call starts. Always at the start of a line.</param>
/// <param name="Done">Nothing after <paramref name="NextOffset"/> is a complete line yet.</param>
/// <param name="Skipped">Lines over the length limit that were passed over.</param>
public sealed record LineBatch(IReadOnlyList<string> Lines, long NextOffset, bool Done, int Skipped);

/// <summary>What <see cref="IFileAccessService.ScanJsonLines"/> picks out of a JSON-lines file (design/plugins/11 §5.2).</summary>
public sealed record JsonLinesQuery
{
    /// <summary>Byte offset to start at, normally a previous <see cref="JsonLinesBatch.NextOffset"/>.</summary>
    public long FromOffset { get; init; }

    /// <summary>
    /// Dotted path → value. A line is kept only when every path exists and its value equals the given one (JSON
    /// equality: <c>1</c> equals <c>1.0</c>, objects compare member by member).
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement>? Where { get; init; }

    /// <summary>
    /// Dotted paths, one column each. A segment is a property name, or an array index on an array
    /// (<c>content.0.type</c>). No wildcards or expressions: this is a projection, not a query language. A path that
    /// does not exist in a line gives <c>null</c>.
    /// </summary>
    public IReadOnlyList<string> Select { get; init; } = [];

    /// <summary>At most this many rows.</summary>
    public int Limit { get; init; } = FileAccessLimits.DefaultLineLimit;

    /// <summary>Lines longer than this many bytes are skipped without being parsed, and counted.</summary>
    public int MaxLineLength { get; init; } = FileAccessLimits.DefaultMaxLineLength;

    /// <summary>
    /// The page stops before <see cref="JsonLinesBatch.RowsJson"/> grows past this many bytes, so the reply stays
    /// under the plugin bridge's 4 MB JSON message limit (design/plugins/13 §1.4).
    /// </summary>
    public int MaxResultBytes { get; init; } = FileAccessLimits.DefaultMaxResultBytes;
}

/// <summary>A page of projected rows.</summary>
/// <param name="RowsJson">
/// The rows as one JSON text: an array with one array per kept line, one value per <see cref="JsonLinesQuery.Select"/>
/// path. Values are copied byte for byte from the file, so producing this costs no re-serialization, and the plugin
/// bridge can splice it into its reply as a raw value (one <c>JSON.parse</c> on the JS side). Host code can use
/// <see cref="ParseRows"/>.
/// </param>
/// <param name="NextOffset">Where the next call starts. Always at the start of a line.</param>
/// <param name="Done">The end of the file was reached.</param>
/// <param name="Skipped">Lines that were not valid JSON or over the length limit.</param>
public sealed record JsonLinesBatch(string RowsJson, int RowCount, long NextOffset, bool Done, int Skipped)
{
    /// <summary>The rows as elements that do not depend on any document (each is a clone).</summary>
    public IReadOnlyList<JsonElement[]> ParseRows()
    {
        using var document = JsonDocument.Parse(RowsJson);
        var rows = new List<JsonElement[]>(RowCount);
        foreach (var row in document.RootElement.EnumerateArray())
        {
            var values = new JsonElement[row.GetArrayLength()];
            var i = 0;
            foreach (var value in row.EnumerateArray()) values[i++] = value.Clone();
            rows.Add(values);
        }
        return rows;
    }
}

/// <summary>The per-call limits of design/plugins/04 §4. Bigger files are read in pieces (offsets, line pages).</summary>
public sealed record FileAccessLimits
{
    public const int DefaultLineLimit = 5000;
    public const int DefaultMaxLineLength = 8 * 1024 * 1024;
    public const int DefaultMaxResultBytes = 2 * 1024 * 1024;

    public static readonly FileAccessLimits Default = new();

    /// <summary>One <c>ReadText</c>, <c>ReadBytes</c>, line page or tail.</summary>
    public int MaxReadBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>One <c>WriteText</c>, <c>WriteBytes</c> or <c>AppendText</c>, after encoding.</summary>
    public int MaxWriteBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>Entries of one <c>List</c> or glob walk.</summary>
    public int MaxListEntries { get; init; } = 50_000;
}

/// <summary>
/// Bounded, crash-safe file primitives for the plugin <c>fs</c> API and, later, file editing (design/plugins/04 §4, §9).
/// There is no permission logic here: callers check every path first (<c>IPluginAccessPolicy</c>) and pass the real
/// path the check returned. All paths must be absolute. Synchronous: callers run it on a worker thread.
/// Every failure is a <see cref="FileAccessException"/>.
/// </summary>
/// <remarks>
/// Nothing here creates links or sets permission bits: new files are created without any execute bit, copies have
/// theirs removed, and an atomic replace keeps the mode of the file it replaces. Directory walks and recursive
/// copies never follow a link into another directory.
/// </remarks>
public interface IFileAccessService
{
    /// <summary>The entry at <paramref name="path"/>; <see cref="FileErrorCode.NotFound"/> when there is none.</summary>
    FileStat Stat(string path);

    /// <summary>The entry at <paramref name="path"/>, or null when there is none.</summary>
    FileStat? TryStat(string path);

    /// <summary>One result per path, in order; null where nothing exists. One round trip for many files (design/plugins/11 §5.2).</summary>
    IReadOnlyList<FileStat?> StatMany(IReadOnlyList<string> paths);

    /// <summary>
    /// Files and directories below <paramref name="root"/> whose path relative to it matches <paramref name="glob"/>
    /// (<see cref="PathGlob"/> syntax), in breadth-first order. Links are reported but never entered.
    /// <paramref name="descend"/> can veto entering a directory (full path); the caller uses it to skip what it may not see.
    /// </summary>
    FileListing StatMany(string root, string glob, Func<string, bool>? descend = null, CancellationToken cancellationToken = default);

    /// <summary>The entries of a directory sorted by name (ordinal), at most <see cref="FileAccessLimits.MaxListEntries"/>.</summary>
    FileListing List(string directory);

    /// <summary>
    /// The whole file as text. Without <paramref name="encoding"/> a UTF-8, UTF-16LE or UTF-16BE byte order mark
    /// decides, and UTF-8 is assumed without one. Invalid bytes become U+FFFD. Over the read limit: <see cref="FileErrorCode.TooLarge"/>.
    /// </summary>
    /// <param name="encoding">An encoding name such as <c>utf-8</c>, <c>utf-16le</c>, <c>latin1</c>; unknown names are <see cref="FileErrorCode.InvalidArgument"/>.</param>
    FileText ReadText(string path, string? encoding = null);

    /// <summary><paramref name="length"/> bytes from <paramref name="offset"/> (to the end when null); fewer at the end of the file.</summary>
    byte[] ReadBytes(string path, long offset = 0, long? length = null);

    /// <summary>True when the first 8 KB contain a NUL byte and do not start with a UTF-16 byte order mark.</summary>
    bool IsBinary(string path);

    /// <summary>Writes text, by default as UTF-8 without a byte order mark. Writes through a link to its target.</summary>
    void WriteText(string path, string text, WriteOptions? options = null, string? encoding = null, bool byteOrderMark = false);

    /// <summary>Writes bytes. Writes through a link to its target.</summary>
    void WriteBytes(string path, ReadOnlySpan<byte> bytes, WriteOptions? options = null);

    /// <summary>Appends UTF-8 text, creating the file when it is missing. Not atomic: appending never loses what is there.</summary>
    void AppendText(string path, string text);

    /// <summary>
    /// Creates a directory. Without <paramref name="recursive"/> the parent must exist. An existing directory is fine;
    /// an existing file is <see cref="FileErrorCode.Exists"/>.
    /// </summary>
    void CreateDirectory(string path, bool recursive = false);

    /// <summary>
    /// Moves or renames a file, directory or link (a link is moved, not its target). A case-only rename works on
    /// case-insensitive file systems. <paramref name="overwrite"/> replaces an existing file, never a directory.
    /// </summary>
    void Move(string source, string destination, bool overwrite = false);

    /// <summary>
    /// Copies a file, or with <paramref name="recursive"/> a directory tree. Links inside a copied tree are skipped:
    /// following them could copy what the caller was never allowed to read, and recreating them is not offered
    /// (design/plugins/04 §3.4). <paramref name="overwrite"/> replaces existing files and merges into an existing directory.
    /// </summary>
    /// <param name="filter">
    /// Called with the full source path of every entry inside a copied tree; false skips it (a directory is not
    /// entered). The caller passes its access check here: checking only the top folder would let a copy carry
    /// files out that the caller may not read one by one (credentials, <c>.git</c>, sensitive files).
    /// </param>
    void Copy(string source, string destination, bool recursive = false, bool overwrite = false, Func<string, bool>? filter = null);

    /// <summary>
    /// Deletes permanently (the trash is <c>ITrashService</c>). A link is removed, never its target. A directory that
    /// is not empty needs <paramref name="recursive"/>, otherwise <see cref="FileErrorCode.IsDirectory"/>.
    /// </summary>
    void Delete(string path, bool recursive = false);

    /// <summary>A page of UTF-8 lines, for logs too big to read whole (design/plugins/11 §5.2).</summary>
    LineBatch ReadLines(string path, LineReadOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>The last <paramref name="lines"/> lines (a final unterminated line counts), read backwards from the end.</summary>
    IReadOnlyList<string> Tail(string path, int lines);

    /// <summary>
    /// Reads JSON lines from <see cref="JsonLinesQuery.FromOffset"/>, keeps the ones that match
    /// <see cref="JsonLinesQuery.Where"/> and returns only the <see cref="JsonLinesQuery.Select"/>ed values. Parsing
    /// happens here, with <c>System.Text.Json</c>, so the plugin engine never sees the parts it does not want
    /// (design/plugins/11 §5). A last line without a line break is taken only when it already parses (a writer may be
    /// in the middle of it). <see cref="JsonLinesQuery.FromOffset"/> past the end of the file is
    /// <see cref="FileErrorCode.InvalidArgument"/>: the file was truncated or replaced, scan again from 0.
    /// </summary>
    JsonLinesBatch ScanJsonLines(string path, JsonLinesQuery query, CancellationToken cancellationToken = default);
}
