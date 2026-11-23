using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Octopus.Agents;
using Octopus.App.Activity;
using Octopus.App.Common;
using Octopus.App.Composition;
using Octopus.App.Remote;
using Octopus.App.Services;
using Octopus.App.Services.Workspaces;
using Octopus.App.Workspace;
using Octopus.Core.Projects;
using Octopus.FileSystem;
using Octopus.Git;
using Octopus.Platform;
using Octopus.Remote.Host;
using Octopus.Remote.Protocol;
using Octopus.Remote.Security;
using Octopus.Remote.Transport;
using Octopus.Storage;
using Octopus.Terminal;
using Octopus.Terminal.Avalonia;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>
/// End-to-end tests for desktop <see cref="HostWorkspace"/> serving branch workspaces over the remote protocol:
/// git detection, branch/task options, worktree creation under ~/.octopus/workspaces, item creation with task prompt injection,
/// and worktree file-access boundary.
/// </summary>
public class RemoteWorkspaceTests
{
    private sealed class FakeTaskProvider : IGitHubTaskProvider
    {
        public Task<TaskProviderResult> ListIssuesAsync(string repositoryRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TaskProviderResult(true, null,
            [
                new RemoteTaskItem(WorkspaceTaskKind.GitHubIssue, 42, "#42", "Add remote branch workspaces",
                    "https://github.com/org/repo/issues/42", "Support creating git worktrees remotely.", "ada", null, ["enhancement"]),
            ]));

        public Task<TaskProviderResult> ListPullRequestsAsync(string repositoryRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TaskProviderResult(true, null,
            [
                new RemoteTaskItem(WorkspaceTaskKind.GitHubPr, 99, "PR #99", "Fix worktree sync",
                    "https://github.com/org/repo/pull/99", null, "ada", "fix/worktree-sync", ["bug"]),
            ]));
    }

    private sealed class GitHostComputer : IAsyncDisposable
    {
        public DeviceIdentity Identity { get; } = DeviceIdentity.Create();
        public ProjectService Projects { get; }
        public WorkspaceManager Workspaces { get; }
        public HostWorkspace Workspace { get; }
        public RemoteHostService Service { get; }
        public TerminalSessionRegistry Sessions { get; } = new();
        public string TempRoot { get; }
        public string HomeDir { get; }
        public string RepoDir { get; }
        public string OutsideDir { get; }
        public Project Project { get; }

        public GitHostComputer()
        {
            TempRoot = RealPath.Resolve(Path.TrimEndingDirectorySeparator(Directory.CreateTempSubdirectory("octopus-remote-ws-").FullName));
            HomeDir = Path.Combine(TempRoot, "home");
            RepoDir = Path.Combine(TempRoot, "DemoRepo");
            OutsideDir = Path.Combine(TempRoot, "outside");
            Directory.CreateDirectory(HomeDir);
            Directory.CreateDirectory(RepoDir);
            Directory.CreateDirectory(OutsideDir);
            File.WriteAllText(Path.Combine(OutsideDir, "secret.txt"), "private");
            InitGitRepo(RepoDir);

            var paths = AppHost.Get<PathComparer>();
            var git = new GitService(AppHost.Get<IProcessRunner>(), AppHost.Get<IExecutableLocator>(), AppHost.Get<ILoginEnvironment>());
            Projects = new ProjectService(new InMemoryProjectStore(), paths);
            var tasks = new FakeTaskProvider();
            Workspaces = new WorkspaceManager(
                Projects,
                git,
                tasks,
                AppHost.Get<IDialogService>(),
                AppHost.Get<IItemOpener>(),
                AppHost.Get<IAgentCatalog>(),
                AppHost.Get<IAgentScanner>(),
                AppHost.Get<ISettingsStore>(),
                paths,
                new WorkspaceRootOptions(HomeDir));
            Workspace = new HostWorkspace(
                Projects,
                AppHost.Get<IItemLaunchService>(),
                AppHost.Get<ITerminalConnectionFactory>(),
                Sessions,
                AppHost.Get<ITabContentRegistry>(),
                AppHost.Get<IItemActivityService>(),
                AppHost.Get<IAgentCatalog>(),
                AppHost.Get<IAgentScanner>(),
                AppHost.Get<ISettingsStore>(),
                AppHost.Get<IShellResolver>(),
                AppHost.Get<IFileTreeService>(),
                git,
                paths,
                Workspaces,
                tasks);
            Service = new RemoteHostService(
                Identity,
                () => new DeviceDescription("MacHost", DeviceKind.Desktop),
                new PairedPeerStore(null),
                Workspace,
                RemoteHostRuntime.Hello);
            Project = Projects.AddProject(RepoDir, "DemoRepo");
        }

        public Task<IMessageTransport> Dial(Uri address, CancellationToken cancellationToken)
        {
            var (device, host) = InMemoryTransport.CreatePair();
            _ = Service.ServeAsync(host);
            return Task.FromResult<IMessageTransport>(device);
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            Workspace.Dispose();
            Identity.Dispose();
            try { Directory.Delete(TempRoot, recursive: true); } catch { }
        }

