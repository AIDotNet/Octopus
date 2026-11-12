using System.Diagnostics;

namespace Octopus.Platform.Tests;

public class ProcessHostTests
{
    private readonly ProcessHost _host = new();

    private static async Task<T> Within<T>(Task<T> task, string what)
    {
        try
        {
            return await task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("Timed out waiting for " + what);
        }
    }

    [Fact]
    public async Task The_error_output_comes_line_by_line_and_the_exit_after_the_last_line()
    {
        if (OperatingSystem.IsWindows()) return;
        var lines = new List<string>();
        var exited = new TaskCompletionSource<int>();

        using var process = _host.Start("/bin/sh", ["-c", "echo one >&2; echo two >&2; echo three >&2; exit 3"],
            line => { lock (lines) lines.Add(line); },
            code => { lock (lines) lines.Add("exit " + code); exited.TrySetResult(code); });

        Assert.Equal(3, await Within(exited.Task, "the exit"));
        lock (lines) Assert.Equal(["one", "two", "three", "exit 3"], lines);
    }

    [Fact]
    public async Task Kill_ends_a_program_that_would_run_on()
    {
        if (OperatingSystem.IsWindows()) return;
        var exited = new TaskCompletionSource<int>();
        var timer = Stopwatch.StartNew();

        using var process = _host.Start("/bin/sh", ["-c", "sleep 60"], _ => { }, code => exited.TrySetResult(code));
        Assert.False(process.HasExited);
        process.Kill();

        await Within(exited.Task, "the exit after a kill");
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(30));
        Assert.True(process.HasExited);
        process.Kill(); // ending an ended process is not an error
    }

    [Fact]
    public void A_program_that_is_missing_is_reported_by_an_exception()
    {
        Assert.Throws<ProcessStartException>(() => _host.Start("/no/such/program-octopus", [], _ => { }, _ => { }));
    }

    [Fact]
    public async Task Standard_input_is_closed_so_that_a_program_that_reads_it_ends()
    {
        if (OperatingSystem.IsWindows()) return;
        var exited = new TaskCompletionSource<int>();

        using var process = _host.Start("/bin/sh", ["-c", "cat > /dev/null; echo done >&2"], _ => { }, code => exited.TrySetResult(code));

        Assert.Equal(0, await Within(exited.Task, "cat to end"));
    }

    [Fact]
    public async Task A_process_is_ended_by_its_id_only_when_it_is_the_one_that_was_started()
    {
        if (OperatingSystem.IsWindows()) return;
        var exited = new TaskCompletionSource<int>();
        using var process = _host.Start("/bin/sleep", ["60"], _ => { }, code => exited.TrySetResult(code));

        // Another start time: the id may belong to a different program by now.
        Assert.False(_host.KillIfSame(process.Id, process.StartedAt.AddMinutes(-5), "sleep"));
        // Another name.
        Assert.False(_host.KillIfSame(process.Id, process.StartedAt, "ssh"));
        Assert.False(process.HasExited);

        Assert.True(_host.KillIfSame(process.Id, process.StartedAt, "sleep"));
        await Within(exited.Task, "the exit");
    }

    [Fact]
    public void A_process_that_is_not_running_is_not_ended()
    {
        Assert.False(_host.KillIfSame(int.MaxValue - 1, DateTimeOffset.UtcNow, "ssh"));
    }
}
