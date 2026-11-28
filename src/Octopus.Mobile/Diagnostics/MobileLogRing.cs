using System.Globalization;
using System.Text;

namespace Octopus.Mobile.Diagnostics;

/// <summary>Single lightweight breadcrumb kept in memory and mirrored to the rotating runtime log.</summary>
public readonly record struct DiagnosticBreadcrumb(
    DateTimeOffset Timestamp,
    string Level,
    string Category,
    string Message)
{
    public string FormatLine() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Timestamp:HH:mm:ss.fff} [{Level}] [{Category}] {Message}");
}

/// <summary>
/// Fixed-capacity thread-safe memory ring buffer paired with a size-capped rotating disk log
/// (<c>runtime.log</c> + <c>runtime.prev.log</c>). Never throws.
/// </summary>
internal sealed class MobileLogRing
{
    public const int DefaultCapacity = 150;
    public const long MaxRuntimeLogBytes = 512 * 1024;
    public const string RuntimeFileName = "runtime.log";
    public const string RuntimePreviousFileName = "runtime.prev.log";

    private readonly Lock _gate = new();
    private readonly DiagnosticBreadcrumb[] _ring;
    private int _head;
    private int _count;
    private string? _directory;

    public MobileLogRing(int capacity = DefaultCapacity)
    {
        _ring = new DiagnosticBreadcrumb[Math.Max(16, capacity)];
    }

    public void ConfigureDirectory(string? directory)
    {
        lock (_gate)
        {
            _directory = directory;
        }
    }

    public void Write(string level, string category, string message, DateTimeOffset? now = null)
    {
        var timestamp = now ?? DateTimeOffset.Now;
        var safeCategory = string.IsNullOrWhiteSpace(category) ? "APP" : category.Trim();
        var safeMessage = LogSanitizer.Sanitize(message);
        if (safeMessage.Length > 1000) safeMessage = safeMessage[..1000] + "…";

        var crumb = new DiagnosticBreadcrumb(timestamp, level, safeCategory, safeMessage);
        var diskLine = string.Create(
            CultureInfo.InvariantCulture,
            $"{timestamp:O} [{level}] [t{Environment.CurrentManagedThreadId}] [{safeCategory}] {safeMessage}{Environment.NewLine}");

        lock (_gate)
        {
            _ring[_head] = crumb;
            _head = (_head + 1) % _ring.Length;
            if (_count < _ring.Length) _count++;

            if (_directory is null) return;
            try
            {
                Directory.CreateDirectory(_directory);
                var path = Path.Combine(_directory, RuntimeFileName);
                RotateIfOversized(path, Path.Combine(_directory, RuntimePreviousFileName));
                File.AppendAllText(path, diskLine, Encoding.UTF8);
            }
            catch
            {
                // Logging must never fail or crash the app.
            }
        }
    }

    public IReadOnlyList<DiagnosticBreadcrumb> SnapshotBreadcrumbs(int maxCount = 60)
    {
        lock (_gate)
        {
            if (_count == 0) return [];
            var take = Math.Min(_count, Math.Max(1, maxCount));
            var result = new DiagnosticBreadcrumb[take];
            var start = (_head - take + _ring.Length) % _ring.Length;
            for (var i = 0; i < take; i++)
            {
                result[i] = _ring[(start + i) % _ring.Length];
            }
            return result;
        }
    }

    public IReadOnlyList<string> ReadRecentLines(int maxLines = 120)
    {
        lock (_gate)
        {
            if (_directory is not null)
            {
                var path = Path.Combine(_directory, RuntimeFileName);
                try
                {
                    if (File.Exists(path))
                    {
                        var lines = File.ReadAllLines(path, Encoding.UTF8);
                        if (lines.Length <= maxLines) return lines;
                        return lines[^maxLines..];
                    }
                }
                catch
                {
                    // Fall back to in-memory ring below.
                }
            }

            if (_count == 0) return [];
            var take = Math.Min(_count, Math.Max(1, maxLines));
            var list = new string[take];
            var start = (_head - take + _ring.Length) % _ring.Length;
            for (var i = 0; i < take; i++)
            {
                list[i] = _ring[(start + i) % _ring.Length].FormatLine();
            }
            return list;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_ring);
            _head = 0;
            _count = 0;
            if (_directory is null) return;
            TryDelete(Path.Combine(_directory, RuntimeFileName));
            TryDelete(Path.Combine(_directory, RuntimePreviousFileName));
        }
    }

    private static void RotateIfOversized(string path, string previousPath)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length >= MaxRuntimeLogBytes)
            {
                File.Move(path, previousPath, overwrite: true);
            }
        }
        catch
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }
}
