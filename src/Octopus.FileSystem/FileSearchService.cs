using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Octopus.FileSystem;

/// <summary>Recursive name and content search. Both run on a background thread and stream their results.</summary>
public sealed class FileSearchService : IFileSearchService
{
    internal const int MaxNameResults = 500;
    internal const int MaxMatchesPerFile = 20;
    internal const int MaxMatchesTotal = 1000;
    internal const int MaxLineLength = 300;
    internal const long MaxFileSize = 1024 * 1024;
    private const int BinaryProbeSize = 8192;

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".vs", ".idea", "dist", "build", "target", ".next", "__pycache__",
        ".gradle", ".venv", ".svn", ".hg",
    };

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
    };

    public async IAsyncEnumerable<FileEntry> SearchNamesAsync(
        string root, string query, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(query)) yield break;
        // Best matches first needs the whole walk: collect on a worker, then order.
        var found = await Task.Run(() => CollectNames(root, query, cancellationToken), cancellationToken).ConfigureAwait(false);
        foreach (var entry in found)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return entry;
        }
    }

    private static List<FileEntry> CollectNames(string root, string query, CancellationToken token)
    {
        var matches = new List<(FileEntry Entry, int Rank)>();
        foreach (var entry in Walk(root, token))
        {
            var index = entry.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;
            var rank = entry.Name.Equals(query, StringComparison.OrdinalIgnoreCase) ? 0 : index == 0 ? 1 : 2;
            matches.Add((entry, rank));
        }
        return matches
            .OrderBy(m => m.Rank)
            .ThenBy(m => m.Entry.FullPath.Length)
            .ThenBy(m => m.Entry.FullPath, StringComparer.OrdinalIgnoreCase)
            .Take(MaxNameResults)
            .Select(m => m.Entry)
            .ToList();
    }

    public async IAsyncEnumerable<ContentMatch> SearchContentAsync(
        string root, string query, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(query)) yield break;
        var channel = Channel.CreateBounded<ContentMatch>(new BoundedChannelOptions(256) { SingleReader = true, SingleWriter = true });
        var worker = Task.Run(() =>
        {
            Exception? error = null;
            try
            {
                var total = 0;
                foreach (var entry in Walk(root, cancellationToken))
                {
                    if (entry.IsDirectory) continue;
                    foreach (var match in SearchFile(entry.FullPath, query, cancellationToken))
                    {
                        channel.Writer.WriteAsync(match, cancellationToken).AsTask().GetAwaiter().GetResult();
                        if (++total >= MaxMatchesTotal) return;
                    }
                }
            }
            catch (Exception e)
            {
                error = e;
            }
            finally
            {
                channel.Writer.TryComplete(error);
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var match in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return match;
        }
        finally
        {
            // Stops the walker when the consumer stops early.
            channel.Writer.TryComplete();
            await worker.ConfigureAwait(false);
        }
    }

    private static IEnumerable<ContentMatch> SearchFile(string path, string query, CancellationToken token)
    {
        var matches = new List<ContentMatch>();
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxFileSize) return matches;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var probe = new byte[Math.Min(BinaryProbeSize, (int)Math.Min(info.Length, BinaryProbeSize))];
            var read = stream.Read(probe, 0, probe.Length);
            if (Array.IndexOf(probe, (byte)0, 0, read) >= 0) return matches;
            stream.Position = 0;
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var number = 0;
            while (reader.ReadLine() is { } line)
            {
                number++;
                if ((number & 0xFF) == 0) token.ThrowIfCancellationRequested();
                var column = line.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                if (column < 0) continue;
                var text = line;
                if (text.Length > MaxLineLength) text = text[..MaxLineLength];
                matches.Add(new ContentMatch(path, number, column, text));
                if (matches.Count >= MaxMatchesPerFile) break;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Unreadable file: no matches.
        }
        return matches;
    }

    /// <summary>Every file and folder below <paramref name="root"/>, breadth-first so shallow matches come early.</summary>
    private static IEnumerable<FileEntry> Walk(string root, CancellationToken token)
    {
        var queue = new Queue<string>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var directory = queue.Dequeue();
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateFileSystemEntries(directory, "*", Options);
            }
            catch (Exception)
            {
                continue;
            }
            foreach (var path in children)
            {
                var name = Path.GetFileName(path);
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(path);
                }
                catch (Exception)
                {
                    continue;
                }
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                var isLink = (attributes & FileAttributes.ReparsePoint) != 0;
                if (isDirectory && SkippedDirectories.Contains(name)) continue;
                var hidden = name.StartsWith('.') || (attributes & FileAttributes.Hidden) != 0;
                yield return new FileEntry(name, path, isDirectory, hidden);
                // A link is listed but not entered: it could loop back.
                if (isDirectory && !isLink) queue.Enqueue(path);
            }
        }
    }
}
