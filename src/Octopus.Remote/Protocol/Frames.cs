using System.Buffers.Binary;

namespace Octopus.Remote.Protocol;

/// <summary>What a message inside the secure channel is (design/12-远程操作.md §6.1).</summary>
public enum FrameType : byte
{
    /// <summary>JSON: a method call that expects a <see cref="Response"/>.</summary>
    Request = 0x01,
    Response = 0x02,
    /// <summary>JSON: a method call nobody answers (an event).</summary>
    Notification = 0x03,
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
}

[Flags]
public enum FrameFlags : byte
{
    None = 0,
    /// <summary>A receiver that does not know this frame's type drops it instead of ending the connection.</summary>
    Droppable = 0x01,
}

/// <summary>The six bytes before every payload: type, flags and the channel (a terminal attached on this connection; 0 for control).</summary>
public readonly record struct FrameHeader(FrameType Type, FrameFlags Flags, uint Channel)
{
    public const int Length = 6;

    public static bool IsKnown(FrameType type) =>
        type is FrameType.Request or FrameType.Response or FrameType.Notification or FrameType.Output or FrameType.Record or FrameType.Input or FrameType.Ack or FrameType.Snapshot;

    /// <summary>The frame as one message: the header, then the payload.</summary>
    public byte[] Wrap(ReadOnlySpan<byte> payload)
    {
        var message = new byte[Length + payload.Length];
        message[0] = (byte)Type;
        message[1] = (byte)Flags;
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(2), Channel);
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
