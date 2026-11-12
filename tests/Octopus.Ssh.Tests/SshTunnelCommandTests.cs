using Octopus.Core.Projects;

namespace Octopus.Ssh.Tests;

public class SshTunnelCommandTests
{
    private static TunnelSpec Local(string bind = "127.0.0.1", int listen = 5173, string target = "localhost", int targetPort = 5173) =>
        new() { Kind = TunnelKind.Local, BindAddress = bind, ListenPort = listen, TargetHost = target, TargetPort = targetPort };

    [Fact]
    public void A_local_tunnel_forwards_a_port_here_to_an_address_of_the_host()
    {
        var arguments = SshTunnelCommand.Arguments("prod", Local(listen: 15432, target: "db.internal", targetPort: 5432));

        Assert.Equal("-N", arguments[0]);
        Assert.Equal(["-L", "127.0.0.1:15432:db.internal:5432", "--", "prod"], arguments.Skip(arguments.Count - 4));
    }

    [Fact]
    public void A_remote_tunnel_uses_the_other_direction()
    {
        var spec = new TunnelSpec { Kind = TunnelKind.Remote, BindAddress = "127.0.0.1", ListenPort = 9000, TargetHost = "localhost", TargetPort = 3000 };

        var arguments = SshTunnelCommand.Arguments("prod", spec);

        Assert.Equal(["-R", "127.0.0.1:9000:localhost:3000", "--", "prod"], arguments.Skip(arguments.Count - 4));
    }

    [Fact]
    public void The_run_ends_when_the_forward_cannot_be_made_and_never_asks_for_anything()
    {
        var line = string.Join(' ', SshTunnelCommand.Arguments("prod", Local()));

        Assert.Contains("-o ExitOnForwardFailure=yes", line);
        Assert.Contains("-o BatchMode=yes", line);
        Assert.Contains("-o ServerAliveInterval=10", line);
    }

    [Fact]
    public void The_run_has_a_connection_of_its_own_and_does_not_join_the_shared_one()
    {
        var line = string.Join(' ', SshTunnelCommand.Arguments("prod", Local()));

        Assert.Contains("-o ControlMaster=no", line);
        Assert.Contains("-o ControlPath=none", line);
    }

    [Fact]
    public void The_host_comes_after_the_end_of_the_options_so_that_it_cannot_be_read_as_one()
    {
        var arguments = SshTunnelCommand.Arguments("-oProxyCommand=evil", Local());

        Assert.Equal("--", arguments[^2]);
        Assert.Equal("-oProxyCommand=evil", arguments[^1]);
    }

    [Fact]
    public void A_valid_spec_has_no_issues()
    {
        Assert.Empty(SshTunnelCommand.Validate(Local()));
        Assert.Empty(SshTunnelCommand.Validate(Local(bind: "0.0.0.0", target: "[::1]")));
        Assert.Empty(SshTunnelCommand.Validate(Local(target: "db.internal-1")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void A_port_outside_the_range_is_refused(int port)
    {
        var issues = SshTunnelCommand.Validate(Local(listen: port, targetPort: port));

        Assert.Contains(new TunnelIssue(TunnelField.ListenPort, TunnelProblem.Port), issues);
        Assert.Contains(new TunnelIssue(TunnelField.TargetPort, TunnelProblem.Port), issues);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_address_is_required(string address)
    {
        var issues = SshTunnelCommand.Validate(Local(bind: address, target: address));

        Assert.Contains(new TunnelIssue(TunnelField.BindAddress, TunnelProblem.Required), issues);
        Assert.Contains(new TunnelIssue(TunnelField.TargetHost, TunnelProblem.Required), issues);
    }

    [Theory]
    [InlineData("db internal")]
    [InlineData("-oProxyCommand=x")]
    [InlineData("host:22")]
    [InlineData("::1")]
    [InlineData("[::1")]
    [InlineData("[zz]")]
    [InlineData("a'b")]
    [InlineData("a\nb")]
    public void An_address_that_would_break_the_forward_is_refused(string address)
    {
        var issues = SshTunnelCommand.Validate(Local(target: address));

        Assert.Equal([new TunnelIssue(TunnelField.TargetHost, TunnelProblem.Address)], issues);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.0.0.2", true)]
    [InlineData("localhost", true)]
    [InlineData("[::1]", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("*", false)]
    [InlineData("192.168.1.5", false)]
    public void Only_the_loopback_addresses_are_private_to_the_computer(string address, bool loopback)
    {
        Assert.Equal(loopback, SshTunnelCommand.IsLoopback(address));
    }
}
