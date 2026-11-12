using System.Diagnostics;
using System.Threading.Channels;
using Jaunt;
using Octopus.Plugins.Runtime;

namespace Octopus.Plugins.Jaunt;

/// <summary>
/// One plugin on one Jaunt engine (design/plugins/02-运行时.md §1–2). Every message is one turn: a synchronous root
/// execution with its own budget that hands the message to the SDK's dispatcher. Promises left pending stay in the
/// realm and settle in later turns. Turns run one at a time on the thread pool, never on the UI thread.
/// </summary>
public sealed class JauntPluginRuntime : IPluginRuntime
{
    /// <summary>A mailbox this long means the plugin does not keep up; it is treated as hung.</summary>
    public const int MailboxLimit = 10_000;

    private readonly Channel<PluginInbound> _mailbox = Channel.CreateUnbounded<PluginInbound>(new UnboundedChannelOptions { SingleReader = true });
    private JsEngine? _engine;
    private JsFunction? _dispatch;
    private JsValue _activate;
    private JsValue _deactivate;
    private IPluginRuntimeSink? _sink;
    private PluginRuntimeOptions? _options;
    private Task? _loop;
    private long _queued;
    private long _turns;
    private double _longestTurnMs;
    private volatile bool _faulted;
    private int _logBudget;
    private long _logWindowStart;

    public bool IsFaulted => _faulted;

    public PluginRuntimeStats Stats => new(Interlocked.Read(ref _turns), Interlocked.Read(ref _queued), _longestTurnMs,
        _engine is { IsFaulted: false } engine && !engine.IsExecuting ? SafeTracked(engine) : 0);

