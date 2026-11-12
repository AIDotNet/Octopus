using Octopus.Agents.Signals;
using Octopus.Platform;

namespace Octopus.Agents.Tests;

/// <summary>Every account of an agent is found without waiting for it to report (design/plugins/12 §3).</summary>
public sealed class AgentAccountDiscoveryTests : IDisposable
{
    private readonly TempDir _root = new();
    private readonly string _home;
    private readonly SignalHub _hub;

    public AgentAccountDiscoveryTests()
    {
        _home = Path.Combine(_root.Path, "home");
        Directory.CreateDirectory(_home);
        _hub = new SignalHub(new TestPaths(_root.Path), Builtins.All);
    }

    public void Dispose()
    {
        _hub.Dispose();
        _root.Dispose();
    }

    private AgentAccountDiscovery Discovery(Dictionary<string, string>? environment = null, TimeProvider? time = null, SignalHub? hub = null) =>
        new(hub ?? _hub, Builtins.All, new Login(environment ?? []), new TestPaths(_root.Path), time, _home);

    private string Make(params string[] parts)
    {
        var path = Path.Combine([_home, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public async Task The_default_directory_and_the_one_in_the_login_environment_are_both_accounts()
    {
        var codex = Make(".codex");
        var work = Make("work", "codex");

        var accounts = await Discovery(new() { ["CODEX_HOME"] = work }).DiscoverAsync("codex");

        Assert.Equal(new[] { codex, work }.Order(), accounts.Select(a => a.Directory).Order());
        Assert.All(accounts, a => Assert.Equal(SignalAccounts.KeyOfDirectory(a.Directory), a.Key));
        // Registered on the hub: a probe can now name them.
        Assert.Equal(2, _hub.Accounts("codex").Count);
    }

    [Fact]
    public async Task A_directory_that_does_not_exist_is_not_an_account()
    {
        var accounts = await Discovery(new() { ["CODEX_HOME"] = Path.Combine(_home, "gone") }).DiscoverAsync("codex");

        Assert.Empty(accounts);
    }

    [Fact]
    public async Task Claude_configuration_folders_of_account_managers_are_found_on_disk()
    {
        var manager = Make(".rec", "claude", "accounts", "55232", "config");
        File.WriteAllText(Path.Combine(manager, ".claude.json"), "{}");
        var plain = Make(".claude");

        var accounts = await Discovery().DiscoverAsync("claude");

        Assert.Equal(new[] { plain, manager }.Order(), accounts.Select(a => a.Directory).Order());
    }

    [Fact]
    public async Task Codex_folders_are_not_searched_for_on_disk()
    {
        // auth.json and config.toml are shared with Grok: a folder with both is not necessarily Codex's.
        var other = Make(".grok");
        File.WriteAllText(Path.Combine(other, "auth.json"), "{}");
        File.WriteAllText(Path.Combine(other, "config.toml"), "");

        Assert.Empty(await Discovery().DiscoverAsync("codex"));
    }

    [Fact]
    public async Task An_account_seen_once_is_still_there_after_a_restart()
    {
        var seen = Make("accounts", "side");
        _hub.RegisterAccount("codex", seen);
        await Discovery().DiscoverAsync("codex");

        // A new run: a new hub that has seen nothing.
        using var fresh = new SignalHub(new TestPaths(_root.Path), Builtins.All);
        var accounts = await Discovery(hub: fresh).DiscoverAsync("codex");

        Assert.Equal(seen, Assert.Single(accounts).Directory);

        // Removed from disk, it is dropped.
        Directory.Delete(seen);
        using var later = new SignalHub(new TestPaths(_root.Path), Builtins.All);
        Assert.Empty(await Discovery(hub: later).DiscoverAsync("codex"));
    }

    [Fact]
    public async Task The_disk_is_searched_again_only_after_a_few_minutes()
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var discovery = Discovery(time: time);
        Assert.Empty(await discovery.DiscoverAsync("claude"));

        var added = Make(".config", "claude-two");
        File.WriteAllText(Path.Combine(added, ".claude.json"), "{}");
        Assert.Empty(await discovery.DiscoverAsync("claude"));

        time.Advance(AgentAccountDiscovery.ScanEvery + TimeSpan.FromSeconds(1));
        Assert.Equal(added, Assert.Single(await discovery.DiscoverAsync("claude")).Directory);
    }

    [Fact]
    public async Task An_agent_with_one_account_has_nothing_to_discover()
    {
        Make(".cursor");

        Assert.Empty(await Discovery().DiscoverAsync("cursor"));
    }

    private sealed class Login(Dictionary<string, string> environment) : ILoginEnvironment
    {
        public Task<IReadOnlyDictionary<string, string>> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(environment);
    }
}
