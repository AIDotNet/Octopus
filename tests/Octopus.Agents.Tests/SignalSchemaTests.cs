using System.Text.Json;
using Octopus.Agents.Signals;
using Octopus.Core.Agents;

namespace Octopus.Agents.Tests;

public class SignalSchemaTests
{
    private static readonly string[] SignalAgents = ["claude", "codex", "grok", "cursor", "opencode"];

    [Fact]
    public void The_five_agents_declare_signals_and_the_others_do_not()
    {
        foreach (var definition in Builtins.All.Definitions)
        {
            if (SignalAgents.Contains(definition.Id))
            {
                Assert.NotNull(definition.Signals);
                Assert.False(string.IsNullOrEmpty(definition.Signals!.MinVersion), definition.Id);
                Assert.NotEmpty(definition.Signals.Channels);
            }
            else
            {
                Assert.Null(definition.Signals);
                Assert.Null(definition.Probes);
            }
        }
    }

    [Fact]
    public void Channels_are_well_formed_and_status_names_only_declared_channels()
    {
        foreach (var id in SignalAgents)
        {
            var signals = Builtins.Get(id).Signals!;
            foreach (var (name, channel) in signals.Channels)
            {
                Assert.Contains(channel.Via, new[] { SignalVia.StatusLine, SignalVia.Hook, SignalVia.Notify, SignalVia.Plugin });
                if (channel.Via is SignalVia.Hook or SignalVia.Plugin)
                {
                    Assert.False(string.IsNullOrEmpty(channel.Event), $"{id}/{name}");
                }
            }
            var status = signals.Status!;
            foreach (var channel in status.Working.Concat(status.Waiting).Concat(status.Done).Concat(status.Error))
            {
                Assert.True(signals.Channels.ContainsKey(channel), $"{id}: status names undeclared channel {channel}");
            }
            foreach (var attach in signals.Attach)
            {
                Assert.False(attach.Verified, $"{id}: no attach method has run against a real session yet (13-P0 §4)");
                Assert.False(string.IsNullOrEmpty(attach.Note), id);
            }
        }
    }

    [Fact]
    public void Claude_matches_the_design_example()
    {
        var signals = Builtins.Get("claude").Signals!;
        Assert.Equal("2.1.80", signals.MinVersion);
        Assert.Equal("CLAUDE_CONFIG_DIR", signals.Account!.Env);
        Assert.Equal("~/.claude", signals.Account.Default);
        Assert.Equal(["--settings", "{signalSettings}"], signals.Attach[0].Args);
        Assert.Equal(["--plugin-dir", "{signalPluginDir}"], signals.Attach[1].Args);
        var statusLine = signals.Channels["statusline"];
        Assert.Equal(SignalVia.StatusLine, statusLine.Via);
        Assert.True(statusLine.ChainUserCommand);
        Assert.Equal(30, statusLine.RefreshInterval);
        Assert.Equal("PermissionRequest", signals.Channels["permission-request"].Event);
        Assert.Equal(["permission-request", "notification"], signals.Status!.Waiting);
        Assert.Equal([".credentials.json"], signals.CredentialFiles);
    }

    [Fact]
    public void Codex_hears_turn_completion_through_notify_and_asks_rate_limits_over_app_server()
    {
        var codex = Builtins.Get("codex");
        Assert.Equal(["-c", "notify={signalNotify}"], codex.Signals!.Attach.Single().Args);
        Assert.Equal(SignalVia.Notify, codex.Signals.Channels["turn-complete"].Via);
        Assert.Equal(["turn-complete"], codex.Signals.Status!.Done);

        var probe = codex.Probes!["rateLimits"];
        Assert.Equal(ProbeKinds.JsonRpcStdio, probe.Kind);
        Assert.Equal(["app-server"], probe.Args);
        Assert.Equal(["initialize", "account/rateLimits/read"], probe.Requests.Select(r => r.Method));
        Assert.True(probe.Requests[1].Result);
        Assert.Equal("{appVersion}", probe.Requests[0].Params!.Value.GetProperty("clientInfo").GetProperty("version").GetString());
        Assert.Null(probe.Requests[1].Params);
        Assert.Equal(["rateLimits", "rateLimitsByLimitId"], probe.Select);
        Assert.Equal(30000, probe.TimeoutMs);
        Assert.Equal(120, probe.MinIntervalSec);
        Assert.Equal("account", probe.ExclusivePer);
    }

    [Fact]
    public void Grok_has_no_active_attach_because_its_overlay_drops_status_line_and_hooks()
    {
        var grok = Builtins.Get("grok");
        Assert.Empty(grok.Signals!.Attach);
        Assert.Equal("GROK_HOME", grok.Signals.Account!.Env);
        Assert.True(grok.Signals.Channels["statusline"].ChainUserCommand);
        var usage = grok.Probes!["sessionUsage"];
        Assert.Equal(["usage", "{sessionId}"], usage.Args);
        Assert.Equal(["sessionId", "updatedAt", "session"], usage.Select);
        Assert.Equal(10, usage.MinIntervalSec);
    }

    [Fact]
    public void Cursor_and_opencode_attach_per_session_and_cursor_shares_only_the_tier()
    {
        var cursor = Builtins.Get("cursor");
        Assert.Equal(["--plugin-dir", "{signalPluginDir}"], cursor.Signals!.Attach.Single().Args);
        Assert.Null(cursor.Signals.Account);
        Assert.Equal(["about", "--format", "json"], cursor.Probes!["account"].Args);
        Assert.Equal(["subscriptionTier"], cursor.Probes["account"].Select);
        Assert.Equal(86400, cursor.Probes["account"].MinIntervalSec);

        var opencode = Builtins.Get("opencode");
        Assert.Equal("{signalConfig}", opencode.Signals!.Attach.Single().Env!["OPENCODE_CONFIG_CONTENT"]);
        Assert.Equal("session.usage.updated", opencode.Signals.Channels["session-usage"].Event);
        Assert.Null(opencode.Probes);
    }

