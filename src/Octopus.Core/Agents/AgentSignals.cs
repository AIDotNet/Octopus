using System.Text.Json;

namespace Octopus.Core.Agents;

/// <summary>
/// How Octopus listens to an agent: the agent's own structured outlets (status line, hooks, notify, in-process
/// plugins) are pointed at the <c>octopus-signal</c> forwarder for the sessions Octopus starts. Data, not code, so a
/// new agent gets signals by contributing a definition. See design/plugins/11-补充能力.md §1.2.
/// </summary>
public sealed class AgentSignals
{
    /// <summary>Oldest agent version the attach arguments work with. Nothing is attached below it or when the version is unknown.</summary>
    public string? MinVersion { get; set; }

    /// <summary>
    /// Whose file formats the generated settings, plugin directory and config overlay use (<c>claude</c>, <c>codex</c>,
    /// <c>grok</c>, <c>cursor</c>, <c>opencode</c>). Null means the definition's id; a wrapper around Claude Code
    /// would set <c>claude</c>.
    /// </summary>
    public string? Dialect { get; set; }

    /// <summary>The variable that tells the agent's accounts apart; null when the agent has a single account.</summary>
    public SignalAccountSpec? Account { get; set; }

    /// <summary>
    /// What is added to the launch. Placeholders: <c>{signalSettings}</c>, <c>{signalPluginDir}</c>,
    /// <c>{signalConfig}</c>, <c>{signalNotify}</c>, <c>{forwarder}</c>.
    /// </summary>
    public List<SignalAttachSpec> Attach { get; set; } = [];

    /// <summary>Channel name (what plugins subscribe to) to where it comes from.</summary>
    public Dictionary<string, SignalChannelSpec> Channels { get; set; } = [];

    /// <summary>Channels that drive the host's status dot; no plugin needed.</summary>
    public SignalStatusSpec? Status { get; set; }

    /// <summary>
    /// Credential files in the account directory (relative paths or globs, e.g. <c>.credentials.json</c>). They are
    /// never readable through an agent data scope, whatever the plugin asks for (design/plugins/11 §4.3).
    /// </summary>
    public List<string> CredentialFiles { get; set; } = [];
}

public sealed class SignalAccountSpec
{
    /// <summary>The environment variable that selects the account directory, e.g. <c>CLAUDE_CONFIG_DIR</c>.</summary>
    public string Env { get; set; } = "";

    /// <summary>The directory used when the variable is unset; <c>~</c> is the user's home.</summary>
    public string? Default { get; set; }
}

/// <summary>One way of attaching: arguments, environment variables, or both.</summary>
public sealed class SignalAttachSpec
{
    public List<string>? Args { get; set; }
    public Dictionary<string, string>? Env { get; set; }

    /// <summary>Whether this attach method was checked against a real session of the CLI.</summary>
    public bool Verified { get; set; }

    /// <summary>Why it is believed to work, or what is still open. Shown nowhere; for whoever maintains the definition.</summary>
    public string? Note { get; set; }
}

/// <summary>Where a channel's payload comes from.</summary>
public sealed class SignalChannelSpec
{
    /// <summary>One of <see cref="SignalVia"/>.</summary>
    public string Via { get; set; } = SignalVia.Hook;

    /// <summary>The agent's own event name for <c>hook</c> and <c>plugin</c> channels, e.g. <c>Stop</c> or <c>session.idle</c>.</summary>
    public string? Event { get; set; }

    /// <summary>
    /// Hook channels only: the agent's matcher for the event, e.g. <c>Agent|Task</c> for <c>PreToolUse</c>, so the
    /// forwarder runs for those tools only. Null runs it for every occurrence of the event.
    /// </summary>
    public string? Matcher { get; set; }

    /// <summary>
    /// The slot holds only one command: Octopus resolves the user's own command at launch and the forwarder runs it
    /// after forwarding, so the user's status line or notify program keeps working (design/plugins/11 §1.4).
    /// </summary>
    public bool ChainUserCommand { get; set; }

    /// <summary>Status line only: ask the agent to re-run it every N seconds even when idle.</summary>
    public int? RefreshInterval { get; set; }
}

/// <summary>The <c>via</c> values of a channel.</summary>
public static class SignalVia
{
    public const string StatusLine = "statusLine";
    public const string Hook = "hook";
    public const string Notify = "notify";
    public const string Plugin = "plugin";
}

/// <summary>Channel names per status. A channel listed in several is taken in this order: error, waiting, done, working.</summary>
public sealed class SignalStatusSpec
{
    public List<string> Working { get; set; } = [];
    public List<string> Waiting { get; set; } = [];
    public List<string> Done { get; set; } = [];
    public List<string> Error { get; set; } = [];
}

/// <summary>
/// A fixed, read-only query that Octopus runs with the agent's own executable, e.g. Codex's rate limits over
/// <c>codex app-server</c>. Plugins can only ask for the result. See design/plugins/11-补充能力.md §1A.
/// </summary>
public sealed class AgentProbe
{
    /// <summary>One of <see cref="ProbeKinds"/>.</summary>
    public string Kind { get; set; } = ProbeKinds.Command;

    /// <summary>Arguments after the executable. Placeholders such as <c>{sessionId}</c> are filled from the request.</summary>
    public List<string> Args { get; set; } = [];

    /// <summary><c>jsonRpcStdio</c>: the requests, sent in order, each after the previous one was answered.</summary>
    public List<ProbeRpcRequest> Requests { get; set; } = [];

    /// <summary><c>command</c>: how standard output is read, <c>json</c> (default) or <c>text</c>.</summary>
    public string Parse { get; set; } = "json";

    /// <summary>Top-level dotted paths kept from the result; everything else is dropped. Empty keeps the whole result.</summary>
    public List<string> Select { get; set; } = [];

    public int TimeoutMs { get; set; } = 10_000;

    /// <summary>A repeated request within this many seconds gets the previous result instead of a new run.</summary>
    public int MinIntervalSec { get; set; }

    /// <summary>
    /// <c>account</c>: only one probe of this agent runs per account directory at a time (two processes sharing
    /// <c>CODEX_HOME</c> could race on its files). Null: one run per probe and account.
    /// </summary>
    public string? ExclusivePer { get; set; }
}

public sealed class ProbeRpcRequest
{
    public string Method { get; set; } = "";

    /// <summary>Sent as is, after placeholders in string values are filled. Omitted when null.</summary>
    public JsonElement? Params { get; set; }

    /// <summary>This request's result is the probe's result. Without one, the last request's is.</summary>
    public bool Result { get; set; }
}

/// <summary>The <c>kind</c> values of a probe.</summary>
public static class ProbeKinds
{
    /// <summary>Run the executable and read its standard output.</summary>
    public const string Command = "command";

    /// <summary>Start the executable and speak newline-delimited JSON-RPC 2.0 over its standard input and output.</summary>
    public const string JsonRpcStdio = "jsonRpcStdio";
}
