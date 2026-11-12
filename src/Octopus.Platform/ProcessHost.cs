using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Octopus.Platform;

/// <summary>A program that could not be started (it is missing, or the system refused).</summary>
public sealed class ProcessStartException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A program that was started by <see cref="IProcessHost"/> and keeps running until it ends or is killed.</summary>
public interface IRunningProcess : IDisposable
{
    int Id { get; }

    /// <summary>When the system says the process started; with <see cref="Id"/> it names this process and no later one that reuses the id.</summary>
    DateTimeOffset StartedAt { get; }

    bool HasExited { get; }

    /// <summary>Ends the process and everything it started. Does nothing when it has ended already.</summary>
    void Kill();
}

/// <summary>
/// Starts programs that run for a long time (as opposed to <see cref="IProcessRunner"/>, which waits for a short
/// command's output): the caller gets the program's error output line by line while it runs, and is told when it ends.
/// </summary>
public interface IProcessHost
{
    /// <summary>
    /// Starts <paramref name="fileName"/>. Its standard input is closed (unless <paramref name="keepInputOpen"/>: for a
    /// program that ends when its input does, and has to be kept running; it then ends when the process is killed or this
    /// app is gone, which closes the pipe), its standard output is discarded. Both callbacks
    /// run on background threads: <paramref name="onErrorLine"/> for each line the program writes to its error output, in
    /// order, and then <paramref name="onExited"/> once, with the exit code, after the last of those lines.
    /// Throws <see cref="ProcessStartException"/> when the program cannot be started.
    /// </summary>
    IRunningProcess Start(string fileName, IEnumerable<string> arguments, Action<string> onErrorLine, Action<int> onExited, bool keepInputOpen = false);

    /// <summary>
    /// Ends the process <paramref name="processId"/> if it is still the one that started at <paramref name="startedAt"/>
    /// and is called <paramref name="processName"/> (an id may be reused by another program after the first ended).
    /// For what an earlier run left behind. True when a process was ended.
    /// </summary>
    bool KillIfSame(int processId, DateTimeOffset startedAt, string processName);
}

public sealed class ProcessHost : IProcessHost
{
    /// <summary>How long to wait for the pipes to close after the process ended: a child that outlives it may hold them.</summary>
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

    public IRunningProcess Start(string fileName, IEnumerable<string> arguments, Action<string> onErrorLine, Action<int> onExited, bool keepInputOpen = false)
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

        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new ProcessStartException("The process did not start.");
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            process.Dispose();
            throw new ProcessStartException(e.Message, e);
        }

        if (!keepInputOpen)
        {
            try { process.StandardInput.Close(); }
            catch (IOException) { }
        }

        var errors = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
                    Invoke(() => onErrorLine(line));
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        });
        // Not read, but not left to fill up either.
        var output = Task.Run(async () =>
        {
            try { await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false); }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        });

        var running = new RunningProcess(process);
        _ = Task.Run(async () =>
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            try { await Task.WhenAll(errors, output).WaitAsync(DrainGrace).ConfigureAwait(false); }
            catch (TimeoutException) { }
            Invoke(() => onExited(process.ExitCode));
        });
        return running;
    }

    public bool KillIfSame(int processId, DateTimeOffset startedAt, string processName)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase)
                || (new DateTimeOffset(process.StartTime) - startedAt).Duration() > StartTimeTolerance)
                return false;
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Not running (any more), or not ours to look at.
            return false;
        }
    }

    /// <summary>A callback that throws must not end the reader, nor take the app down from a pool thread.</summary>
    private static void Invoke(Action callback)
    {
        try { callback(); }
        catch (Exception) { }
    }

    private sealed class RunningProcess : IRunningProcess
    {
        private readonly Process _process;

        public RunningProcess(Process process)
        {
            _process = process;
            Id = process.Id;
            StartedAt = TryStartTime(process);
        }

        public int Id { get; }

        public DateTimeOffset StartedAt { get; }

        public bool HasExited
        {
            get
            {
                try { return _process.HasExited; }
                catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException) { return true; }
            }
        }

        public void Kill()
        {
            try { _process.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception or ObjectDisposedException) { /* already gone */ }
        }

        public void Dispose() => _process.Dispose();

        private static DateTimeOffset TryStartTime(Process process)
        {
            try { return new DateTimeOffset(process.StartTime); }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException) { return DateTimeOffset.Now; }
        }
    }
}