    [Fact]
    public void Signals_and_probes_round_trip_through_the_source_generated_context()
    {
        var codex = Builtins.Get("codex");
        var json = JsonSerializer.Serialize(codex, AgentJsonContext.Default.AgentDefinition);
        var back = JsonSerializer.Deserialize(json, AgentJsonContext.Default.AgentDefinition)!;
        Assert.Equal("CODEX_HOME", back.Signals!.Account!.Env);
        Assert.True(back.Signals.Channels["turn-complete"].ChainUserCommand);
        var request = back.Probes!["rateLimits"].Requests[0];
        Assert.Equal("octopus", request.Params!.Value.GetProperty("clientInfo").GetProperty("name").GetString());
        Assert.DoesNotContain("\"params\": null", json);
    }

    [Fact]
    public void A_user_definition_can_declare_signals_and_probes()
    {
        var json = """
            { "id": "mine", "signals": { "minVersion": "1.0.0", "dialect": "claude",
                "attach": [ { "args": ["--settings", "{signalSettings}"], "verified": true } ],
                "channels": { "statusline": { "via": "statusLine", "chainUserCommand": true } } },
              "probes": { "p": { "kind": "command", "args": ["x"], "timeoutMs": 500 } } }
            """;
        var definition = JsonSerializer.Deserialize(json, AgentJsonContext.Default.AgentDefinition)!;
        Assert.Equal("claude", definition.Signals!.Dialect);
        Assert.True(definition.Signals.Attach[0].Verified);
        Assert.Equal("json", definition.Probes!["p"].Parse);
        Assert.Equal(500, definition.Probes["p"].TimeoutMs);
        Assert.Empty(definition.Probes["p"].Select);
    }

    [Fact]
    public void Status_map_follows_the_definition_and_prefers_the_most_urgent()
    {
        var claude = Builtins.Get("claude");
        Assert.Equal(SignalStatus.Working, SignalStatusMap.Resolve(claude, "prompt-submit"));
        Assert.Equal(SignalStatus.Waiting, SignalStatusMap.Resolve(claude, "permission-request"));
        Assert.Equal(SignalStatus.Waiting, SignalStatusMap.Resolve(claude, "notification"));
        Assert.Equal(SignalStatus.Done, SignalStatusMap.Resolve(claude, "stop"));
        Assert.Equal(SignalStatus.Error, SignalStatusMap.Resolve(claude, "stop-failure"));
        Assert.Null(SignalStatusMap.Resolve(claude, "statusline"));
        Assert.Null(SignalStatusMap.Resolve(Builtins.Get("aider"), "stop"));

        var both = new AgentDefinition
        {
            Signals = new AgentSignals { Status = new SignalStatusSpec { Working = ["x"], Error = ["x"] } },
        };
        Assert.Equal(SignalStatus.Error, SignalStatusMap.Resolve(both, "x"));
        Assert.Equal(SignalStatus.Done, SignalStatusMap.Resolve(Builtins.Get("opencode"), new AgentSignal("opencode", "session-idle", "i", "", 0, 0, "{}")));
    }

    [Theory]
    [InlineData("2.1.322", "2.1.80", true)]
    [InlineData("2.1.80", "2.1.80", true)]
    [InlineData("2.1.79", "2.1.80", false)]
    [InlineData("2.2", "2.1.80", true)]
    [InlineData("2026.08.11", "2026.08.11", true)]
    [InlineData("2026.8.12", "2026.08.11", true)]
    [InlineData("2026.08.10", "2026.08.11", false)]
    [InlineData("0.167.0-alpha.3", "0.167.0", true)]
    [InlineData("v1.19.3", "1.19.3", true)]
    [InlineData(null, "1.0.0", false)]
    [InlineData("unknown", "1.0.0", false)]
    [InlineData(null, null, true)]
    public void Versions_compare_by_leading_numbers(string? installed, string? minimum, bool expected) =>
        Assert.Equal(expected, AgentVersions.IsAtLeast(installed, minimum));

    [Fact]
    public void Account_key_is_a_short_hash_and_the_default_directory_equals_unset()
    {
        var claude = Builtins.Get("claude");
        var home = OperatingSystem.IsWindows() ? @"C:\Users\x" : "/Users/x";
        var unset = SignalAccounts.Key(claude, "", home);
        var explicitDefault = SignalAccounts.Key(claude, Path.Combine(home, ".claude") + Path.DirectorySeparatorChar, home);
        var tilde = SignalAccounts.Key(claude, "~/.claude", home);
        var other = SignalAccounts.Key(claude, Path.Combine(home, "work"), home);

        Assert.Equal(SignalAccounts.KeyLength, unset.Length);
        Assert.Matches("^[0-9a-f]+$", unset);
        Assert.Equal(unset, explicitDefault);
        Assert.Equal(unset, tilde);
        Assert.NotEqual(unset, other);
        Assert.DoesNotContain("claude", unset);
        Assert.Equal("", SignalAccounts.Key(Builtins.Get("cursor"), "/anything", home));
        Assert.Equal("accounts/work", SignalAccounts.DefaultLabel("/Users/x/accounts/work"));
    }
}
