using System.Text;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Octopus.Core.Terminals;
using Octopus.Terminal;
using Octopus.Terminal.Avalonia;
using TKCode.Terminal.Avalonia;
using Xunit;

namespace Octopus.App.Tests;

public class TerminalScrollTests
{
    [AvaloniaFact]
    public async Task History_is_accessible_by_wheel_scrollbar_and_keyboard_with_mouse_reporting_override()
    {
        var factory = new OutputFactory();
        var terminal = new TerminalControl();
        var window = new Window { Width = 760, Height = 320, Content = terminal };
        window.Show();
        terminal.Start(new LaunchSpec("test-shell", [], null, new Dictionary<string, string>()), factory);
        try
        {
            var output = "已成功生成。\r\n0 个警告\r\n0 个错误\r\n" +
                string.Concat(Enumerable.Range(0, 100).Select(i => $"conversation-{i:D3}\r\n"));
            // Every UTF-8 character crosses transport chunk boundaries.
            foreach (var b in Encoding.UTF8.GetBytes(output)) factory.Connection.Output.Writer.TryWrite(new[] { b });
            Assert.True(Frames.Until(() => terminal.ReadScreenText().Contains("conversation-099")));
            var view = terminal.GetVisualDescendants().OfType<TerminalView>().Single();
            var bar = terminal.GetVisualDescendants().OfType<ScrollBar>().Single();
            Assert.True(Frames.Until(() => bar.IsVisible && bar.Maximum > 0));
            Assert.Equal(bar.Maximum, bar.Value);
            Assert.DoesNotContain("conversation-000", VisibleText(view));
            var point = view.TranslatePoint(new Point(100, 100), window)!.Value;

            window.MouseWheel(point, new Vector(0, 1));
            Assert.True(Frames.Until(() => bar.Value < bar.Maximum));
            bar.Value = 0;
            Assert.True(Frames.Until(() => VisibleText(view).Contains("已成功生成。")));
            Assert.Contains("0 个警告", VisibleText(view));
            Assert.Contains("0 个错误", VisibleText(view));
            Assert.DoesNotContain('\uFFFD', terminal.ReadScreenText());

            terminal.FocusTerminal();
            window.KeyPress(Key.PageDown, RawInputModifiers.Shift, PhysicalKey.PageDown, null);
            Assert.True(Frames.Until(() => bar.Value > 0));
            window.KeyPress(Key.Home, RawInputModifiers.Shift, PhysicalKey.Home, null);
            Assert.True(Frames.Until(() => bar.Value == 0));
            window.KeyPress(Key.End, RawInputModifiers.Shift, PhysicalKey.End, null);
            Assert.True(Frames.Until(() => bar.Value == bar.Maximum));
            window.KeyPress(Key.PageUp, RawInputModifiers.Shift, PhysicalKey.PageUp, null);
            Assert.True(Frames.Until(() => bar.Value < bar.Maximum));

            factory.Connection.Output.Writer.TryWrite("\u001b[?1000h\u001b[?1006h"u8.ToArray());
            factory.Connection.Output.Writer.TryWrite("MOUSE-READY"u8.ToArray());
            Assert.True(Frames.Until(() => terminal.ReadScreenText().Contains("MOUSE-READY")));
            bar.Value = bar.Maximum;
            Frames.Pump();
            window.MouseWheel(point, new Vector(0, 1), RawInputModifiers.Shift);
            Assert.True(Frames.Until(() => bar.Value < bar.Maximum));
            window.MouseWheel(point, new Vector(0, 1));
            Assert.True(Frames.Until(() => factory.Connection.Writes.Any(s => s.StartsWith("\u001b[<64;"))));

            factory.Connection.Output.Writer.TryWrite("\u001b[?1049hALT-SCREEN"u8.ToArray());
            Assert.True(Frames.Until(() => VisibleText(view).Contains("ALT-SCREEN")));
            Assert.False(bar.IsVisible);
            factory.Connection.Output.Writer.TryWrite("\u001b[?1049l"u8.ToArray());
            Assert.True(Frames.Until(() => bar.IsVisible));
            bar.Value = bar.Maximum;
            Frames.Pump();
        }
        finally
        {
            await terminal.CloseAsync();
            window.Close();
        }
    }

    private static string VisibleText(TerminalView view) => view.Snapshot is not { } frame ? "" :
        string.Join('\n', frame.Rows.Select(row => string.Concat(Enumerable.Range(0, row.Cells.Length).Select(row.TextAt))));

    private sealed class OutputFactory : ITerminalConnectionFactory
    {
        public OutputConnection Connection { get; } = new();
        public ITerminalConnection Start(LaunchSpec spec, int columns, int rows) => Connection;
    }

    private sealed class OutputConnection : ITerminalConnection
    {
        private readonly TaskCompletionSource<TerminalExit> _exit = new();
        public Channel<byte[]> Output { get; } = Channel.CreateUnbounded<byte[]>();
        public System.Collections.Concurrent.ConcurrentQueue<string> Writes { get; } = new();
        public int ProcessId => 1;
        public Task<TerminalExit> Exited => _exit.Task;
        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var bytes in Output.Reader.ReadAllAsync(cancellationToken)) yield return bytes;
        }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default)
        {
            Writes.Enqueue(Encoding.UTF8.GetString(input.Span));
            return ValueTask.CompletedTask;
        }
        public ValueTask ResizeAsync(int columns, int rows, int pixelWidth, int pixelHeight) => ValueTask.CompletedTask;
        public ValueTask SignalAsync(TerminalSignal signal) => ValueTask.CompletedTask;
        public ValueTask<string?> GetForegroundProcessNameAsync() => ValueTask.FromResult<string?>(null);
        public ValueTask DisposeAsync()
        {
            Output.Writer.TryComplete();
            _exit.TrySetResult(new TerminalExit(0, null));
            return ValueTask.CompletedTask;
        }
    }
}
