using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Avalonia.Headless.XUnit;
using Octopus.Agents;
using Octopus.Agents.Signals;
using Octopus.App.Collab;
using Octopus.App.Composition;
using Octopus.App.Services;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.Core.Terminals;
using Octopus.Platform;
using Octopus.Terminal;
using Octopus.Terminal.Avalonia;
using TKCode.Terminal.Core;
using Xunit;

namespace Octopus.App.Tests;

public sealed class TerminalSignalEmitterTests
{
    private sealed class TempAppPaths(string root) : IAppPaths
    {
        public string DataDirectory => Path.Combine(root, "data");
        public string CacheDirectory => Path.Combine(root, "cache");
        public string LogDirectory => Path.Combine(root, "logs");
        public string SettingsPath => Path.Combine(DataDirectory, "settings.json");
        public string ProjectsPath => Path.Combine(DataDirectory, "projects.json");
        public string SessionsPath => Path.Combine(DataDirectory, "sessions.json");
        public string HistoryDirectory => Path.Combine(DataDirectory, "history");
        public string PluginsDirectory => Path.Combine(DataDirectory, "plugins");
        public string SkillsDirectory => Path.Combine(DataDirectory, "skills");
        public string AgentsDirectory => Path.Combine(DataDirectory, "agents");
    }

