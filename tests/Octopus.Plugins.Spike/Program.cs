using System.Diagnostics;
using System.Text;
using Jaunt;

// P0 checks for the plugin runtime on Jaunt (design/plugins/10-分期与风险.md §1, design/plugins/02-运行时.md).
// Runs the turn-based message loop the design depends on and prints what it measured.
// Exit code 0 when every check passed. Usage: dotnet run -c Release --project tests/Octopus.Plugins.Spike
internal static class Program
{
    private static readonly List<(string Name, bool Ok, string Detail)> Results = [];

    public static int Main()
    {
        Console.WriteLine($"Jaunt {typeof(JsEngine).Assembly.GetName().Version} · .NET {Environment.Version} · {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        Console.WriteLine();

        Run("1 prelude under Hardened removes the native functions", CheckPrelude);
        Run("2 a promise settles across turns (host request → later resolve)", CheckCrossTurnPromise);
        Run("3 turn cost: 50,000 host round trips = 100,000 turns", CheckTurnThroughput);
        Run("4 JSON bridge: 10 KB and 1 MB payloads both ways", CheckBridgeCost);
        Run("4b where the large-payload time goes", CheckPayloadBreakdown);
        Run("4c per-turn timing of one 1 MB host call", CheckTurnBreakdown);
        Run("5 runaway handler: timeout faults the engine, rebuild time", CheckTimeoutAndRebuild);
        Run("6 top-level await in the entry module is rejected before running", CheckTopLevelAwait);
        Run("7 work units per second (to size MaxInstructions)", CheckWorkUnits);
        Run("8 engine options: local time zone, Intl", CheckEnvironment);
        Run("9 modern syntax a bundler emits loads under Hardened", CheckModernSyntax);

        Console.WriteLine();
        var failed = Results.Count(r => !r.Ok);
        Console.WriteLine(failed == 0 ? "P0 RUNTIME OK" : $"P0 RUNTIME: {failed} check(s) failed");
        return failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Func<string> check)
    {
        string detail;
        bool ok;
        try
        {
            detail = check();
            ok = true;
        }
        catch (Exception ex)
        {
            detail = $"{ex.GetType().Name}: {ex.Message}";
            ok = false;
        }
        Results.Add((name, ok, detail));
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}");
        foreach (var line in detail.Split('\n'))
        {
            Console.WriteLine($"       {line}");
        }
    }

    // ------------------------------------------------------------------ checks

    private static string CheckPrelude()
    {
        using var host = PluginHost.Start(Scripts.EchoPlugin);
        var leaked = host.Engine.Evaluate("typeof globalThis.__oct_post + ',' + typeof globalThis.__oct_log").AsString();
        if (leaked != "undefined,undefined")
        {
            throw new InvalidOperationException($"natives still visible to scripts: {leaked}");
        }
        var frozen = host.Engine.Evaluate("Object.isFrozen(Array.prototype)").AsBoolean();
        var evalBlocked = host.Engine.Evaluate("(() => { try { eval('1'); return false; } catch (e) { return e instanceof EvalError; } })()").AsBoolean();
        if (!frozen || !evalBlocked)
        {
            throw new InvalidOperationException($"Hardened not in effect: frozen={frozen}, evalBlocked={evalBlocked}");
        }
        return "natives captured by the SDK module and deleted from globalThis; intrinsics frozen; eval throws EvalError";
    }

    private static string CheckCrossTurnPromise()
    {
        using var host = PluginHost.Start(Scripts.EchoPlugin);
        var result = host.CallThroughHost("echo", "\"hello\"", out var turns);
        if (result != "\"hello\"")
        {
            throw new InvalidOperationException($"unexpected result {result}");
        }
        return $"handler awaited a host request; the reply resolved it in a later turn; result arrived after {turns} turns";
    }

