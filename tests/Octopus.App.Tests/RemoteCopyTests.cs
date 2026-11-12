using Octopus.App.Services;
using Octopus.Platform;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>A save of an open file is written to the host only after the host's content was checked against the last one seen.</summary>
public class RemoteCopyTests
{
    /// <summary>A host with one file: what it holds, and every write made to it.</summary>
    private sealed class Host(string? content) : IProcessRunner
    {
        public string? Content { get; set; } = content;
        public List<byte[]> Writes { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment = null, string? workingDirectory = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            var command = arguments.Last();
            if (command.StartsWith("if [ -e", StringComparison.Ordinal)) return Ok(Content is null ? "no\n" : "yes\n");
            if (command.StartsWith("wc -c", StringComparison.Ordinal)) return Ok(Content is null ? "" : System.Text.Encoding.UTF8.GetByteCount(Content) + "\n");
            if (command.StartsWith("base64", StringComparison.Ordinal)) return Ok(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Content ?? "")) + "\n");
            return Ok("");
        }

        public Task<ProcessResult> RunWithInputAsync(string fileName, IEnumerable<string> arguments, ReadOnlyMemory<byte> input, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            Writes.Add(input.ToArray());
            Content = System.Text.Encoding.UTF8.GetString(input.Span);
            return Ok("");
        }

        private static Task<ProcessResult> Ok(string output) => Task.FromResult(new ProcessResult(0, output, "", false));
    }

    private static (RemoteCopy Copy, Host Host, FakeDialogService Dialogs) Open(string hostContent, string lastSeen)
    {
        var host = new Host(hostContent);
        var dialogs = new FakeDialogService();
        var files = new RemoteFiles(host, new TestPaths(), Path.Combine(Path.GetTempPath(), "octopus-rc-" + Guid.NewGuid().ToString("N")));
        var copy = new RemoteCopy(files, "prod", "/srv/notes.md", System.Text.Encoding.UTF8.GetBytes(lastSeen), dialogs);
        return (copy, host, dialogs);
    }

    private sealed class TestPaths : IAppPaths
    {
        public string DataDirectory => Path.GetTempPath();
        public string CacheDirectory => Path.GetTempPath();
    }

    [Fact]
    public async Task An_unchanged_host_file_is_written_without_a_question()
    {
        var (copy, host, dialogs) = Open(hostContent: "old", lastSeen: "old");

        var result = await copy.WriteAsync("new"u8.ToArray());

        Assert.Equal(RemoteWriteOutcome.Written, result.Outcome);
        Assert.Equal(0, dialogs.Asked);
        Assert.Equal("new", host.Content);
    }

    [Fact]
    public async Task A_file_changed_on_the_host_asks_first_and_is_kept_when_the_answer_is_no()
    {
        var (copy, host, dialogs) = Open(hostContent: "changed on the server", lastSeen: "old");
        dialogs.Answer = false;

        var result = await copy.WriteAsync("mine"u8.ToArray());

        Assert.Equal(RemoteWriteOutcome.Declined, result.Outcome);
        Assert.Equal(1, dialogs.Asked);
        Assert.Empty(host.Writes);
        Assert.Equal("changed on the server", host.Content);
    }

    [Fact]
    public async Task A_file_changed_on_the_host_is_overwritten_when_the_answer_is_yes()
    {
        var (copy, host, dialogs) = Open(hostContent: "changed on the server", lastSeen: "old");
        dialogs.Answer = true;

        var result = await copy.WriteAsync("mine"u8.ToArray());

        Assert.Equal(RemoteWriteOutcome.Written, result.Outcome);
        Assert.Equal("mine", host.Content);
    }

    [Fact]
    public async Task A_file_deleted_on_the_host_is_created_again_after_a_question()
    {
        var (copy, host, dialogs) = Open(hostContent: null, lastSeen: "old");
        dialogs.Answer = true;

        var result = await copy.WriteAsync("mine"u8.ToArray());

        Assert.Equal(RemoteWriteOutcome.Written, result.Outcome);
        Assert.Equal(1, dialogs.Asked);
        Assert.Equal("mine", host.Content);
    }

    [Fact]
    public async Task A_write_that_the_host_refuses_says_why_and_the_copy_keeps_its_last_seen_content()
    {
        var host = new FailingHost();
        var dialogs = new FakeDialogService();
        var copy = new RemoteCopy(new RemoteFiles(host, new TestPaths(), Path.Combine(Path.GetTempPath(), "octopus-rc-" + Guid.NewGuid().ToString("N"))), "prod", "/srv/notes.md", "old"u8.ToArray(), dialogs);

        var result = await copy.WriteAsync("mine"u8.ToArray());

        Assert.Equal(RemoteWriteOutcome.Failed, result.Outcome);
        Assert.Contains("Permission denied", result.Error);
    }

    /// <summary>The host exists and is unchanged, but it refuses the write.</summary>
    private sealed class FailingHost : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment = null, string? workingDirectory = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            var command = arguments.Last();
            if (command.StartsWith("if [ -e", StringComparison.Ordinal)) return Task.FromResult(new ProcessResult(0, "yes\n", "", false));
            if (command.StartsWith("wc -c", StringComparison.Ordinal)) return Task.FromResult(new ProcessResult(0, "3\n", "", false));
            return Task.FromResult(new ProcessResult(0, "b2xk\n", "", false));
        }

        public Task<ProcessResult> RunWithInputAsync(string fileName, IEnumerable<string> arguments, ReadOnlyMemory<byte> input, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProcessResult(1, "", "Permission denied", false));
    }
}
