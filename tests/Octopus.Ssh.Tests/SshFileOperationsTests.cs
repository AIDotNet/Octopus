using Octopus.Platform;

namespace Octopus.Ssh.Tests;

public class SshFileOperationsTests
{
    /// <summary>Records every run (its remote command and any input) and answers with the given output.</summary>
    private sealed class RecordingRunner(string output = "") : IProcessRunner
    {
        public List<string> Commands { get; } = [];
        public List<byte[]> Inputs { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment = null, string? workingDirectory = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            Commands.Add(arguments.Last());
            return Task.FromResult(new ProcessResult(0, output, "", false));
        }

        public Task<ProcessResult> RunWithInputAsync(string fileName, IEnumerable<string> arguments, ReadOnlyMemory<byte> input, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            Commands.Add(arguments.Last());
            Inputs.Add(input.ToArray());
            return Task.FromResult(new ProcessResult(0, "", "", false));
        }
    }

    [Fact]
    public async Task Creating_a_file_refuses_a_taken_name_on_the_host_and_reads_the_folder_again()
    {
        var runner = new RecordingRunner();
        var tree = new SshFileTree("prod", runner, []);
        var raised = new List<string>();
        using var watch = tree.Watch("/srv", raised.Add);

        await tree.CreateFileAsync("/srv/app/new.txt");

        Assert.Equal("if [ -e '/srv/app/new.txt' ]; then echo 'The name is taken.' >&2; exit 1; fi; : > '/srv/app/new.txt'", runner.Commands[0]);
        Assert.Equal(["/srv/app"], raised);
    }

    [Fact]
    public async Task Creating_a_folder_and_renaming_use_the_host_shell_on_quoted_paths()
    {
        var runner = new RecordingRunner();
        var tree = new SshFileTree("prod", runner, []);

        await tree.CreateDirectoryAsync("/srv/it's");
        await tree.RenameAsync("/srv/a b", "/srv/c d");

        Assert.Equal("mkdir -- '/srv/it'\\''s'", runner.Commands[0]);
        Assert.Equal("if [ -e '/srv/c d' ]; then echo 'The name is taken.' >&2; exit 1; fi; mv -- '/srv/a b' '/srv/c d'", runner.Commands[1]);
    }

    [Fact]
    public async Task Deleting_a_folder_removes_it_and_a_file_is_removed_alone()
    {
        var runner = new RecordingRunner();
        var tree = new SshFileTree("prod", runner, []);

        await tree.DeleteAsync("/srv/app/old", isDirectory: true);
        await tree.DeleteAsync("/srv/app/old.txt", isDirectory: false);

        Assert.Equal("rm -rf -- '/srv/app/old'", runner.Commands[0]);
        Assert.Equal("rm -f -- '/srv/app/old.txt'", runner.Commands[1]);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("~")]
    public async Task The_root_and_the_home_folder_are_never_deleted(string path)
    {
        var runner = new RecordingRunner();
        var tree = new SshFileTree("prod", runner, []);

        await Assert.ThrowsAsync<ArgumentException>(() => tree.DeleteAsync(path, isDirectory: true));
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task Paths_on_the_host_must_be_absolute_or_start_at_home()
    {
        var tree = new SshFileTree("prod", new RecordingRunner(), []);

        await Assert.ThrowsAsync<ArgumentException>(() => tree.CreateFileAsync("relative/file.txt"));
    }

    [Fact]
    public async Task A_write_sends_the_bytes_through_a_temporary_file_that_takes_the_name_and_the_permissions()
    {
        var runner = new RecordingRunner();
        var tree = new SshFileTree("prod", runner, []);
        var bytes = "# Title\n\nUnicode: 文件\n"u8.ToArray();

        await tree.WriteAsync("/srv/notes.md", bytes);

        Assert.Equal(bytes, runner.Inputs[0]);
        var command = runner.Commands[0];
        Assert.Contains("mode=$(stat -c %a -- '/srv/notes.md' 2>/dev/null || stat -f %Lp '/srv/notes.md' 2>/dev/null)", command);
        Assert.Contains("cat > '/srv/notes.md.octopus-save'", command);
        Assert.Contains("mv -f -- '/srv/notes.md.octopus-save' '/srv/notes.md' || { rm -f -- '/srv/notes.md.octopus-save'; exit 1; }", command);
    }

    [Fact]
    public async Task Existence_is_asked_on_the_host()
    {
        Assert.True(await new SshFileTree("prod", new RecordingRunner("yes\n"), []).ExistsAsync("/srv/x"));
        Assert.False(await new SshFileTree("prod", new RecordingRunner("no\n"), []).ExistsAsync("/srv/x"));
    }
}
