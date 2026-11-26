using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Octopus.Core.Terminals;
using Octopus.Terminal.Avalonia;
using TKCode.Terminal.Core;
using Xunit;

namespace Octopus.Terminal.Tests;

[Collection(TerminalSessionCollection.Name)]
public class FileDropTests
{
    [Theory]
    [InlineData("/Users/token/code/README.md", false, "/Users/token/code/README.md")]
    [InlineData("/Users/token/code/my file.txt", false, "'/Users/token/code/my file.txt'")]
    [InlineData("/Users/token/it's (1).png", false, "'/Users/token/it'\\''s (1).png'")]
    [InlineData("/Users/token/dir/", false, "/Users/token/dir")]
    [InlineData("/", false, "/")]
    [InlineData("/Users/token/中文/测试.txt", false, "/Users/token/中文/测试.txt")]
    [InlineData(@"C:\Users\token\code\app.cs", true, @"C:\Users\token\code\app.cs")]
    [InlineData(@"C:\Users\token\my docs\spec (1).md", true, "\"C:\\Users\\token\\my docs\\spec (1).md\"")]
    [InlineData(@"C:\Users\token\folder\", true, @"C:\Users\token\folder")]
    [InlineData(@"C:\", true, @"C:\")]
    public void Formats_single_path_safely(string input, bool isWindows, string expected) =>
        Assert.Equal(expected, TerminalFileDrop.FormatPath(input, isWindows));

    [Fact]
    public void Formats_multiple_paths_space_separated_and_skips_blanks()
    {
        var formatted = TerminalFileDrop.FormatPaths(
            ["/tmp/a.txt", "", "  ", "/tmp/my folder/", "/tmp/b&c.log"],
            isWindows: false);
        Assert.Equal("/tmp/a.txt '/tmp/my folder' '/tmp/b&c.log'", formatted);
    }

    [Fact]
    public void Detects_files_and_extracts_normalized_local_paths()
    {
        var tempDir = Directory.CreateTempSubdirectory("octopus-drop-unit-");
        try
        {
            var filePath = Path.Combine(tempDir.FullName, "sample file.txt");
            File.WriteAllText(filePath, "hi");

            var textOnly = new DataTransfer();
            textOnly.Add(DataTransferItem.CreateText("hello"));
            Assert.False(TerminalFileDrop.HasFiles(textOnly));
            Assert.Empty(TerminalFileDrop.ExtractLocalPaths(textOnly));

            var withFiles = new DataTransfer();
            withFiles.Add(DataTransferItem.CreateFile(CreateStorageItem(filePath, isFolder: false)));
            withFiles.Add(DataTransferItem.CreateFile(CreateStorageItem(tempDir.FullName + Path.DirectorySeparatorChar, isFolder: true)));
            Assert.True(TerminalFileDrop.HasFiles(withFiles));
            var paths = TerminalFileDrop.ExtractLocalPaths(withFiles);
            Assert.Equal([Path.TrimEndingDirectorySeparator(filePath), Path.TrimEndingDirectorySeparator(tempDir.FullName)], paths);
        }
        finally
        {
            try { tempDir.Delete(recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task PasteText_honors_bracketed_paste_mode()
    {
        var conn = new FakeConnection();
        await using var session = new TerminalSession(
            new LaunchSpec("x", [], null, new Dictionary<string, string>()),
            conn,
            new TerminalSize(80, 24, 0, 0),
            TerminalPalettes.Dark,
            ColorScheme.Dark,
            100,
            () => false);
        session.Start();
        using var router = new TerminalInputRouter(
            session,
            () => new MacTerminalKeymap(),
            () => Task.FromResult<string?>(null),
            () => Task.FromResult<string?>(null),
            _ => Task.CompletedTask,
            (_, _) => Task.FromResult(false), // PasteText must NOT require confirmation
            () => { });

        router.PasteText("/tmp/plain.txt");
        for (var i = 0; i < 50 && !conn.Text().Contains("/tmp/plain.txt", StringComparison.Ordinal); i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        Assert.Equal("/tmp/plain.txt", conn.Text());

        // Enable bracketed paste (DEC mode 2004).
        conn.Output.Writer.TryWrite("\u001b[?2004h"u8.ToArray());
        for (var i = 0; i < 50 && !session.ReadInputModes().BracketedPaste; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        Assert.True(session.ReadInputModes().BracketedPaste);

        conn.Clear();
        router.PasteText("'/tmp/my file.txt'");
        for (var i = 0; i < 50 && !conn.Text().Contains("/tmp/my file.txt", StringComparison.Ordinal); i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        Assert.Equal("\u001b[200~'/tmp/my file.txt'\u001b[201~", conn.Text());
    }

    private static IStorageItem CreateStorageItem(string fullPath, bool isFolder)
    {
        var typeName = isFolder
            ? "Avalonia.Platform.Storage.FileIO.BclStorageFolder"
            : "Avalonia.Platform.Storage.FileIO.BclStorageFile";
        var type = typeof(IStorageItem).Assembly.GetType(typeName, throwOnError: true)!;
        object info = isFolder ? new DirectoryInfo(fullPath) : new FileInfo(fullPath);
        return (IStorageItem)Activator.CreateInstance(type, info)!;
    }

    private sealed class FakeConnection : ITerminalConnection, ITerminalConnectionFactory
    {
        private readonly List<byte> _written = [];
        private readonly TaskCompletionSource<TerminalExit> _exit = new();
        public Channel<byte[]> Output { get; } = Channel.CreateUnbounded<byte[]>();
        public int ProcessId => 1;
        public Task<TerminalExit> Exited => _exit.Task;
        public ITerminalConnection Start(LaunchSpec spec, int columns, int rows) => this;
        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var bytes in Output.Reader.ReadAllAsync(ct).ConfigureAwait(false)) yield return bytes;
        }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken ct = default)
        {
            lock (_written) _written.AddRange(input.ToArray());
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
        public void Clear() { lock (_written) _written.Clear(); }
        public string Text() { lock (_written) return Encoding.UTF8.GetString(_written.ToArray()); }
    }
}
