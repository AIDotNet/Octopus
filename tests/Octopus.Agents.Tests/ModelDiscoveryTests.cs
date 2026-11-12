using Octopus.Platform;

namespace Octopus.Agents.Tests;

public class ModelDiscoveryTests
{
    [Fact]
    public void Lines_parser_strips_ansi_and_skips_prose()
    {
        var parsed = ModelDiscovery.Parse("\u001b[32mopencode/big-pickle\u001b[0m\n\nSome note here\nanthropic/claude\nanthropic/claude\n", "lines");
        Assert.Equal(["opencode/big-pickle", "anthropic/claude"], parsed.Select(o => o.Id));
    }

    [Fact]
    public void IdDashLabel_parser_reads_label()
    {
        var parsed = ModelDiscovery.Parse("Available models\n\nauto - Auto (current, default)\ngpt-5.3-codex-low - Codex 5.3 Low\n", "idDashLabel");
        Assert.Equal(["auto", "gpt-5.3-codex-low"], parsed.Select(o => o.Id));
        Assert.Equal("Codex 5.3 Low", parsed[1].Label);
    }

    [Fact]
    public async Task Discover_runs_once_and_caches()
    {
        var runner = new FakeRunner((_, args, _) => new ProcessResult(0, "a/b\nc/d\n", "", false));
        var discovery = new ModelDiscovery(runner, new FakeLoginEnvironment());
        var def = Builtins.Get("opencode");

        var first = await discovery.DiscoverAsync(def, "opencode");
        var second = await discovery.DiscoverAsync(def, "opencode");
        await discovery.DiscoverAsync(def, "opencode", refresh: true);

        Assert.Equal(2, first.Count);
        Assert.Same(first, second);
        Assert.Equal(2, runner.Calls.Count);
    }

    [Fact]
    public async Task Failure_or_missing_discover_gives_empty()
    {
        var runner = new FakeRunner((_, _, _) => new ProcessResult(1, "", "not logged in", false));
        var discovery = new ModelDiscovery(runner, new FakeLoginEnvironment());
        Assert.Empty(await discovery.DiscoverAsync(Builtins.Get("cursor"), "cursor-agent"));
        Assert.Empty(await discovery.DiscoverAsync(Builtins.Get("claude"), "claude"));
        Assert.Single(runner.Calls);
    }
}
