using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Octopus.App.Terminals;
using Octopus.Core.Terminals;
using Octopus.Terminal;
using Octopus.Terminal.Avalonia;
using TKCode.Arc;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>Renders the terminal's search bar and the paste confirmation (light and dark) to <c>bin/.../shots</c>.</summary>
public class TerminalScreenshots
{
    private static readonly string s_dir = Path.Combine(AppContext.BaseDirectory, "shots");

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
        "\u001b[32m~/code/octopus\u001b[0m $ dotnet build\r\n" +
        "  Determining projects to restore...\r\n" +
        "src/App/Program.cs(12,5): \u001b[31merror\u001b[0m CS1002: ; expected\r\n" +
        "src/App/Shell.cs(40,9): \u001b[33mwarning\u001b[0m CS8618: field must contain a non-null value\r\n" +
        "src/App/Tabs.cs(8,1): \u001b[31merror\u001b[0m CS0103: the name 'tab' does not exist\r\n" +
        "Docs: https://learn.microsoft.com/dotnet/csharp/language-reference/compiler-messages/cs0103\r\n" +
        "\u001b[32m~/code/octopus\u001b[0m $ git log --oneline -3\r\n" +
        "a1b2c3d Fix error handling in the tab strip\r\n" +
        "9f8e7d6 Add search\r\n" +
        "\u001b[32m~/code/octopus\u001b[0m $ ";

    [AvaloniaFact]
    public void Render_search_bar_and_paste_dialog()
    {
        Directory.CreateDirectory(s_dir);
        foreach (var dark in new[] { false, true })
        {
            ArcTheme.Apply(t => t.Appearance(dark ? ArcAppearance.Dark : ArcAppearance.Light));
            var theme = dark ? "dark" : "light";

            var terminal = new TerminalControl { FontSize = 13 };
            var bar = new TerminalSearchBar();
            bar.Attach(terminal);
            terminal.SearchBar = bar;
            var window = new Window { Width = 780, Height = 460, Content = terminal };
            window.Show();
            terminal.Start(new LaunchSpec("/bin/sh", [], null, new Dictionary<string, string>()), new ScriptedFactory(Sample));
            WaitFor(() => terminal.ReadScreenText().Contains("Add search"));

            terminal.OpenSearch();
            var field = bar.FindControl<TextBox>("Field")!;
            field.Text = "error";
            WaitFor(() => terminal.SearchTotal > 0);
            Frames.Pump();
            Frames.Save(window, Path.Combine(s_dir, $"terminal-search-{theme}.png"));
            Assert.Equal(3, terminal.SearchTotal);

            terminal.SearchNext();
            WaitFor(() => terminal.SearchPosition == 2);
            Frames.Save(window, Path.Combine(s_dir, $"terminal-search-next-{theme}.png"));

            field.Text = "zzzz";
            WaitFor(() => terminal.SearchTotal == 0);
            Frames.Save(window, Path.Combine(s_dir, $"terminal-search-none-{theme}.png"));
            terminal.CloseSearch();

            var preview = TerminalPasteGuard.Preview("cd ~/code\nrm -rf build\ndotnet build\ndotnet test\ngit status\necho done\necho more\necho even more\n");
            _ = PasteConfirmation.ShowAsync(terminal, preview);
            Frames.Pump();
            Frames.Save(window, Path.Combine(s_dir, $"terminal-paste-confirm-{theme}.png"));
            window.Close();
        }
    }

    private static void WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
        Frames.Pump();
    }
}
