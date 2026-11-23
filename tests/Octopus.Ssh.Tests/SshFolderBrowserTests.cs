using Octopus.FileSystem;
using Octopus.Platform;

namespace Octopus.Ssh.Tests;

public class SshFolderBrowserTests
{
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

    [Fact]
    public async Task Parses_motd_pwd_and_directory_only_listing()
    {
        var runner = new FakeRunner
        {
            Answer = _ => new ProcessResult(
                0,
                "Welcome to Ubuntu\n/home/ops\n---OCTOPUS-BROWSE---\n./\n../\n.ssh/\nREADME.md\napp10/\napp2/\nnotes.txt\n",
                "",
                false),
        };
        var browser = new SshFolderBrowser("prod", runner, []);

        var page = await browser.BrowseAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("/home/ops", page.CurrentPath);
        Assert.Equal("/home", page.ParentPath);
        Assert.Equal("/home/ops", page.HomePath);
        Assert.Equal("/", page.Separator);
        Assert.Equal([".ssh", "app2", "app10"], page.Entries.Select(e => e.Name).ToArray());
        Assert.True(page.Entries.Single(e => e.Name == ".ssh").IsHidden);
        Assert.Equal("/home/ops/app2", page.Entries.Single(e => e.Name == "app2").Path);
        Assert.Single(page.Roots);
        Assert.Equal("/", page.Roots[0].Path);
    }

    [Fact]
    public async Task Root_has_no_parent_and_joins_children_without_double_slash()
    {
        var runner = new FakeRunner
        {
            Answer = _ => new ProcessResult(0, "/\n---OCTOPUS-BROWSE---\netc/\nvar/\n", "", false),
        };
        var browser = new SshFolderBrowser("prod", runner, []);

        var page = await browser.BrowseAsync(listRoots: true, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("/", page.CurrentPath);
        Assert.Null(page.ParentPath);
        Assert.Equal("/etc", page.Entries.Single(e => e.Name == "etc").Path);
    }

    [Fact]
    public async Task Creates_folder_with_quoted_remote_path_and_surfaces_ssh_errors()
    {
        var runner = new FakeRunner { Answer = _ => new ProcessResult(0, "", "", false) };
        var browser = new SshFolderBrowser("prod", runner, []);

        var created = await browser.CreateFolderAsync("/home/ops", "new app", TestContext.Current.CancellationToken);

        Assert.Equal("/home/ops/new app", created);
        Assert.Contains("mkdir -- '/home/ops/new app'", runner.Commands[0]);

        runner.Answer = _ => new ProcessResult(1, "", "Permission denied\n", false);
        var error = await Assert.ThrowsAsync<IOException>(() => browser.CreateFolderAsync("/root", "nope", TestContext.Current.CancellationToken));
        Assert.Equal("Permission denied", error.Message);
    }
}
