using Octopus.Core.Agents;

namespace Octopus.Agents.Signals;

/// <summary>What the status dot shows for an agent tab.</summary>
public enum SignalStatus
{
    Working,
    Waiting,
    Done,
    Error,
}

/// <summary>
/// Maps a channel to a status through the definition's <c>status</c> block, so the host drives the status dot from
/// signals without knowing any agent (design/plugins/11 §1.8).
/// </summary>
public static class SignalStatusMap
{
    /// <summary>
    /// The status a signal on <paramref name="channel"/> means, or null when it means none (e.g. the status line).
    /// A channel listed under several statuses resolves to the most urgent: error, waiting, done, working.
    /// </summary>
    public static SignalStatus? Resolve(AgentDefinition definition, string channel)
    {
        var status = definition.Signals?.Status;
        if (status is null || string.IsNullOrEmpty(channel))
        {
            return null;
        }
        if (status.Error.Contains(channel, StringComparer.Ordinal))
        {
            return SignalStatus.Error;
        }
        if (status.Waiting.Contains(channel, StringComparer.Ordinal))
        {
            return SignalStatus.Waiting;
        }
        if (status.Done.Contains(channel, StringComparer.Ordinal))
        {
            return SignalStatus.Done;
        }
        if (status.Working.Contains(channel, StringComparer.Ordinal))
        {
            return SignalStatus.Working;
        }
        return null;
    }

    /// <summary>Convenience for a received signal.</summary>
    public static SignalStatus? Resolve(AgentDefinition definition, AgentSignal signal) => Resolve(definition, signal.Channel);
}
