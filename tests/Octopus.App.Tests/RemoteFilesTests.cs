using Avalonia.Headless.XUnit;
using Octopus.App.Common;
using Octopus.App.Explorer.Files;
using Octopus.App.Composition;
using Octopus.App.Services;
using Octopus.FileSystem;
using Octopus.Platform;
using Octopus.Ssh;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>Copies of files on SSH hosts are read-only files in the cache; a folder on a host has no reveal on this computer.</summary>
public class RemoteFilesTests
{
    private sealed class TestPaths(string root) : IAppPaths
    {
        public string DataDirectory => root;
        public string CacheDirectory => Path.Combine(root, "cache");
    }

    private sealed class FakeRunner(Func<string, ProcessResult> answer) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            IReadOnlyDictionary<string, string>? environment = null,
            string? workingDirectory = null,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(answer(arguments.Last()));
    }

    private static (RemoteFiles Files, string Root) Create(Func<string, ProcessResult> answer)
    {
        var root = Directory.CreateTempSubdirectory("octopus-remote-").FullName;
        return (new RemoteFiles(new FakeRunner(answer), new TestPaths(root), Path.Combine(root, "sockets")), root);
    }

    [Fact]
    public async Task A_copy_holds_the_bytes_of_the_host_file_and_can_be_written()
    {
        var (files, _) = Create(command => command.StartsWith("wc -c", StringComparison.Ordinal)
            ? new ProcessResult(0, "5\n", "", false)
            : new ProcessResult(0, "aGVsbG8=\n", "", false));

        var copy = await files.CopyAsync("prod", "/srv/app/hello.txt");

        Assert.Equal("hello.txt", Path.GetFileName(copy));
        Assert.Equal("hello", File.ReadAllText(copy));
        // The copy is writable: a save writes it back to the host.
        if (OperatingSystem.IsWindows())
        {
            Assert.False(File.GetAttributes(copy).HasFlag(FileAttributes.ReadOnly));
        }
        else
        {
            Assert.True((File.GetUnixFileMode(copy) & UnixFileMode.UserWrite) != 0);
        }

        // A second copy replaces the first one: the copy is made again, not refused.
        await files.CopyAsync("prod", "/srv/app/hello.txt");
    }

    [Fact]
    public async Task A_file_over_the_limit_is_not_copied()
    {
        var (files, root) = Create(_ => new ProcessResult(0, (RemoteFiles.MaxCopyBytes + 1) + "\n", "", false));

        await Assert.ThrowsAsync<SshFileTooLargeException>(() => files.CopyAsync("prod", "/srv/big.bin"));

        // Nothing was written: not even the folder of the copy.
        Assert.False(Directory.Exists(Path.Combine(root, "cache", "remote")));
    }
}

public class RemoteFolderTests
{
    [AvaloniaFact]
    public void A_folder_on_an_SSH_host_cannot_be_revealed_on_this_computer()
    {
        var paths = new PathComparer(ignoreCase: false);
        var model = new FileTreeViewModel(AppHost.Get<IFileTreeService>(), AppHost.Get<IFileOpener>(), paths, AppHost.Get<IDialogService>());
        var remote = new SshFileTree("prod", new NoRuns(), []);

        model.Load("/srv/app", remote);
        Assert.True(model.IsRemote);
        Assert.False(model.RevealProjectCommand.CanExecute(null));

        model.Load(Directory.CreateTempSubdirectory("octopus-local-").FullName);
        Assert.False(model.IsRemote);
        Assert.True(model.RevealProjectCommand.CanExecute(null));
    }

    /// <summary>The tree is only asked for its shape here; nothing is run.</summary>
    private sealed class NoRuns : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            IReadOnlyDictionary<string, string>? environment = null,
            string? workingDirectory = null,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProcessResult(1, "", "not run", false));
    }
}
