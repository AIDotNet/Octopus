using Octopus.Core.Projects;

namespace Octopus.Ssh.Tests;

public class SshSharedCommandTests
{
    private static readonly string[] Client = ["-o", "ControlMaster=no", "-o", "ControlPath=/home/me/.octopus/ssh/%C"];

    private static TunnelSpec Local() => new() { Kind = TunnelKind.Local, ListenPort = 5173, TargetHost = "localhost", TargetPort = 5173 };

    private static TunnelSpec Remote() => new() { Kind = TunnelKind.Remote, ListenPort = 9000, TargetHost = "127.0.0.1", TargetPort = 3000 };

    [Fact]
    public void Every_run_starts_with_the_options_of_the_shared_connection_and_ends_with_the_host_after_the_end_of_the_options()
    {
        foreach (var arguments in new[]
        {
            SshSharedCommand.Check("prod", Client),
            SshSharedCommand.Forward("prod", Local(), Client),
            SshSharedCommand.Cancel("prod", Local(), Client),
        })
        {
            Assert.Equal(Client, arguments.Take(Client.Length));
            Assert.Equal(["--", "prod"], arguments.Skip(arguments.Count - 2));
        }
    }

    [Fact]
    public void The_connection_is_asked_with_check_and_given_a_forward_with_forward_and_loses_it_with_cancel()
    {
        Assert.Equal(["-O", "check", "--", "prod"], SshSharedCommand.Check("prod", []));
        Assert.Equal(["-O", "forward", "-L", "127.0.0.1:5173:localhost:5173", "--", "prod"], SshSharedCommand.Forward("prod", Local(), []));
        Assert.Equal(["-O", "cancel", "-L", "127.0.0.1:5173:localhost:5173", "--", "prod"], SshSharedCommand.Cancel("prod", Local(), []));
    }

    [Fact]
    public void A_remote_tunnel_uses_the_other_flag_and_cancel_needs_the_same_forward_as_was_added()
    {
        var forward = SshSharedCommand.Forward("prod", Remote(), []);
        var cancel = SshSharedCommand.Cancel("prod", Remote(), []);

        Assert.Equal(["-O", "forward", "-R", "127.0.0.1:9000:127.0.0.1:3000", "--", "prod"], forward);
        Assert.Equal(forward.Skip(2), cancel.Skip(2));
    }

    [Fact]
    public void The_anchor_is_a_quiet_session_that_waits_on_the_host_and_never_asks_for_anything()
    {
        var arguments = SshSharedCommand.Anchor("prod", Client);

        Assert.Equal(Client, arguments.Take(Client.Length));
        Assert.Equal(["-o", "BatchMode=yes", "-o", "ConnectTimeout=10", "-T", "--", "prod", "cat"], arguments.Skip(Client.Length));
    }

    [Fact]
    public void The_client_options_are_those_of_a_run_that_joins_a_connection_and_never_starts_one()
    {
        var options = SshCommands.SharedClient("/home/me/.octopus/ssh");

        if (OperatingSystem.IsWindows())
        {
            Assert.Empty(options);
            return;
        }
        Assert.Contains("ControlMaster=no", options);
        Assert.Contains("ControlPath=" + Path.Combine("/home/me/.octopus/ssh", "%C"), options);
        Assert.DoesNotContain("ControlPersist=300", options);
    }

    // Lines of real runs (OpenSSH 10.5): `ssh -O ...` against a master that is there, and against one that is not.
    [Theory]
    [InlineData("Control socket connect(ctl/8d6691c93a43f3e743379cd3067a93cb53cf3d65): No such file or directory", SshTunnelEvent.NoSharedConnection)]
    [InlineData("Control socket connect(/home/me/.octopus/ssh/8d66): Connection refused", SshTunnelEvent.NoSharedConnection)]
    [InlineData("mux_client_forward: forwarding request failed: Port forwarding failed", SshTunnelEvent.LocalPortUnavailable)]
    [InlineData("mux_client_forward: forwarding request failed: remote port forwarding failed for listen port 19430", SshTunnelEvent.RemoteForwardRefused)]
    [InlineData("muxclient: master forward request failed", SshTunnelEvent.None)]
    [InlineData("Master running (pid=68920)", SshTunnelEvent.None)]
    [InlineData("mux_client_forward: forwarding request failed: port not forwarded", SshTunnelEvent.None)]
    public void What_the_shared_connection_says_is_read_as_the_events_of_a_tunnel(string line, SshTunnelEvent expected)
    {
        Assert.Equal(expected, SshTunnelLog.EventOf(line));
    }
}
