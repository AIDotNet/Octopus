using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Octopus.App.Terminals;
using Octopus.Core.Terminals;
using Octopus.Terminal;
using Octopus.Terminal.Avalonia;
using Xunit;

namespace Octopus.App.Tests;

public class TerminalSearchBarTests
{
    private sealed class ScriptedConnection(string output) : ITerminalConnection
    {
        private readonly TaskCompletionSource<TerminalExit> _exit = new();
        public int ProcessId => 1;
        public Task<TerminalExit> Exited => _exit.Task;

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return Encoding.UTF8.GetBytes(output);
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int columns, int rows, int pixelWidth, int pixelHeight) => ValueTask.CompletedTask;
        public ValueTask SignalAsync(TerminalSignal signal) => ValueTask.CompletedTask;
        public ValueTask<string?> GetForegroundProcessNameAsync() => ValueTask.FromResult<string?>(null);

        public ValueTask DisposeAsync()
        {
            _exit.TrySetResult(new TerminalExit(0, null));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedFactory(string output) : ITerminalConnectionFactory
    {
        public ITerminalConnection Start(LaunchSpec spec, int columns, int rows) => new ScriptedConnection(output);
    }

    private const string Sample =
        "src/App/Program.cs(12,5): error CS1002: ; expected\r\n" +
        "src/App/Shell.cs(40,9): warning CS8618: field must contain a non-null value\r\n" +
        "src/App/Tabs.cs(8,1): error CS0103: the name 'tab' does not exist\r\n" +
        "a1b2c3d Fix error handling in the tab strip\r\n" +
        "9f8e7d6 Add search\r\n";

    [AvaloniaFact]
    public async Task Search_bar_finds_steps_through_and_clears_matches()
    {
        var terminal = new TerminalControl { FontSize = 13 };
        var bar = new TerminalSearchBar();
        bar.Attach(terminal);
        terminal.SearchBar = bar;
        var window = new Window { Width = 780, Height = 460, Content = terminal };
        window.Show();
        terminal.Start(new LaunchSpec("/bin/sh", [], null, new Dictionary<string, string>()), new ScriptedFactory(Sample));
        try
        {
            Assert.True(Frames.Until(() => terminal.ReadScreenText().Contains("Add search")));

            terminal.OpenSearch();
            var field = bar.FindControl<TextBox>("Field")!;
            field.Text = "error";
            Assert.True(Frames.Until(() => terminal.SearchTotal == 3));

            terminal.SearchNext();
            Assert.True(Frames.Until(() => terminal.SearchPosition == 2));

            field.Text = "zzzz";
            Assert.True(Frames.Until(() => terminal.SearchTotal == 0));

            terminal.CloseSearch();
            Frames.Pump();
        }
        finally
        {
            await terminal.CloseAsync();
            window.Close();
        }
    }
}
