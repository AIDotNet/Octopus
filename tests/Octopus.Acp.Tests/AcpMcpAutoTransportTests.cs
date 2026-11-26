using Octopus.Acp.Protocol;
using Octopus.Core.Agents;

namespace Octopus.Acp.Tests;

public sealed class AcpMcpAutoTransportTests
{
    [Fact]
    public void Auto_transport_uses_http_when_agent_supports_http_and_falls_back_to_stdio_otherwise()
    {
        var config = new AcpMcpServerConfig
        {
            Name = "octopus-collab",
            Transport = "auto",
            Url = "http://127.0.0.1:51234/mcp",
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer abc" },
            Command = "/usr/local/bin/octopus-signal",
            Args = ["--mcp"],
            Env = new Dictionary<string, string> { ["OCTOPUS_COLLAB_TOKEN"] = "abc" },
            Enabled = true,
        };

        var withHttp = AcpProtocolParser.SerializeMcpServers([config], new AcpMcpCapabilities { Http = true });
        Assert.Single(withHttp);
        Assert.Equal("http", withHttp[0]!["type"]?.GetValue<string>());
        Assert.Equal("http://127.0.0.1:51234/mcp", withHttp[0]!["url"]?.GetValue<string>());

        var stdioOnly = AcpProtocolParser.SerializeMcpServers([config], new AcpMcpCapabilities { Http = false });
        Assert.Single(stdioOnly);
        Assert.Null(stdioOnly[0]!["type"]);
        Assert.Equal("/usr/local/bin/octopus-signal", stdioOnly[0]!["command"]?.GetValue<string>());
    }
}
