using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using Octopus.Remote.Security;

namespace Octopus.Remote.Protocol;

/// <summary>Compression algorithms supported by protocol v2 frame payloads.</summary>
public enum FrameCompressionKind : byte
{
    None = 0,
    Deflate = 1,
    Brotli = 2,
}

/// <summary>
/// Adaptive frame payload compression and pooled frame assembly (design/remote/传输协议v2与多传输优化.md §3.3, §3.4).
/// Compressed payload wire format: <c>[u8 kind][u32 uncompressedLength (LE)][compressedBytes...]</c>.
/// </summary>
public static class FrameCodec
{
    public const int DefaultThreshold = 1024;
    private const int HeaderBytes = 5; // 1 byte kind + 4 bytes uncompressed length
    private const int MinSavingsBytes = 32;

    public static FrameCompressionKind ParseKind(string? name) =>
        string.Equals(name, CompressionAlgorithms.Brotli, StringComparison.OrdinalIgnoreCase) ? FrameCompressionKind.Brotli
        : string.Equals(name, CompressionAlgorithms.Deflate, StringComparison.OrdinalIgnoreCase) ? FrameCompressionKind.Deflate
        : FrameCompressionKind.None;

    public static string? NameOf(FrameCompressionKind kind) => kind switch
    {
        FrameCompressionKind.Brotli => CompressionAlgorithms.Brotli,
        FrameCompressionKind.Deflate => CompressionAlgorithms.Deflate,
        _ => null,
    };

    /// <summary>
    /// Packs a frame header and payload into a rented buffer from <see cref="ArrayPool{T}.Shared"/>, optionally compressing
    /// the payload when <paramref name="compression"/> is enabled and compressing saves at least <see cref="MinSavingsBytes"/>.
    /// The caller must return the rented array to <see cref="ArrayPool{T}.Shared"/>.
    /// </summary>
    public static (byte[] Buffer, int Length) PackPooled(
        FrameHeader header,
        ReadOnlySpan<byte> payload,
        FrameCompressionKind compression = FrameCompressionKind.None,
        int threshold = DefaultThreshold)
    {
        var shouldCompress = compression != FrameCompressionKind.None
            && payload.Length >= Math.Max(64, threshold)
            && !header.Flags.HasFlag(FrameFlags.Compressed)
            && header.Type is FrameType.Request or FrameType.Response or FrameType.Notification
                or FrameType.Snapshot or FrameType.Output or FrameType.StreamData;

        if (shouldCompress)
        {
            var maxCompressed = FrameHeader.Length + HeaderBytes + payload.Length;
            var candidate = ArrayPool<byte>.Shared.Rent(maxCompressed);
            var target = candidate.AsSpan(FrameHeader.Length + HeaderBytes, payload.Length - HeaderBytes - MinSavingsBytes);
            if (TryCompress(compression, payload, target, out var written)
                && FrameHeader.Length + HeaderBytes + written <= SecureChannel.MaxMessageLength)
            {
                var compressedHeader = header with { Flags = header.Flags | FrameFlags.Compressed };
                compressedHeader.Write(candidate);
                candidate[FrameHeader.Length] = (byte)compression;
                BinaryPrimitives.WriteUInt32LittleEndian(candidate.AsSpan(FrameHeader.Length + 1, 4), (uint)payload.Length);
                return (candidate, FrameHeader.Length + HeaderBytes + written);
            }
            ArrayPool<byte>.Shared.Return(candidate);
        }

        var total = FrameHeader.Length + payload.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(total);
        header.Write(buffer);
        payload.CopyTo(buffer.AsSpan(FrameHeader.Length));
        return (buffer, total);
    }

    /// <summary>
    /// Decompresses a payload marked with <see cref="FrameFlags.Compressed"/>. Throws <see cref="IOException"/> when the
    /// header is malformed, the declared length exceeds <see cref="SecureChannel.MaxMessageLength"/>, or decompression fails.
    /// </summary>
    public static byte[] Decompress(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HeaderBytes) throw new IOException("A compressed frame payload is too short.");
        var kind = (FrameCompressionKind)payload[0];
        var uncompressedLength = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(1, 4));
        if (uncompressedLength > SecureChannel.MaxMessageLength)
        {
            throw new IOException($"A compressed frame declares {uncompressedLength} bytes, exceeding the {SecureChannel.MaxMessageLength} limit.");
        }
        if (uncompressedLength == 0) return [];

        var compressed = payload[HeaderBytes..];
        var output = new byte[uncompressedLength];
        switch (kind)
        {
            case FrameCompressionKind.Brotli:
                if (!BrotliDecoder.TryDecompress(compressed, output, out var brotliWritten) || brotliWritten != output.Length)
                {
                    throw new IOException("A Brotli-compressed frame failed to decompress.");
                }
                return output;

            case FrameCompressionKind.Deflate:
                try
                {
                    using var input = new MemoryStream(compressed.ToArray(), writable: false);
                    using var deflate = new DeflateStream(input, CompressionMode.Decompress);
                    deflate.ReadExactly(output);
                    if (deflate.ReadByte() != -1) throw new IOException("A Deflate-compressed frame has trailing data.");
                    return output;
                }
                catch (Exception e) when (e is InvalidDataException or EndOfStreamException)
                {
                    throw new IOException("A Deflate-compressed frame failed to decompress.", e);
                }

            default:
                throw new IOException($"Unknown frame compression algorithm {(byte)kind}.");
        }
    }

    private static bool TryCompress(FrameCompressionKind kind, ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten)
    {
        bytesWritten = 0;
        if (destination.IsEmpty) return false;
        switch (kind)
        {
            case FrameCompressionKind.Brotli:
                // Quality 4 with window 20 gives fast, low-memory compression suitable for real-time terminal and JSON streams.
                return BrotliEncoder.TryCompress(source, destination, out bytesWritten, quality: 4, window: 20);

            case FrameCompressionKind.Deflate:
                using (var buffer = new MemoryStream(destination.Length))
                {
                    using (var deflate = new DeflateStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
                    {
                        deflate.Write(source);
                    }
                    if (buffer.Length > destination.Length) return false;
                    bytesWritten = (int)buffer.Length;
                    buffer.GetBuffer().AsSpan(0, bytesWritten).CopyTo(destination);
                    return true;
                }

            default:
                return false;
        }
    }
}