    private static string CheckTurnThroughput()
    {
        using var host = PluginHost.Start(Scripts.EchoPlugin);
        host.CallThroughHost("echo", "1", out _); // warm up
        GC.Collect();
        var managedBefore = GC.GetTotalMemory(true);
        var trackedBefore = host.Engine.GetStatistics().TrackedBytes;
        const int calls = 50_000;
        var sw = Stopwatch.StartNew();
        var trackedAt = new List<long>();
        for (var i = 1; i <= calls; i++)
        {
            host.CallThroughHost("echo", "{\"n\":1}", out _);
            if (i % 10_000 == 0)
            {
                trackedAt.Add(host.Engine.GetStatistics().TrackedBytes);
            }
        }
        sw.Stop();
        GC.Collect();
        var managedAfter = GC.GetTotalMemory(true);
        var turns = calls * 2;
        var perTurnUs = sw.Elapsed.TotalMicroseconds / turns;
        return $"{turns:N0} turns in {sw.ElapsedMilliseconds:N0} ms → {perTurnUs:F1} µs per turn\n" +
               $"managed heap after full GC: {Mb(managedBefore)} → {Mb(managedAfter)}\n" +
               $"Jaunt TrackedBytes (cumulative, never decreases): {Mb(trackedBefore)} → {string.Join(" → ", trackedAt.Select(Mb))}";
    }

