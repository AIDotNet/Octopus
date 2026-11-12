namespace Octopus.Plugins.Runtime;

/// <summary>Host → plugin message kinds (design/plugins/02-运行时.md §2.3).</summary>
public enum InboundKind
{
    /// <summary>The answer to a plugin's request.</summary>
    Resolve,
    /// <summary>A plugin's request failed; <see cref="PluginInbound.Json"/> is <c>{code, message, …}</c>.</summary>
    Reject,
    /// <summary>A subscribed event; <see cref="PluginInbound.Name"/> is the subscription key.</summary>
    Event,
    /// <summary>Run a handler the plugin registered; <see cref="PluginInbound.Json"/> is the argument array. Answered by a Result or Error.</summary>
    Invoke,
    /// <summary>A timer fired; <see cref="PluginInbound.Id"/> is the timer id.</summary>
    Timer,
    /// <summary><c>activate</c> or <c>deactivate</c>. Answered by a Result or Error.</summary>
    Lifecycle,
}

/// <param name="Text">Large strings (file contents) travel here instead of inside the JSON: crossing the engine boundary is nearly free, JSON parsing is not.</param>
public readonly record struct PluginInbound(InboundKind Kind, long Id, string Name, string Json, string Text = "");

/// <summary>Plugin → host message kinds.</summary>
public enum OutboundKind
{
    /// <summary>Call a host API and wait for a Resolve or Reject with the same id.</summary>
    Request,
    /// <summary>Call a host API without an answer.</summary>
    Notify,
    /// <summary>The value an Invoke or Lifecycle call produced.</summary>
    Result,
    /// <summary>The Invoke or Lifecycle call threw; <see cref="PluginOutbound.Json"/> is <c>{code, message, stack}</c>.</summary>
    Error,
}

public readonly record struct PluginOutbound(OutboundKind Kind, long Id, string Method, string Json, string Text);

public enum PluginLogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

/// <summary>Why a runtime stopped working. After a fault the runtime only accepts disposal.</summary>
public enum PluginFaultKind
{
    /// <summary>A turn ran past its time budget.</summary>
    Timeout,
    /// <summary>Work units, stack depth, output size or another engine limit.</summary>
    Limit,
    /// <summary>The mailbox overflowed: the plugin does not keep up.</summary>
    Unresponsive,
    /// <summary>The engine failed internally.</summary>
    Internal,
}

public sealed record PluginFault(PluginFaultKind Kind, string Message);

/// <summary>Raised by <see cref="IPluginRuntime.StartAsync"/> when the plugin's code cannot be loaded.</summary>
public sealed class PluginLoadException(string message, string? source = null, int line = 0, int column = 0) : Exception(message)
{
    public string? SourceName { get; } = source;
    public int Line { get; } = line;
    public int Column { get; } = column;
}

/// <summary>What a runtime reports to its host. Called on the plugin's own loop thread: implementations must return quickly.</summary>
public interface IPluginRuntimeSink
{
    void OnOutbound(in PluginOutbound message);
    void OnLog(PluginLogLevel level, string text);
    /// <summary>A synchronous read of a snapshot the host keeps in memory. Must never block on IO or the UI thread.</summary>
    string OnSync(string method, string json);
    void OnFault(PluginFault fault);
}

/// <summary>Time and work budgets per kind of turn (design 02 §5, measured in 13 §1.6).</summary>
public sealed record TurnBudgets(
    TimeSpan Activate, long ActivateWork,
    TimeSpan Normal, long NormalWork,
    TimeSpan Render, long RenderWork)
{
    public static readonly TurnBudgets Default = new(
        TimeSpan.FromSeconds(10), 300_000_000,
        TimeSpan.FromSeconds(2), 100_000_000,
        TimeSpan.FromMilliseconds(500), 25_000_000);
}

/// <param name="MainModule">Path of the entry module relative to the package directory.</param>
public sealed record PluginRuntimeOptions(string PluginId, string PackageDirectory, string MainModule, bool UnsafeEval, TurnBudgets Budgets);

public sealed record PluginRuntimeStats(long Turns, long Mailbox, double LongestTurnMs, long TrackedBytes);

/// <summary>
/// One plugin's script engine and its turn loop. Nothing of the engine library appears here, so the runtime can be
/// replaced or moved to another process without touching the host (design/plugins/01-总体设计.md D8).
/// </summary>
public interface IPluginRuntime : IAsyncDisposable
{
    /// <summary>Creates the engine and loads the SDK and the entry module. Does not call <c>activate</c>.</summary>
    /// <exception cref="PluginLoadException">The code cannot be compiled or loaded.</exception>
    Task StartAsync(PluginRuntimeOptions options, IPluginRuntimeSink sink, CancellationToken cancellationToken = default);

    /// <summary>Queues a message; each message is one turn on the plugin's loop. Never blocks.</summary>
    void Post(in PluginInbound message);

    PluginRuntimeStats Stats { get; }
    bool IsFaulted { get; }
}

public interface IPluginRuntimeFactory
{
    IPluginRuntime Create();
}
