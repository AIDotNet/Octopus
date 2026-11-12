using Octopus.Core.Agents;

namespace Octopus.Agents.Signals;

/// <summary>
/// One signal from an agent, as delivered to subscribers.
/// </summary>
/// <param name="AgentId">The definition id the forwarder named, e.g. <c>claude</c>.</param>
/// <param name="Channel">A channel the definition declares, or <c>probe:&lt;name&gt;</c> for a probe result.</param>
/// <param name="ItemId">The Octopus item (tab) whose agent sent it; empty for probe results.</param>
/// <param name="Account">
/// The account key: a short hash of the account directory, never the path itself (design/plugins/11 §2.3). Empty
/// for agents with a single account.
/// </param>
/// <param name="SentAt">When the forwarder sent it, Unix milliseconds (the forwarder's clock).</param>
/// <param name="ReceivedAt">When the hub received it, Unix milliseconds.</param>
/// <param name="Payload">The agent's own payload, usually JSON, exactly as the agent produced it.</param>
public sealed record AgentSignal(
    string AgentId,
    string Channel,
    string ItemId,
    string Account,
    long SentAt,
    long ReceivedAt,
    string Payload);

/// <summary>Where the forwarder delivers signals and the per-run secret it must present.</summary>
/// <param name="Address">A Unix domain socket path, or <c>\\.\pipe\name</c> on Windows.</param>
public sealed record SignalEndpoint(string Address, string Token);

/// <summary>An account directory seen in a signal, so a key can be shown with a name and probes can run against it.</summary>
/// <param name="Directory">The normalized account directory. Stays inside the host; plugins only see the key.</param>
/// <param name="DefaultLabel">The last two path segments, until the user names it (design/plugins/11 §2.3).</param>
public sealed record AgentAccount(string AgentId, string Key, string Directory, string DefaultLabel);

/// <summary>Counters for diagnostics.</summary>
/// <param name="Received">Envelopes accepted and delivered.</param>
/// <param name="Dropped">Connections that did not carry a valid envelope (bad token, version, size, JSON, agent or channel).</param>
public sealed record SignalHubStatistics(long Received, long Dropped);

/// <summary>
/// The host side of the signal channel: a private local endpoint the <c>octopus-signal</c> forwarder writes to. It
/// keeps the last signal per item and channel and hands signals to subscribers. See design/plugins/11 §1.3–1.6.
/// </summary>
public interface ISignalHub : IDisposable
{
    /// <summary>The endpoint and token to give to agent launches; null before <see cref="Start"/> or when it could not start.</summary>
    SignalEndpoint? Endpoint { get; }

    /// <summary>
    /// Creates the endpoint and starts accepting. Idempotent. Returns false (and stays without an endpoint) when the
    /// platform refuses, so the caller simply launches agents without signals.
    /// </summary>
    bool Start();

    /// <summary>
    /// Calls <paramref name="listener"/> for each signal of <paramref name="agentId"/> on <paramref name="channel"/>
    /// (null matches any). Called on a background thread; an exception from the listener is swallowed.
    /// </summary>
    IDisposable Subscribe(string? agentId, string? channel, Action<AgentSignal> listener);

    /// <summary>The latest signal of the agent on the channel: for one item, or across all items when null.</summary>
    AgentSignal? LastSignal(string agentId, string channel, string? itemId = null);

    /// <summary>Forgets the last signals of an item whose tab was closed.</summary>
    void ForgetItem(string itemId);

    /// <summary>The account directories seen in signals from this agent since start.</summary>
    IReadOnlyList<AgentAccount> Accounts(string agentId);

    /// <summary>Delivers a signal produced inside the host (a probe result) like one that came from a forwarder.</summary>
    void Publish(AgentSignal signal);

    /// <summary>
    /// Makes an account directory known (as a forwarder signal would): returns its key, which signals published for it
    /// must carry. Used by host-side sources such as the Claude status line bridge.
    /// </summary>
    string RegisterAccount(string agentId, string directory);

    SignalHubStatistics Statistics { get; }
}

/// <summary>Inputs for attaching the signal channel to one agent launch.</summary>
/// <param name="ItemId">The Octopus item the agent runs in; sent back with every signal.</param>
/// <param name="ForwarderPath">Absolute path of the <c>octopus-signal</c> executable shipped with the app.</param>
/// <param name="Endpoint">From <see cref="ISignalHub.Endpoint"/>; null means no hub, so nothing is attached.</param>
/// <param name="InstalledVersion">The version found by the installation scan; compared with <c>minVersion</c>.</param>
/// <param name="AgentEnvironment">
/// The environment the agent will run with, as far as Octopus knows (login environment plus the compiled command's
/// variables). Used to find the account directory and to merge with an overlay variable the user already sets.
/// </param>
/// <param name="WorkingDirectory">Where the agent starts; project-level settings are looked up there.</param>
/// <param name="Enabled">The global and per-agent switches; false attaches nothing.</param>
public sealed record SignalAttachRequest(
    AgentDefinition Definition,
    string ItemId,
    string ForwarderPath,
    SignalEndpoint? Endpoint,
    string? InstalledVersion,
    IReadOnlyDictionary<string, string>? AgentEnvironment = null,
    string? WorkingDirectory = null,
    bool Enabled = true);

/// <summary>What to add to an agent launch: arguments (after the definition's base arguments) and environment variables.</summary>
public sealed record SignalAttachment(IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment)
{
    public static SignalAttachment None { get; } = new([], new Dictionary<string, string>(StringComparer.Ordinal));

    public bool IsEmpty => Arguments.Count == 0 && Environment.Count == 0;
}

/// <summary>
/// Works out the per-launch additions that point an agent's outlets at the forwarder. Never writes to the user's
/// own configuration: everything is arguments, environment variables and files under Octopus' cache directory.
/// </summary>
public interface ISignalAttachPlanner
{
    /// <summary><see cref="SignalAttachment.None"/> when disabled, unsupported, too old, or nothing could be attached.</summary>
    SignalAttachment Plan(SignalAttachRequest request);
}
