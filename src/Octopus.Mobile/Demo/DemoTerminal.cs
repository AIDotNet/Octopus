using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Octopus.Core.Terminals;
using Octopus.Terminal;

namespace Octopus.Mobile.Demo;

/// <summary>
/// A terminal with no process behind it: it shows a fixed screen and answers input with what <c>respond</c> returns.
/// Stands in for a session on a host until the remote protocol client exists.
/// </summary>
internal sealed class DemoTerminalConnection : ITerminalConnection
{
    private readonly Channel<byte[]> _output = Channel.CreateUnbounded<byte[]>();
    private readonly TaskCompletionSource<TerminalExit> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<string, string?> _respond;

    public DemoTerminalConnection(string screen, Func<string, string?> respond)
    {
        _respond = respond;
        _output.Writer.TryWrite(Encoding.UTF8.GetBytes(screen));
    }

    public int ProcessId => 0;
    public Task<TerminalExit> Exited => _exit.Task;
    /// <summary>The last size the view asked for.</summary>
    public (int Columns, int Rows) Size { get; private set; }
    /// <summary>Everything typed so far, for tests.</summary>
    public string Received { get { lock (_received) return _received.ToString(); } }
    private readonly StringBuilder _received = new();

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var chunk in _output.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default)
    {
        var typed = Encoding.UTF8.GetString(input.Span);
        lock (_received) _received.Append(typed);
        if (_respond(typed) is { Length: > 0 } reply)
        {
            _output.Writer.TryWrite(Encoding.UTF8.GetBytes(reply));
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask ResizeAsync(int columns, int rows, int pixelWidth, int pixelHeight)
    {
        Size = (columns, rows);
        return ValueTask.CompletedTask;
    }

    public ValueTask SignalAsync(TerminalSignal signal) => ValueTask.CompletedTask;

    public ValueTask<string?> GetForegroundProcessNameAsync() => ValueTask.FromResult<string?>(null);

    public ValueTask DisposeAsync()
    {
        _output.Writer.TryComplete();
        _exit.TrySetResult(new TerminalExit(0, null));
        return ValueTask.CompletedTask;
    }
}

internal sealed class DemoTerminalFactory(Func<DemoTerminalConnection> create) : ITerminalConnectionFactory
{
    /// <summary>The connection of the last start, for tests.</summary>
    public DemoTerminalConnection? Last { get; private set; }

    public ITerminalConnection Start(LaunchSpec spec, int columns, int rows) => Last = create();
}

/// <summary>The screens of the demo sessions.</summary>
internal static class DemoScreens
{
    private const string Dim = "\u001b[90m";
    private const string Accent = "\u001b[35m";
    private const string Green = "\u001b[32m";
    private const string Red = "\u001b[31m";
    private const string Bold = "\u001b[1m";
    private const string Reset = "\u001b[0m";

    public static string Prompt(string folder) => $"{Green}➜{Reset}  {Bold}{folder}{Reset} {Dim}git:(main){Reset} ";

    /// <summary>An agent that stopped to ask whether it may run a command.</summary>
    public static string Asking(string folder, string said, string command) => Lines(
        $"{Dim}~/code/{folder} · main{Reset}",
        "",
        $"{Accent}●{Reset} {said}",
        "",
        $"{Accent}●{Reset} {Bold}Bash{Reset}({command})",
        "",
        $"{Dim}────────────────────────────────────────{Reset}",
        $" {Bold}要运行这条命令吗？{Reset}",
        "",
        $" {Accent}❯ 1. 是{Reset}",
        "   2. 是，本次会话不再询问",
        $"   3. 否，告诉它怎么改 {Dim}(esc){Reset}") ;

    /// <summary>An agent in the middle of a turn.</summary>
    public static string Working(string folder, string doing, string detail) => Lines(
        $"{Dim}~/code/{folder} · main{Reset}",
        "",
        $"{Accent}●{Reset} {doing}",
        $"  {Dim}└ {detail}{Reset}",
        "",
        $"{Dim}  esc 中断 · 3 分 12 秒{Reset}");

    /// <summary>An agent whose turn ended with an error.</summary>
    public static string Failed(string folder, string doing, string error) => Lines(
        $"{Dim}~/code/{folder} · main{Reset}",
        "",
        $"{Accent}●{Reset} {doing}",
        $"  {Red}└ {error}{Reset}",
        "",
        "> ");

    /// <summary>A shell that ran a test watcher.</summary>
    public static string Shell(string folder) => Lines(
        Prompt(folder) + "dotnet watch test",
        $"{Dim}dotnet watch ⌚ 已启动{Reset}",
        $"  已通过 HandshakeTests.Vectors {Dim}[12 ms]{Reset}",
        $"  已通过 RecordTests.NonceCounter {Dim}[3 ms]{Reset}",
        $"  {Red}失败{Reset} PairingTests.SasCommitment {Dim}[41 ms]{Reset}",
        "    应为 482913，实际为 482931",
        "",
        $"{Dim}共 38 个：通过 37，失败 1{Reset}") + "\r\n" + Prompt(folder);

    public static string Lines(params string[] lines) => string.Join("\r\n", lines);
}
