using Octopus.Platform;
using Octopus.Ssh;

namespace Octopus.Ssh.Tests;

public class SshFileTreeTests
{
    /// <summary>Answers each run by its remote command (the last argument) and records what was asked.</summary>
    private sealed class FakeRunner : IProcessRunner
    {
        public List<string> Commands { get; } = [];
        public Func<string, ProcessResult> Answer { get; set; } = _ => new ProcessResult(0, "", "", false);

        public Task<ProcessResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            IReadOnlyDictionary<string, string>? environment = null,
            string? workingDirectory = null,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            var args = arguments.ToList();
            Commands.Add(args[^1]);
            return Task.FromResult(Answer(args[^1]));
        }
    }

    private static ProcessResult Ok(string output) => new(0, output, "", false);

    [Fact]
    public void Folders_come_first_then_files_in_name_order_and_git_data_and_dot_entries_are_left_out()
    {
        var entries = SshFileTree.Parse("/srv", "b.txt\nsrc/\n.env\n.git/\n./\n../\nA/\nnotes.md\n");

        Assert.Equal(["A", "src", ".env", "b.txt", "notes.md"], entries.Select(e => e.Name));
        Assert.True(entries[0].IsDirectory);
        Assert.False(entries[2].IsDirectory);
        Assert.True(entries[2].IsHidden);
        Assert.Equal("/srv/A", entries[0].FullPath);
    }

    [Fact]
    public void A_folder_at_the_root_is_joined_without_a_double_slash()
    {
        var entries = SshFileTree.Parse("/", "etc/\n");

        Assert.Equal("/etc", entries.Single().FullPath);
    }

    [Fact]
    public async Task List_answers_at_once_and_tells_the_watchers_when_the_folder_has_been_read()
    {
        var runner = new FakeRunner { Answer = _ => Ok("a.txt\n") };
        var tree = new SshFileTree("prod", runner, []);
        var read = new TaskCompletionSource();
        using var watch = tree.Watch("/srv", _ => read.TrySetResult());

        Assert.Empty(tree.List("/srv"));
        await read.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["a.txt"], tree.List("/srv").Select(e => e.Name));
        // The answer is fresh now: asking again does not run ssh again.
        Assert.Single(runner.Commands);
        Assert.Equal("ls -1Ap -- '/srv'", runner.Commands[0]);
    }

    [Fact]
    public async Task A_failed_read_is_reported_with_the_reason_ssh_gave()
    {
        var runner = new FakeRunner { Answer = _ => new ProcessResult(255, "", "Warning: something\nPermission denied (publickey).\n", false) };
        var tree = new SshFileTree("prod", runner, []);
        var read = new TaskCompletionSource();
        using var watch = tree.Watch("/root", _ => read.TrySetResult());

        Assert.Empty(tree.List("/root"));
        await read.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("Permission denied (publickey).", tree.Status);
        Assert.Empty(tree.List("/root"));
    }

    [Fact]
    public async Task A_read_that_times_out_says_so()
    {
        var runner = new FakeRunner { Answer = _ => new ProcessResult(-1, "", "", true) };
        var tree = new SshFileTree("prod", runner, []);
        var read = new TaskCompletionSource();
        using var watch = tree.Watch("/", _ => read.TrySetResult());

        tree.List("/");
        await read.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("The host did not answer in time.", tree.Status);
    }

    [Fact]
    public async Task A_stopped_watch_is_not_told_any_more()
    {
        var runner = new FakeRunner { Answer = _ => Ok("") };
        var tree = new SshFileTree("prod", runner, []);
        var count = 0;
        var watch = tree.Watch("/srv", _ => Interlocked.Increment(ref count));
        watch.Dispose();

        tree.List("/srv");
        await Task.Delay(300);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ResolveAsync_gives_the_absolute_folder_and_enters_it_with_the_tilde_kept_for_the_host()
    {
        var runner = new FakeRunner { Answer = _ => Ok("/root/code/app\n") };
        var tree = new SshFileTree("prod", runner, []);

        var resolved = await tree.ResolveAsync("~/code/app");

        Assert.Equal("/root/code/app", resolved);
        Assert.Equal("cd -- ~/'code/app' && pwd -P", runner.Commands[0]);
    }

    [Fact]
    public async Task ResolveAsync_throws_with_the_reason_when_the_folder_cannot_be_entered()
    {
        var runner = new FakeRunner { Answer = _ => new ProcessResult(1, "", "sh: cd: /no/such: No such file or directory\n", false) };
        var tree = new SshFileTree("prod", runner, []);

        var error = await Assert.ThrowsAsync<SshCommandException>(() => tree.ResolveAsync("/no/such"));

        Assert.Equal("sh: cd: /no/such: No such file or directory", error.Message);
    }

    [Fact]
    public async Task ReadAsync_gives_the_bytes_of_a_file_within_the_limit()
    {
        var runner = new FakeRunner
        {
            Answer = command => command.StartsWith("wc -c", StringComparison.Ordinal) ? Ok("5\n") : Ok("aGVsbG8=\n"),
        };
        var tree = new SshFileTree("prod", runner, []);

        var bytes = await tree.ReadAsync("/srv/hello.txt", 1024);

        Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(bytes));
        Assert.Equal("base64 < '/srv/hello.txt' | tr -d '\\n'", runner.Commands[1]);
    }

    [Fact]
    public async Task ReadAsync_refuses_a_file_over_the_limit_without_reading_it()
    {
        var runner = new FakeRunner { Answer = _ => Ok("2000\n") };
        var tree = new SshFileTree("prod", runner, []);

        var error = await Assert.ThrowsAsync<SshFileTooLargeException>(() => tree.ReadAsync("/srv/big.log", 100));

        Assert.Equal(2000, error.Size);
        Assert.Equal(100, error.Limit);
        Assert.Single(runner.Commands);
    }

    [Fact]
    public void Remote_paths_are_quoted_so_nothing_in_them_is_read_as_shell_syntax()
    {
        Assert.Equal("'/srv/it'\\''s'", SshCommands.RemotePath("/srv/it's"));
        Assert.Equal("'/srv/a b; rm -rf x'", SshCommands.RemotePath("/srv/a b; rm -rf x"));
        Assert.Equal("~", SshCommands.RemotePath("~"));
        Assert.Equal("~/'a b'", SshCommands.RemotePath("~/a b"));
    }

    [Fact]
    public void Runs_are_unattended_and_share_one_connection_where_OpenSSH_has_sockets()
    {
        Assert.Contains("BatchMode=yes", SshCommands.Unattended);

        var shared = SshCommands.SharedConnection("/home/u/.octopus/ssh");
        if (OperatingSystem.IsWindows())
        {
            Assert.Empty(shared);
        }
        else
        {
            Assert.Contains("ControlMaster=auto", shared);
            Assert.Contains("ControlPath=" + Path.Combine("/home/u/.octopus/ssh", "%C"), shared);
        }
    }

    [Fact]
    public void The_host_is_given_after_the_double_dash_so_an_alias_cannot_be_read_as_an_option()
    {
        var args = SshCommands.Arguments("-oProxyCommand=x", "true", ["-o", "BatchMode=yes"]);

        Assert.Equal(["-o", "BatchMode=yes", "--", "-oProxyCommand=x", "true"], args);
    }
}
