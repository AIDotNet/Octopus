using System.Diagnostics;
using System.Text.Json;
using Octopus.Agents.Probes;
using Octopus.Agents.Signals;
using Octopus.Core.Agents;
using static Octopus.Agents.Tests.SignalTestHelpers;

namespace Octopus.Agents.Tests;

/// <summary>Probes against small /bin/sh programs standing in for the agents; the real CLIs are never run.</summary>
public sealed class ProbeRunnerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero));
    private readonly ProbeRunner _runner;

    public ProbeRunnerTests() => _runner = new ProbeRunner(time: _time);

    public void Dispose()
    {
        _runner.Dispose();
        _dir.Dispose();
    }

    private string Log(string name) => Path.Combine(_dir.Path, name);

    private int Runs => File.Exists(Log("count")) ? File.ReadAllLines(Log("count")).Length : 0;

    private Dictionary<string, string> Env(params (string Key, string Value)[] extra)
    {
        var env = new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin", ["LOG_DIR"] = _dir.Path, ["HOME"] = _dir.Path };
        foreach (var (key, value) in extra)
        {
            env[key] = value;
        }
        return env;
    }

    private static AgentDefinition Agent(string id, Dictionary<string, AgentProbe> probes, string? accountVariable = null) => new()
    {
        Id = id,
        Signals = accountVariable is null ? null : new AgentSignals { Account = new SignalAccountSpec { Env = accountVariable, Default = "~/.agent" } },
        Probes = probes,
    };

    private Task<ProbeResult> Run(AgentDefinition definition, string probe, string executable, Dictionary<string, string>? env = null,
        string? account = null, Dictionary<string, string>? args = null) =>
        _runner.RunAsync(new ProbeRequest(definition, probe, executable, env ?? Env(), account, args, null, "9.9.9"));

    // ---- command ----

    private string AboutScript() => Script(_dir.Path, "about", """
        printf '%s\n' "$@" > "$LOG_DIR/args"
        printf '%s|%s|%s' "${CODEX_HOME-unset}" "${OCTOPUS_SIGNAL_TOKEN-unset}" "${OCTOPUS_ITEM_ID-unset}" > "$LOG_DIR/env"
        echo run >> "$LOG_DIR/count"
        printf '{"cliVersion":"1.0","subscriptionTier":"pro","userEmail":"me@example.com","nested":{"a":1,"b":{"c":2,"d":3}}}\n'
        """);

    [Fact]
    public async Task Command_probe_keeps_only_the_selected_fields()
    {
        var definition = Agent("cursor", new() { ["account"] = new AgentProbe { Args = ["about", "--format", "json"], Select = ["subscriptionTier", "nested.b.c", "missing"] } });

        var result = await Run(definition, "account", AboutScript());

        Assert.True(result.Success, result.Message);
        Assert.Equal(ProbeErrorKind.None, result.Error);
        Assert.Equal("""{"subscriptionTier":"pro","nested":{"b":{"c":2}}}""", result.Json);
        Assert.DoesNotContain("example.com", result.Json);
        Assert.Equal(["about", "--format", "json"], File.ReadAllLines(Log("args")));
        Assert.Equal("cursor", result.AgentId);
        Assert.Equal("", result.Account);
        Assert.False(result.FromCache);
        Assert.True(result.Elapsed > TimeSpan.Zero);
    }

    [Fact]
    public async Task The_account_directory_is_set_and_octopus_variables_never_reach_the_probe()
    {
        var definition = Agent("codex", new() { ["p"] = new AgentProbe() }, "CODEX_HOME");
        var env = Env(("CODEX_HOME", "/old"), ("OCTOPUS_SIGNAL_TOKEN", "secret"), ("OCTOPUS_ITEM_ID", "item"));

        var result = await Run(definition, "p", AboutScript(), env, account: "/accounts/work");

        Assert.True(result.Success, result.Message);
        Assert.Equal("/accounts/work|unset|unset", File.ReadAllText(Log("env")));
        Assert.Equal(SignalAccounts.KeyOfDirectory("/accounts/work"), result.Account);
        Assert.Contains("userEmail", result.Json); // no select: the whole result
    }

    [Fact]
    public async Task Arguments_are_filled_and_passed_without_a_shell()
    {
        var definition = Agent("grok", new() { ["sessionUsage"] = new AgentProbe { Args = ["usage", "{sessionId}", "--x={appVersion}"] } });

        var result = await Run(definition, "sessionUsage", AboutScript(), args: new() { ["sessionId"] = "a b;$(touch pwned)" });

        Assert.True(result.Success, result.Message);
        Assert.Equal(["usage", "a b;$(touch pwned)", "--x=9.9.9"], File.ReadAllLines(Log("args")));
        Assert.False(File.Exists(Path.Combine(_dir.Path, "pwned")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("--help")]
    [InlineData("line\nbreak")]
    public async Task Missing_or_unsafe_argument_values_are_refused_without_running(string? sessionId)
    {
        var definition = Agent("grok", new() { ["sessionUsage"] = new AgentProbe { Args = ["usage", "{sessionId}"] } });
        var args = sessionId is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["sessionId"] = sessionId };

        var result = await Run(definition, "sessionUsage", AboutScript(), args: args);

        Assert.Equal(ProbeErrorKind.InvalidArgument, result.Error);
        Assert.Equal(0, Runs);
    }

    [Fact]
    public async Task Unknown_probes_and_missing_programs_are_reported()
    {
        var definition = Agent("cursor", new() { ["odd"] = new AgentProbe { Kind = "http" } });
        Assert.Equal(ProbeErrorKind.NotDeclared, (await Run(definition, "nope", AboutScript())).Error);
        Assert.Equal(ProbeErrorKind.NotDeclared, (await Run(definition, "odd", AboutScript())).Error);
        Assert.Equal(ProbeErrorKind.NotDeclared, (await Run(new AgentDefinition { Id = "x" }, "p", AboutScript())).Error);

        var missing = Agent("cursor", new() { ["p"] = new AgentProbe() });
        Assert.Equal(ProbeErrorKind.StartFailed, (await Run(missing, "p", Path.Combine(_dir.Path, "not-there"))).Error);
        Assert.Equal(ProbeErrorKind.StartFailed, (await Run(missing, "p", "")).Error);
    }

    [Fact]
    public async Task Output_that_is_not_json_or_a_non_object_with_select_is_invalid()
    {
        var text = Script(_dir.Path, "text", "echo 'Subscription: pro'\n");
        var definition = Agent("a", new()
        {
            ["json"] = new AgentProbe(),
            ["text"] = new AgentProbe { Parse = "text" },
            ["array"] = new AgentProbe { Select = ["x"] },
        });

        Assert.Equal(ProbeErrorKind.InvalidOutput, (await Run(definition, "json", text)).Error);
        var asText = await Run(definition, "text", text);
        Assert.True(asText.Success);
        Assert.Equal("\"Subscription: pro\"", asText.Json);
        Assert.Equal(ProbeErrorKind.InvalidOutput, (await Run(definition, "array", Script(_dir.Path, "array", "echo '[1,2]'\n"))).Error);
    }

    [Fact]
    public async Task A_non_zero_exit_is_a_failure()
    {
        var failing = Script(_dir.Path, "fail", "echo '{}'; exit 3\n");
        var result = await Run(Agent("a", new() { ["p"] = new AgentProbe() }), "p", failing);
        Assert.Equal(ProbeErrorKind.ExitCode, result.Error);
        Assert.Equal("exit code 3", result.Message);
        Assert.Null(result.Json);
    }

    [Fact]
    public async Task Output_over_one_megabyte_stops_the_probe()
    {
        var chatty = Script(_dir.Path, "chatty", "head -c 1200000 /dev/zero | tr '\\000' a\n");
        var result = await Run(Agent("a", new() { ["p"] = new AgentProbe() }), "p", chatty);
        Assert.Equal(ProbeErrorKind.OutputTooLarge, result.Error);
    }

    [Fact]
    public async Task A_probe_that_runs_too_long_is_killed_with_its_children()
    {
        var slow = Script(_dir.Path, "slow", """
            sleep 30 &
            echo $! > "$LOG_DIR/child.pid"
            wait
            """);
        var stopwatch = Stopwatch.StartNew();

        // Long enough for sh to start and write the pid even while other tests load the machine.
        var result = await Run(Agent("a", new() { ["p"] = new AgentProbe { TimeoutMs = 2000 } }), "p", slow);

        Assert.Equal(ProbeErrorKind.Timeout, result.Error);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(8), stopwatch.Elapsed.ToString());
        await AssertGoneAsync(int.Parse(File.ReadAllText(Log("child.pid")).Trim()));
    }

    // ---- caching and back-off ----

    [Fact]
    public async Task A_repeat_within_the_minimum_interval_returns_the_cached_result()
    {
        var definition = Agent("cursor", new() { ["account"] = new AgentProbe { Select = ["subscriptionTier"], MinIntervalSec = 60 } });
        var script = AboutScript();

        var first = await Run(definition, "account", script);
        _time.Advance(TimeSpan.FromSeconds(59));
        var second = await Run(definition, "account", script);

        Assert.Equal(1, Runs);
        Assert.True(second.FromCache);
        Assert.Equal(first.Json, second.Json);
        Assert.Equal(first.CompletedAt, second.CompletedAt);

        _time.Advance(TimeSpan.FromSeconds(2));
        var third = await Run(definition, "account", script);
        Assert.False(third.FromCache);
        Assert.Equal(2, Runs);

        // Another account is another cache entry.
        var withAccount = Agent("cursor", definition.Probes!, "CURSOR_HOME");
        await Run(withAccount, "account", script, account: "/a");
        await Run(withAccount, "account", script, account: "/b");
        Assert.Equal(4, Runs);
    }

    [Fact]
    public async Task Consecutive_failures_back_off_one_five_then_thirty_minutes_and_success_resets()
    {
        var flag = Log("succeed");
        var script = Script(_dir.Path, "flaky", $$"""
            echo run >> "$LOG_DIR/count"
            if [ -f '{{flag}}' ]; then echo '{"ok":true}'; else exit 1; fi
            """);
        var definition = Agent("a", new() { ["p"] = new AgentProbe() });

        var first = await Run(definition, "p", script);
        Assert.Equal(ProbeErrorKind.ExitCode, first.Error);
        Assert.Equal(_time.Now + TimeSpan.FromMinutes(1), first.RetryAt);

        var waiting = await Run(definition, "p", script);
        Assert.Equal(ProbeErrorKind.BackingOff, waiting.Error);
        Assert.Equal(first.RetryAt, waiting.RetryAt);
        Assert.Equal(1, Runs);

        _time.Advance(TimeSpan.FromMinutes(1));
        var second = await Run(definition, "p", script);
        Assert.Equal(_time.Now + TimeSpan.FromMinutes(5), second.RetryAt);

        _time.Advance(TimeSpan.FromMinutes(5));
        var third = await Run(definition, "p", script);
        Assert.Equal(_time.Now + TimeSpan.FromMinutes(30), third.RetryAt);

        _time.Advance(TimeSpan.FromMinutes(30));
        var fourth = await Run(definition, "p", script);
        Assert.Equal(_time.Now + TimeSpan.FromMinutes(30), fourth.RetryAt);
        Assert.Equal(4, Runs);

        File.WriteAllText(flag, "");
        _time.Advance(TimeSpan.FromMinutes(30));
        var success = await Run(definition, "p", script);
        Assert.True(success.Success);
        Assert.Null(success.RetryAt);

        File.Delete(flag);
        var again = await Run(definition, "p", script);
        Assert.Equal(_time.Now + TimeSpan.FromMinutes(1), again.RetryAt); // the count started over
    }

    // ---- concurrency ----

    private string ConcurrencyScript() => Script(_dir.Path, "busy", """
        mkdir "$LOG_DIR/running.$$"
        ls -d "$LOG_DIR"/running.* | wc -l | tr -d ' ' >> "$LOG_DIR/concurrency"
        echo run >> "$LOG_DIR/count"
        sleep 0.4
        rmdir "$LOG_DIR/running.$$"
        echo '{}'
        """);

    private int MaxConcurrency => File.ReadAllLines(Log("concurrency")).Select(int.Parse).Max();

    [Fact]
    public async Task At_most_two_probes_run_at_once_in_the_whole_app()
    {
        var script = ConcurrencyScript();
        var definition = Agent("a", Enumerable.Range(1, 5).ToDictionary(i => $"p{i}", _ => new AgentProbe()));
        var stopwatch = Stopwatch.StartNew();

        var results = await Task.WhenAll(Enumerable.Range(1, 5).Select(i => Run(definition, $"p{i}", script)));

        Assert.All(results, r => Assert.True(r.Success, r.Message));
        Assert.Equal(5, Runs);
        Assert.True(MaxConcurrency <= ProbeRunner.MaxConcurrentProbes, $"saw {MaxConcurrency} at once");
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromSeconds(1.1), $"5 runs of 0.4 s two at a time took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task One_run_per_agent_probe_and_account_and_exclusive_per_account_across_probes()
    {
        var script = ConcurrencyScript();
        var sessions = Agent("grok", new() { ["usage"] = new AgentProbe { Args = ["{sessionId}"] } });
        await Task.WhenAll(Run(sessions, "usage", script, args: new() { ["sessionId"] = "s1" }), Run(sessions, "usage", script, args: new() { ["sessionId"] = "s2" }));
        Assert.Equal(1, MaxConcurrency);

        File.Delete(Log("concurrency"));
        var shared = Agent("codex", new()
        {
            ["a"] = new AgentProbe { ExclusivePer = "account" },
            ["b"] = new AgentProbe { ExclusivePer = "account" },
        }, "CODEX_HOME");
        await Task.WhenAll(Run(shared, "a", script, account: "/x"), Run(shared, "b", script, account: "/x"));
        Assert.Equal(1, MaxConcurrency);
        Assert.Equal(4, Runs);
    }

    [Fact]
    public async Task Concurrent_identical_requests_share_one_run()
    {
        var script = ConcurrencyScript();
        var definition = Agent("a", new() { ["p"] = new AgentProbe() });
        var results = await Task.WhenAll(Run(definition, "p", script), Run(definition, "p", script), Run(definition, "p", script));
        Assert.Equal(1, Runs);
        Assert.All(results, r => Assert.True(r.Success));
    }

    [Fact]
    public async Task A_caller_giving_up_does_not_cancel_the_shared_run()
    {
        var script = ConcurrencyScript();
        var definition = Agent("a", new() { ["p"] = new AgentProbe() });
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var impatient = _runner.RunAsync(new ProbeRequest(definition, "p", script, Env()), cancel.Token);
        var patient = Run(definition, "p", script);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => impatient);
        Assert.True((await patient).Success);
        Assert.Equal(1, Runs);
    }

    [Fact]
    public async Task A_success_is_published_on_the_hub_as_a_probe_channel()
    {
        using var root = new TempDir();
        using var hub = new SignalHub(new TestPaths(root.Path), Builtins.All);
        using var runner = new ProbeRunner(hub, _time);
        var (subscription, first) = Next(hub, "cursor", "probe:account");
        using var _ = subscription;

        var result = await runner.RunAsync(new ProbeRequest(Agent("cursor", new() { ["account"] = new AgentProbe { Select = ["subscriptionTier"] } }), "account", AboutScript(), Env()));
        var signal = await first;

        Assert.Equal(result.Json, signal.Payload);
        Assert.Equal("", signal.ItemId);
    }

    // ---- jsonRpcStdio ----

    private string AppServer() => Script(_dir.Path, "codex", """
        [ "$1" = "app-server" ] || exit 64
        echo $$ > "$LOG_DIR/server.pid"
        sleep 300 &
        echo $! > "$LOG_DIR/child.pid"
        while IFS= read -r line; do
          printf '%s\n' "$line" >> "$LOG_DIR/requests"
          id=$(printf '%s' "$line" | sed -n 's/.*"id":\([0-9][0-9]*\).*/\1/p')
          case "$line" in
            *'"method":"initialize"'*)
              printf '{"jsonrpc":"2.0","method":"note","params":{"x":1}}\n'
              printf 'a log line that is not json\n'
              printf '{"jsonrpc":"2.0","id":%s,"result":{"userAgent":"fake"}}\n' "$id" ;;
            *'"method":"account/rateLimits/read"'*)
              printf '{"jsonrpc":"2.0","id":%s,"result":{"accountId":"acct-1","email":"me@example.com","rateLimits":{"primary":{"usedPercent":42,"windowDurationMins":43200,"resetsAt":1794069588}},"rateLimitsByLimitId":{"codex":{"limitId":"codex"}}}}\n' "$id" ;;
            *'"method":"fail"'*)
              printf '{"jsonrpc":"2.0","id":%s,"error":{"code":-32002,"message":"Not initialized"}}\n' "$id" ;;
            *'"method":"exit"'*)
              exit 0 ;;
          esac
        done
        """);

    private static AgentDefinition CodexWith(params ProbeRpcRequest[] requests) => Agent("codex", new()
    {
        ["rateLimits"] = new AgentProbe
        {
            Kind = ProbeKinds.JsonRpcStdio,
            Args = ["app-server"],
            Requests = [.. requests],
            Select = ["rateLimits", "rateLimitsByLimitId"],
            TimeoutMs = 5000,
        },
    }, "CODEX_HOME");

    private static ProbeRpcRequest Request(string method, string? paramsJson = null, bool result = false) => new()
    {
        Method = method,
        Params = paramsJson is null ? null : JsonDocument.Parse(paramsJson).RootElement.Clone(),
        Result = result,
    };

    [Fact]
    public async Task Json_rpc_probe_sends_requests_in_order_returns_the_marked_result_and_stops_the_server()
    {
        var codex = Builtins.Get("codex");
        var server = AppServer();

        var result = await Run(codex, "rateLimits", server, account: "/accounts/codex");

        Assert.True(result.Success, result.Message);
        using var json = JsonDocument.Parse(result.Json!);
        Assert.Equal(43200, json.RootElement.GetProperty("rateLimits").GetProperty("primary").GetProperty("windowDurationMins").GetInt32());
        Assert.True(json.RootElement.TryGetProperty("rateLimitsByLimitId", out _));
        Assert.DoesNotContain("acct-1", result.Json);
        Assert.DoesNotContain("example.com", result.Json);
        Assert.Equal(SignalAccounts.KeyOfDirectory("/accounts/codex"), result.Account);

        var requests = File.ReadAllLines(Log("requests")).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        Assert.Equal(2, requests.Count);
        Assert.Equal("2.0", requests[0].GetProperty("jsonrpc").GetString());
        Assert.Equal(1, requests[0].GetProperty("id").GetInt32());
        Assert.Equal("initialize", requests[0].GetProperty("method").GetString());
        Assert.Equal("9.9.9", requests[0].GetProperty("params").GetProperty("clientInfo").GetProperty("version").GetString());
        Assert.Equal("octopus", requests[0].GetProperty("params").GetProperty("clientInfo").GetProperty("name").GetString());
        Assert.Equal(2, requests[1].GetProperty("id").GetInt32());
        Assert.Equal("account/rateLimits/read", requests[1].GetProperty("method").GetString());
        Assert.False(requests[1].TryGetProperty("params", out _));

        await AssertGoneAsync(int.Parse(File.ReadAllText(Log("server.pid")).Trim()));
        await AssertGoneAsync(int.Parse(File.ReadAllText(Log("child.pid")).Trim()));
    }

    [Fact]
    public async Task Json_rpc_without_a_marked_result_uses_the_last_request()
    {
        var result = await Run(CodexWith(Request("initialize", "{}"), Request("account/rateLimits/read")), "rateLimits", AppServer());
        Assert.True(result.Success, result.Message);
        Assert.Contains("43200", result.Json);
    }

    [Fact]
    public async Task Json_rpc_error_responses_are_reported()
    {
        var result = await Run(CodexWith(Request("initialize", "{}"), Request("fail", result: true)), "rateLimits", AppServer());
        Assert.Equal(ProbeErrorKind.RpcError, result.Error);
        Assert.Equal("fail: Not initialized", result.Message);
    }

    [Fact]
    public async Task Json_rpc_server_that_exits_before_answering_fails_fast()
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await Run(CodexWith(Request("exit"), Request("account/rateLimits/read", result: true)), "rateLimits", AppServer());
        Assert.Equal(ProbeErrorKind.ExitCode, result.Error);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4), stopwatch.Elapsed.ToString());
    }

    [Fact]
    public async Task Json_rpc_server_that_never_answers_times_out()
    {
        var definition = CodexWith(Request("silent", result: true));
        // Long enough for sh to start and write its pid even while other tests load the machine.
        definition.Probes!["rateLimits"].TimeoutMs = 2000;
        var result = await Run(definition, "rateLimits", AppServer());
        Assert.Equal(ProbeErrorKind.Timeout, result.Error);
        await AssertGoneAsync(int.Parse(File.ReadAllText(Log("server.pid")).Trim()));
    }

    [Fact]
    public void Json_rpc_requests_are_one_line_with_placeholders_filled_in_string_values_only()
    {
        var request = Request("initialize", """{"clientInfo":{"name":"octopus","version":"{appVersion}"},"n":1,"list":["{appVersion}",true]}""");
        var line = System.Text.Encoding.UTF8.GetString(ProbeProcess.BuildRequest(7, request, s => s.Replace("{appVersion}", "1.2.3")));
        Assert.Equal("""{"jsonrpc":"2.0","id":7,"method":"initialize","params":{"clientInfo":{"name":"octopus","version":"1.2.3"},"n":1,"list":["1.2.3",true]}}""" + "\n", line);
    }

    private static async Task AssertGoneAsync(int pid)
    {
        await WaitUntilAsync(() =>
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return process.HasExited;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }, $"process {pid} to be gone");
    }
}
