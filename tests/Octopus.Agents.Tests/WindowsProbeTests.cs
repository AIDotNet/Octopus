using System.Text.Json;
using Octopus.Agents.Probes;
using Octopus.Core.Agents;
using Octopus.Platform;

namespace Octopus.Agents.Tests;

public sealed class WindowsProbeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Batch_agent_probes_support_command_and_rpc_stdio(bool rpc)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TempDir();
        var shim = Path.Combine(directory.Path, "agent shim.cmd");
        var requestFile = Path.Combine(directory.Path, "request.json");
        await File.WriteAllTextAsync(shim, rpc
            ? "@echo off\r\nset /p request=\r\necho %request%>\"%REQUEST_FILE%\"\r\necho {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"plan\":\"pro\"}}\r\nping -n 30 127.0.0.1 >nul\r\n"
            : "@echo off\r\necho {\"plan\":\"pro\",\"private\":\"not selected\"}\r\n");
        var environment = HostEnvironmentFilter.SnapshotProcessEnvironment(StringComparer.OrdinalIgnoreCase);
        environment["REQUEST_FILE"] = requestFile;
        var definition = new AgentDefinition
        {
            Id = "windows-fixture",
            Probes = new()
            {
                ["account"] = new AgentProbe
                {
                    Kind = rpc ? ProbeKinds.JsonRpcStdio : ProbeKinds.Command,
                    Select = ["plan"],
                    Requests = rpc ? [new ProbeRpcRequest { Method = "account/read", Result = true }] : [],
                    TimeoutMs = 5000,
                }
            }
        };
        using var runner = new ProbeRunner();

        var result = await runner.RunAsync(new ProbeRequest(definition, "account", shim, environment));

        Assert.True(result.Success, result.Message);
        Assert.Equal("{\"plan\":\"pro\"}", result.Json);
        if (rpc)
        {
            using var request = JsonDocument.Parse(await File.ReadAllTextAsync(requestFile));
            Assert.Equal("account/read", request.RootElement.GetProperty("method").GetString());
        }
    }
}
