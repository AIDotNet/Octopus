using System.Text;

namespace Octopus.SignalForwarder;

/// <summary>
/// Reads what the agent hands over, passes it to Octopus, then does what the agent expects from the slot: shows the
/// user's own status line, runs the user's own notify program, or prints the hook reply.
/// </summary>
internal static class Forwarder
{
    public const int MaxPayloadBytes = 8 * 1024 * 1024;
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan SendTimeout = TimeSpan.FromMilliseconds(150);

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        SignalEnvironment environment,
        Stream stdin,
        Stream stdout,
        SignalThrottle? throttle = null,
        Func<long>? clock = null)
    {
        var options = ForwarderOptions.Parse(args);
        if (options is null)
        {
            return 0;
        }
        var payload = options.Mode == ForwarderMode.Notify
            ? options.ArgumentPayload ?? ""
            : await ReadPayloadAsync(stdin);

        if (environment.Endpoint is { Length: > 0 } endpoint && payload.Length > 0)
        {
            var now = clock?.Invoke() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            throttle ??= SignalThrottle.ForCurrentUser();
            if (throttle.ShouldSend(environment.ItemId ?? "", options.Channel, payload, options.MinIntervalMs, now))
            {
                await SignalTransport.TrySendAsync(endpoint, SignalEnvelope.Build(options, environment, payload, now), SendTimeout);
            }
        }

        var chain = ChainCommand.Parse(environment.Chain);
        switch (options.Mode)
        {
            case ForwarderMode.StatusLine when chain is not null:
            {
                var (exitCode, output) = await chain.RunAsync(payload, null, environment.All);
                await stdout.WriteAsync(output);
                await stdout.FlushAsync();
                return exitCode;
            }
            case ForwarderMode.Notify when chain is not null:
                await chain.RunAsync(null, payload, environment.All);
                return 0;
            case ForwarderMode.Hook when options.Reply is not null:
                await stdout.WriteAsync(Encoding.UTF8.GetBytes(options.Reply));
                await stdout.FlushAsync();
                return 0;
            default:
                return 0;
        }
    }

    /// <summary>
    /// Standard input to the end. Empty when it is larger than the cap or does not end within the timeout: a cut-off
    /// JSON document is of no use to Octopus, and the forwarder must not hold the agent up.
    /// </summary>
    private static async Task<string> ReadPayloadAsync(Stream stdin)
    {
        var read = Task.Run(async () =>
        {
            var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            int n;
            while ((n = await stdin.ReadAsync(chunk)) > 0)
            {
                if (buffer.Length + n > MaxPayloadBytes)
                {
                    return "";
                }
                buffer.Write(chunk, 0, n);
            }
            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        });
        return await Task.WhenAny(read, Task.Delay(ReadTimeout)) == read ? await read : "";
    }
}
