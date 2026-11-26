using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Octopus.App.Services;

/// <summary>
/// Writes a line to <c>&lt;data&gt;/logs/startup-*.log</c> at each start-up step and at every way the process can end
/// (normal exit, unhandled exception, or a Posix signal such as SIGHUP), so a start-up that ends before the window is
/// usable leaves behind where it stopped. Every line is flushed to disk immediately: a later abort() does not lose it.
/// It also keeps signals from killing the app outright: SIGHUP is ignored, a SIGTERM that follows it (the terminal that
/// started the app closing) is ignored too, and any other SIGTERM quits through the normal exit path.
/// </summary>
public static class StartupTrace
{
    private static readonly Lock Gate = new();
    private static readonly List<PosixSignalRegistration> Signals = [];
    private static StreamWriter? s_writer;
    private static FileStream? s_stream;
    private static string? s_path;
    private static int s_installed;

    /// <summary>Where the log for this run is written; null before <see cref="Install"/> or when the file could not be opened.</summary>
    public static string? Path => s_path;

    /// <summary>Call once, first thing in Main: opens the log, writes a header and watches every way the process can end.</summary>
    public static void Install(string[] args)
    {
        if (Interlocked.Exchange(ref s_installed, 1) != 0) return;
        try
        {
            var directory = CrashLog.ResolveLogDirectory();
            Directory.CreateDirectory(directory);
            PruneOldFiles(directory);
            var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var path = System.IO.Path.Combine(directory, $"startup-{stamp}-{Environment.ProcessId}.log");
            // Append (O_APPEND) so a line the signal handler writes with write(2) is not overwritten by the next managed write.
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096);
            s_stream = stream;
            s_writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            s_path = path;

            WriteRaw(Header(args, path));
        }
        catch
        {
            // Tracing must never keep the app from starting.
        }

