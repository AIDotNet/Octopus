using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Threading;
using Octopus.Platform;

namespace Octopus.App.Services;

/// <summary>
/// Captures unhandled managed exceptions to disk before the process terminates.
/// Desktop apps launched by the OS (Finder/Dock, Start Menu, desktop entry) discard stderr, and on macOS Avalonia
/// rethrows UI-thread exceptions through <c>ExceptionDispatchInfo.Throw</c> after unwinding AppKit's run loop, so the
/// native crash report loses the original C# stack. Writing <see cref="Exception.ToString"/> here preserves it.
/// </summary>
public static class CrashLog
{
    private const int MaxCrashFiles = 20;
    private const long MaxUnobservedLogBytes = 2 * 1024 * 1024;
    private const string CrashPrefix = "crash-";
    private const string CrashExtension = ".log";
    private const string UnobservedFileName = "unobserved.log";
    private const string UnobservedPreviousFileName = "unobserved.prev.log";

    private static readonly Lock s_gate = new();
    private static int s_installed;
    private static int s_dispatcherAttached;
    private static string? s_directory;
    private static Exception? s_lastCrashException;

    private static string AppVersion { get; } =
        typeof(CrashLog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(CrashLog).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>
    /// Directory where crash and unobserved-task logs are written.
    /// Uses <c>&lt;OCTOPUS_DATA_DIR&gt;/logs</c> when isolated, otherwise <c>~/.octopus/logs</c>.
    /// </summary>
    public static string ResolveLogDirectory(string? dataDirOverride = null, string? homeOverride = null)
    {
        var isolated = dataDirOverride ?? Environment.GetEnvironmentVariable(FixedAppPaths.Variable);
        if (!string.IsNullOrWhiteSpace(isolated))
        {
            return Path.Combine(Path.GetFullPath(isolated), "logs");
        }

        var home = homeOverride
            ?? (OperatingSystem.IsWindows()
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        return Path.Combine(home, ".octopus", "logs");
    }

    /// <summary>
    /// Registers process-wide handlers for <see cref="AppDomain.UnhandledException"/> and
    /// <see cref="TaskScheduler.UnobservedTaskException"/>. Safe to call before Avalonia initializes.
    /// </summary>
    public static void Install(string? directory = null)
    {
        if (Interlocked.Exchange(ref s_installed, 1) != 0) return;
        s_directory = directory ?? ResolveLogDirectory();
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    /// <summary>
    /// Subscribes to <see cref="Dispatcher.UnhandledException"/> on the UI thread: the managed stack is recorded before the
    /// native run loop unwinds, and an exception the app can survive is marked handled (see <see cref="ShouldRecover"/>).
    /// </summary>
    public static void AttachDispatcher()
    {
        if (Interlocked.Exchange(ref s_dispatcherAttached, 1) != 0) return;
        Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;
    }

    /// <summary>
    /// Writes a standalone crash log file for <paramref name="exceptionObject"/> and returns its path, or null when
    /// the same exception instance was already recorded or writing failed. Never throws.
    /// </summary>
    public static string? WriteCrash(
        string source,
        object? exceptionObject,
        bool isTerminating,
        string? directory = null,
        DateTimeOffset? now = null)
    {
        try
        {
            var exception = exceptionObject as Exception;
            var timestamp = now ?? DateTimeOffset.Now;
            var targetDir = directory ?? s_directory ?? ResolveLogDirectory();

            lock (s_gate)
            {
                // One fatal UI exception often passes through Dispatcher.UnhandledException, Program.Main's catch,
                // and AppDomain.UnhandledException in sequence; keep a single report for the same instance.
                if (exception is not null && ReferenceEquals(exception, s_lastCrashException))
                {
                    return null;
                }

                Directory.CreateDirectory(targetDir);
                var path = AllocateCrashPath(targetDir, timestamp);
                var report = FormatReport(source, exceptionObject, isTerminating, timestamp);
                File.WriteAllText(path, report, Encoding.UTF8);

                if (exception is not null)
                {
                    s_lastCrashException = exception;
                }

                PruneOldCrashFiles(targetDir);
                TryWriteStderr($"[Octopus] Crash log written to {path}{Environment.NewLine}{report}");
                return path;
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Appends an unobserved task exception to <c>unobserved.log</c> (rotating when over 2 MB). Never throws.
    /// </summary>
    public static string? WriteUnobserved(
        Exception exception,
        string? directory = null,
        DateTimeOffset? now = null)
    {
        try
        {
            var timestamp = now ?? DateTimeOffset.Now;
            var targetDir = directory ?? s_directory ?? ResolveLogDirectory();

            lock (s_gate)
            {
                Directory.CreateDirectory(targetDir);
                var path = Path.Combine(targetDir, UnobservedFileName);
                RotateIfOversized(path, Path.Combine(targetDir, UnobservedPreviousFileName));
                var entry = FormatReport("TaskScheduler.UnobservedTaskException", exception, isTerminating: false, timestamp)
                    + Environment.NewLine + new string('-', 72) + Environment.NewLine;
                File.AppendAllText(path, entry, Encoding.UTF8);
                return path;
            }
        }
        catch
        {
            return null;
        }
    }

    internal static void ResetDeduplicationForTests()
    {
        lock (s_gate)
        {
            s_lastCrashException = null;
            s_recoveredAt.Clear();
        }
    }

    private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e) =>
        WriteCrash("AppDomain.UnhandledException", e.ExceptionObject, e.IsTerminating);

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // An exception that escapes a UI-thread callback (a click handler, a binding, a posted job) is not a reason to
        // take the whole app down: on macOS Avalonia rethrows it after unwinding AppKit's run loop and the process ends
        // in abort(). Record it and keep running, unless the exception means the process state can no longer be trusted
        // or the same failure keeps repeating (a handler that throws on every frame would leave a frozen window).
        var recover = !e.Handled && ShouldRecover(e.Exception, Environment.TickCount64);
        WriteCrash(recover ? "Dispatcher.UIThread (recovered)" : "Dispatcher.UIThread", e.Exception, isTerminating: !e.Handled && !recover);
        if (recover)
        {
            e.Handled = true;
            StartupTrace.LogException("dispatcher:recovered", e.Exception);
        }
    }

    private const int RecoverBurstLimit = 20;
    private const long RecoverBurstWindowMs = 10_000;
    private static readonly Queue<long> s_recoveredAt = new();

    /// <summary>
    /// Whether a UI-thread exception may be swallowed: not when it signals a corrupted process (out of memory, bad
    /// native memory access, broken code), and not past <see cref="RecoverBurstLimit"/> within
    /// <see cref="RecoverBurstWindowMs"/>, where the app is stuck in a loop of failures and ending it is the lesser harm.
    /// </summary>
    internal static bool ShouldRecover(Exception exception, long nowMs)
    {
        if (exception is OutOfMemoryException or AccessViolationException or InvalidProgramException or BadImageFormatException)
        {
            return false;
        }

        lock (s_gate)
        {
            while (s_recoveredAt.Count > 0 && nowMs - s_recoveredAt.Peek() > RecoverBurstWindowMs)
            {
                s_recoveredAt.Dequeue();
            }
            if (s_recoveredAt.Count >= RecoverBurstLimit) return false;
            s_recoveredAt.Enqueue(nowMs);
            return true;
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e) =>
        WriteUnobserved(e.Exception);

    private static string AllocateCrashPath(string directory, DateTimeOffset timestamp)
    {
        var stamp = timestamp.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var pid = Environment.ProcessId;
        var baseName = $"{CrashPrefix}{stamp}-{pid}";
        var candidate = Path.Combine(directory, baseName + CrashExtension);
        for (var i = 1; File.Exists(candidate); i++)
        {
            candidate = Path.Combine(directory, $"{baseName}-{i}{CrashExtension}");
        }
        return candidate;
    }

    private static string FormatReport(string source, object? exceptionObject, bool isTerminating, DateTimeOffset timestamp)
    {
        var thread = Thread.CurrentThread;
        var sb = new StringBuilder(1024);
        sb.AppendLine("Octopus Crash Report");
        sb.Append("Timestamp: ")
          .Append(timestamp.ToString("O", CultureInfo.InvariantCulture))
          .Append(" (UTC: ")
          .Append(timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
          .AppendLine(")");
        sb.Append("Source: ").AppendLine(source);
        sb.Append("IsTerminating: ").AppendLine(isTerminating ? "True" : "False");
        sb.Append("Version: ").AppendLine(AppVersion);
        sb.Append("PID: ").AppendLine(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        sb.Append("Thread: #").Append(thread.ManagedThreadId.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(thread.Name))
        {
            sb.Append(" (").Append(thread.Name).Append(')');
        }
        sb.Append(" [ThreadPool=").Append(thread.IsThreadPoolThread ? "True" : "False").AppendLine("]");
        sb.Append("OS: ")
          .Append(RuntimeInformation.OSDescription)
          .Append(" (")
          .Append(RuntimeInformation.ProcessArchitecture)
          .AppendLine(")");
        sb.Append("Runtime: ").AppendLine(RuntimeInformation.FrameworkDescription);

        if (exceptionObject is Exception ex)
        {
            sb.Append("ExceptionType: ").AppendLine(ex.GetType().FullName ?? ex.GetType().Name);
            sb.Append("HResult: 0x").AppendLine(ex.HResult.ToString("X8", CultureInfo.InvariantCulture));
            sb.Append("Message: ").AppendLine(ex.Message);
            sb.AppendLine();
            sb.AppendLine("StackTrace / Details:");
            sb.AppendLine(ex.ToString());
        }
        else
        {
            sb.Append("ExceptionObject: ").AppendLine(exceptionObject?.ToString() ?? "<null>");
        }

        return sb.ToString();
    }

    private static void PruneOldCrashFiles(string directory)
    {
        try
        {
            var files = Directory.GetFiles(directory, CrashPrefix + "*" + CrashExtension);
            if (files.Length <= MaxCrashFiles) return;
            Array.Sort(files, StringComparer.Ordinal);
            var excess = files.Length - MaxCrashFiles;
            for (var i = 0; i < excess; i++)
            {
                try { File.Delete(files[i]); }
                catch { }
            }
        }
        catch { }
    }

    private static void RotateIfOversized(string path, string previousPath)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length >= MaxUnobservedLogBytes)
            {
                File.Move(path, previousPath, overwrite: true);
            }
        }
        catch { }
    }

    private static void TryWriteStderr(string text)
    {
        try { Console.Error.WriteLine(text); }
        catch { }
    }
}
