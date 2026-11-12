using System.Text;
using Octopus.Agents.Signals;
using static Octopus.Agents.Tests.SignalTestHelpers;

namespace Octopus.Agents.Tests;

/// <summary>The hub against real connections on a Unix domain socket (macOS and Linux).</summary>
public sealed class SignalHubTests : IDisposable
{
    private readonly ShortTempDir _root = new();
    private readonly SignalHub _hub;

    public SignalHubTests()
    {
        _hub = new SignalHub(new TestPaths(_root.Path), Builtins.All);
    }

    public void Dispose()
    {
        _hub.Dispose();
        _root.Dispose();
    }

    private SignalEndpoint Start()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix domain socket");
        Assert.True(_hub.Start());
        return _hub.Endpoint!;
    }

    private static UnixFileMode Mode(string path) => OperatingSystem.IsWindows() ? default : File.GetUnixFileMode(path);

    [Fact]
    public void Endpoint_is_a_socket_in_a_private_directory_under_the_cache_with_a_random_token()
    {
        var endpoint = Start();
        var directory = Path.GetDirectoryName(endpoint.Address)!;

        Assert.StartsWith(Path.Combine(_root.Path, "cache", "signal-hub"), directory);
        Assert.Equal(SignalHub.SocketFileName, Path.GetFileName(endpoint.Address));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, Mode(directory));
        Assert.Matches("^[0-9a-f]{64}$", endpoint.Token);
        Assert.True(_hub.Start(), "Start is idempotent");
        Assert.Same(endpoint, _hub.Endpoint);

        using var other = new SignalHub(new TestPaths(_root.Path), Builtins.All);
        Assert.True(other.Start());
        Assert.NotEqual(endpoint.Token, other.Endpoint!.Token);
        Assert.NotEqual(endpoint.Address, other.Endpoint.Address);
    }

    [Fact]
    public async Task A_valid_envelope_reaches_subscribers_with_the_account_hashed()
    {
        var endpoint = Start();
        var (subscription, first) = Next(_hub, "claude", "statusline");
        using var _ = subscription;
        var payload = "{\"rate_limits\":{\"five_hour\":{\"used_percentage\":42}},\"text\":\"引号\\\" 和换行\\n\"}";

        await SendAsync(endpoint.Address, Envelope(endpoint.Token, "claude", "statusline", payload, "item-7", "/Users/x/accounts/work"));
        var signal = await first;

        Assert.Equal("claude", signal.AgentId);
        Assert.Equal("statusline", signal.Channel);
        Assert.Equal("item-7", signal.ItemId);
        Assert.Equal(payload, signal.Payload);
        Assert.Equal(1794069588307, signal.SentAt);
        Assert.Equal(SignalAccounts.KeyOfDirectory("/Users/x/accounts/work"), signal.Account);
        Assert.DoesNotContain("/", signal.Account);
        Assert.True(signal.ReceivedAt > 0);
        Assert.Equal(new SignalHubStatistics(1, 0), _hub.Statistics);

        var account = Assert.Single(_hub.Accounts("claude"));
        Assert.Equal("/Users/x/accounts/work", account.Directory);
        Assert.Equal("accounts/work", account.DefaultLabel);
        Assert.Equal(signal.Account, account.Key);
    }

    [Fact]
    public async Task An_unset_account_variable_means_the_definitions_default_directory()
    {
        var endpoint = Start();
        var (subscription, first) = Next(_hub, "codex", null);
        using var _ = subscription;

        await SendAsync(endpoint.Address, Envelope(endpoint.Token, "codex", "turn-complete", "{\"type\":\"agent-turn-complete\"}", account: ""));
        var signal = await first;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(SignalAccounts.KeyOfDirectory(Path.Combine(home, ".codex")), signal.Account);
    }

    [Theory]
    [InlineData("wrong-token")]
    [InlineData("version")]
    [InlineData("unknown-agent")]
    [InlineData("agent-without-signals")]
    [InlineData("undeclared-channel")]
    [InlineData("not-json")]
    [InlineData("array")]
    [InlineData("payload-not-string")]
    [InlineData("trailing-garbage")]
    [InlineData("empty")]
    public async Task Bad_envelopes_are_counted_and_dropped(string kind)
    {
        var endpoint = Start();
        var delivered = 0;
        using var _ = _hub.Subscribe(null, null, _ => Interlocked.Increment(ref delivered));
        var valid = Envelope(endpoint.Token, "claude", "stop", "{}");
        var text = kind switch
        {
            "wrong-token" => Envelope((endpoint.Token[0] == '0' ? "1" : "0") + endpoint.Token[1..], "claude", "stop", "{}"),
            "version" => Envelope(endpoint.Token, "claude", "stop", "{}", version: 2),
            "unknown-agent" => Envelope(endpoint.Token, "nobody", "stop", "{}"),
            "agent-without-signals" => Envelope(endpoint.Token, "aider", "stop", "{}"),
            "undeclared-channel" => Envelope(endpoint.Token, "claude", "PreToolUse", "{}"),
            "not-json" => "hello",
            "array" => "[" + valid + "]",
            "payload-not-string" => valid.Replace("\"payload\":\"{}\"", "\"payload\":{}"),
            "trailing-garbage" => valid + "x",
            _ => "",
        };

        await SendAsync(endpoint.Address, text);
        await WaitUntilAsync(() => _hub.Statistics.Dropped == 1, "the drop");

        Assert.Equal(0, delivered);
        Assert.Equal(0, _hub.Statistics.Received);
        Assert.Null(_hub.LastSignal("claude", "stop"));
    }

    [Fact]
    public async Task An_envelope_over_the_size_cap_is_dropped_and_the_hub_keeps_working()
    {
        var endpoint = Start();
        var huge = Encoding.UTF8.GetBytes(Envelope(endpoint.Token, "claude", "stop", new string('a', SignalHub.MaxEnvelopeBytes)));
        try
        {
            await SendAsync(endpoint.Address, huge);
        }
        catch (System.Net.Sockets.SocketException)
        {
            // The hub may close the connection before everything is written.
        }
        await WaitUntilAsync(() => _hub.Statistics.Dropped == 1, "the drop");

        var (subscription, first) = Next(_hub, "claude", "stop");
        using var _ = subscription;
        await SendAsync(endpoint.Address, Envelope(endpoint.Token, "claude", "stop", "{\"ok\":true}"));
        Assert.Equal("{\"ok\":true}", (await first).Payload);
    }

    [Fact]
    public async Task Last_signal_is_kept_per_item_and_channel_and_forgotten_with_the_item()
    {
        var endpoint = Start();
        await SendAsync(endpoint.Address, Envelope(endpoint.Token, "claude", "statusline", "a1", "item-a"));
        await WaitUntilAsync(() => _hub.LastSignal("claude", "statusline", "item-a") is not null, "item-a");
        await SendAsync(endpoint.Address, Envelope(endpoint.Token, "claude", "statusline", "b1", "item-b"));
        await WaitUntilAsync(() => _hub.LastSignal("claude", "statusline", "item-b") is not null, "item-b");
        await SendAsync(endpoint.Address, Envelope(endpoint.Token, "claude", "statusline", "a2", "item-a"));
        await WaitUntilAsync(() => _hub.LastSignal("claude", "statusline", "item-a")?.Payload == "a2", "item-a again");

        Assert.Equal("b1", _hub.LastSignal("claude", "statusline", "item-b")!.Payload);
        Assert.Equal("a2", _hub.LastSignal("CLAUDE", "statusline")!.Payload); // latest across items; ids are case-insensitive
        Assert.Null(_hub.LastSignal("claude", "stop", "item-a"));

        _hub.ForgetItem("item-a");
        Assert.Null(_hub.LastSignal("claude", "statusline", "item-a"));
        Assert.Equal("b1", _hub.LastSignal("claude", "statusline")!.Payload);
    }

    [Fact]
    public async Task Subscribers_are_filtered_and_a_throwing_one_does_not_stop_the_others()
    {
        var endpoint = Start();
        var stops = new List<string>();
        using var throwing = _hub.Subscribe(null, null, _ => throw new InvalidOperationException("bug"));
        using var onlyStop = _hub.Subscribe("claude", "stop", s => { lock (stops) stops.Add(s.Payload); });
        var codexSignals = 0;
        using var onlyCodex = _hub.Subscribe("codex", null, _ => Interlocked.Increment(ref codexSignals));
        var (all, first) = Next(_hub, null, "statusline");
        using var _ = all;

        await SendAsync(endpoint.Address, Envelope(endpoint.Token, "claude", "stop", "s1"));
        await WaitUntilAsync(() => { lock (stops) return stops.Count == 1; }, "stop");
        await SendAsync(endpoint.Address, Envelope(endpoint.Token, "claude", "statusline", "x"));
        await first;

        Assert.Equal(["s1"], stops);
        Assert.Equal(0, codexSignals);
        Assert.Equal(0, _hub.Statistics.Dropped);

        onlyStop.Dispose();
        await SendAsync(endpoint.Address, Envelope(endpoint.Token, "claude", "stop", "s2"));
        await WaitUntilAsync(() => _hub.LastSignal("claude", "stop")?.Payload == "s2", "s2");
        Assert.Equal(["s1"], stops);
    }

    [Fact]
    public async Task Many_connections_at_once_are_all_delivered()
    {
        var endpoint = Start();
        var count = 0;
        using var _ = _hub.Subscribe("claude", "stop", _ => Interlocked.Increment(ref count));
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => SendAsync(endpoint.Address, Envelope(endpoint.Token, "claude", "stop", $"{i}", $"item-{i}"))));
        await WaitUntilAsync(() => Volatile.Read(ref count) == 20, "20 signals");
        Assert.Equal(20, _hub.Statistics.Received);
    }

    [Fact]
    public void Publish_delivers_in_process_signals_such_as_probe_results()
    {
        var received = new List<AgentSignal>();
        using var _ = _hub.Subscribe("codex", "probe:rateLimits", received.Add);
        _hub.Publish(new AgentSignal("Codex", "probe:rateLimits", "", "abc", 1, 2, "{}"));
        Assert.Equal("codex", Assert.Single(received).AgentId);
        Assert.NotNull(_hub.LastSignal("codex", "probe:rateLimits"));
    }

    [Fact]
    public async Task Dispose_removes_the_socket_and_its_directory()
    {
        var endpoint = Start();
        var directory = Path.GetDirectoryName(endpoint.Address)!;
        Assert.True(File.Exists(endpoint.Address) || Directory.Exists(directory));

        _hub.Dispose();

        Assert.False(Directory.Exists(directory));
        Assert.Null(_hub.Endpoint);
        Assert.False(_hub.Start());
        await Assert.ThrowsAnyAsync<Exception>(() => SendAsync(endpoint.Address, "x"));
    }

    [Fact]
    public void A_directory_left_by_a_dead_process_is_removed_at_start()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix domain socket");
        var root = Path.Combine(_root.Path, "cache", "signal-hub");
        var stale = Path.Combine(root, "deadbeef0000");
        var alive = Path.Combine(root, "cafebabe0000");
        Directory.CreateDirectory(stale);
        Directory.CreateDirectory(alive);
        File.WriteAllText(Path.Combine(stale, "owner"), "999999");
        File.WriteAllText(Path.Combine(alive, "owner"), Environment.ProcessId.ToString());

        Assert.True(_hub.Start());

        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(alive));
    }

    [Fact]
    public void A_cache_path_too_long_for_a_socket_falls_back_to_a_private_temporary_directory()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix domain socket");
        var longRoot = Path.Combine(_root.Path, new string('x', 120));
        using var hub = new SignalHub(new TestPaths(longRoot), Builtins.All);

        Assert.True(hub.Start());

        var directory = Path.GetDirectoryName(hub.Endpoint!.Address)!;
        Assert.True(Encoding.UTF8.GetByteCount(hub.Endpoint.Address) <= 100);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, Mode(directory));
        hub.Dispose();
        Assert.False(Directory.Exists(directory));
    }
}