        try
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Log("process-exit");
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception ex) LogException("appdomain-unhandled", ex);
                else Log("appdomain-unhandled", e.ExceptionObject?.ToString());
            };
        }
        catch
        {
            // Same.
        }

        // The desktop app is a GUI process: when the OS (or a shell that started it) hangs it up with SIGHUP, the managed
        // runtime's default is to terminate silently. A native handler records who sent it (siginfo.si_pid) and keeps the
        // app alive; it is installed last so it replaces anything the runtime set up for SIGHUP.
        InstallHangupWatcher(s_path);
        WatchTerminate();
        TryWatch(PosixSignal.SIGINT);
        TryWatch(PosixSignal.SIGQUIT);
    }

    public static void Log(string phase, string? detail = null)
    {
        if (s_writer is null) return;
        try
        {
            var now = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);
            var thread = Thread.CurrentThread;
            var line = string.Create(CultureInfo.InvariantCulture,
                $"{now} [pid {Environment.ProcessId} t{Environment.CurrentManagedThreadId}{(string.IsNullOrEmpty(thread.Name) ? string.Empty : " " + thread.Name)}] {phase}{(string.IsNullOrEmpty(detail) ? string.Empty : ": " + detail)}");
            lock (Gate)
            {
                s_writer.WriteLine(line);
                s_writer.Flush();
                s_stream?.Flush(flushToDisk: true);
            }
        }
        catch
        {
            // Never let tracing break the app.
        }
    }

    public static void LogException(string phase, Exception exception)
    {
        Log(phase, $"{exception.GetType().FullName}: {exception.Message}");
        Log(phase + "-stack", "\n" + exception);
    }

    // macOS signal numbers / sigaction layout (verified against <signal.h> on arm64):
    //   struct sigaction { union { void (*handler)(int); void (*action)(int, siginfo_t *, void *); }; int sa_mask; int sa_flags; }
    //   siginfo_t { int si_signo; int si_errno; int si_code; pid_t si_pid; uid_t si_uid; ... }
    private const int SighupNumber = 1;
    private const int SaSiginfo = 0x40;
    private const int SaRestart = 0x2;
    private const int OpenWriteAppend = 0x0001 | 0x0008;

    private static int s_signalFd = -1;
    private static IntPtr s_signalBuffer;
    private static int s_hangupPid;
    private static int s_hangupUid;
    private static int s_hangupCount;
    private static int s_hangupCountSeen;
    private static long s_lastHangupMs = long.MinValue;
    private static Action? s_shutdown;
    private static int s_shutdownRequested;

    /// <summary>A SIGTERM this soon after a SIGHUP is the terminal (or IDE) that started the app closing, not a quit.</summary>
    private const long HangupTerminateWindowMs = 15_000;

    /// <summary>How long a graceful shutdown asked for by SIGTERM may take before the process is ended anyway.</summary>
    private const int ShutdownGraceMs = 10_000;

    /// <summary>
    /// Lets SIGTERM end the app through its normal exit path (windows closed, settings flushed, tunnels stopped) instead
    /// of the runtime's immediate termination. Set once the desktop lifetime exists; before that SIGTERM ends the process.
    /// </summary>
    public static void SetShutdownHandler(Action shutdown) => Volatile.Write(ref s_shutdown, shutdown);

    [DllImport("libSystem.dylib", EntryPoint = "sigaction", SetLastError = true)]
    private static extern int NativeSigaction(int signum, IntPtr act, IntPtr oldact);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA2101", Justification = "Unix C API taking UTF-8; marshalled explicitly as LPUTF8Str.")]
    [DllImport("libSystem.dylib", EntryPoint = "open", SetLastError = true)]
    private static extern int NativeOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, int mode);

    [DllImport("libSystem.dylib", EntryPoint = "write", SetLastError = true)]
    private static extern nint NativeWrite(int fd, IntPtr buffer, nint count);

    /// <summary>Installs the native SIGHUP handler and a thread that names the sender once a signal was seen.</summary>
    private static unsafe void InstallHangupWatcher(string? path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path))
            {
                s_signalFd = NativeOpen(path, OpenWriteAppend, 0x1A4);
                s_signalBuffer = Marshal.AllocHGlobal(256);
            }

            var act = Marshal.AllocHGlobal(16);
            *(nint*)act = (nint)(delegate* unmanaged[Cdecl]<int, nint, nint, void>)&OnHangup;
            *(int*)(act + 8) = 0;
            *(int*)(act + 12) = SaSiginfo | SaRestart;
            _ = NativeSigaction(SighupNumber, act, IntPtr.Zero);
            Marshal.FreeHGlobal(act);

            var thread = new Thread(WatchSignalFlag)
            {
                IsBackground = true,
                Name = "startup-trace-signal",
            };
            thread.Start();
            Log("trace:sighup-watcher-installed", path);
        }
        catch
        {
            // Diagnosis only: a watcher that cannot install leaves the app as it was.
        }
    }

    /// <summary>Runs in signal context: record the sender, append one line with write(2), and swallow the signal.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnHangup(int signo, nint info, nint context)
    {
        try
        {
            var pid = info == 0 ? 0 : *(int*)(info + 12);
            var uid = info == 0 ? 0 : *(int*)(info + 16);
            Volatile.Write(ref s_hangupPid, pid);
            Volatile.Write(ref s_hangupUid, uid);
            Interlocked.Increment(ref s_hangupCount);

            if (s_signalFd < 0 || s_signalBuffer == IntPtr.Zero) return;
            var buffer = (byte*)s_signalBuffer;
            var n = 0;
            Append(buffer, ref n, "[signal] SIGHUP from pid=");
            AppendInt(buffer, ref n, pid);
            Append(buffer, ref n, " uid=");
            AppendInt(buffer, ref n, uid);
            Append(buffer, ref n, " (ignored)\n");
            _ = NativeWrite(s_signalFd, (nint)buffer, n);
        }
        catch
        {
            // A signal handler must not throw.
        }
    }

    private static unsafe void Append(byte* buffer, ref int n, string text)
    {
        foreach (var c in text) buffer[n++] = c > 0x7f ? (byte)'?' : (byte)c;
    }

    private static unsafe void AppendInt(byte* buffer, ref int n, int value)
    {
        if (value < 0)
        {
            buffer[n++] = (byte)'-';
            value = -value;
        }
        if (value == 0)
        {
            buffer[n++] = (byte)'0';
            return;
        }
        var digits = stackalloc byte[10];
        var count = 0;
        while (value > 0 && count < 10)
        {
            digits[count++] = (byte)('0' + value % 10);
            value /= 10;
        }
        while (count > 0) buffer[n++] = digits[--count];
    }

    private static void WatchSignalFlag()
    {
        while (true)
        {
            NoteHangups();
            var pid = Volatile.Read(ref s_hangupPid);
            if (pid != 0)
            {
                Volatile.Write(ref s_hangupPid, 0);
                var uid = Volatile.Read(ref s_hangupUid);
                Log("signal:sighup-sender", Describe(pid, uid));
            }
            Thread.Sleep(100);
        }
    }

    private static string Describe(int pid, int uid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            string path;
            try { path = process.MainModule?.FileName ?? "?"; }
            catch { path = "?"; }
            return $"pid={pid} uid={uid} name={process.ProcessName} path={path}";
        }
        catch (Exception e)
        {
            return $"pid={pid} uid={uid} name=<{e.GetType().Name}>";
        }
    }

    /// <summary>Stamps the time of a SIGHUP the native handler counted (kept out of signal context on purpose).</summary>
    private static void NoteHangups()
    {
        var count = Volatile.Read(ref s_hangupCount);
        if (Interlocked.Exchange(ref s_hangupCountSeen, count) != count)
        {
            Volatile.Write(ref s_lastHangupMs, Environment.TickCount64);
        }
    }

    /// <summary>
    /// SIGTERM no longer kills the app on the spot. Closing the terminal tab (or stopping the IDE run) that started it
    /// sends SIGHUP and then, a few seconds later, SIGTERM to its jobs: the SIGHUP is already ignored, so that SIGTERM is
    /// ignored too. Any other SIGTERM (kill, logout, an installer) quits through the normal exit path.
    /// </summary>
    private static void WatchTerminate()
    {
        try
        {
            Signals.Add(PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                NoteHangups();
                var sinceHangup = Environment.TickCount64 - Volatile.Read(ref s_lastHangupMs);
                if (Volatile.Read(ref s_lastHangupMs) != long.MinValue && sinceHangup <= HangupTerminateWindowMs)
                {
                    context.Cancel = true;
                    Log("signal:SIGTERM", $"ignored: {sinceHangup} ms after SIGHUP (the terminal that started the app closed)");
                    return;
                }

                var shutdown = Volatile.Read(ref s_shutdown);
                if (shutdown is null)
                {
                    Log("signal:SIGTERM", "default action will run (app not ready for a graceful shutdown)");
                    return;
                }

                context.Cancel = true;
                if (Interlocked.Exchange(ref s_shutdownRequested, 1) != 0)
                {
                    Log("signal:SIGTERM", "shutdown already in progress");
                    return;
                }
                Log("signal:SIGTERM", "graceful shutdown");
                StartShutdownDeadline();
                try { shutdown(); }
                catch (Exception e) { LogException("signal:SIGTERM:shutdown-failed", e); Environment.Exit(143); }
            }));
        }
        catch
        {
            // The signal is not available on this platform.
        }
    }

    private static void StartShutdownDeadline()
    {
        var thread = new Thread(() =>
        {
            Thread.Sleep(ShutdownGraceMs);
            Log("signal:SIGTERM", $"graceful shutdown did not finish within {ShutdownGraceMs} ms; exiting");
            Environment.Exit(143);
        })
        {
            IsBackground = true,
            Name = "startup-trace-shutdown-deadline",
        };
        thread.Start();
    }

    private static void TryWatch(PosixSignal signal)
    {
        try
        {
            Signals.Add(PosixSignalRegistration.Create(signal, context =>
            {
                Log($"signal:{signal}", $"default action will run (cancel={context.Cancel})");
            }));
        }
        catch
        {
            // The signal is not available on this platform.
        }
    }

    private static string Header(string[] args, string path)
    {
        var sb = new StringBuilder(1024);
        sb.AppendLine("Octopus start-up trace");
        sb.AppendLine($"Log: {path}");
        sb.AppendLine($"Started: {DateTimeOffset.Now:O}");
        sb.AppendLine($"Version: {AppVersion}");
        sb.AppendLine($"Process: {Environment.ProcessId} ({Environment.ProcessPath})");
        sb.AppendLine($"Args: {string.Join(' ', args.Select(a => a.Contains(' ') ? '"' + a + '"' : a))}");
        sb.AppendLine($"Cwd: {Environment.CurrentDirectory}");
        sb.AppendLine($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})");
        sb.AppendLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"Interactive: {Environment.UserInteractive}");
        foreach (var name in new[] { "HOME", "PATH", "OCTOPUS_DATA_DIR", "OCTOPUS_RELAY_URL", "OCTOPUS_SERVICE_URL" })
        {
            sb.Append(name).Append(": ").AppendLine(Environment.GetEnvironmentVariable(name) ?? "<unset>");
        }
        sb.AppendLine("---");
        return sb.ToString();
    }

    private static string AppVersion { get; } =
        typeof(StartupTrace).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(StartupTrace).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    private static void WriteRaw(string text)
    {
        if (s_writer is null) return;
        lock (Gate)
        {
            s_writer.Write(text);
            s_writer.Flush();
            s_stream?.Flush(flushToDisk: true);
        }
    }

    private static void PruneOldFiles(string directory)
    {
        try
        {
            var files = Directory.GetFiles(directory, "startup-*.log");
            if (files.Length <= 20) return;
            Array.Sort(files, StringComparer.Ordinal);
            for (var i = 0; i < files.Length - 20; i++)
            {
                try { File.Delete(files[i]); } catch { }
            }
        }
        catch { }
    }
}
