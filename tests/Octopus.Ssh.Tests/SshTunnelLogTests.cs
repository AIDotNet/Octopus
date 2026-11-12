namespace Octopus.Ssh.Tests;

public class SshTunnelLogTests
{
    [Theory]
    [InlineData("debug1: Authenticated to 10.0.0.5 ([10.0.0.5]:22) using \"publickey\".", SshTunnelEvent.Authenticated)]
    [InlineData("Authenticated to 10.0.0.5 ([10.0.0.5]:22) using \"publickey\".", SshTunnelEvent.Authenticated)]
    [InlineData("debug1: Entering interactive session.", SshTunnelEvent.SessionOpen)]
    [InlineData("debug1: remote forward success for: listen 9000, connect localhost:3000", SshTunnelEvent.ForwardConfirmed)]
    [InlineData("debug1: Reading configuration data /Users/me/.ssh/config", SshTunnelEvent.None)]
    [InlineData("debug1: Local connections to 127.0.0.1:5173 forwarded to remote address localhost:5173", SshTunnelEvent.None)]
    public void The_progress_of_a_tunnel_is_read_from_its_log(string line, SshTunnelEvent expected)
    {
        Assert.Equal(expected, SshTunnelLog.EventOf(line));
    }

    [Theory]
    [InlineData("bind [127.0.0.1]:5173: Address already in use")]
    [InlineData("channel_setup_fwd_listener_tcpip: cannot listen to port: 5173")]
    [InlineData("Could not request local forwarding.")]
    [InlineData("bind [127.0.0.1]:80: Permission denied")]
    public void A_port_here_that_cannot_be_listened_on_is_told_apart(string line)
    {
        Assert.Equal(SshTunnelEvent.LocalPortUnavailable, SshTunnelLog.EventOf(line));
    }

    [Theory]
    [InlineData("Error: remote port forwarding failed for listen port 9000")]
    [InlineData("Could not request remote forwarding.")]
    public void A_port_on_the_host_that_was_not_given_is_told_apart(string line)
    {
        Assert.Equal(SshTunnelEvent.RemoteForwardRefused, SshTunnelLog.EventOf(line));
    }

    [Theory]
    [InlineData("user@prod: Permission denied (publickey,password).", SshTunnelEvent.LoginDenied)]
    [InlineData("Too many authentication failures", SshTunnelEvent.LoginDenied)]
    [InlineData("Host key verification failed.", SshTunnelEvent.HostKeyRejected)]
    public void A_refused_login_or_key_needs_a_change_of_setup(string line, SshTunnelEvent expected)
    {
        var value = SshTunnelLog.EventOf(line);

        Assert.Equal(expected, value);
        Assert.True(SshTunnelLog.NeedsChange(value));
    }

    [Theory]
    [InlineData("Connection reset by 10.0.0.5 port 22")]
    [InlineData("Connection timed out during banner exchange")]
    [InlineData("ssh: connect to host prod port 22: Operation timed out")]
    [InlineData("ssh: connect to host prod port 22: Connection refused")]
    [InlineData("ssh: Could not resolve hostname prod: nodename nor servname provided")]
    [InlineData("Timeout, server 10.0.0.5 not responding.")]
    [InlineData("client_loop: send disconnect: Broken pipe")]
    public void A_lost_connection_is_one_to_try_again(string line)
    {
        var value = SshTunnelLog.EventOf(line);

        Assert.Equal(SshTunnelEvent.ConnectionLost, value);
        Assert.False(SshTunnelLog.NeedsChange(value));
    }

    [Fact]
    public void A_connection_through_the_tunnel_that_fails_is_not_a_lost_connection()
    {
        // "Connection refused" here is the target's, not the host's: the tunnel itself is fine.
        Assert.Equal(SshTunnelEvent.TargetUnreachable, SshTunnelLog.EventOf("channel 2: open failed: connect failed: Connection refused"));
        Assert.Equal(SshTunnelEvent.ForwardingProhibited, SshTunnelLog.EventOf("channel 2: open failed: administratively prohibited: open failed"));
        Assert.False(SshTunnelLog.NeedsChange(SshTunnelEvent.TargetUnreachable));
    }

