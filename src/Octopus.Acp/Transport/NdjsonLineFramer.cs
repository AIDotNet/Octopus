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
    private const int InitialBufferCapacity = 4096;
    private const int ShrinkBufferThreshold = 64 * 1024;

    private readonly int _maxLineBytes;
    private MemoryStream _buffer = new(InitialBufferCapacity);
    private readonly List<byte> _oversizedPrefix = new(PrefixCaptureBytes);
    private bool _droppingOversized;
    private long _oversizedBytes;

    public NdjsonLineFramer(int maxLineBytes = DefaultMaxLineBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLineBytes, 256);
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
        ResetBuffer();
        _oversizedPrefix.Clear();
        _droppingOversized = false;
        _oversizedBytes = 0;
    }

    private void ResetBuffer()
    {
        if (_buffer.Capacity > ShrinkBufferThreshold)
        {
            // Release multi-megabyte backing array retained after a large tool output line.
            _buffer.Dispose();
            _buffer = new MemoryStream(InitialBufferCapacity);
        }
        else
        {
            _buffer.SetLength(0);
        }
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
            ResetBuffer();
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

        var hasUnderlying = _buffer.TryGetBuffer(out var seg);
        var raw = hasUnderlying ? seg.AsSpan() : _buffer.ToArray().AsSpan();

        // Trim ASCII whitespace without allocating a full UTF-8 string on the hot JSON path.
        var start = 0;
        var end = raw.Length;
        while (start < end && IsAsciiWhitespace(raw[start])) start++;
        while (end > start && IsAsciiWhitespace(raw[end - 1])) end--;

        if (start >= end)
        {
            ResetBuffer();
            return;
        }

        var trimmedSpan = raw[start..end];
        if (trimmedSpan[0] != (byte)'{')
        {
            var other = Encoding.UTF8.GetString(trimmedSpan).Trim();
            ResetBuffer();
            if (other.Length > 0)
            {
                onOtherLine(other);
            }
            return;
        }

        // Parse directly from the pooled buffer slice; callbacks must consume/dispose doc synchronously.
        var slice = hasUnderlying
            ? new ReadOnlyMemory<byte>(seg.Array!, seg.Offset + start, end - start)
            : trimmedSpan.ToArray();

        JsonDocument? doc = null;
        string? fallbackOther = null;
        try
        {
            doc = JsonDocument.Parse(slice);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                doc.Dispose();
                doc = null;
                fallbackOther = Encoding.UTF8.GetString(trimmedSpan).Trim();
            }
        }
        catch (JsonException)
        {
            fallbackOther = Encoding.UTF8.GetString(trimmedSpan).Trim();
        }

        try
        {
            if (doc is not null)
            {
                onMessage(doc);
            }
            else if (!string.IsNullOrEmpty(fallbackOther))
            {
                onOtherLine(fallbackOther);
            }
        }
        finally
        {
            ResetBuffer();
        }
    }

    private static bool IsAsciiWhitespace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    internal static string? TryExtractId(string prefix)
    {
        var match = IdExtractRegex().Match(prefix);
        if (!match.Success) return null;
        return match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
    }

    [GeneratedRegex(@"""id""\s*:\s*(?:""([^""\\]{1,128})""|(-?\d{1,18}))", RegexOptions.CultureInvariant)]
    private static partial Regex IdExtractRegex();
}