    public Task StartAsync(PluginRuntimeOptions options, IPluginRuntimeSink sink, CancellationToken cancellationToken = default)
    {
        _options = options;
        _sink = sink;
        var engine = new JsEngine(new JsEngineOptions
        {
            Profile = options.UnsafeEval ? JsProfile.Standard : JsProfile.Hardened,
            TimeZone = TimeZoneInfo.Local,
            Limits = new JsLimits
            {
                Timeout = options.Budgets.Activate,
                MaxStackDepth = 2000,
                MaxPendingJobs = 100_000,
                MaxOutputBytes = 4L * 1024 * 1024,
                MaxModules = 500,
                MaxStringLength = 64 * 1024 * 1024,
                // MaxTrackedBytes stays unset: it counts every allocation of the engine's lifetime (13-P0验证记录 §1.5).
            },
            UnhandledRejection = rejection => sink.OnLog(PluginLogLevel.Error, $"Unhandled promise rejection: {rejection.Reason}"),
        });
        _engine = engine;
        InstallNatives(engine, sink);

        var resolver = new PluginModuleResolver(options.PluginId, options.PackageDirectory);
        try
        {
            engine.LoadModule(PluginModuleResolver.SdkModule, resolver, PluginSdk.ModuleId, cancellationToken);
            if (_dispatch is null)
            {
                throw new PluginLoadException("The plugin SDK did not register its dispatcher.");
            }
            var main = resolver.LoadEntry(options.MainModule);
            var instance = engine.LoadModule(main, resolver, resolver.IdOf(options.MainModule), cancellationToken);
            _activate = Export(instance, "activate");
            _deactivate = Export(instance, "deactivate");
            if (!_activate.IsFunction)
            {
                throw new PluginLoadException($"{options.MainModule} must export an activate function.", resolver.IdOf(options.MainModule));
            }
        }
        catch (JsCompileException e)
        {
            engine.Dispose();
            throw new PluginLoadException($"{e.Description}", e.SourceName, e.Line, e.Column);
        }
        catch (JsAsyncRequiredException)
        {
            engine.Dispose();
            throw new PluginLoadException("Plugin modules cannot use top-level await. Move asynchronous set-up into activate().");
        }
        catch (JsException e)
        {
            engine.Dispose();
            throw new PluginLoadException($"Loading the plugin threw: {e.Message}");
        }
        catch (Exception e) when (e is JsLimitExceededException or JsEngineFaultedException or OperationCanceledException)
        {
            engine.Dispose();
            throw new PluginLoadException($"Loading the plugin was stopped: {e.Message}");
        }
        catch (PluginLoadException)
        {
            engine.Dispose();
            throw;
        }
        _loop = Task.Run(RunLoopAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    public void Post(in PluginInbound message)
    {
        if (_faulted)
        {
            return;
        }
        if (Interlocked.Increment(ref _queued) > MailboxLimit)
        {
            Fault(PluginFaultKind.Unresponsive, $"The plugin has more than {MailboxLimit} unhandled messages.");
            return;
        }
        _mailbox.Writer.TryWrite(message);
    }

    public async ValueTask DisposeAsync()
    {
        _mailbox.Writer.TryComplete();
        if (_engine is { } engine)
        {
            engine.Interrupt();
        }
        if (_loop is { } loop)
        {
            try { await loop.ConfigureAwait(false); } catch (Exception) { }
        }
        _engine?.Dispose();
    }

    // ---------------------------------------------------------------- the loop

    private async Task RunLoopAsync()
    {
        await foreach (var message in _mailbox.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            Interlocked.Decrement(ref _queued);
            if (_faulted)
            {
                continue;
            }
            RunTurn(message);
        }
    }

    private void RunTurn(in PluginInbound message)
    {
        var engine = _engine!;
        var budgets = _options!.Budgets;
        var (time, work) = message.Kind switch
        {
            InboundKind.Lifecycle => (budgets.Activate, budgets.ActivateWork),
            InboundKind.Invoke when message.Name.StartsWith("view.render", StringComparison.Ordinal) => (budgets.Render, budgets.RenderWork),
            _ => (budgets.Normal, budgets.NormalWork),
        };
        var fn = message.Kind == InboundKind.Lifecycle
            ? message.Name == "activate" ? _activate : _deactivate
            : JsValue.Undefined;
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var execution = engine.BeginExecution(new JsExecutionOptions { Timeout = time, MaxInstructions = work });
            execution.Invoke(_dispatch!, JsValue.From(Kind(message.Kind)), JsValue.From(message.Id), JsValue.From(message.Name),
                JsValue.From(message.Json), JsValue.From(message.Text), fn);
            execution.Complete();
        }
        catch (JsLimitExceededException e)
        {
            Fault(e.Kind == JsLimitKind.Timeout ? PluginFaultKind.Timeout : PluginFaultKind.Limit,
                $"A {Describe(message)} turn exceeded its {e.Kind} limit ({e.Message}).");
        }
        catch (JsException e)
        {
            // The SDK catches handler errors itself; reaching here means the dispatcher itself threw. Answer the
            // call anyway, so the host does not wait for a result that will never come.
            _sink!.OnLog(PluginLogLevel.Error, $"The SDK failed to handle a {Describe(message)} message: {e.Message}");
            if (message.Kind is InboundKind.Invoke or InboundKind.Lifecycle)
            {
                var error = new System.Text.Json.Nodes.JsonObject { ["code"] = "PluginError", ["message"] = e.Message }.ToJsonString();
                _sink.OnOutbound(new PluginOutbound(OutboundKind.Error, message.Id, "", error, ""));
            }
        }
        catch (Exception e) when (e is JsEngineFaultedException or OperationCanceledException or ObjectDisposedException)
        {
            Fault(PluginFaultKind.Internal, e.Message);
        }
        finally
        {
            Interlocked.Increment(ref _turns);
            var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (ms > _longestTurnMs) _longestTurnMs = ms;
        }
    }

    private void Fault(PluginFaultKind kind, string message)
    {
        if (_faulted)
        {
            return;
        }
        _faulted = true;
        _mailbox.Writer.TryComplete();
        _sink?.OnFault(new PluginFault(kind, message));
    }

    private static string Kind(InboundKind kind) => kind switch
    {
        InboundKind.Resolve => "resolve",
        InboundKind.Reject => "reject",
        InboundKind.Event => "event",
        InboundKind.Invoke => "invoke",
        InboundKind.Timer => "timer",
        _ => "lifecycle",
    };

    private static string Describe(in PluginInbound message) => message.Kind switch
    {
        InboundKind.Invoke or InboundKind.Lifecycle or InboundKind.Event => $"{Kind(message.Kind)} \"{message.Name}\"",
        _ => Kind(message.Kind),
    };

    // ---------------------------------------------------------------- natives

    private void InstallNatives(JsEngine engine, IPluginRuntimeSink sink)
    {
        engine.SetFunction("__oct_post", (ref JsCallContext c) =>
        {
            var kind = c.GetString(0) switch
            {
                "request" => OutboundKind.Request,
                "notify" => OutboundKind.Notify,
                "result" => OutboundKind.Result,
                "error" => OutboundKind.Error,
                var other => throw c.TypeError($"unknown message kind {other}"),
            };
            sink.OnOutbound(new PluginOutbound(kind, (long)c.GetNumber(1), c.GetString(2) ?? "", c.GetString(3) ?? "", c.GetString(4) ?? ""));
            return JsValue.Undefined;
        }, 5);
        engine.SetFunction("__oct_sync", (ref JsCallContext c) => JsValue.From(sink.OnSync(c.GetString(0) ?? "", c.GetString(1) ?? "")), 2);
        engine.SetFunction("__oct_log", (ref JsCallContext c) =>
        {
            if (AllowLog())
            {
                var level = c.GetString(0) switch
                {
                    "debug" => PluginLogLevel.Debug,
                    "warn" => PluginLogLevel.Warn,
                    "error" => PluginLogLevel.Error,
                    _ => PluginLogLevel.Info,
                };
                var text = c.GetString(1) ?? "";
                sink.OnLog(level, text.Length > 16_384 ? text[..16_384] + "…" : text);
            }
            return JsValue.Undefined;
        }, 2);
        var clock = Stopwatch.StartNew();
        engine.SetFunction("__oct_now", (ref JsCallContext c) => JsValue.From(clock.Elapsed.TotalMilliseconds), 0);
        engine.SetFunction("__oct_register", (ref JsCallContext c) =>
        {
            if (_dispatch is not null)
            {
                throw c.TypeError("the dispatcher is already registered");
            }
            if (!c[0].IsFunction)
            {
                throw c.TypeError("the dispatcher must be a function");
            }
            _dispatch = JsFunction.FromValue(engine, c[0]);
            return JsValue.Undefined;
        }, 1);
    }

    /// <summary>At most 200 log lines per second per plugin; the rest is dropped (a log loop must not flood the host).</summary>
    private bool AllowLog()
    {
        var now = Environment.TickCount64;
        if (now - _logWindowStart >= 1000)
        {
            _logWindowStart = now;
            _logBudget = 200;
        }
        return _logBudget-- > 0;
    }

    private static JsValue Export(JsModuleInstance instance, string name) =>
        instance.ExportNames.Contains(name) ? instance.GetValue(name) : JsValue.Undefined;

    private static long SafeTracked(JsEngine engine)
    {
        try { return engine.GetStatistics().TrackedBytes; } catch (InvalidOperationException) { return 0; }
    }
}

public sealed class JauntPluginRuntimeFactory : IPluginRuntimeFactory
{
    public IPluginRuntime Create() => new JauntPluginRuntime();
}