    private sealed class ScriptedConnection : ITerminalConnection
    {
        private readonly Channel<byte[]> _output = Channel.CreateUnbounded<byte[]>();
        private readonly TaskCompletionSource<TerminalExit> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ProcessId => 4242;
        public Task<TerminalExit> Exited => _exit.Task;
        public ConcurrentQueue<string> Written { get; } = new();

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var chunk in _output.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return chunk;
            }
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default)
        {
            Written.Enqueue(Encoding.UTF8.GetString(input.Span));
            return ValueTask.CompletedTask;
        }

        public ValueTask ResizeAsync(int columns, int rows, int pixelWidth, int pixelHeight) => ValueTask.CompletedTask;
        public ValueTask SignalAsync(TerminalSignal signal) { _output.Writer.TryComplete(); _exit.TrySetResult(new TerminalExit(0, null)); return ValueTask.CompletedTask; }
        public ValueTask<string?> GetForegroundProcessNameAsync() => ValueTask.FromResult<string?>(null);
        public ValueTask DisposeAsync() { _output.Writer.TryComplete(); _exit.TrySetResult(new TerminalExit(0, null)); return ValueTask.CompletedTask; }
    }

    private sealed class ScriptedFactory : ITerminalConnectionFactory
    {
        public List<LaunchSpec> Specs { get; } = [];
        public ITerminalConnection Start(LaunchSpec spec, int columns, int rows)
        {
            Specs.Add(spec);
            return new ScriptedConnection();
        }
    }

    private sealed class StubParentBridge(ProjectItem item, string cwd) : ICollabSessionBridge
    {
        public string ItemId => item.Id;
        public string AgentId => item.AgentId ?? "claude";
        public string DisplayTitle => item.DisplayTitle;
        public string WorkingDirectory => cwd;
        public bool IsBusy => false;
        public bool IsConnected => true;
        public string? CurrentStatusDetail => null;
        public Task<bool> DeliverCollabPromptAsync(string promptText, string? threadId = null, string? taskId = null, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public string ReadRecentSummary(int maxTurns = 5) => "";
    }

    private static string? FindNode()
    {
        foreach (var candidate in new[] { "/opt/homebrew/bin/node", "/usr/local/bin/node", "/usr/bin/node" })
        {
            if (File.Exists(candidate)) return candidate;
        }
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var full = Path.Combine(dir, OperatingSystem.IsWindows() ? "node.exe" : "node");
            if (File.Exists(full)) return full;
        }
        return null;
    }

    private static JsonObject Body(JsonObject toolResult) =>
        JsonNode.Parse(toolResult["content"]![0]!["text"]!.GetValue<string>())!.AsObject();

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct, int maxSteps = 120)
    {
        for (var i = 0; i < maxSteps && !condition(); i++)
        {
            Frames.Pump();
            await Task.Delay(25, ct);
        }
        Assert.True(condition());
    }

    [AvaloniaFact, FreshApp]
    public async Task OpenCode_generated_plugin_settles_fresh_job_after_terminal_restart()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix socket");
        var node = FindNode();
        Assert.SkipWhen(node is null, "node is not installed");

        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("octopus-opencode-restart-").FullName;
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(dir);
        var cli = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "opencode", Title = "CLI", Surface = AgentSurface.Terminal };
        var parent = new ProjectItem { Kind = ProjectItemKind.Agent, AgentId = "claude", Title = "Parent", Surface = AgentSurface.Gui };
        projects.AddItem(project, cli);
        projects.AddItem(project, parent);

        var host = (CollabCoordinator)AppHost.Get<ICollabCoordinator>();
        host.Server.Start();
        host.RegisterGuiSession(new StubParentBridge(parent, dir));

        var hub = AppHost.Get<ISignalHub>();
        Assert.True(hub.Start());
        var endpoint = hub.Endpoint!;

        var definition = AppHost.Get<IAgentCatalog>().Find("opencode")!;
        var planner = new SignalAttachPlanner(new TempAppPaths(dir));
        var attachment = planner.Plan(new SignalAttachRequest(definition, cli.Id, "/unused/octopus-signal", endpoint, "1.19.3"));
        var config = JsonElement.Parse(attachment.Environment["OPENCODE_CONFIG_CONTENT"]);
        var pluginUrl = config.GetProperty("plugin")[0].GetString()!;

        var harness = Path.Combine(dir, "emit-idle.mjs");
        await File.WriteAllTextAsync(harness, """
            const mod = await import(process.argv[2]);
            const hooks = await mod.OctopusSignal({});
            await hooks.event({ event: { type: "session.idle" } });
            await new Promise(resolve => setTimeout(resolve, 150));
            """, ct);

        var registry = AppHost.Get<TerminalSessionRegistry>();
        var factory = new ScriptedFactory();
        var spec = new LaunchSpec("opencode", [], dir, new Dictionary<string, string>(attachment.Environment, StringComparer.Ordinal));
        var term = registry.GetOrStart(cli.Id, spec, factory, new TerminalSize(80, 24, 8, 16), TerminalPalettes.Dark, ColorScheme.Dark, 1000, out _);

        async Task<string> DelegateAsync(string goal) => Body(await host.CallToolAsync(
            parent.Id,
            "delegate_task",
            new JsonObject { ["target"] = cli.Id, ["goal"] = goal },
            ct))["task_id"]!.GetValue<string>();

        async Task<AgentSignal> EmitIdleFromCurrentLinkAsync()
        {
            var seen = new TaskCompletionSource<AgentSignal>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var sub = hub.Subscribe("opencode", "session-idle", sig => seen.TrySetResult(sig));
            var launchEnv = factory.Specs.Last().Environment;
            var start = new ProcessStartInfo(node!) { UseShellExecute = false, RedirectStandardError = true };
            start.ArgumentList.Add(harness);
            start.ArgumentList.Add(pluginUrl);
            start.Environment.Clear();
            foreach (var (key, value) in launchEnv)
            {
                start.Environment[key] = value;
            }
            using var proc = Process.Start(start)!;
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.True(proc.ExitCode == 0, stderr);
            return await seen.Task.WaitAsync(TimeSpan.FromSeconds(4), ct);
        }

        try
        {
            var firstTask = await DelegateAsync("first");
            var firstSignal = await EmitIdleFromCurrentLinkAsync();
            Assert.Equal(term.CurrentLinkId, firstSignal.LinkId);
            await WaitUntilAsync(() => host.GetTasksForItem(parent.Id).Single(x => x.TaskId == firstTask).Status == CollabTaskStatus.Completed, ct);

            await term.RestartAsync();
            var secondTask = await DelegateAsync("second");
            var secondSignal = await EmitIdleFromCurrentLinkAsync();
            Assert.Equal(term.CurrentLinkId, secondSignal.LinkId);
            Assert.NotEqual(firstSignal.LinkId, secondSignal.LinkId);

            await WaitUntilAsync(() => host.GetTasksForItem(parent.Id).Single(x => x.TaskId == secondTask).Status == CollabTaskStatus.Completed, ct);
        }
        finally
        {
            await registry.CloseAllAsync();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
