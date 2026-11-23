using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Octopus.Acp.Transport;

/// <summary>Diagnostic record emitted when a single stdout line exceeds <see cref="NdjsonLineFramer.MaxLineBytes"/>.</summary>
public sealed record OversizedAcpLine(string Prefix, long ByteCount, string? ExtractedRequestId);

/// <summary>
/// Incremental byte-level NDJSON framer modeled after Orca's <c>main-process-ndjson-framer</c> and
/// <c>acp-oversized-lines</c>. Protects memory from runaway stdout lines while still recovering JSON-RPC
/// request ids so pending calls fail immediately instead of hanging, and routes non-JSON CLI banners to
/// <c>onOtherLine</c> without tearing down the session.
/// </summary>
public sealed partial class NdjsonLineFramer
{
    public const int DefaultMaxLineBytes = 16 * 1024 * 1024;
    private const int PrefixCaptureBytes = 4096;

    private readonly int _maxLineBytes;
    private readonly MemoryStream _buffer = new(4096);
    private readonly List<byte> _oversizedPrefix = new(PrefixCaptureBytes);
    private bool _droppingOversized;
    private long _oversizedBytes;

    public NdjsonLineFramer(int maxLineBytes = DefaultMaxLineBytes)
    {
        if (maxLineBytes < 256) throw new ArgumentOutOfRangeException(nameof(maxLineBytes));
        _maxLineBytes = maxLineBytes;
    }

    public int MaxLineBytes => _maxLineBytes;

    public void Feed(
        ReadOnlySpan<byte> chunk,
        Action<JsonDocument> onMessage,
        Action<string> onOtherLine,
        Action<OversizedAcpLine> onOversized)
    {
        var offset = 0;
        while (offset < chunk.Length)
        {
            var nl = chunk[offset..].IndexOf((byte)'\n');
            if (nl < 0)
            {
                AppendSegment(chunk[offset..]);
                return;
            }

            AppendSegment(chunk.Slice(offset, nl));
            EmitCurrentLine(onMessage, onOtherLine, onOversized);
            offset += nl + 1;
        }
    }

    /// <summary>Flushes any unterminated trailing data at EOF.</summary>
    public void Complete(
        Action<JsonDocument> onMessage,
        Action<string> onOtherLine,
        Action<OversizedAcpLine> onOversized)
    {
        if (_buffer.Length == 0 && !_droppingOversized) return;
        EmitCurrentLine(onMessage, onOtherLine, onOversized);
    }

    public void Reset()
    {
        _buffer.SetLength(0);
        _oversizedPrefix.Clear();
        _droppingOversized = false;
        _oversizedBytes = 0;
    }

    private void AppendSegment(ReadOnlySpan<byte> segment)
    {
        if (segment.IsEmpty) return;

        if (_droppingOversized)
        {
            _oversizedBytes += segment.Length;
            CapturePrefix(segment);
            return;
        }

        if (_buffer.Length + segment.Length > _maxLineBytes)
        {
            _droppingOversized = true;
            _oversizedBytes = _buffer.Length + segment.Length;
            _oversizedPrefix.Clear();
            if (_buffer.TryGetBuffer(out var existing))
            {
                CapturePrefix(existing.AsSpan());
            }
            CapturePrefix(segment);
            _buffer.SetLength(0);
            return;
        }

        _buffer.Write(segment);
    }

    private void CapturePrefix(ReadOnlySpan<byte> bytes)
    {
        var remaining = PrefixCaptureBytes - _oversizedPrefix.Count;
        if (remaining <= 0) return;
        var take = Math.Min(remaining, bytes.Length);
        for (var i = 0; i < take; i++)
        {
            _oversizedPrefix.Add(bytes[i]);
        }
    }

    private void EmitCurrentLine(
        Action<JsonDocument> onMessage,
        Action<string> onOtherLine,
        Action<OversizedAcpLine> onOversized)
    {
        if (_droppingOversized)
        {
            var prefix = Encoding.UTF8.GetString([.. _oversizedPrefix]);
            var bytes = _oversizedBytes;
            _droppingOversized = false;
            _oversizedBytes = 0;
            _oversizedPrefix.Clear();
            onOversized(new OversizedAcpLine(prefix, bytes, TryExtractId(prefix)));
            return;
        }

        if (_buffer.Length == 0) return;

        var raw = _buffer.TryGetBuffer(out var seg) ? seg.AsSpan() : _buffer.ToArray().AsSpan();
        // Strip trailing CR from CRLF lines.
        if (!raw.IsEmpty && raw[^1] == (byte)'\r')
        {
            raw = raw[..^1];
        }
        var copy = raw.ToArray();
        _buffer.SetLength(0);

        // Skip empty / whitespace-only lines silently.
        var trimmed = Encoding.UTF8.GetString(copy).Trim();
        if (trimmed.Length == 0) return;

        if (!trimmed.StartsWith('{'))
        {
            onOtherLine(trimmed);
            return;
        }

        try
        {
            var doc = JsonDocument.Parse(copy);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                doc.Dispose();
                onOtherLine(trimmed);
                return;
            }
            onMessage(doc);
        }
        catch (JsonException)
        {
            onOtherLine(trimmed);
        }
    }

    internal static string? TryExtractId(string prefix)
    {
        var match = IdExtractRegex().Match(prefix);
        if (!match.Success) return null;
        return match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
    }

    [GeneratedRegex(@"""id""\s*:\s*(?:""([^""\\]{1,128})""|(-?\d{1,18}))", RegexOptions.CultureInvariant)]
    private static partial Regex IdExtractRegex();
}
