using System.Text;
using Octopus.Acp.Transport;

namespace Octopus.Acp.Client;

/// <summary>
/// Client-hosted filesystem callbacks for ACP <c>fs/read_text_file</c> and <c>fs/write_text_file</c>.
/// Advertised only when <c>clientCapabilities.fs</c> is enabled by the user.
/// </summary>
public interface IAcpFileSystemHandler
{
    Task<string> ReadTextFileAsync(
        string sessionId,
        string path,
        int? line = null,
        int? limit = null,
        CancellationToken cancellationToken = default);

    Task WriteTextFileAsync(
        string sessionId,
        string path,
        string content,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Default local filesystem handler for ACP <c>fs/read_text_file</c> and <c>fs/write_text_file</c>.
/// Supports optional unsaved editor buffer lookup, optional workspace root restriction, 1-based line slicing,
/// and atomic writes with parent directory creation.
/// </summary>
public sealed class AcpFileSystemHandler : IAcpFileSystemHandler
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IReadOnlyList<string>? _allowedRoots;
    private readonly Func<string, string?>? _tryReadUnsavedBuffer;
    private readonly Action<string, string>? _onFileWritten;

    public AcpFileSystemHandler(
        IReadOnlyList<string>? allowedRoots = null,
        Func<string, string?>? tryReadUnsavedBuffer = null,
        Action<string, string>? onFileWritten = null)
    {
        _allowedRoots = allowedRoots is { Count: > 0 }
            ? allowedRoots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(NormalizeDir).ToList()
            : null;
        _tryReadUnsavedBuffer = tryReadUnsavedBuffer;
        _onFileWritten = onFileWritten;
    }

    public async Task<string> ReadTextFileAsync(
        string sessionId,
        string path,
        int? line = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var fullPath = ValidatePath(path);
        if (line is < 1)
        {
            throw new AcpRpcException(-32602, $"Invalid 1-based line number: {line}.");
        }
        if (limit is < 0)
        {
            throw new AcpRpcException(-32602, $"Invalid line limit: {limit}.");
        }

        string text;
        if (_tryReadUnsavedBuffer?.Invoke(fullPath) is { } bufferText)
        {
            text = bufferText;
        }
        else
        {
            if (!File.Exists(fullPath))
            {
                throw new AcpRpcException(-32002, $"File not found: {fullPath}");
            }
            text = await File.ReadAllTextAsync(fullPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }

        return SliceLines(text, line, limit);
    }

    public async Task WriteTextFileAsync(
        string sessionId,
        string path,
        string content,
        CancellationToken cancellationToken = default)
    {
        var fullPath = ValidatePath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tempPath = Path.Combine(dir ?? ".", $".acp-write-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(tempPath, content, Utf8NoBom, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            await File.WriteAllTextAsync(fullPath, content, Utf8NoBom, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }

        try { _onFileWritten?.Invoke(fullPath, content); }
        catch { /* UI observers cannot fail file write RPCs. */ }
    }

    internal static string SliceLines(string text, int? line, int? limit)
    {
        if (line is null && limit is null)
        {
            return text;
        }
        if (limit == 0)
        {
            return "";
        }

        var startLine = Math.Max(1, line ?? 1);
        var normalized = text.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');
        var startIdx = startLine - 1;
        if (startIdx >= lines.Length)
        {
            return "";
        }
        var count = limit.HasValue
            ? Math.Min(limit.Value, lines.Length - startIdx)
            : lines.Length - startIdx;
        return string.Join('\n', lines, startIdx, count);
    }

    private string ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new AcpRpcException(-32602, "Path is required.");
        }
        if (!Path.IsPathRooted(path))
        {
            throw new AcpRpcException(-32602, $"ACP fs path must be absolute: {path}");
        }
        var fullPath = Path.GetFullPath(path);
        if (_allowedRoots is { Count: > 0 })
        {
            var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            var inside = false;
            foreach (var root in _allowedRoots)
            {
                if (fullPath.Equals(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), comparison)
                    || fullPath.StartsWith(root, comparison))
                {
                    inside = true;
                    break;
                }
            }
            if (!inside)
            {
                throw new AcpRpcException(-32001, $"Access denied outside workspace roots: {fullPath}");
            }
        }
        return fullPath;
    }

    private static string NormalizeDir(string dir)
    {
        var full = Path.GetFullPath(dir);
        return full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar;
    }
}
