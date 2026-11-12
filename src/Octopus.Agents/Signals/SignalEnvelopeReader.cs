using System.Text.Json;

namespace Octopus.Agents.Signals;

/// <summary>An envelope exactly as the forwarder wrote it, before any check.</summary>
internal sealed record SignalEnvelope(
    int Version,
    string Token,
    string Item,
    string Agent,
    string Channel,
    string Account,
    long Timestamp,
    string Payload);

/// <summary>
/// Reads the forwarder's wire format (design/plugins/13-P0验证记录.md §2): one UTF-8 JSON object per connection with
/// <c>v</c>, <c>token</c>, <c>item</c>, <c>agent</c>, <c>channel</c>, <c>account</c>, <c>ts</c> and <c>payload</c>
/// (the agent's own payload as a string). Unknown members are skipped so a newer forwarder can add some.
/// </summary>
internal static class SignalEnvelopeReader
{
    public const int Version = 1;

    /// <summary>Null when the bytes are not a single JSON object with the required members of the right types.</summary>
    public static SignalEnvelope? TryRead(ReadOnlySpan<byte> utf8)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 64 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }
            int? version = null;
            string? token = null, item = null, agent = null, channel = null, account = null, payload = null;
            long timestamp = 0;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    return null;
                }
                var name = reader.GetString();
                if (!reader.Read())
                {
                    return null;
                }
                switch (name)
                {
                    case "v" when reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var v):
                        version = v;
                        break;
                    case "ts" when reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var ts):
                        timestamp = ts;
                        break;
                    case "token" or "item" or "agent" or "channel" or "account" or "payload":
                        if (reader.TokenType != JsonTokenType.String)
                        {
                            return null;
                        }
                        var value = reader.GetString();
                        switch (name)
                        {
                            case "token": token = value; break;
                            case "item": item = value; break;
                            case "agent": agent = value; break;
                            case "channel": channel = value; break;
                            case "account": account = value; break;
                            default: payload = value; break;
                        }
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }
            // Nothing but whitespace may follow the object.
            if (reader.Read())
            {
                return null;
            }
            if (version is null || token is null || agent is null || channel is null || payload is null)
            {
                return null;
            }
            return new SignalEnvelope(version.Value, token, item ?? "", agent, channel, account ?? "", timestamp, payload);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
