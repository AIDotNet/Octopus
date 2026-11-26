using System.Buffers.Binary;

namespace Octopus.Remote.Protocol;

/// <summary>What a message inside the secure channel is (design/12-远程操作.md §6.1, design/remote/传输协议v2与多传输优化.md §3.1).</summary>
public enum FrameType : byte
{
    /// <summary>JSON: a method call that expects a <see cref="Response"/>.</summary>
    Request = 0x01,
    Response = 0x02,
    /// <summary>JSON: a method call nobody answers (an event).</summary>
    Notification = 0x03,
    /// <summary>8 bytes of timestamp ticks; answered with <see cref="Pong"/> for RTT measurement and idle keep-alive.</summary>
    Ping = 0x04,
    /// <summary>8 bytes of timestamp ticks echoed back from a <see cref="Ping"/>.</summary>
    Pong = 0x05,
    /// <summary>Host to device: 8 bytes of position, then the terminal's output from there.</summary>
    Output = 0x10,
    /// <summary>Host to device: 8 bytes of position and one of kind, then a resize or a reset that takes effect at that place in the output.</summary>
    Record = 0x11,
    /// <summary>Device to host: bytes typed into the terminal.</summary>
    Input = 0x12,
    /// <summary>Device to host: 8 bytes, the position up to which it has processed the output.</summary>
    Ack = 0x13,
    /// <summary>
    /// Host to device, right after a terminal was attached: one byte of flags (bit 0: this is the last piece), then a
    /// piece of the terminal's snapshot. The output that follows starts where the snapshot ends.
    /// </summary>
    Snapshot = 0x14,
    /// <summary>Raw binary chunk of a file or upload stream on a dedicated channel (v2).</summary>
    StreamData = 0x15,
    /// <summary>Marks the end of a binary stream on a channel: 1 byte of status (0 = completed, 1 = aborted) and optional UTF-8 error text (v2).</summary>
    StreamEnd = 0x16,
    /// <summary>Receiver to sender on a binary stream channel: 8 bytes (LE), total bytes consumed so far for sliding-window flow control (v2).</summary>
    StreamWindow = 0x17,
}

[Flags]
public enum FrameFlags : byte
{
    None = 0,
    /// <summary>A receiver that does not know this frame's type drops it instead of ending the connection.</summary>
    Droppable = 0x01,
    /// <summary>The payload is compressed with <see cref="FrameCodec"/> (1 byte algorithm id + 4 bytes uncompressed length + compressed data).</summary>
    Compressed = 0x02,
    /// <summary>Sent ahead of queued bulk frames when the connection's send lock is contended.</summary>
    HighPriority = 0x04,
}

/// <summary>The six bytes before every payload: type, flags and the channel (a terminal or binary stream on this connection; 0 for control).</summary>
public readonly record struct FrameHeader(FrameType Type, FrameFlags Flags, uint Channel)
{
    public const int Length = 6;

    public static bool IsKnown(FrameType type) =>
        type is FrameType.Request or FrameType.Response or FrameType.Notification
            or FrameType.Ping or FrameType.Pong
            or FrameType.Output or FrameType.Record or FrameType.Input or FrameType.Ack or FrameType.Snapshot
            or FrameType.StreamData or FrameType.StreamEnd or FrameType.StreamWindow;

    /// <summary>True when this frame should jump ahead of queued bulk data frames under contention.</summary>
    public bool IsHighPriority =>
        Flags.HasFlag(FrameFlags.HighPriority)
        || Type is FrameType.Input or FrameType.Ack or FrameType.Ping or FrameType.Pong or FrameType.StreamWindow;

    /// <summary>Writes the 6-byte header at the start of <paramref name="destination"/>.</summary>
    public void Write(Span<byte> destination)
    {
        destination[0] = (byte)Type;
        destination[1] = (byte)Flags;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[2..], Channel);
    }

    /// <summary>The frame as one message: the header, then the payload.</summary>
    public byte[] Wrap(ReadOnlySpan<byte> payload)
    {
        var message = new byte[Length + payload.Length];
        Write(message);
        payload.CopyTo(message.AsSpan(Length));
        return message;
    }

    /// <summary>The header of a message; false when it is too short to have one.</summary>
    public static bool TryRead(ReadOnlySpan<byte> message, out FrameHeader header)
    {
        if (message.Length < Length)
        {
            header = default;
            return false;
        }
        header = new FrameHeader((FrameType)message[0], (FrameFlags)message[1], BinaryPrimitives.ReadUInt32LittleEndian(message[2..]));
        return true;
    }
}
