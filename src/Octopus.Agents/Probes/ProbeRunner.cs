using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Octopus.Agents.Signals;
using Octopus.Core.Agents;
using Octopus.Platform;

namespace Octopus.Agents.Probes;

/// <summary>
/// Runs definition-declared probes (design/plugins/11 §1A.3). Every request goes through, in order: the cached result
/// while <c>minIntervalSec</c> has not passed; the back-off after consecutive failures (1, 5, then 30 minutes); joining
/// a run already in flight for the same probe, account and arguments; one run per agent, probe and account (or per
/// agent and account with <c>exclusivePer: "account"</c>); at most <see cref="MaxConcurrentProbes"/> runs in the app.
/// A successful run is also published on the signal hub as channel <c>probe:&lt;name&gt;</c>.
/// </summary>
public sealed partial class ProbeRunner : IProbeRunner, IDisposable
{
    public const int MaxOutputBytes = 1024 * 1024;
    public const int MaxConcurrentProbes = 2;

    /// <summary>Upper bound for a definition's <c>timeoutMs</c>, so a typo cannot keep a process for hours.</summary>
    public const int MaxTimeoutMs = 120_000;

    private const int MaxArgumentLength = 256;
    private const int MaxRememberedStates = 1024;

    /// <summary>Wait after the first, second and every later consecutive failure.</summary>
    public static IReadOnlyList<TimeSpan> Backoff { get; } = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30)];

    private readonly ISignalHub? _hub;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _global = new(MaxConcurrentProbes, MaxConcurrentProbes);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _exclusive = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProbeState> _states = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();

    public ProbeRunner(ISignalHub? hub = null, TimeProvider? time = null)
    {
        _hub = hub;
        _time = time ?? TimeProvider.System;
    }

    public async Task<ProbeResult> RunAsync(ProbeRequest request, CancellationToken cancellationToken = default)
    {
        var definition = request.Definition;
        var environment = BuildEnvironment(request);
        var accountVariable = definition.Signals?.Account?.Env;
        var account = SignalAccounts.Key(
            definition,
            !string.IsNullOrEmpty(accountVariable) ? environment.GetValueOrDefault(accountVariable) : null,
            SignalAccounts.HomeOf(environment));

        if (definition.Probes is null
            || !definition.Probes.TryGetValue(request.Probe, out var probe)
            || probe.Kind is not (ProbeKinds.Command or ProbeKinds.JsonRpcStdio))
        {
            return Immediate(request, account, ProbeErrorKind.NotDeclared, $"'{request.Probe}' is not a probe of {definition.Id}");
        }
        if (string.IsNullOrWhiteSpace(request.Executable))
        {
            return Immediate(request, account, ProbeErrorKind.StartFailed, $"{definition.Id} is not installed");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in request.Arguments ?? new Dictionary<string, string>())
        {
            values[name] = value;
        }
        values["appVersion"] = string.IsNullOrEmpty(request.AppVersion) ? HostEnvironmentFilter.DefaultAppVersion() : request.AppVersion;
        var arguments = new List<string>();
        foreach (var template in probe.Args)
        {
            if (Fill(template, values, validate: true) is not { } argument)
            {
                return Immediate(request, account, ProbeErrorKind.InvalidArgument, $"a value for '{template}' is missing or not allowed");
            }
            arguments.Add(argument);
        }
        if (request.PrefixArguments is { Count: > 0 } prefix)
        {
            arguments.InsertRange(0, prefix);
        }

        var key = string.Join('\0', [definition.Id, request.Probe, account, .. arguments]);
        Task<ProbeResult> run;
        lock (_gate)
        {
            if (!_states.TryGetValue(key, out var state))
            {
                if (_states.Count >= MaxRememberedStates)
                {
                    // Keys include arguments such as session ids. Forgetting idle ones only risks one extra run.
                    foreach (var idle in _states.Where(s => s.Value.InFlight is null).Select(s => s.Key).ToList())
                    {
                        _states.Remove(idle);
                    }
                }
                state = new ProbeState();
                _states[key] = state;
            }
            var now = _time.GetUtcNow();
            if (state.LastSuccess is { } last && probe.MinIntervalSec > 0 && now - last.CompletedAt < TimeSpan.FromSeconds(probe.MinIntervalSec))
            {
                return last with { FromCache = true };
            }
            if (state.RetryAt is { } retryAt && now < retryAt)
            {
                return Immediate(request, account, ProbeErrorKind.BackingOff, state.LastFailure?.Message ?? "failed recently") with { RetryAt = retryAt };
            }
            if (state.InFlight is null)
            {
                var exclusive = string.Equals(probe.ExclusivePer, "account", StringComparison.OrdinalIgnoreCase)
                    ? $"{definition.Id}\0{account}"
                    : $"{definition.Id}\0{request.Probe}\0{account}";
                state.InFlight = RunAndRecordAsync(request, probe, arguments, environment, values, account, exclusive, state);
            }
            run = state.InFlight;
        }
        // The run is shared: one caller giving up must not cancel it for the others.
        return await run.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (!_shutdown.IsCancellationRequested)
        {
            _shutdown.Cancel();
        }
    }

    private async Task<ProbeResult> RunAndRecordAsync(
        ProbeRequest request,
        AgentProbe probe,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyDictionary<string, string> values,
        string account,
        string exclusiveKey,
        ProbeState state)
    {
        await Task.Yield(); // leave the caller's lock before doing anything
        var stopwatch = Stopwatch.StartNew();
        ProbeOutcome outcome;
        try
        {
            var exclusive = _exclusive.GetOrAdd(exclusiveKey, _ => new SemaphoreSlim(1, 1));
            await exclusive.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            try
            {
                await _global.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                try
                {
                    stopwatch.Restart(); // waiting in line is not the probe's time
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                    timeout.CancelAfter(Math.Clamp(probe.TimeoutMs, 100, MaxTimeoutMs));
                    outcome = await ProbeProcess.RunAsync(
                        probe,
                        request.Executable,
                        arguments,
                        environment,
                        request.WorkingDirectory ?? SignalAccounts.HomeOf(environment),
                        text => Fill(text, values, validate: false)!,
                        MaxOutputBytes,
                        timeout.Token).ConfigureAwait(false);
                }
                finally
                {
                    _global.Release();
                }
            }
            finally
            {
                exclusive.Release();
            }
        }
        catch (OperationCanceledException)
        {
            outcome = ProbeOutcome.Fail(ProbeErrorKind.Canceled, "Octopus is shutting down");
        }
        catch (Exception ex)
        {
            outcome = ProbeOutcome.Fail(ProbeErrorKind.StartFailed, ex.Message);
        }
        if (_shutdown.IsCancellationRequested && outcome.Error == ProbeErrorKind.Timeout)
        {
            outcome = ProbeOutcome.Fail(ProbeErrorKind.Canceled, "Octopus is shutting down");
        }

        var result = new ProbeResult(
            request.Definition.Id,
            request.Probe,
            account,
            outcome.Error == ProbeErrorKind.None,
            outcome.Error,
            outcome.Message,
            stopwatch.Elapsed,
            outcome.Json,
            _time.GetUtcNow());

        lock (_gate)
        {
            state.InFlight = null;
            if (result.Success)
            {
                state.LastSuccess = result;
                state.Failures = 0;
                state.RetryAt = null;
                state.LastFailure = null;
            }
            else if (result.Error != ProbeErrorKind.Canceled)
            {
                state.Failures++;
                state.RetryAt = result.CompletedAt + Backoff[Math.Min(state.Failures, Backoff.Count) - 1];
                state.LastFailure = result;
                result = result with { RetryAt = state.RetryAt };
            }
        }

        if (result.Success && _hub is not null)
        {
            var at = result.CompletedAt.ToUnixTimeMilliseconds();
            try
            {
                _hub.Publish(new AgentSignal(result.AgentId, "probe:" + result.Probe, "", account, at, at, result.Json!));
            }
            catch (Exception)
            {
                // Broadcasting is a convenience; the caller has the result either way.
            }
        }
        return result;
    }

    private ProbeResult Immediate(ProbeRequest request, string account, ProbeErrorKind error, string message) =>
        new(request.Definition.Id, request.Probe, account, false, error, message, TimeSpan.Zero, null, _time.GetUtcNow());

    /// <summary>
    /// The agent's environment without anything of Octopus' own (the signal token must not reach a probe), with the
    /// requested account directory in the account variable.
    /// </summary>
    private static Dictionary<string, string> BuildEnvironment(ProbeRequest request)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var environment = new Dictionary<string, string>(comparer);
        if (request.Environment is not null)
        {
            foreach (var (key, value) in request.Environment)
            {
                environment[key] = value;
            }
        }
        else
        {
            foreach (var (key, value) in HostEnvironmentFilter.SnapshotProcessEnvironment(comparer))
            {
                if (!HostEnvironmentFilter.ShouldRemove(key))
                {
                    environment[key] = value;
                }
            }
        }
        foreach (var key in environment.Keys.Where(k => k.StartsWith("OCTOPUS_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            environment.Remove(key);
        }
        if (request.Definition.Signals?.Account is { Env.Length: > 0 } account && !string.IsNullOrWhiteSpace(request.Account))
        {
            environment[account.Env] = request.Account;
        }
        return environment;
    }

    /// <summary>
    /// Fills <c>{name}</c> placeholders. Values for arguments are checked: they come from plugins and must not be able
    /// to turn into options (<c>--…</c>) or smuggle control characters. Null when a value is missing or refused.
    /// </summary>
    private static string? Fill(string template, IReadOnlyDictionary<string, string> values, bool validate)
    {
        var failed = false;
        var result = PlaceholderRegex().Replace(template, match =>
        {
            var name = match.Groups[1].Value;
            if (!values.TryGetValue(name, out var value) || (validate && !IsAllowedValue(value)))
            {
                failed = true;
                return match.Value;
            }
            return value;
        });
        return failed && validate ? null : result;
    }

    private static bool IsAllowedValue(string value) =>
        value.Length is > 0 and <= MaxArgumentLength
        && !value.StartsWith('-')
        && !value.Any(char.IsControl);

    [GeneratedRegex(@"\{([A-Za-z][A-Za-z0-9_]*)\}")]
    private static partial Regex PlaceholderRegex();

    private sealed class ProbeState
    {
        public ProbeResult? LastSuccess { get; set; }
        public ProbeResult? LastFailure { get; set; }
        public int Failures { get; set; }
        public DateTimeOffset? RetryAt { get; set; }
        public Task<ProbeResult>? InFlight { get; set; }
    }
}
