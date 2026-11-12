using System.Buffers;
using System.Text.Json;

namespace Octopus.SignalForwarder;

/// <summary>
/// One signal on the wire: a single UTF-8 JSON object per connection. The agent's payload travels as a string so
/// the forwarder never has to understand it.
/// </summary>
internal static class SignalEnvelope
{
    public const int Version = 1;

    public static byte[] Build(ForwarderOptions options, SignalEnvironment environment, string payload, long timestampMs)
    {
        var buffer = new ArrayBufferWriter<byte>(payload.Length + 256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", Version);
            writer.WriteString("token", environment.Token ?? "");
            writer.WriteString("item", environment.ItemId ?? "");
            writer.WriteString("agent", options.Agent);
            writer.WriteString("channel", options.Channel);
            writer.WriteString("account", environment.Account);
            writer.WriteNumber("ts", timestampMs);
            writer.WriteString("payload", payload);
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }
}
