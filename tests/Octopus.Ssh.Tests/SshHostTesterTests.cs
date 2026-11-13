using System.Net;
using System.Net.Sockets;
using Octopus.Platform;

namespace Octopus.Ssh.Tests;

public class SshHostTesterTests
{
    private sealed class FakeRunner(ProcessResult result) : IProcessRunner
    {
        public List<string> Arguments { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment = null, string? workingDirectory = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            Arguments.AddRange(arguments);
            return Task.FromResult(result);
        }
    }

    private static Task<SshTestResult> Run(ProcessResult result, SshHostDraft draft) => new SshHostTester(new FakeRunner(result)).TestAsync(draft);

    [Fact]
    public void Every_field_is_an_argument_of_its_own_and_nothing_is_written_to_the_known_hosts()
    {
        var args = SshHostTester.Arguments(new SshHostDraft("盒子", "10.0.0.5", "root", "2222", "~/.ssh/my key", "bastion"));

        Assert.Equal(["-p", "2222", "-l", "root", "-i", "~/.ssh/my key", "-J", "bastion", "--", "10.0.0.5", "exit 0"], args.Skip(args.ToList().IndexOf("-p")));
        Assert.Contains("BatchMode=yes", args);
        Assert.Contains("StrictHostKeyChecking=no", args);
        Assert.Contains(args, a => a.StartsWith("UserKnownHostsFile=", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_a_host_name_the_alias_is_the_host_and_mosh_looks_for_its_server()
    {
        var args = SshHostTester.Arguments(new SshHostDraft("box", Protocol: SshHostProtocol.Mosh));

        Assert.Equal(["--", "box", "command -v mosh-server"], args.TakeLast(3));
    }

    [Fact]
    public async Task A_run_that_exits_with_zero_is_a_connection()
    {
        Assert.Equal(SshTestOutcome.Connected, (await Run(new ProcessResult(0, "", "", false), new SshHostDraft("a"))).Outcome);
    }

    [Fact]
    public async Task A_refused_login_means_the_host_answered_but_needs_more()
    {
        var result = await Run(new ProcessResult(255, "", "ops@a: Permission denied (publickey,password).\n", false), new SshHostDraft("a"));

        Assert.Equal(SshTestOutcome.AuthenticationNeeded, result.Outcome);
        Assert.Contains("Permission denied", result.Detail);
    }

    [Fact]
    public async Task A_failed_connection_says_what_ssh_said()
    {
        var result = await Run(new ProcessResult(255, "", "ssh: Could not resolve hostname nope: Name or service not known\n", false), new SshHostDraft("nope"));

        Assert.Equal(SshTestOutcome.Failed, result.Outcome);
        Assert.Contains("Could not resolve hostname", result.Detail);
    }

    [Fact]
    public async Task A_timeout_is_a_failure()
    {
        Assert.Equal(SshTestOutcome.Failed, (await Run(new ProcessResult(-1, "", "", true), new SshHostDraft("a"))).Outcome);
    }

    [Fact]
    public async Task Mosh_without_its_server_on_the_host_is_told_apart_from_a_failed_connection()
    {
        var mosh = new SshHostDraft("a", Protocol: SshHostProtocol.Mosh);

        Assert.Equal(SshTestOutcome.MoshServerMissing, (await Run(new ProcessResult(1, "", "", false), mosh)).Outcome);
        Assert.Equal(SshTestOutcome.Failed, (await Run(new ProcessResult(255, "", "Connection refused", false), mosh)).Outcome);
    }

    [Fact]
    public async Task Telnet_is_tried_with_a_connection_to_the_port()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var tester = new SshHostTester(new FakeRunner(new ProcessResult(1, "", "", false)));

        var open = await tester.TestAsync(new SshHostDraft("路由器", "127.0.0.1", Port: port.ToString(), Protocol: SshHostProtocol.Telnet));
        listener.Stop();
        var closed = await tester.TestAsync(new SshHostDraft("路由器", "127.0.0.1", Port: port.ToString(), Protocol: SshHostProtocol.Telnet));

        Assert.Equal(SshTestOutcome.Connected, open.Outcome);
        Assert.Equal(SshTestOutcome.Failed, closed.Outcome);
    }

    [Fact]
    public async Task A_program_that_cannot_start_is_a_failure_not_an_exception()
    {
        var result = await new SshHostTester(new ThrowingRunner()).TestAsync(new SshHostDraft("a"));

        Assert.Equal(SshTestOutcome.Failed, result.Outcome);
        Assert.Contains("no ssh", result.Detail);
    }

    private sealed class ThrowingRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment = null, string? workingDirectory = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("no ssh");
    }
}
