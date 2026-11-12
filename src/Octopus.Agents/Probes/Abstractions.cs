using Octopus.Core.Agents;

namespace Octopus.Agents.Probes;

/// <summary>Why a probe produced no result.</summary>
public enum ProbeErrorKind
{
    None,
    /// <summary>The definition has no probe of that name, or of an unknown kind.</summary>
    NotDeclared,
    /// <summary>A placeholder had no value, or the value was refused (empty, too long, control characters, starts with <c>-</c>).</summary>
    InvalidArgument,
    /// <summary>The executable could not be started.</summary>
    StartFailed,
    /// <summary>It exited with a non-zero code, or before answering.</summary>
    ExitCode,
    Timeout,
    /// <summary>Standard output passed <see cref="ProbeRunner.MaxOutputBytes"/>.</summary>
    OutputTooLarge,
    /// <summary>The output was not the JSON the probe declares.</summary>
    InvalidOutput,
    /// <summary>A JSON-RPC request was answered with an error.</summary>
    RpcError,
    /// <summary>Not run: the previous runs failed and the back-off has not passed yet (see <see cref="ProbeResult.RetryAt"/>).</summary>
    BackingOff,
    /// <summary>The runner was disposed while it ran.</summary>
    Canceled,
}

/// <summary>One request to run a probe.</summary>
/// <param name="Probe">The name in the definition's <c>probes</c> block.</param>
/// <param name="Executable">The installed executable found by the scan; run directly, never through a shell.</param>
/// <param name="Environment">
/// The environment the agent is launched with. Null means the app's own environment minus host variables.
/// <c>OCTOPUS_*</c> variables are always removed.
/// </param>
/// <param name="Account">
/// The account directory (the value for the definition's account variable). Null keeps whatever
/// <paramref name="Environment"/> says, which usually means the agent's default account.
/// </param>
/// <param name="Arguments">Values for placeholders such as <c>{sessionId}</c>.</param>
/// <param name="AppVersion">Fills <c>{appVersion}</c>, e.g. in Codex's <c>initialize</c> client info.</param>
/// <param name="PrefixArguments">
/// Arguments that come before the probe's own: the rest of the user's command when it is a wrapper
/// (<c>rec claude</c> runs <c>rec</c> with <c>claude</c> first).
/// </param>
public sealed record ProbeRequest(
    AgentDefinition Definition,
    string Probe,
    string Executable,
    IReadOnlyDictionary<string, string>? Environment = null,
    string? Account = null,
    IReadOnlyDictionary<string, string>? Arguments = null,
    string? WorkingDirectory = null,
    string? AppVersion = null,
    IReadOnlyList<string>? PrefixArguments = null);

/// <summary>The outcome of a probe.</summary>
/// <param name="Account">The account key (a hash, never the directory), as in signals.</param>
/// <param name="Json">The result after <c>select</c>, compact JSON; null unless <paramref name="Success"/>.</param>
/// <param name="Message">A short reason for a failure, for diagnostics. Never the program's error output.</param>
/// <param name="Elapsed">How long the run took; for a cached result, the original run's time.</param>
/// <param name="FromCache">The result of an earlier run, returned because <c>minIntervalSec</c> has not passed.</param>
/// <param name="RetryAt">After a failure: when the next attempt will be made.</param>
public sealed record ProbeResult(
    string AgentId,
    string Probe,
    string Account,
    bool Success,
    ProbeErrorKind Error,
    string? Message,
    TimeSpan Elapsed,
    string? Json,
    DateTimeOffset CompletedAt,
    bool FromCache = false,
    DateTimeOffset? RetryAt = null);

/// <summary>
/// Runs the fixed queries agent definitions declare (design/plugins/11 §1A), with the limits that keep a misbehaving
/// caller from turning Octopus into a process spawner: per-probe minimum interval, one run per agent, probe and account
/// at a time, two probes at once in the whole app, 1 MB of output, a timeout, and back-off after failures.
/// </summary>
public interface IProbeRunner
{
    /// <summary>Never throws for a failing probe; the result says what went wrong. Only caller cancellation propagates.</summary>
    Task<ProbeResult> RunAsync(ProbeRequest request, CancellationToken cancellationToken = default);
}
