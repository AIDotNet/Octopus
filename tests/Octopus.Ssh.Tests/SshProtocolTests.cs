using Octopus.Platform;

namespace Octopus.Ssh.Tests;

public class SshProtocolTests
{
    /// <summary>Lines of a real connection's debug log (ssh -E with LogLevel=DEBUG1), in the order ssh wrote them.</summary>
    private static readonly string[] Connection =
    [
        "debug1: OpenSSH_10.3p1, LibreSSL 3.3.6",
        "debug1: Reading configuration data /Users/token/.ssh/config",
        "debug1: Connecting to 137.184.127.219 [137.184.127.219] port 22.",
        "debug1: Connection established.",
        "debug1: Local version string SSH-2.0-OpenSSH_10.3",
        "debug1: Remote protocol version 2.0, remote software version OpenSSH_8.9p1 Ubuntu-3ubuntu0.18",
        "debug1: SSH2_MSG_KEXINIT sent",
        "debug1: kex: algorithm: ecdh-sha2-nistp256",
        "debug1: Server host key: ssh-ed25519 SHA256:/MSmA1gqBw/tfXcyTpkIIj0n0iqPibvqVTL5s12aW2Q",
        "Warning: Permanently added '137.184.127.219' (ED25519) to the list of known hosts.",
        "debug1: Authenticating to 137.184.127.219:22 as 'root'",
        "debug1: Authentications that can continue: publickey",
        "debug1: Offering public key: /Users/token/.ssh/id_ed25519 ED25519 SHA256:9/aXYjtAUysOtqRlo5wwfQQ/oPzQLBpNsPH7xITDw5o",
        "Authenticated to 137.184.127.219 ([137.184.127.219]:22) using \"publickey\".",
        "debug1: channel 0: new session [client-session] (inactive timeout: 0)",
        "debug1: Entering interactive session.",
        "debug1: Sending command: exec $SHELL -l",
    ];

    [Fact]
    public void The_steps_of_a_connection_come_in_order_and_end_with_the_session()
    {
        var steps = Connection.Select(SshProtocol.StageOf).Where(s => s is not null).Select(s => s!.Value).Distinct().ToList();

        Assert.Equal(
            [SshStage.Connecting, SshStage.Negotiating, SshStage.CheckingHostKey, SshStage.Authenticating, SshStage.OpeningSession, SshStage.Ready],
            steps);
    }

    [Fact]
    public void A_line_that_says_nothing_about_progress_has_no_stage()
    {
        Assert.Null(SshProtocol.StageOf("debug1: Reading configuration data /etc/ssh/ssh_config"));
        Assert.Null(SshProtocol.StageOf("debug1: Transferred: sent 2564, received 2864 bytes"));
    }

    [Fact]
    public void The_level_prefix_is_removed_for_display()
    {
        Assert.Equal("Connection established.", SshProtocol.Clean("debug1: Connection established."));
        Assert.Equal("keep this", SshProtocol.Clean("debug2: keep this"));
        Assert.Equal("Permission denied (publickey).", SshProtocol.Clean("Permission denied (publickey)."));
    }

    [Theory]
    [InlineData("Permission denied (publickey,password).", true)]
    [InlineData("ssh: Could not resolve hostname nosuch.invalid: nodename nor servname provided, or not known", true)]
    [InlineData("debug1: Connection established.", false)]
    [InlineData("debug1: Authentication succeeded (publickey).", false)]
    public void Failures_are_recognised(string line, bool failed) =>
        Assert.Equal(failed, SshProtocol.FailureOf(line) is not null);
}

public class SshFileTreeBusyTests
{
    /// <summary>A runner whose answers come when the test lets them: the run stays under way until then.</summary>
    private sealed class GatedRunner(TaskCompletionSource<ProcessResult> gate) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            IReadOnlyDictionary<string, string>? environment = null,
            string? workingDirectory = null,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default) => gate.Task;
    }

    [Fact]
    public async Task Busy_is_raised_while_a_run_to_the_host_is_under_way()
    {
        var gate = new TaskCompletionSource<ProcessResult>();
        var tree = new SshFileTree("prod", new GatedRunner(gate), []);
        var states = new List<bool>();
        tree.BusyChanged += (_, busy) => states.Add(busy);

        tree.List("/srv");
        await WaitUntil(() => tree.IsBusy);
        gate.SetResult(new ProcessResult(0, "", "", false));
        await WaitUntil(() => !tree.IsBusy);

        Assert.Equal([true, false], states);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }
}
