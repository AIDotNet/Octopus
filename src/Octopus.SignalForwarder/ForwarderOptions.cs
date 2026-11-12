namespace Octopus.SignalForwarder;

/// <summary>How the agent hands over its payload, and what the agent expects back.</summary>
internal enum ForwarderMode
{
    /// <summary>JSON on standard input; the user's own status line command runs after it and its output is ours.</summary>
    StatusLine,
    /// <summary>JSON on standard input; prints <see cref="ForwarderOptions.Reply"/> (usually nothing).</summary>
    Hook,
    /// <summary>JSON as the last argument (Codex <c>notify</c>); the user's own notify program runs after it.</summary>
    Notify,
}

/// <summary>
/// The command line: <c>octopus-signal &lt;statusline|hook|notify&gt; --agent &lt;id&gt; --channel &lt;name&gt;
/// [--min-interval &lt;ms&gt;] [--reply &lt;text&gt;] [payload]</c>.
/// </summary>
internal sealed record ForwarderOptions(
    ForwarderMode Mode,
    string Agent,
    string Channel,
    int MinIntervalMs,
    string? Reply,
    string? ArgumentPayload)
{
    public static ForwarderOptions? Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return null;
        }
        ForwarderMode? mode = args[0] switch
        {
            "statusline" => ForwarderMode.StatusLine,
            "hook" => ForwarderMode.Hook,
            "notify" => ForwarderMode.Notify,
            _ => null,
        };
        if (mode is null)
        {
            return null;
        }
        string? agent = null, channel = null, reply = null, payload = null;
        var minInterval = 0;
        for (var i = 1; i < args.Count; i++)
        {
            var value = i + 1 < args.Count ? args[i + 1] : null;
            switch (args[i])
            {
                case "--agent" when value is not null:
                    agent = value;
                    i++;
                    break;
                case "--channel" when value is not null:
                    channel = value;
                    i++;
                    break;
                case "--min-interval" when value is not null && int.TryParse(value, out var ms) && ms >= 0:
                    minInterval = ms;
                    i++;
                    break;
                case "--reply" when value is not null:
                    reply = value;
                    i++;
                    break;
                default:
                    // Codex appends the notification JSON after our own arguments.
                    payload = args[i];
                    break;
            }
        }
        if (string.IsNullOrEmpty(agent) || string.IsNullOrEmpty(channel))
        {
            return null;
        }
        return new ForwarderOptions(mode.Value, agent, channel, minInterval, reply, payload);
    }
}
