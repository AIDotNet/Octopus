using System.Buffers;

namespace Octopus.FileSystem;

/// <summary>What a <see cref="LineScanner"/> callback wants next.</summary>
internal enum LineAction
{
    /// <summary>The line is consumed; go on.</summary>
    Continue,
    /// <summary>The line is consumed; stop after it.</summary>
    Stop,
    /// <summary>Stop and leave this line for the next call (it is not consumed).</summary>
    StopBefore,
}

/// <param name="line">The line without its terminator (<c>\n</c>, and a <c>\r</c> before it); the UTF-8 byte order mark of the first line of a file is removed.</param>
/// <param name="terminated">False for a last line that has no line break (yet).</param>
internal delegate LineAction LineCallback(ReadOnlySpan<byte> line, bool terminated);

/// <param name="NextOffset">The start of the first line that was not consumed.</param>
/// <param name="ReachedEnd">The whole file was looked at (what is left after <paramref name="NextOffset"/> is at most an unconsumed last line).</param>
/// <param name="Overlong">Terminated lines over the length limit, passed over without being buffered.</param>
internal readonly record struct LineScanResult(long NextOffset, bool ReachedEnd, int Overlong);

/// <summary>
/// Splits a file into lines from a byte offset without ever holding more than one line (up to the length limit) and
/// a 64 KB read buffer. Offsets stay exact, so every <see cref="LineScanResult.NextOffset"/> is a line start that a
/// later call can resume from. Lines are bytes; decoding is the caller's.
/// </summary>
internal static class LineScanner
{
    private const int ChunkSize = 64 * 1024;

    public static LineScanResult Scan(Stream stream, long fromOffset, int maxLineLength, LineCallback callback, CancellationToken token)
    {
        stream.Seek(fromOffset, SeekOrigin.Begin);
        var chunk = ArrayPool<byte>.Shared.Rent(ChunkSize);
        var pending = new PendingLine();
        try
        {
            var chunkOffset = fromOffset;   // file offset of chunk[0]
            var lineStart = fromOffset;
            var overlong = 0;
            var skipping = false;           // inside a line that is already over the limit
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var read = stream.Read(chunk, 0, ChunkSize);
                if (read == 0) break;
                var index = 0;
                while (index < read)
                {
                    var rest = chunk.AsSpan(index, read - index);
                    var newline = rest.IndexOf((byte)'\n');
                    if (newline < 0)
                    {
                        if (!skipping && !pending.TryAppend(rest, maxLineLength)) skipping = true;
                        break;
                    }
                    var lineEnd = chunkOffset + index + newline + 1;
                    if (skipping)
                    {
                        overlong++;
                        skipping = false;
                        pending.Clear();
                    }
                    else
                    {
                        ReadOnlySpan<byte> line = default;
                        bool fits;
                        if (pending.Length == 0)
                        {
                            line = rest[..newline]; // the whole line is in this chunk: no copy
                            fits = line.Length <= maxLineLength;
                        }
                        else
                        {
                            fits = pending.TryAppend(rest[..newline], maxLineLength);
                            if (fits) line = pending.Span;
                        }
                        if (!fits)
                        {
                            overlong++;
                        }
                        else
                        {
                            var action = callback(Trim(line, lineStart), terminated: true);
                            if (action == LineAction.StopBefore) return new LineScanResult(lineStart, false, overlong);
                            if (action == LineAction.Stop) return new LineScanResult(lineEnd, false, overlong);
                        }
                        pending.Clear();
                    }
                    lineStart = lineEnd;
                    index += newline + 1;
                }
                chunkOffset += read;
            }

            // End of file. A line still over the limit, or one without a line break, may be half written.
            if (!skipping && pending.Length > 0)
            {
                var action = callback(Trim(pending.Span, lineStart), terminated: false);
                if (action != LineAction.StopBefore) return new LineScanResult(chunkOffset, true, overlong);
            }
            return new LineScanResult(lineStart, true, overlong);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
            pending.Dispose();
        }
    }

    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> line, long lineStart)
    {
        if (lineStart == 0 && line.StartsWith(TextCodec.Utf8Preamble)) line = line[3..];
        if (line.Length > 0 && line[^1] == (byte)'\r') line = line[..^1];
        return line;
    }

    /// <summary>The part of a line that started in an earlier chunk. Pooled, grown on demand, never past the limit.</summary>
    private struct PendingLine : IDisposable
    {
        private byte[]? _buffer;

        public int Length { get; private set; }

        public readonly ReadOnlySpan<byte> Span => _buffer.AsSpan(0, Length);

        /// <summary>False (and nothing kept) when the line would exceed <paramref name="max"/>.</summary>
        public bool TryAppend(ReadOnlySpan<byte> bytes, int max)
        {
            if ((long)Length + bytes.Length > max)
            {
                Clear();
                return false;
            }
            if (_buffer is null || _buffer.Length < Length + bytes.Length)
            {
                var larger = ArrayPool<byte>.Shared.Rent(Math.Max(Length + bytes.Length, Math.Min(max, Math.Max(4096, (_buffer?.Length ?? 0) * 2))));
                Span.CopyTo(larger);
                if (_buffer is not null) ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = larger;
            }
            bytes.CopyTo(_buffer.AsSpan(Length));
            Length += bytes.Length;
            return true;
        }

        public void Clear() => Length = 0;

        public void Dispose()
        {
            if (_buffer is not null) ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = null;
            Length = 0;
        }
    }
}