        private static void InitGitRepo(string dir)
        {
            RunGit(dir, "init -b main");
            RunGit(dir, "config user.email test@octopus.local");
            RunGit(dir, "config user.name Test");
            File.WriteAllText(Path.Combine(dir, "README.md"), "# DemoRepo\n");
            RunGit(dir, "add README.md");
            RunGit(dir, "commit -m init");
            RunGit(dir, "branch existing-free");
        }

        private static void RunGit(string dir, string args)
        {
            using var p = Process.Start(new ProcessStartInfo("git", args)
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            p.WaitForExit();
            Assert.Equal(0, p.ExitCode);
        }
    }

    private static async Task<T> Wait<T>(Task<T> task, int milliseconds = 15_000)
    {
        var end = Environment.TickCount64 + milliseconds;
        while (!task.IsCompleted && Environment.TickCount64 < end)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Assert.True(task.IsCompleted, "The operation did not finish in time.");
        return await task;
    }

    [AvaloniaFact]
    public async Task Host_workspace_exposes_options_creates_worktree_injects_task_prompt_and_allows_worktree_files()
    {
        await using var host = new GitHostComputer();
        var runtime = AppHost.Get<RemoteClientRuntime>();
        runtime.Dial = host.Dial;
        var mine = await Wait(AppHost.Get<RemoteIdentityProvider>().GetAsync());
        host.Service.Devices.Add(mine.PublicKey, runtime.Description);
        runtime.Hosts.Add(host.Identity.PublicKey, new DeviceDescription("MacHost", DeviceKind.Desktop), ["ws://machost.test:47600"]);
        var client = await Wait(runtime.ConnectAsync(host.Identity.Id));

        Assert.True(client.SupportsWorkspaces);

        var projects = await Wait(client.ListProjectsAsync());
        var projMsg = Assert.Single(projects);
        Assert.True(projMsg.IsGitRepository);
        Assert.Equal("main", projMsg.DefaultBranch);
        Assert.Null(projMsg.Workspaces);

        var options = await Wait(client.GetWorkspaceOptionsAsync(projMsg.Id));
        Assert.True(options.IsGitRepository);
        Assert.Equal("main", options.DefaultBranch);
        Assert.True(options.GhAvailable);
        Assert.Contains(options.Branches, b => b.Name == "main" && b.OccupiedBy is not null);
        Assert.Contains(options.Branches, b => b.Name == "existing-free" && b.OccupiedBy is null);
        Assert.Equal("#42", Assert.Single(options.Issues).Key);
        Assert.Equal("PR #99", Assert.Single(options.PullRequests).Key);

        // Create a real git worktree on the host via the remote protocol.
        var createdWs = await Wait(client.CreateWorkspaceAsync(new CreateWorkspaceMessage
        {
            ProjectId = projMsg.Id,
            CreateBranch = true,
            BranchName = "feat/42-remote-ws",
            BaseRef = "main",
            TaskKind = "issue",
            TaskKey = "#42",
            TaskTitle = "Add remote branch workspaces",
            TaskUrl = "https://github.com/org/repo/issues/42",
            TaskBody = "Support creating git worktrees remotely.",
        }));
        Assert.Equal("feat/42-remote-ws", createdWs.Branch);
        Assert.Equal("#42", createdWs.TaskKey);
        Assert.True(Directory.Exists(createdWs.Path));
        Assert.StartsWith(Path.Combine(host.HomeDir, ".octopus", "workspaces", "DemoRepo"), createdWs.Path);

        // Project list now exposes the new workspace.
        var afterProjects = await Wait(client.ListProjectsAsync());
        var syncedWs = Assert.Single(afterProjects[0].Workspaces!);
        Assert.Equal(createdWs.Id, syncedWs.Id);

        // Starting an agent in that workspace injects the task context and binds WorkingDirectory to the worktree.
        var installedAgent = AppHost.Get<IAgentScanner>().Current.Installations.FirstOrDefault()?.AgentId;
        var createdItem = await Wait(client.CreateItemAsync(new CreateItemMessage
        {
            ProjectId = projMsg.Id,
            Kind = installedAgent is not null ? ItemKinds.Agent : ItemKinds.Terminal,
            AgentId = installedAgent,
            Prompt = "Please start with tests.",
            WorkspaceId = createdWs.Id,
            InjectTaskPrompt = true,
        }));
        Assert.Equal((createdWs.Id, "feat/42-remote-ws"), (createdItem.WorkspaceId, createdItem.WorkspaceBranch));
        var hostItem = host.Projects.FindItem(createdItem.Id)!;
        Assert.Equal(createdWs.Id, hostItem.WorkspaceId);
        Assert.Equal(createdWs.Path, hostItem.WorkingDirectory);
        if (installedAgent is not null)
        {
            Assert.Contains("#42", hostItem.Launch?.Prompt);
            Assert.Contains("Please start with tests.", hostItem.Launch?.Prompt);
        }

        // Worktree folder is allowed by the host's file security boundary, while outside folders remain blocked.
        var entries = await Wait(client.ListFolderAsync(createdWs.Path));
        Assert.Contains(entries, e => e.Name == "README.md");
        var refused = await Assert.ThrowsAsync<RemoteException>(() => Wait(client.ListFolderAsync(host.OutsideDir)));
        Assert.Equal(RemoteMethods.NotAllowed, refused.Code);
    }
}