    /// <summary>Lines of real runs (ssh -N with the options of <see cref="SshTunnelCommand"/>, OpenSSH 10.5 to an OpenSSH server), in the order ssh wrote them.</summary>
    private static readonly string[] LocalRun =
    [
        "debug1: OpenSSH_10.5p1, LibreSSL 3.3.6",
        "debug1: Reading configuration data /Users/me/.ssh/config",
        "debug1: Authenticator provider $SSH_SK_PROVIDER did not resolve; disabling",
        "debug1: Connecting to 10.0.0.5 [10.0.0.5] port 22.",
        "debug1: Connection established.",
        "debug1: Server host key: ssh-ed25519 SHA256:abc",
        "debug1: Authentications that can continue: publickey",
        "Authenticated to 10.0.0.5 ([10.0.0.5]:22) using \"publickey\".",
        "debug1: Local connections to 127.0.0.1:15173 forwarded to remote address localhost:8765",
        "debug1: Requesting no-more-sessions@openssh.com",
        "debug1: Entering interactive session.",
        "debug1: pledge: network",
        "debug1: Connection to port 15173 forwarding to localhost port 8765 requested.",
        "debug1: channel 1: new direct-tcpip [direct-tcpip] (inactive timeout: 0)",
        "debug1: channel 1: free: direct-tcpip: listening port 15173 for localhost port 8765, connect from 127.0.0.1 port 49259 to 127.0.0.1 port 15173, nchannels 2",
    ];

    [Fact]
    public void A_healthy_local_run_says_it_logged_in_and_is_running_and_nothing_else()
    {
        var events = LocalRun.Select(SshTunnelLog.EventOf).Where(e => e != SshTunnelEvent.None).ToList();

        Assert.Equal([SshTunnelEvent.Authenticated, SshTunnelEvent.SessionOpen], events);
    }

    [Fact]
    public void A_healthy_remote_run_confirms_the_forward_after_the_session_is_open()
    {
        string[] lines =
        [
            "Authenticated to 10.0.0.5 ([10.0.0.5]:22) using \"publickey\".",
            "debug1: Remote connections from 127.0.0.1:19000 forwarded to local address localhost:8765",
            "debug1: Entering interactive session.",
            "debug1: remote forward success for: listen 127.0.0.1:19000, connect localhost:8765",
        ];

        var events = lines.Select(SshTunnelLog.EventOf).Where(e => e != SshTunnelEvent.None).ToList();

        Assert.Equal([SshTunnelEvent.Authenticated, SshTunnelEvent.SessionOpen, SshTunnelEvent.ForwardConfirmed], events);
    }

    [Fact]
    public void A_remote_forward_that_the_host_refuses_says_so_in_two_lines()
    {
        Assert.Equal(SshTunnelEvent.RemoteForwardRefused, SshTunnelLog.EventOf("debug1: remote forward failure for: listen 127.0.0.1:19000, connect localhost:8765"));
        Assert.Equal(SshTunnelEvent.RemoteForwardRefused, SshTunnelLog.EventOf("Error: remote port forwarding failed for listen port 19000"));
    }

    [Fact]
    public void The_host_ending_a_connection_that_was_up_is_a_lost_connection()
    {
        Assert.Equal(SshTunnelEvent.ConnectionLost, SshTunnelLog.EventOf("Connection to 127.0.0.1 closed by remote host."));
    }

    [Fact]
    public void A_remote_forward_whose_target_here_refuses_is_a_failed_connection_and_the_tunnel_stays_up()
    {
        // Seen when the target listens on 127.0.0.1 only and "localhost" is tried as ::1 first.
        Assert.Equal(SshTunnelEvent.TargetUnreachable, SshTunnelLog.EventOf("debug1: channel 0: connection failed: Connection refused"));
    }

    [Fact]
    public void The_debug_noise_of_a_run_is_not_taken_for_a_problem()
    {
        string[] noise =
        [
            "debug1: Authenticator provider $SSH_SK_PROVIDER did not resolve; disabling",
            "debug1: ssh_packet_send2_wrapped: resetting send seqnr 3",
            "debug1: channel 0: new port-listener [port listener] (inactive timeout: 0)",
            "debug1: Remote: /home/me/.ssh/authorized_keys:1: key options: agent-forwarding port-forwarding pty user-rc x11-forwarding",
            "debug1: client_input_global_request: rtype hostkeys-00@openssh.com want_reply 0",
            "Transferred: sent 3556, received 3556 bytes, in 2.0 seconds",
            "debug1: Exit status -1",
        ];

        Assert.All(noise, line => Assert.Equal(SshTunnelEvent.None, SshTunnelLog.EventOf(line)));
    }
}
