using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Octopus.Platform;

/// <summary>
/// Runs short helper commands (dscl, getent, shell probes). Failures are reported in the result instead of thrown so
/// that callers can fall back without try/catch; only caller cancellation propagates.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    /// <summary>How long to wait for the output pipes to drain after the process was killed.</summary>
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(2);

    public Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(fileName, arguments, environment, workingDirectory, null, timeout, cancellationToken);

    public Task<ProcessResult> RunWithInputAsync(
        string fileName,
        IEnumerable<string> arguments,
        ReadOnlyMemory<byte> input,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(fileName, arguments, null, null, input, timeout, cancellationToken);

    private async Task<ProcessResult> RunCoreAsync(
        string fileName,
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        string? workingDirectory,
        ReadOnlyMemory<byte>? input,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        if (environment is not null)
        {
            // Overlay on the inherited environment: callers add or override variables, they do not rebuild it.
            foreach (var (key, value) in environment)
                startInfo.Environment[key] = value;
        }
        if (workingDirectory is not null)
            startInfo.WorkingDirectory = workingDirectory;

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return new ProcessResult(-1, "", "The process did not start.", false);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            return new ProcessResult(-1, "", e.Message, false);
        }

        // Read both pipes concurrently: a child that fills one pipe while we wait on the other would block forever.
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        // The input (when there is some) is written while the child runs; without input the pipe is closed at once.
        var writing = input is { } data ? WriteInputAsync(process, data) : Task.Run(() => CloseInput(process));

        using var timeoutSource = new CancellationTokenSource();
        if (timeout is { } limit)
            timeoutSource.CancelAfter(limit);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            await DrainAsync(process, stdout, stderr).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
                throw;
            timedOut = true;
        }

        try { await writing.WaitAsync(DrainGrace).ConfigureAwait(false); }
        catch (TimeoutException) { }

        var output = await WithGrace(stdout).ConfigureAwait(false);
        var error = await WithGrace(stderr).ConfigureAwait(false);
        return new ProcessResult(timedOut ? -1 : process.ExitCode, output, error, timedOut);
    }

    private static void CloseInput(Process process)
    {
        try { process.StandardInput.Close(); }
        catch (IOException) { }
    }

    private static async Task WriteInputAsync(Process process, ReadOnlyMemory<byte> data)
    {
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(data).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The child ended before it took all of the input: its exit code says how the run went.
        }
        finally
        {
            CloseInput(process);
        }
    }

    private static void KillTree(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception) { /* already gone */ }
    }

    private static async Task DrainAsync(Process process, Task<string> stdout, Task<string> stderr)
    {
        try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(DrainGrace).ConfigureAwait(false); }
        catch (TimeoutException) { }
        await WithGrace(stdout).ConfigureAwait(false);
        await WithGrace(stderr).ConfigureAwait(false);
    }

    /// <summary>A grandchild that escaped the kill may keep the pipe open; do not wait for it forever.</summary>
    private static async Task<string> WithGrace(Task<string> read)
    {
        try { return await read.WaitAsync(DrainGrace).ConfigureAwait(false); }
        catch (TimeoutException) { return ""; }
    }
}