    private static string CheckBridgeCost()
    {
        using var host = PluginHost.Start(Scripts.EchoPlugin);
        var sb = new StringBuilder();
        foreach (var size in new[] { 10 * 1024, 1024 * 1024 })
        {
            var json = BuildJson(size);
            host.CallThroughHost("echo", json, out _);
            const int reps = 20;
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < reps; i++)
            {
                var back = host.CallThroughHost("echo", json, out _);
                if (back.Length != json.Length)
                {
                    throw new InvalidOperationException($"payload changed size: {json.Length} → {back.Length}");
                }
            }
            sw.Stop();
            sb.AppendLine($"{Kb(json.Length)} JSON: {sw.Elapsed.TotalMilliseconds / reps:F2} ms per host call (parse + stringify twice each way)");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Times JSON.parse, JSON.stringify and a plain string crossing the native boundary, at several sizes.</summary>
    private static string CheckPayloadBreakdown()
    {
        using var engine = new JsEngine(new JsEngineOptions { Profile = JsProfile.Hardened, Limits = new JsLimits { MaxStringLength = 64 * 1024 * 1024 } });
        string? received = null;
        engine.SetFunction("__take", (ref JsCallContext c) => { received = c.GetString(0); return JsValue.Undefined; }, 1);
        engine.Execute("""
            globalThis.parse = s => { globalThis.obj = JSON.parse(s); return 0; };
            globalThis.stringify = () => JSON.stringify(globalThis.obj).length;
            globalThis.passIn = s => s.length;
            globalThis.passOut = n => { __take('x'.repeat(n)); return 0; };
            globalThis.concatOut = n => { let s = ''; while (s.length < n) s += 'abcdefghij'; __take(s); return 0; };
            """);
        var parse = engine.GetFunction("parse");
        var stringify = engine.GetFunction("stringify");
        var passIn = engine.GetFunction("passIn");
        var passOut = engine.GetFunction("passOut");
        var concatOut = engine.GetFunction("concatOut");

        var sb = new StringBuilder("size      JSON.parse  JSON.stringify  string in  string out (repeat)  string out (+= 10 chars)\n");
        foreach (var size in new[] { 10 * 1024, 100 * 1024, 1024 * 1024 })
        {
            var json = BuildJson(size);
            var text = new string('y', size);
            double Time(Action a, int reps = 5)
            {
                a();
                var sw = Stopwatch.StartNew();
                for (var i = 0; i < reps; i++) a();
                return sw.Elapsed.TotalMilliseconds / reps;
            }
            var tParse = Time(() => engine.Invoke(parse, JsValue.From(json)));
            var tStringify = Time(() => engine.Invoke(stringify));
            var tIn = Time(() => engine.Invoke(passIn, JsValue.From(text)));
            var tOut = Time(() => engine.Invoke(passOut, JsValue.From(size)));
            var tConcat = Time(() => engine.Invoke(concatOut, JsValue.From(size)));
            if (received is null || received.Length < size) throw new InvalidOperationException("string did not cross the boundary");
            sb.AppendLine($"{Kb(size),-9} {tParse,8:F2} ms  {tStringify,11:F2} ms  {tIn,6:F3} ms  {tOut,13:F3} ms  {tConcat,18:F2} ms");
        }
        return sb.ToString().TrimEnd();
    }

    private static string CheckTurnBreakdown()
    {
        var json = BuildJson(1024 * 1024);
        var sb = new StringBuilder();
        foreach (var (label, budget) in new (string, TimeSpan?)[] { ("turn with a 2 s timeout", TimeSpan.FromSeconds(2)), ("turn without a timeout", null) })
        {
            using var host = PluginHost.Start(Scripts.EchoPlugin);
            host.TurnBudget = budget;
            host.CallThroughHost("echo", json, out _);
            host.TurnTimes.Clear();
            var sw = Stopwatch.StartNew();
            host.CallThroughHost("echo", json, out _);
            sw.Stop();
            sb.AppendLine($"{label}: {sw.Elapsed.TotalMilliseconds:F0} ms total; turns: {string.Join(", ", host.TurnTimes.Select(t => $"{t:F0} ms"))}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string CheckTimeoutAndRebuild()
    {
        var host = PluginHost.Start(Scripts.EchoPlugin);
        var sw = Stopwatch.StartNew();
        try
        {
            host.Invoke("spin", "null", TimeSpan.FromMilliseconds(200));
            throw new InvalidOperationException("the runaway handler was not stopped");
        }
        catch (JsLimitExceededException ex) when (ex.Kind == JsLimitKind.Timeout)
        {
        }
        var stoppedAfter = sw.ElapsedMilliseconds;
        if (!host.Engine.IsFaulted)
        {
            throw new InvalidOperationException("engine not faulted after the timeout");
        }
        host.Dispose();

        const int rebuilds = 20;
        var rebuild = Stopwatch.StartNew();
        for (var i = 0; i < rebuilds; i++)
        {
            using var fresh = PluginHost.Start(Scripts.EchoPlugin);
            fresh.CallThroughHost("echo", "1", out _);
        }
        rebuild.Stop();
        return $"200 ms turn budget stopped an infinite loop after {stoppedAfter} ms; engine faulted as documented\n" +
               $"dispose + new engine + load SDK and plugin + activate + first call: {rebuild.Elapsed.TotalMilliseconds / rebuilds:F1} ms (mean of {rebuilds}, compiled modules shared)";
    }

    private static string CheckTopLevelAwait()
    {
        try
        {
            using var host = PluginHost.Start("await Promise.resolve(); export function activate() {}");
            throw new InvalidOperationException("module with top-level await was loaded synchronously");
        }
        catch (JsAsyncRequiredException ex)
        {
            return $"JsAsyncRequiredException: {ex.Message}";
        }
    }

    private static string CheckWorkUnits()
    {
        using var host = PluginHost.Start(Scripts.EchoPlugin);
        host.Invoke("count", "1000", TimeSpan.FromSeconds(5));
        const int n = 20_000_000;
        var sw = Stopwatch.StartNew();
        host.Invoke("count", n.ToString(), TimeSpan.FromSeconds(30));
        sw.Stop();
        var units = host.Engine.GetStatistics().LastWorkUnits;
        var perSecond = units / sw.Elapsed.TotalSeconds;
        return $"tight loop of {n:N0} iterations: {sw.ElapsedMilliseconds:N0} ms, {units:N0} work units → ~{perSecond / 1e6:F0} M units/s\n" +
               $"a 2 s turn ≈ {perSecond * 2 / 1e6:F0} M units at this speed";
    }

    private static string CheckEnvironment()
    {
        using var host = PluginHost.Start(Scripts.EchoPlugin);
        var jsOffset = (int)host.Engine.Evaluate("new Date().getTimezoneOffset()").AsNumber();
        var expected = -(int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes;
        if (jsOffset != expected)
        {
            throw new InvalidOperationException($"Date offset {jsOffset}, local {expected}");
        }
        var intl = host.Engine.Evaluate("typeof Intl").AsString();
        var formatted = intl == "object"
            ? host.Engine.Evaluate("new Intl.NumberFormat('zh-CN').format(1234567.891)").AsString()
            : "(no Intl)";
        return $"Date uses the local zone (offset {jsOffset} min); ICU {JsEngine.IcuVersion ?? "unavailable"}; Intl: {intl}, zh-CN number: {formatted}";
    }

    private static string CheckModernSyntax()
    {
        using var host = PluginHost.Start(Scripts.ModernPlugin);
        var result = host.CallThroughHost("run", "null", out _);
        if (result != "\"ok:6:3:b,c:true\"")
        {
            throw new InvalidOperationException($"unexpected result {result}");
        }
        return "classes with private fields, static blocks, async generators, for await, destructuring, ?. and ??, Map/Set, structuredClone-free JSON round trip";
    }

    // ------------------------------------------------------------------ helpers

    private static string BuildJson(int approxBytes)
    {
        var sb = new StringBuilder("{\"rows\":[");
        var i = 0;
        while (sb.Length < approxBytes)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":").Append(i).Append(",\"name\":\"item ").Append(i).Append("\",\"tags\":[\"a\",\"b\"],\"ok\":true}");
            i++;
        }
        return sb.Append("]}").ToString();
    }

    private static string Mb(long bytes) => $"{bytes / 1024.0 / 1024.0:F1} MB";
    private static string Kb(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:F0} MB" : $"{bytes / 1024.0:F0} KB";
}

/// <summary>
/// The smallest version of the design's plugin host: one hardened engine, the `octopus` SDK module, one plugin module,
/// and a synchronous loop that runs one turn per message. Messages the plugin posts are collected during a turn and
/// answered by the host in the next ones.
/// </summary>
internal sealed class PluginHost : IDisposable
{
    private static readonly JsModule Sdk = JsCompiler.CompileModule(Scripts.Sdk, new JsCompileOptions { SourceName = "octopus" });

    private readonly List<Outbound> _outbox;
    private readonly JsFunction _dispatch;
    private int _nextCall = 1;

    public JsEngine Engine { get; }

    private PluginHost(JsEngine engine, JsFunction dispatch, List<Outbound> outbox)
    {
        Engine = engine;
        _dispatch = dispatch;
        _outbox = outbox;
    }

    public static PluginHost Start(string pluginSource)
    {
        var engine = new JsEngine(new JsEngineOptions
        {
            Profile = JsProfile.Hardened,
            TimeZone = TimeZoneInfo.Local,
            Limits = new JsLimits { MaxStackDepth = 2000, MaxStringLength = 64 * 1024 * 1024 },
        });
        var outbox = new List<Outbound>();
        engine.SetFunction("__oct_post", (ref JsCallContext c) =>
        {
            outbox.Add(new Outbound(c.GetString(0)!, c.GetInt32(1), c.GetString(2) ?? "", c.GetString(3) ?? ""));
            return JsValue.Undefined;
        }, 4);
        engine.SetFunction("__oct_log", (ref JsCallContext c) => JsValue.Undefined, 2);

        var sdk = engine.LoadModule(Sdk, id: "octopus");
        var dispatch = sdk.GetFunction("__dispatch");
        var plugin = JsCompiler.CompileModule(pluginSource, new JsCompileOptions { SourceName = "plugin://spike/main.js" });
        var resolver = new JsDictionaryModuleResolver().Add("octopus", Sdk);
        var main = engine.LoadModule(plugin, resolver, "plugin://spike/main.js");

        var activate = main.GetFunction("activate");
        using (var execution = engine.BeginExecution(new JsExecutionOptions { Timeout = TimeSpan.FromSeconds(10) }))
        {
            execution.Invoke(activate);
            execution.Complete();
        }
        return new PluginHost(engine, dispatch, outbox);
    }

    /// <summary>Asks the plugin to run a handler and keeps answering its host requests until the handler's result arrives.</summary>
    public string CallThroughHost(string handler, string json, out int turns)
    {
        var call = _nextCall++;
        turns = 0;
        var inbox = new Queue<(string Kind, int Id, string Name, string Json)>();
        inbox.Enqueue(("invoke", call, handler, json));
        while (inbox.Count > 0)
        {
            var (kind, id, name, payload) = inbox.Dequeue();
            Turn(kind, id, name, payload, TurnBudget);
            turns++;
            foreach (var message in Drain())
            {
                switch (message.Kind)
                {
                    case "request" when message.Method == "host.echo":
                        inbox.Enqueue(("resolve", message.Id, "", message.Json));
                        break;
                    case "request":
                        inbox.Enqueue(("reject", message.Id, "", $"unknown method {message.Method}"));
                        break;
                    case "result" when message.Id == call:
                        return message.Json;
                    case "error" when message.Id == call:
                        throw new InvalidOperationException($"handler failed: {message.Json}");
                }
            }
        }
        throw new InvalidOperationException("the call ended without a result");
    }

    /// <summary>One turn that runs a handler without answering host requests (for budget checks).</summary>
    public void Invoke(string handler, string json, TimeSpan budget)
    {
        Turn("invoke", _nextCall++, handler, json, budget);
        Drain();
    }

    /// <summary>Overrides the per-turn timeout of <see cref="CallThroughHost"/>; null runs turns without one.</summary>
    public TimeSpan? TurnBudget { get; set; } = TimeSpan.FromSeconds(2);
    public List<double> TurnTimes { get; } = [];

    /// <summary>One turn: a root execution with its own budget that delivers one message to the SDK's dispatcher.</summary>
    private void Turn(string kind, int id, string name, string json, TimeSpan? budget)
    {
        var started = Stopwatch.GetTimestamp();
        using (var execution = Engine.BeginExecution(new JsExecutionOptions { Timeout = budget }))
        {
            execution.Invoke(_dispatch, JsValue.From(kind), JsValue.From(id), JsValue.From(name), JsValue.From(json));
            execution.Complete();
        }
        TurnTimes.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private List<Outbound> Drain()
    {
        var copy = _outbox.ToList();
        _outbox.Clear();
        return copy;
    }

    public void Dispose() => Engine.Dispose();
}

internal readonly record struct Outbound(string Kind, int Id, string Method, string Json);

internal static class Scripts
{
    /// <summary>The `octopus` module: captures the natives, deletes them from globalThis, owns the request table.</summary>
    public const string Sdk = """
        const post = globalThis.__oct_post;
        const log = globalThis.__oct_log;
        delete globalThis.__oct_post;
        delete globalThis.__oct_log;

        let nextRequest = 1;
        const pending = new Map();
        const handlers = new Map();

        export function request(method, payload) {
          const id = nextRequest++;
          return new Promise((resolve, reject) => {
            pending.set(id, { resolve, reject });
            post('request', id, method, payload === undefined ? '' : JSON.stringify(payload));
          });
        }

        export function handle(name, fn) { handlers.set(name, fn); }
        export function writeLog(text) { log('info', String(text)); }

        export function __dispatch(kind, id, name, json) {
          switch (kind) {
            case 'resolve': { const p = pending.get(id); pending.delete(id); p?.resolve(json === '' ? undefined : JSON.parse(json)); return; }
            case 'reject':  { const p = pending.get(id); pending.delete(id); p?.reject(new Error(json)); return; }
            case 'invoke': {
              const fn = handlers.get(name);
              if (!fn) { post('error', id, '', 'no handler ' + name); return; }
              Promise.resolve()
                .then(() => fn(json === '' ? undefined : JSON.parse(json)))
                .then(r => post('result', id, '', JSON.stringify(r ?? null)),
                      e => post('error', id, '', String(e?.message ?? e)));
              return;
            }
          }
        }
        """;

    public const string EchoPlugin = """
        import { request, handle } from 'octopus';

        export async function activate() {
          handle('echo', async value => await request('host.echo', value));
          handle('spin', () => { for (;;) {} });
          handle('count', n => { let s = 0; for (let i = 0; i < n; i++) s += i; return s; });
        }
        """;

    public const string ModernPlugin = """
        import { request, handle } from 'octopus';

        class Counter {
          #count = 0;
          static instances = 0;
          static { Counter.instances = 0; }
          constructor() { Counter.instances++; }
          bump(by = 1) { this.#count += by; return this; }
          get value() { return this.#count; }
        }

        async function* numbers() { for (const n of [1, 2, 3]) yield await request('host.echo', n); }

        export function activate() {
          handle('run', async () => {
            const c = new Counter();
            for await (const n of numbers()) c.bump(n);
            const { a, ...rest } = { a: 1, b: 2, c: 3 };
            const seen = new Set(Object.keys(rest));
            const map = new Map([['k', { deep: { value: Counter.instances } }]]);
            const fallback = map.get('missing')?.deep?.value ?? Counter.instances * 3;
            const roundTrip = JSON.parse(JSON.stringify({ ok: true }));
            return `ok:${c.value}:${fallback}:${[...seen].join(',')}:${roundTrip.ok}`;
          });
        }
        """;
}
