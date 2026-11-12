using System.Diagnostics;

namespace Octopus.Platform.Tests;

public class ProcessRunnerTests
{
    private readonly ProcessRunner _runner = new();

    [Fact]
    public async Task Captures_stdout_and_exit_code()
    {
        if (OperatingSystem.IsWindows()) return;
        var r = await _runner.RunAsync("/bin/echo", ["hello", "世界"]);
        Assert.True(r.Succeeded);
        Assert.Equal("hello 世界\n", r.StandardOutput);
    }

    [Fact]
    public async Task Missing_program_returns_minus_one()
    {
        var r = await _runner.RunAsync("/no/such/program-octopus", []);
        Assert.Equal(-1, r.ExitCode);
        Assert.False(r.TimedOut);
        Assert.False(r.Succeeded);
    }

    [Fact]
    public async Task Timeout_kills_process_tree()
    {
        if (OperatingSystem.IsWindows()) return;
        var sw = Stopwatch.StartNew();
        var r = await _runner.RunAsync("/bin/sh", ["-c", "sleep 30 & sleep 30"], timeout: TimeSpan.FromMilliseconds(300));
        Assert.True(r.TimedOut);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Large_output_on_both_streams_does_not_deadlock()
    {
        if (OperatingSystem.IsWindows()) return;
        var r = await _runner.RunAsync("/bin/sh",
            ["-c", "i=0; while [ $i -lt 20000 ]; do echo 0123456789012345678901234567890123456789; echo err-0123456789012345678901234567890123456789 >&2; i=$((i+1)); done"],
            timeout: TimeSpan.FromSeconds(30));
        Assert.True(r.Succeeded);
        Assert.True(r.StandardOutput.Length > 800_000);
        Assert.True(r.StandardError.Length > 800_000);
    }

    [Fact]
    public async Task Environment_overlay_and_nonzero_exit()
    {
        if (OperatingSystem.IsWindows()) return;
        var r = await _runner.RunAsync("/bin/sh", ["-c", "echo $OCTOPUS_T; exit 3"],
            new Dictionary<string, string> { ["OCTOPUS_T"] = "v" });
        Assert.Equal(3, r.ExitCode);
        Assert.Equal("v\n", r.StandardOutput);
    }

    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        if (OperatingSystem.IsWindows()) return;
        using var cts = new CancellationTokenSource(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _runner.RunAsync("/bin/sleep", ["30"], cancellationToken: cts.Token));
    }
}
