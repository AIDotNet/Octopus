using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Octopus.Agents.Signals;
using static Octopus.Agents.Tests.SignalTestHelpers;

namespace Octopus.Agents.Tests;

/// <summary>
/// Planner output → forwarder → hub, the way an agent would run it: the generated commands go through /bin/sh with the
/// attachment's environment. Uses the real <c>octopus-signal</c> from its build output and Node for the OpenCode
/// plugin; each test is skipped when its program is not there. No agent CLI is run.
/// </summary>
public sealed class SignalEndToEndTests : IDisposable
{
    private readonly ShortTempDir _root = new();
    private readonly SignalHub _hub;
    private readonly string _home;

    public SignalEndToEndTests()
    {
        _hub = new SignalHub(new TestPaths(_root.Path), Builtins.All);
        _home = Path.Combine(_root.Path, "home");
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        _hub.Dispose();
        _root.Dispose();
    }

    private static string? Forwarder()
    {
        var root = RepositoryRoot();
        if (root is null)
        {
            return null;
        }
        foreach (var configuration in new[] { "Debug", "Release" })
        {
            var path = Path.Combine(root, "src", "Octopus.SignalForwarder", "bin", configuration, "net11.0", "octopus-signal");
            if (File.Exists(path))
            {
                return path;
            }
        }
        return null;
    }

    /// <summary>Runs a generated command line the way the agent would: through /bin/sh, payload on standard input.</summary>
    private static async Task<(int ExitCode, string Output)> RunShellAsync(string commandLine, string stdin, IReadOnlyDictionary<string, string> environment)
    {
        var start = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(commandLine);
        start.Environment.Clear();
        foreach (var (key, value) in environment)
        {
            start.Environment[key] = value;
        }
        using var process = Process.Start(start)!;
        await process.StandardInput.WriteAsync(stdin);
        process.StandardInput.Close();
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return (process.ExitCode, output);
    }

    private Dictionary<string, string> AgentEnvironment(SignalAttachment attachment)
    {
        // The apphost finds the runtime this test runs on through DOTNET_ROOT.
        var runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var dotnetRoot = Path.GetFullPath(Path.Combine(runtime, "..", "..", ".."));
        var env = new Dictionary<string, string>
        {
            ["PATH"] = "/usr/bin:/bin",
            ["HOME"] = _home,
            ["TMPDIR"] = Path.Combine(_root.Path, "tmp"),
            ["DOTNET_ROOT"] = dotnetRoot,
        };
        Directory.CreateDirectory(env["TMPDIR"]);
        foreach (var (key, value) in attachment.Environment)
        {
            env[key] = value;
        }
        return env;
    }

