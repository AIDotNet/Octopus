using Octopus.Agents;
using Octopus.Core.Agents;
using Octopus.Core.Settings;
using Octopus.Plugins.Runtime.Modules;
using Xunit;

namespace Octopus.Plugins.Tests;

/// <summary>
/// <c>agents.runProbe</c> starts the agent with what a session would get. agy behind a proxy never answered
/// <c>/usage</c> when the probe ran with the bare login environment, and the usage panel showed a timeout.
/// </summary>
public sealed class ProbeEnvironmentTests
{
    private static readonly Dictionary<string, string> Login = new() { ["PATH"] = "/usr/bin", ["HOME"] = "/home/u" };

    private static AgentDefinition Agy() => new() { Id = "agy" };

    private sealed class FixedProxy(Uri? proxy) : ISystemProxy
    {
        public Uri? Resolve() => proxy;
    }

    [Fact]
    public void TheProfileEnvironmentReachesTheProbe()
    {
        var agents = new AgentSettings();
        agents.Profiles["agy"] = new AgentProfile { Env = { ["HTTPS_PROXY"] = "http://127.0.0.1:10808" } };

        var environment = AgentSignalsModule.ProbeEnvironment(Login, Agy(), agents, new FixedProxy(new Uri("http://127.0.0.1:7890")));

        Assert.Equal("/usr/bin", environment["PATH"]);
        Assert.Equal("http://127.0.0.1:10808", environment["HTTPS_PROXY"]);
        Assert.False(environment.ContainsKey("HTTP_PROXY")); // the user's own proxy wins over the system one
    }

    [Fact]
    public void TheSystemProxyIsAddedWhenNothingElseNamesOne()
    {
        var environment = AgentSignalsModule.ProbeEnvironment(Login, Agy(), new AgentSettings(), new FixedProxy(new Uri("http://127.0.0.1:7890")));

        Assert.Equal("http://127.0.0.1:7890", environment["HTTPS_PROXY"]);
        Assert.Equal("http://127.0.0.1:7890", environment["https_proxy"]);
        Assert.Equal(AgentProxyEnvironment.DefaultNoProxy, environment["NO_PROXY"]);
    }

    [Fact]
    public void TheSystemProxyStaysOutWhenTurnedOff()
    {
        var environment = AgentSignalsModule.ProbeEnvironment(
            Login, Agy(), new AgentSettings { UseSystemProxy = false }, new FixedProxy(new Uri("http://127.0.0.1:7890")));

        Assert.False(environment.ContainsKey("HTTPS_PROXY"));
    }
}
