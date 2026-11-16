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
    [Theory]
    [InlineData(".cmd")]
    [InlineData(".bat")]
    public async Task Windows_batch_shim_preserves_arguments_output_environment_and_exit(string extension)
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Directory.CreateTempSubdirectory("octopus batch & test ");
        try
        {
            var script = Path.Combine(directory.FullName, "agent shim" + extension);
            await File.WriteAllTextAsync(script,
                "@echo off\r\necho [%1]\r\necho [%2]\r\necho [%3]\r\necho [%4]\r\necho %OCTOPUS_TEST_VALUE% 1>&2\r\nexit /b 7\r\n");
            var result = await _runner.RunAsync(script, ["hello world", "a&b|c", "%PATH%", "bang!"],
                new Dictionary<string, string> { ["OCTOPUS_TEST_VALUE"] = "probe-error" });
            Assert.Equal(7, result.ExitCode);
            Assert.Equal("[\"hello world\"]\r\n[\"a&b|c\"]\r\n[\"%PATH%\"]\r\n[\"bang!\"]\r\n", result.StandardOutput);
            Assert.Contains("probe-error", result.StandardError);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task Windows_batch_timeout_and_caller_cancellation_remain_bounded()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Directory.CreateTempSubdirectory("octopus-batch-timeout");
        try
        {
            var script = Path.Combine(directory.FullName, "wait.cmd");
            await File.WriteAllTextAsync(script, "@echo off\r\nping -n 30 127.0.0.1 >nul\r\n");
            var result = await _runner.RunAsync(script, [], timeout: TimeSpan.FromMilliseconds(300));
            Assert.True(result.TimedOut);
            using var cts = new CancellationTokenSource(300);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                _runner.RunAsync(script, [], cancellationToken: cts.Token));
        }
        finally { directory.Delete(true); }
    }
}
