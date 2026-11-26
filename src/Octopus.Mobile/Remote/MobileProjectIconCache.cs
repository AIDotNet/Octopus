using Avalonia.Media.Imaging;
using Octopus.Remote.Protocol;

namespace Octopus.Mobile.Remote;

/// <summary>
/// Local disk + in-memory cache of custom project thumbnail PNGs synced from paired desktop hosts.
/// Desktop <c>ProjectIconStore</c> assigns a brand-new unique file name (<c>&lt;project&gt;-&lt;8hex&gt;.png</c>) whenever
/// a project's picture changes, so <c>(hostId, imageKey)</c> acts as an immutable cache key: unchanged icons are served
/// directly from memory/disk with zero network traffic, and obsolete cached files are pruned on sync.
/// </summary>
public sealed class MobileProjectIconCache
{
    private const int ThumbnailSize = 128;

    private readonly string? _rootDirectory;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Bitmap?> _bitmaps = new(StringComparer.Ordinal);

    public MobileProjectIconCache(string? dataDirectory)
    {
        _rootDirectory = string.IsNullOrWhiteSpace(dataDirectory)
            ? null
            : Path.Combine(dataDirectory, "project-icons");
    }

    public string? DirectoryPath => _rootDirectory;

    /// <summary>Returns a cached bitmap from memory or disk without hitting the network; null on cache miss.</summary>
    public Bitmap? TryLoad(string hostId, string? imageKey)
    {
        if (!IsSafeKey(imageKey)) return null;
        var cacheKey = CacheKey(hostId, imageKey!);

        lock (_gate)
        {
            if (_bitmaps.TryGetValue(cacheKey, out var cached) && cached is not null)
            {
                return cached;
            }
        }

        var filePath = FilePath(hostId, imageKey!);
        if (filePath is null || !File.Exists(filePath)) return null;

        try
        {
            var info = new FileInfo(filePath);
            if (info.Length is <= 0 or > RemoteMethods.ProjectIconMaxBytes)
            {
                TryDelete(filePath);
                return null;
            }
            var bytes = File.ReadAllBytes(filePath);
            var bitmap = DecodeBitmap(bytes);
            if (bitmap is null)
            {
                TryDelete(filePath);
                return null;
            }
            lock (_gate)
            {
                if (_bitmaps.TryGetValue(cacheKey, out var existing) && existing is not null)
                {
                    bitmap.Dispose();
                    return existing;
                }
                _bitmaps[cacheKey] = bitmap;
                return bitmap;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Persists downloaded icon bytes to disk (atomically) and returns the decoded bitmap.</summary>
    public Bitmap? SaveAndLoad(string hostId, string imageKey, byte[] bytes)
    {
        if (!IsSafeKey(imageKey) || bytes.Length is <= 0 or > RemoteMethods.ProjectIconMaxBytes) return null;

        var bitmap = DecodeBitmap(bytes);
        if (bitmap is null) return null;

        var filePath = FilePath(hostId, imageKey);
        if (filePath is not null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                var tmp = $"{filePath}.{Guid.NewGuid():N}.tmp";
                File.WriteAllBytes(tmp, bytes);
                File.Move(tmp, filePath, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Still keep the in-memory bitmap even if disk persistence fails.
            }
        }

        var cacheKey = CacheKey(hostId, imageKey);
        lock (_gate)
        {
            if (_bitmaps.TryGetValue(cacheKey, out var existing) && existing is not null)
            {
                bitmap.Dispose();
                return existing;
            }
            _bitmaps[cacheKey] = bitmap;
            return bitmap;
        }
    }

    /// <summary>Deletes cached icon files for <paramref name="hostId"/> that are no longer referenced by any project.</summary>
    public void Prune(string hostId, IReadOnlySet<string> activeImageKeys)
    {
        var prefix = SafeHostFolder(hostId) + "/";
        lock (_gate)
        {
            foreach (var key in _bitmaps.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                var imageKey = key[prefix.Length..];
                if (!activeImageKeys.Contains(imageKey))
                {
                    _bitmaps.Remove(key);
                }
            }
        }

        var hostDir = HostDirectory(hostId);
        if (hostDir is null || !Directory.Exists(hostDir)) return;
        try
        {
            foreach (var file in Directory.GetFiles(hostDir))
            {
                var name = Path.GetFileName(file);
                if (!activeImageKeys.Contains(name))
                {
                    TryDelete(file);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string? HostDirectory(string hostId) =>
        _rootDirectory is null ? null : Path.Combine(_rootDirectory, SafeHostFolder(hostId));

    private string? FilePath(string hostId, string imageKey) =>
        HostDirectory(hostId) is { } dir ? Path.Combine(dir, imageKey) : null;

    private static string CacheKey(string hostId, string imageKey) =>
        $"{SafeHostFolder(hostId)}/{imageKey}";

    private static string SafeHostFolder(string hostId)
    {
        var safe = new string([.. hostId.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').Take(48)]);
        return safe.Length > 0 ? safe : "host";
    }

    private static bool IsSafeKey(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= 128
        && name == Path.GetFileName(name)
        && !name.Contains("..", StringComparison.Ordinal)
        && !name.Contains('/', StringComparison.Ordinal)
        && !name.Contains('\\', StringComparison.Ordinal);

    private static Bitmap? DecodeBitmap(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return Bitmap.DecodeToWidth(stream, ThumbnailSize, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception e) when (e is IOException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