    [Fact]
    public async Task Claude_hook_and_status_line_reach_the_hub_and_the_users_status_line_still_prints()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "/bin/sh and a Unix socket");
        var forwarder = Forwarder();
        Assert.SkipWhen(forwarder is null, "octopus-signal is not built (dotnet build src/Octopus.SignalForwarder)");
        Assert.True(_hub.Start());
        Directory.CreateDirectory(Path.Combine(_home, ".claude"));
        File.WriteAllText(Path.Combine(_home, ".claude", "settings.json"), """{ "statusLine": { "type": "command", "command": "printf 'mine:'; cat" } }""");

        var planner = new SignalAttachPlanner(new TestPaths(_root.Path));
        var attachment = planner.Plan(new SignalAttachRequest(
            Builtins.Get("claude"), "item-e2e", forwarder!, _hub.Endpoint, "2.1.322",
            new Dictionary<string, string> { ["HOME"] = _home }, _root.Path));
        var env = AgentEnvironment(attachment);

        // A hook, from the generated plugin.
        var hooks = JsonDocument.Parse(File.ReadAllText(Path.Combine(attachment.Arguments[3], "hooks", "hooks.json"))).RootElement;
        var stopCommand = hooks.GetProperty("hooks").GetProperty("Stop")[0].GetProperty("hooks")[0].GetProperty("command").GetString()!;
        var (stopSubscription, stop) = Next(_hub, "claude", "stop");
        using (stopSubscription)
        {
            var stopPayload = "{\"hook_event_name\":\"Stop\",\"session_id\":\"s-1\"}";
            var (hookExit, hookOutput) = await RunShellAsync(stopCommand, stopPayload, env);
            Assert.Equal(0, hookExit);
            Assert.Equal("", hookOutput);
            var signal = await stop;
            Assert.Equal(stopPayload, signal.Payload);
            Assert.Equal("item-e2e", signal.ItemId);
            // CLAUDE_CONFIG_DIR is unset, so the forwarder reports "" and the hub takes the default directory in the
            // app's own home (the test's fake HOME only exists for the agent).
            Assert.Equal(SignalAccounts.Key(Builtins.Get("claude"), ""), signal.Account);
            Assert.Equal(SignalStatus.Done, SignalStatusMap.Resolve(Builtins.Get("claude"), signal));
        }

        // The status line, from the generated settings, chained to the user's own.
        var statusLine = JsonDocument.Parse(File.ReadAllText(attachment.Arguments[1])).RootElement.GetProperty("statusLine").GetProperty("command").GetString()!;
        var (lineSubscription, line) = Next(_hub, "claude", "statusline");
        using (lineSubscription)
        {
            var payload = "{\"rate_limits\":{\"five_hour\":{\"used_percentage\":42}}}";
            var (exit, output) = await RunShellAsync(statusLine, payload, env);
            Assert.Equal(0, exit);
            Assert.Equal("mine:" + payload, output);
            Assert.Equal(payload, (await line).Payload);
        }
        Assert.Equal(0, _hub.Statistics.Dropped);
    }

    [Fact]
    public async Task Codex_notify_reaches_the_hub_and_runs_the_users_program()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "/bin/sh and a Unix socket");
        var forwarder = Forwarder();
        Assert.SkipWhen(forwarder is null, "octopus-signal is not built (dotnet build src/Octopus.SignalForwarder)");
        Assert.True(_hub.Start());
        var codexHome = Path.Combine(_home, ".codex");
        Directory.CreateDirectory(codexHome);
        var notified = Path.Combine(_root.Path, "notified.txt");
        var userProgram = Script(_root.Path, "user-notify", $"printf %s \"$1\" > '{notified}'\n");
        File.WriteAllText(Path.Combine(codexHome, "config.toml"), $"notify = [\"{userProgram}\"]\n");

        var planner = new SignalAttachPlanner(new TestPaths(_root.Path));
        var attachment = planner.Plan(new SignalAttachRequest(
            Builtins.Get("codex"), "item-codex", forwarder!, _hub.Endpoint, "0.167.0", new Dictionary<string, string> { ["HOME"] = _home }));
        var notify = (List<object?>)MiniToml.Parse(attachment.Arguments[1])["notify"]!;
        var argv = notify.Cast<string>().ToList();

        // Codex runs the notify program directly with the JSON as its last argument.
        var payload = "{\"type\":\"agent-turn-complete\",\"last-assistant-message\":\"it's done\"}";
        var start = new ProcessStartInfo(argv[0]) { UseShellExecute = false };
        foreach (var arg in argv.Skip(1).Append(payload))
        {
            start.ArgumentList.Add(arg);
        }
        start.Environment.Clear();
        foreach (var (key, value) in AgentEnvironment(attachment))
        {
            start.Environment[key] = value;
        }
        var (subscription, first) = Next(_hub, "codex", "turn-complete");
        using var _ = subscription;
        using (var process = Process.Start(start)!)
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(0, process.ExitCode);
        }

        Assert.Equal(payload, (await first).Payload);
        Assert.Equal(payload, File.ReadAllText(notified));
    }

    [Fact]
    public async Task OpenCode_plugin_forwards_declared_events_to_the_hub()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix socket");
        var node = FindOnPath("node");
        Assert.SkipWhen(node is null, "node is not installed");
        Assert.True(_hub.Start());

        var planner = new SignalAttachPlanner(new TestPaths(_root.Path));
        var attachment = planner.Plan(new SignalAttachRequest(Builtins.Get("opencode"), "item-oc", "/unused/octopus-signal", _hub.Endpoint, "1.19.3"));
        var config = JsonDocument.Parse(attachment.Environment["OPENCODE_CONFIG_CONTENT"]).RootElement;
        var pluginUrl = config.GetProperty("plugin")[0].GetString()!;

        // What OpenCode does with a plugin: import it, call the export, hand events to its `event` hook.
        var harness = Path.Combine(_root.Path, "harness.mjs");
        File.WriteAllText(harness, """
            const mod = await import(process.argv[2]);
            const hooks = await mod.OctopusSignal({});
            await hooks.event({ event: { type: "session.usage.updated", properties: { sessionID: "s1", cost: 0.12, tokens: { input: 10, output: 2 } } } });
            await hooks.event({ event: { type: "session.status", properties: { sessionID: "s1", status: { type: "busy" } } } });
            await hooks.event({ event: { type: "message.updated", properties: {} } });
            await hooks.event({ event: { type: "session.usage.updated", properties: { sessionID: "s1", cost: 0.12, tokens: { input: 10, output: 2 } } } });
            await new Promise(resolve => setTimeout(resolve, 500));
            """);
        var received = new List<AgentSignal>();
        using var _ = _hub.Subscribe("opencode", null, s => { lock (received) received.Add(s); });

        var start = new ProcessStartInfo(node!) { UseShellExecute = false, RedirectStandardError = true };
        start.ArgumentList.Add(harness);
        start.ArgumentList.Add(pluginUrl);
        foreach (var (key, value) in attachment.Environment)
        {
            start.Environment[key] = value;
        }
        using (var process = Process.Start(start)!)
        {
            var errors = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(process.ExitCode == 0, errors);
        }
        await WaitUntilAsync(() => { lock (received) return received.Count >= 2; }, "two signals");
        await Task.Delay(200);

        lock (received)
        {
            Assert.Equal(["session-busy", "session-usage"], received.Select(s => s.Channel).Order());
            var usage = received.Single(s => s.Channel == "session-usage");
            Assert.Equal("item-oc", usage.ItemId);
            Assert.Equal("", usage.Account);
            using var payload = JsonDocument.Parse(usage.Payload);
            Assert.Equal("session.usage.updated", payload.RootElement.GetProperty("type").GetString());
            Assert.Equal(0.12, payload.RootElement.GetProperty("properties").GetProperty("cost").GetDouble());
        }
        Assert.Equal(0, _hub.Statistics.Dropped);
    }
}
