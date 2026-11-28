using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;

namespace Octopus.Mobile.Diagnostics;

/// <summary>
/// Cross-cutting mobile diagnostics and crash capture coordinator.
/// Collection is strictly opt-in and disabled by default (<see cref="IsCollectionEnabled"/> is <c>false</c>)
/// until the user explicitly grants permission. While disabled, no logs, crash files, or OS exit records are written.
/// </summary>
public static class MobileDiagnostics
{
    public const int MaxCrashFiles = 15;
    public const int MaxNativeExitFiles = 10;
    public const long MaxUnobservedLogBytes = 512 * 1024;

    private const string CrashPrefix = "crash-";
    private const string NativeExitPrefix = "native-exit-";
    private const string LogExtension = ".log";
    private const string UnobservedFileName = "unobserved.log";
    private const string UnobservedPreviousFileName = "unobserved.prev.log";
    private const string SessionStateFileName = "session-state.json";
    private const string PendingNoticeFileName = "pending-notice.json";
    private const string SeenNativeExitsFileName = "seen-native-exits.json";
    private const string ConsentFileName = "consent.txt";

    private const int RecoverBurstLimit = 12;
    private const long RecoverBurstWindowMs = 10_000;

    private static readonly Lock Gate = new();
    private static readonly MobileLogRing Ring = new();
    private static readonly Queue<long> RecoveredAt = new();

    private static IMobileDiagnosticsPlatform s_platform = new DefaultDiagnosticsPlatform();
    private static int s_processHooksInstalled;
    private static int s_dispatcherAttached;
    private static bool s_collectionEnabled;
    private static Exception? s_lastCrashException;
    private static PendingCrashNotice? s_pendingNotice;
    private static string s_activePage = "Startup";
    private static string s_appState = "foreground";
    private static DateTimeOffset s_startedAtUtc = DateTimeOffset.UtcNow;

    /// <summary>Raised whenever a new crash/error notice becomes available or is dismissed.</summary>
    public static event Action<PendingCrashNotice?>? PendingNoticeChanged;

    /// <summary>Raised whenever user consent for local diagnostic log collection changes.</summary>
    public static event Action<bool>? CollectionEnabledChanged;

    /// <summary>Current platform provider (Android, iOS, Desktop, or default fallback for tests).</summary>
    public static IMobileDiagnosticsPlatform Platform => Volatile.Read(ref s_platform);

    /// <summary>
    /// Whether the user has explicitly allowed local crash and diagnostic log collection.
    /// Defaults to <c>false</c>; when false, zero logs or crash files are collected or written.
    /// </summary>
    public static bool IsCollectionEnabled => Volatile.Read(ref s_collectionEnabled);

    /// <summary>Latest unacknowledged crash or native-exit notice to surface in the UI (always null when disabled).</summary>
    public static PendingCrashNotice? PendingNotice
    {
        get
        {
            lock (Gate) return s_collectionEnabled ? s_pendingNotice : null;
        }
    }

    /// <summary>Name of the currently visible page for crash report headers.</summary>
    public static string ActivePage => Volatile.Read(ref s_activePage);

    /// <summary>Informational version of <c>Octopus.Mobile</c>.</summary>
    public static string AppVersion { get; } =
        typeof(MobileDiagnostics).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(MobileDiagnostics).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>
    /// Installs diagnostics on <paramref name="platform"/> and attaches process hooks.
    /// If the user has not opted in (default), no log files, directories, sentinels, or OS exit records are written.
    /// </summary>
    public static void Initialize(
        IMobileDiagnosticsPlatform platform,
        bool attachProcessHooks = true,
        DateTimeOffset? now = null,
        bool? initialConsentOverride = null)
    {
        ArgumentNullException.ThrowIfNull(platform);
        var timestamp = now ?? DateTimeOffset.Now;
        bool enabled;
        PendingCrashNotice? activeNotice = null;

        lock (Gate)
        {
            s_platform = platform;
            s_startedAtUtc = timestamp.ToUniversalTime();
            s_appState = "foreground";
            enabled = initialConsentOverride ?? ReadPersistedConsentLocked(platform.LogDirectory);
            Volatile.Write(ref s_collectionEnabled, enabled);

            if (enabled)
            {
                activeNotice = ActivateCollectionLocked(platform, timestamp);
            }
            else
            {
                Ring.ConfigureDirectory(null);
                Ring.Clear();
                s_pendingNotice = null;
            }
        }

        if (attachProcessHooks && Interlocked.Exchange(ref s_processHooksInstalled, 1) == 0)
        {
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        }

        if (enabled)
        {
            var env = platform.GetEnvironment();
            Info("LIFE", $"Diagnostics initialized ({env.Platform} {env.OsVersion}, {env.DeviceModel}, v{env.AppVersion}, AOT={env.IsNativeAot})");
            if (activeNotice is not null)
            {
                PendingNoticeChanged?.Invoke(activeNotice);
            }
        }
    }

    /// <summary>
    /// Turns local crash and diagnostic log collection on or off per explicit user consent.
    /// Turning it off immediately stops all recording, clears the in-memory ring, and removes the foreground sentinel.
    /// </summary>
    public static void SetCollectionEnabled(bool enabled, DateTimeOffset? now = null)
    {
        var timestamp = now ?? DateTimeOffset.Now;
        PendingCrashNotice? noticeToNotify = null;
        bool notifyNotice;
        bool changed;

        lock (Gate)
        {
            changed = s_collectionEnabled != enabled;
            Volatile.Write(ref s_collectionEnabled, enabled);

            var platform = s_platform;
            if (enabled)
            {
                WritePersistedConsentLocked(platform.LogDirectory, enabled: true);
                noticeToNotify = ActivateCollectionLocked(platform, timestamp);
                notifyNotice = noticeToNotify is not null;
            }
            else
            {
                Ring.ConfigureDirectory(null);
                Ring.Clear();
                TryDelete(Path.Combine(platform.LogDirectory, ConsentFileName));
                TryDelete(Path.Combine(platform.LogDirectory, SessionStateFileName));
                notifyNotice = s_pendingNotice is not null;
                s_pendingNotice = null;
                TryDelete(Path.Combine(platform.LogDirectory, PendingNoticeFileName));
            }
        }

        if (enabled && changed)
        {
            Info("LIFE", "Local diagnostics collection enabled by user");
        }
        if (notifyNotice)
        {
            PendingNoticeChanged?.Invoke(noticeToNotify);
        }
        if (changed)
        {
            CollectionEnabledChanged?.Invoke(enabled);
        }
    }

    /// <summary>
    /// Attaches <see cref="Dispatcher.UIThread.UnhandledException"/> once Avalonia is initialized so recoverable UI
    /// exceptions do not bring down the app (crash details are persisted only when <see cref="IsCollectionEnabled"/> is true).
    /// </summary>
    public static void AttachDispatcher()
    {
        if (Interlocked.Exchange(ref s_dispatcherAttached, 1) != 0) return;
        Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;
    }

    /// <summary>Records an informational breadcrumb when collection is enabled.</summary>
    public static void Info(string category, string message)
    {
        if (!IsCollectionEnabled) return;
        Ring.Write("INFO", category, message);
    }

    /// <summary>Records a warning breadcrumb when collection is enabled.</summary>
    public static void Warn(string category, string message)
    {
        if (!IsCollectionEnabled) return;
        Ring.Write("WARN", category, message);
    }

    /// <summary>Records a handled error and optional exception summary when collection is enabled.</summary>
    public static void Error(string category, string message, Exception? exception = null)
    {
        if (!IsCollectionEnabled) return;
        var detail = exception is null
            ? message
            : $"{message} | {exception.GetType().Name}: {exception.Message}";
        Ring.Write("ERROR", category, detail);
    }

    /// <summary>Tracks active page transitions when collection is enabled.</summary>
    public static void NotePage(string pageName)
    {
        if (string.IsNullOrWhiteSpace(pageName)) return;
        Volatile.Write(ref s_activePage, pageName);
        if (!IsCollectionEnabled) return;
        Info("NAV", $"Active page -> {pageName}");
        UpdateSessionSentinelState(s_appState);
    }

    /// <summary>Marks the app as backgrounded (prevents normal OS background reclamation from triggering dirty-exit alerts).</summary>
    public static void NoteBackgrounded()
    {
        if (!IsCollectionEnabled) return;
        Info("LIFE", "App moved to background");
        UpdateSessionSentinelState("background");
    }

    /// <summary>Marks the app as foregrounded again.</summary>
    public static void NoteForegrounded()
    {
        if (!IsCollectionEnabled) return;
        Info("LIFE", "App returned to foreground");
        UpdateSessionSentinelState("foreground");
    }

    /// <summary>Records an OS low-memory pressure callback when collection is enabled.</summary>
    public static void NoteMemoryWarning(string detail)
    {
        if (!IsCollectionEnabled) return;
        var managedMb = GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0);
        Warn("MEM", string.Create(CultureInfo.InvariantCulture, $"Memory warning ({detail}), managedHeap={managedMb:F1}MB"));
    }

    /// <summary>Records a managed crash exception to disk when collection is enabled; returns null when disabled, deduped, or failed.</summary>
    public static string? RecordCrash(
        string source,
        object? exceptionObject,
        bool isTerminating,
        bool isRecovered = false,
        DateTimeOffset? now = null)
    {
        if (!IsCollectionEnabled) return null;

        PendingCrashNotice? createdNotice = null;
        string? writtenPath = null;

        try
        {
            var exception = exceptionObject as Exception;
            var timestamp = now ?? DateTimeOffset.Now;
            var platform = Platform;
            var targetDir = platform.LogDirectory;

            lock (Gate)
            {
                if (!s_collectionEnabled) return null;
                if (exception is not null && ReferenceEquals(exception, s_lastCrashException))
                {
                    return null;
                }

                Directory.CreateDirectory(targetDir);
                var reportId = GenerateReportId(timestamp);
                var path = AllocatePath(targetDir, CrashPrefix, timestamp);
                var breadcrumbs = Ring.SnapshotBreadcrumbs(60);
                var report = FormatManagedCrashReport(
                    reportId,
                    source,
                    exceptionObject,
                    isTerminating,
                    isRecovered,
                    timestamp,
                    platform.GetEnvironment(),
                    breadcrumbs);

                WriteTextFlushToDisk(path, report);
                writtenPath = path;

                if (exception is not null)
                {
                    s_lastCrashException = exception;
                }

                PruneFilesLocked(targetDir, CrashPrefix + "*" + LogExtension, MaxCrashFiles);

                var summary = exception is not null
                    ? LogSanitizer.Sanitize($"{exception.GetType().Name}: {exception.Message}")
                    : LogSanitizer.Sanitize(exceptionObject?.ToString() ?? "Unknown error");
                if (summary.Length > 180) summary = summary[..180] + "…";

                createdNotice = new PendingCrashNotice
                {
                    ReportId = reportId,
                    FileName = Path.GetFileName(path),
                    Category = source,
                    Summary = summary,
                    Timestamp = timestamp,
                    IsRecovered = isRecovered,
                };
                s_pendingNotice = createdNotice;
                SavePendingNoticeLocked(targetDir, createdNotice);
            }

            Ring.Write("CRASH", source, $"Recorded {createdNotice.ReportId}: {createdNotice.Summary}", now);
            PendingNoticeChanged?.Invoke(createdNotice);
        }
        catch
        {
            return null;
        }

        return writtenPath;
    }

    /// <summary>Records a platform-native crash synchronously to disk when collection is enabled.</summary>
    public static string? RecordPlatformCrash(
        string source,
        string exceptionType,
        string message,
        string stackTrace,
        bool isTerminating,
        DateTimeOffset? now = null)
    {
        if (!IsCollectionEnabled) return null;

        PendingCrashNotice? createdNotice = null;
        string? writtenPath = null;

        try
        {
            var timestamp = now ?? DateTimeOffset.Now;
            var platform = Platform;
            var targetDir = platform.LogDirectory;

            lock (Gate)
            {
                if (!s_collectionEnabled) return null;

                Directory.CreateDirectory(targetDir);
                var reportId = GenerateReportId(timestamp);
                var path = AllocatePath(targetDir, CrashPrefix, timestamp);
                var breadcrumbs = Ring.SnapshotBreadcrumbs(60);
                var report = FormatPlatformCrashReport(
                    reportId,
                    source,
                    exceptionType,
                    message,
                    stackTrace,
                    isTerminating,
                    timestamp,
                    platform.GetEnvironment(),
                    breadcrumbs);

                WriteTextFlushToDisk(path, report);
                writtenPath = path;
                PruneFilesLocked(targetDir, CrashPrefix + "*" + LogExtension, MaxCrashFiles);

                var summary = LogSanitizer.Sanitize($"{exceptionType}: {message}");
                if (summary.Length > 180) summary = summary[..180] + "…";

                createdNotice = new PendingCrashNotice
                {
                    ReportId = reportId,
                    FileName = Path.GetFileName(path),
                    Category = source,
                    Summary = summary,
                    Timestamp = timestamp,
                    IsRecovered = !isTerminating,
                };
                s_pendingNotice = createdNotice;
                SavePendingNoticeLocked(targetDir, createdNotice);
            }

            Ring.Write("CRASH", source, $"Recorded {createdNotice.ReportId}: {createdNotice.Summary}", now);
            PendingNoticeChanged?.Invoke(createdNotice);
        }
        catch
        {
            return null;
        }

        return writtenPath;
    }

    /// <summary>Appends an unobserved task exception to <c>unobserved.log</c> when collection is enabled. Never throws.</summary>
    public static string? RecordUnobserved(Exception exception, DateTimeOffset? now = null)
    {
        if (!IsCollectionEnabled) return null;

        try
        {
            var timestamp = now ?? DateTimeOffset.Now;
            var platform = Platform;
            var targetDir = platform.LogDirectory;

            lock (Gate)
            {
                if (!s_collectionEnabled) return null;

                Directory.CreateDirectory(targetDir);
                var path = Path.Combine(targetDir, UnobservedFileName);
                RotateIfOversized(path, Path.Combine(targetDir, UnobservedPreviousFileName), MaxUnobservedLogBytes);
                var entry = FormatManagedCrashReport(
                    GenerateReportId(timestamp),
                    "TaskScheduler.UnobservedTaskException",
                    exception,
                    isTerminating: false,
                    isRecovered: true,
                    timestamp,
                    platform.GetEnvironment(),
                    Ring.SnapshotBreadcrumbs(20))
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

    /// <summary>Records a newly discovered native diagnostic payload (e.g. iOS MetricKit) when collection is enabled.</summary>
    public static string? RecordNativeExit(NativeExitRecord record, bool raiseNotice = true)
    {
        if (!IsCollectionEnabled) return null;

        PendingCrashNotice? notice = null;
        string? path = null;
        lock (Gate)
        {
            if (!s_collectionEnabled) return null;
            var platform = Platform;
            try
            {
                Directory.CreateDirectory(platform.LogDirectory);
                var seen = LoadSeenNativeExitsLocked(platform.LogDirectory);
                if (!seen.Add(record.Id)) return null;
                path = WriteNativeExitRecordLocked(platform.LogDirectory, record, platform.GetEnvironment());
                SaveSeenNativeExitsLocked(platform.LogDirectory, seen);
                PruneFilesLocked(platform.LogDirectory, NativeExitPrefix + "*" + LogExtension, MaxNativeExitFiles);

                if (raiseNotice)
                {
                    notice = new PendingCrashNotice
                    {
                        ReportId = GenerateReportId(record.Timestamp),
                        FileName = Path.GetFileName(path),
                        Category = record.Reason,
                        Summary = LogSanitizer.Sanitize(record.Description),
                        Timestamp = record.Timestamp,
                        IsRecovered = false,
                    };
                    s_pendingNotice = notice;
                    SavePendingNoticeLocked(platform.LogDirectory, notice);
                }
            }
            catch
            {
                return null;
            }
        }

        if (notice is not null)
        {
            PendingNoticeChanged?.Invoke(notice);
        }
        return path;
    }

    /// <summary>Dismisses the active post-crash or recovered-error banner without deleting underlying log files.</summary>
    public static void DismissPendingNotice()
    {
        lock (Gate)
        {
            s_pendingNotice = null;
            TryDelete(Path.Combine(Platform.LogDirectory, PendingNoticeFileName));
        }
        PendingNoticeChanged?.Invoke(null);
    }

    /// <summary>Lists persisted crash and native-exit records newest-first for the Diagnostics UI.</summary>
    public static IReadOnlyList<DiagnosticRecord> ListRecords()
    {
        var results = new List<DiagnosticRecord>();
        var directory = Platform.LogDirectory;
        if (!Directory.Exists(directory)) return results;

        try
        {
            var files = Directory.GetFiles(directory, CrashPrefix + "*" + LogExtension)
                .Concat(Directory.GetFiles(directory, NativeExitPrefix + "*" + LogExtension))
                .OrderByDescending(static f => File.GetLastWriteTimeUtc(f))
                .ThenByDescending(static f => Path.GetFileName(f), StringComparer.Ordinal);

            foreach (var file in files)
            {
                if (TryParseRecordMetadata(file) is { } record)
                {
                    results.Add(record);
                }
            }
        }
        catch
        {
        }

        return results;
    }

    /// <summary>Reads a persisted crash/native-exit log with optional additional identifier masking.</summary>
    public static string ReadRecordContent(DiagnosticRecord record, bool maskIdentifiers = true, int maxChars = 16_000)
    {
        try
        {
            if (!File.Exists(record.FilePath)) return string.Empty;
            var content = File.ReadAllText(record.FilePath, Encoding.UTF8);
            var clean = LogSanitizer.Sanitize(content, maskIdentifiers);
            return clean.Length <= maxChars ? clean : clean[..maxChars] + Environment.NewLine + "… (truncated)";
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>Reads recent sanitized runtime log lines for in-app preview.</summary>
    public static IReadOnlyList<string> ReadRecentRuntimeLines(int maxLines = 120, bool maskIdentifiers = true)
    {
        if (!IsCollectionEnabled) return [];
        var lines = Ring.ReadRecentLines(maxLines);
        if (!maskIdentifiers) return lines;
        var result = new string[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            result[i] = LogSanitizer.Sanitize(lines[i], maskIdentifiers: true);
        }
        return result;
    }

    /// <summary>Returns total bytes currently occupied by persisted logs on disk.</summary>
    public static long GetTotalLogBytes()
    {
        var directory = Platform.LogDirectory;
        if (!Directory.Exists(directory)) return 0;
        try
        {
            long total = 0;
            foreach (var file in Directory.GetFiles(directory, "*.log"))
            {
                total += new FileInfo(file).Length;
            }
            return total;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Deletes all crash, native-exit, unobserved, and runtime logs and clears the pending banner.</summary>
    public static void ClearAllLogs()
    {
        lock (Gate)
        {
            Ring.Clear();
            s_lastCrashException = null;
            s_pendingNotice = null;
            var dir = Platform.LogDirectory;
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.GetFiles(dir, "*.log"))
                {
                    TryDelete(file);
                }
                TryDelete(Path.Combine(dir, PendingNoticeFileName));
            }
            var exportDir = Platform.ExportDirectory;
            if (Directory.Exists(exportDir))
            {
                foreach (var file in Directory.GetFiles(exportDir, "octopus-mobile-diag-*.zip"))
                {
                    TryDelete(file);
                }
            }
        }
        PendingNoticeChanged?.Invoke(null);
    }

    /// <summary>
    /// Determines whether a UI-thread exception can be safely swallowed and recovered from:
    /// refuses process-corrupting faults and stops recovering past <see cref="RecoverBurstLimit"/> within
    /// <see cref="RecoverBurstWindowMs"/> so an infinite render-loop crash terminates instead of freezing.
    /// </summary>
    internal static bool ShouldRecover(Exception exception, long nowMs)
    {
        if (exception is OutOfMemoryException or AccessViolationException or InvalidProgramException or BadImageFormatException)
        {
            return false;
        }

        lock (Gate)
        {
            while (RecoveredAt.Count > 0 && nowMs - RecoveredAt.Peek() > RecoverBurstWindowMs)
            {
                RecoveredAt.Dequeue();
            }
            if (RecoveredAt.Count >= RecoverBurstLimit) return false;
            RecoveredAt.Enqueue(nowMs);
            return true;
        }
    }

    internal static void ResetForTests(IMobileDiagnosticsPlatform? platform = null)
    {
        lock (Gate)
        {
            Volatile.Write(ref s_collectionEnabled, false);
            s_platform = platform ?? new DefaultDiagnosticsPlatform();
            Ring.ConfigureDirectory(null);
            Ring.Clear();
            s_lastCrashException = null;
            s_pendingNotice = null;
            s_activePage = "Startup";
            s_appState = "foreground";
            RecoveredAt.Clear();
        }
        PendingNoticeChanged?.Invoke(null);
        CollectionEnabledChanged?.Invoke(false);
    }

    private static PendingCrashNotice? ActivateCollectionLocked(IMobileDiagnosticsPlatform platform, DateTimeOffset timestamp)
    {
        Ring.ConfigureDirectory(platform.LogDirectory);
        try
        {
            Directory.CreateDirectory(platform.LogDirectory);
        }
        catch
        {
            // Never block startup on storage failure.
        }

        var loadedNotice = LoadPendingNoticeLocked(platform.LogDirectory);
        var nativeNotice = ImportNativeExitRecordsLocked(platform, timestamp);
        var dirtyNotice = InspectPreviousSessionSentinelLocked(platform, timestamp);
        s_pendingNotice = nativeNotice ?? dirtyNotice ?? loadedNotice;
        if (s_pendingNotice is not null)
        {
            SavePendingNoticeLocked(platform.LogDirectory, s_pendingNotice);
        }

        WriteSessionSentinelLocked(platform.LogDirectory, s_appState, timestamp);
        return s_pendingNotice;
    }

    private static bool ReadPersistedConsentLocked(string logDirectory)
    {
        try
        {
            var path = Path.Combine(logDirectory, ConsentFileName);
            if (!File.Exists(path)) return false;
            return string.Equals(File.ReadAllText(path, Encoding.UTF8).Trim(), "1", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static void WritePersistedConsentLocked(string logDirectory, bool enabled)
    {
        try
        {
            var path = Path.Combine(logDirectory, ConsentFileName);
            if (!enabled)
            {
                TryDelete(path);
                return;
            }
            Directory.CreateDirectory(logDirectory);
            File.WriteAllText(path, "1", Encoding.UTF8);
        }
        catch
        {
        }
    }

    private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e) =>
        RecordCrash("AppDomain.UnhandledException", e.ExceptionObject, e.IsTerminating);

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        RecordUnobserved(e.Exception);
        e.SetObserved();
    }

    private static void OnProcessExit(object? sender, EventArgs e)
    {
        if (!IsCollectionEnabled) return;
        Info("LIFE", "Process exiting cleanly");
        UpdateSessionSentinelState("exited");
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var recover = !e.Handled && ShouldRecover(e.Exception, Environment.TickCount64);
        if (IsCollectionEnabled)
        {
            RecordCrash(
                recover ? "Dispatcher.UIThread (recovered)" : "Dispatcher.UIThread",
                e.Exception,
                isTerminating: !e.Handled && !recover,
                isRecovered: recover);
        }
        if (recover)
        {
            e.Handled = true;
        }
    }

    private static PendingCrashNotice? ImportNativeExitRecordsLocked(IMobileDiagnosticsPlatform platform, DateTimeOffset now)
    {
        PendingCrashNotice? latest = null;
        try
        {
            var records = platform.CollectNativeExitRecords();
            if (records.Count == 0) return null;
            var seen = LoadSeenNativeExitsLocked(platform.LogDirectory);
            var changed = false;

            foreach (var record in records.OrderBy(static r => r.Timestamp))
            {
                if (!seen.Add(record.Id)) continue;
                var path = WriteNativeExitRecordLocked(platform.LogDirectory, record, platform.GetEnvironment());
                changed = true;
                latest = new PendingCrashNotice
                {
                    ReportId = GenerateReportId(record.Timestamp),
                    FileName = Path.GetFileName(path),
                    Category = record.Reason,
                    Summary = LogSanitizer.Sanitize(record.Description),
                    Timestamp = record.Timestamp,
                    IsRecovered = false,
                };
            }

            if (changed)
            {
                SaveSeenNativeExitsLocked(platform.LogDirectory, seen);
                PruneFilesLocked(platform.LogDirectory, NativeExitPrefix + "*" + LogExtension, MaxNativeExitFiles);
            }
        }
        catch
        {
        }
        return latest;
    }

    private static PendingCrashNotice? InspectPreviousSessionSentinelLocked(IMobileDiagnosticsPlatform platform, DateTimeOffset now)
    {
        var sentinelPath = Path.Combine(platform.LogDirectory, SessionStateFileName);
        if (!File.Exists(sentinelPath)) return null;

        try
        {
            var json = File.ReadAllText(sentinelPath, Encoding.UTF8);
            var previous = JsonSerializer.Deserialize(json, MobileDiagnosticsJsonContext.Default.SessionStateMarker);
            if (previous is null || !string.Equals(previous.State, "foreground", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var hasNewerCrash = Directory.GetFiles(platform.LogDirectory, CrashPrefix + "*" + LogExtension)
                .Concat(Directory.GetFiles(platform.LogDirectory, NativeExitPrefix + "*" + LogExtension))
                .Any(f => File.GetLastWriteTimeUtc(f) >= previous.StartedAtUtc.UtcDateTime.AddSeconds(-2));
            if (hasNewerCrash) return null;

            var record = new NativeExitRecord(
                Id: $"dirty-{previous.Pid}-{previous.StartedAtUtc.ToUnixTimeSeconds()}",
                Timestamp: previous.UpdatedAtUtc == default ? now : previous.UpdatedAtUtc,
                Reason: "DirtyExit (Suspected Native Crash / OOM / Watchdog)",
                Description: $"Previous foreground session (v{previous.AppVersion}, page={previous.LastPage}) ended unexpectedly without a managed crash handler.",
                Details: $"PreviousPid={previous.Pid}\nStartedAtUtc={previous.StartedAtUtc:O}\nLastUpdatedUtc={previous.UpdatedAtUtc:O}\nLastPage={previous.LastPage}");

            var path = WriteNativeExitRecordLocked(platform.LogDirectory, record, platform.GetEnvironment());
            PruneFilesLocked(platform.LogDirectory, NativeExitPrefix + "*" + LogExtension, MaxNativeExitFiles);

            return new PendingCrashNotice
            {
                ReportId = GenerateReportId(record.Timestamp),
                FileName = Path.GetFileName(path),
                Category = record.Reason,
                Summary = record.Description,
                Timestamp = record.Timestamp,
                IsRecovered = false,
            };
        }
        catch
        {
            return null;
        }
    }

    private static string WriteNativeExitRecordLocked(string directory, NativeExitRecord record, DeviceEnvironmentInfo env)
    {
        var path = AllocatePath(directory, NativeExitPrefix, record.Timestamp);
        var reportId = GenerateReportId(record.Timestamp);
        var sb = new StringBuilder(1024);
        sb.AppendLine("=== Octopus Mobile Native Exit Report ===");
        sb.Append("ReportId: ").AppendLine(reportId);
        sb.Append("Timestamp: ").AppendLine(record.Timestamp.ToString("O", CultureInfo.InvariantCulture));
        sb.Append("Category: ").AppendLine(LogSanitizer.Sanitize(record.Reason));
        sb.Append("IsTerminating: True").AppendLine();
        sb.Append("AppVersion: ").AppendLine(env.AppVersion);
        sb.Append("Platform: ").Append(env.Platform).Append(' ').Append(env.OsVersion).Append(" / ").Append(env.DeviceModel).Append(" (").Append(env.Architecture).AppendLine(")");
        sb.Append("Runtime: ").Append(env.RuntimeDescription).Append(" (NativeAOT=").Append(env.IsNativeAot ? "True" : "False").AppendLine(")");
        sb.Append("Summary: ").AppendLine(LogSanitizer.Sanitize(record.Description));
        if (!string.IsNullOrWhiteSpace(record.Details))
        {
            sb.AppendLine();
            sb.AppendLine("--- Details ---");
            sb.AppendLine(LogSanitizer.Sanitize(record.Details));
        }

        WriteTextFlushToDisk(path, sb.ToString());
        return path;
    }

    private static void UpdateSessionSentinelState(string state)
    {
        lock (Gate)
        {
            s_appState = state;
            if (!s_collectionEnabled) return;
            WriteSessionSentinelLocked(Platform.LogDirectory, state, DateTimeOffset.Now);
        }
    }

    private static void WriteSessionSentinelLocked(string directory, string state, DateTimeOffset now)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var marker = new SessionStateMarker
            {
                Pid = Environment.ProcessId,
                StartedAtUtc = s_startedAtUtc,
                UpdatedAtUtc = now.ToUniversalTime(),
                State = state,
                LastPage = s_activePage,
                AppVersion = AppVersion,
            };
            var json = JsonSerializer.Serialize(marker, MobileDiagnosticsJsonContext.Default.SessionStateMarker);
            var path = Path.Combine(directory, SessionStateFileName);
            File.WriteAllText(path, json, Encoding.UTF8);
        }
        catch
        {
        }
    }

    private static PendingCrashNotice? LoadPendingNoticeLocked(string directory)
    {
        var path = Path.Combine(directory, PendingNoticeFileName);
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize(json, MobileDiagnosticsJsonContext.Default.PendingCrashNotice);
        }
        catch
        {
            return null;
        }
    }

    private static void SavePendingNoticeLocked(string directory, PendingCrashNotice notice)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(notice, MobileDiagnosticsJsonContext.Default.PendingCrashNotice);
            File.WriteAllText(Path.Combine(directory, PendingNoticeFileName), json, Encoding.UTF8);
        }
        catch
        {
        }
    }

    private static HashSet<string> LoadSeenNativeExitsLocked(string directory)
    {
        var path = Path.Combine(directory, SeenNativeExitsFileName);
        if (!File.Exists(path)) return new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            var data = JsonSerializer.Deserialize(json, MobileDiagnosticsJsonContext.Default.SeenNativeExitsState);
            return data?.Ids is { } ids
                ? new HashSet<string>(ids, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
        }
        catch
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private static void SaveSeenNativeExitsLocked(string directory, HashSet<string> ids)
    {
        try
        {
            var trimmed = ids.TakeLast(64).ToList();
            var json = JsonSerializer.Serialize(new SeenNativeExitsState { Ids = trimmed }, MobileDiagnosticsJsonContext.Default.SeenNativeExitsState);
            File.WriteAllText(Path.Combine(directory, SeenNativeExitsFileName), json, Encoding.UTF8);
        }
        catch
        {
        }
    }

    private static string FormatManagedCrashReport(
        string reportId,
        string source,
        object? exceptionObject,
        bool isTerminating,
        bool isRecovered,
        DateTimeOffset timestamp,
        DeviceEnvironmentInfo env,
        IReadOnlyList<DiagnosticBreadcrumb> breadcrumbs)
    {
        var thread = Thread.CurrentThread;
        var managedMb = GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0);
        var sb = new StringBuilder(2048);
        sb.AppendLine("=== Octopus Mobile Crash Report ===");
        sb.Append("ReportId: ").AppendLine(reportId);
        sb.Append("Timestamp: ")
          .Append(timestamp.ToString("O", CultureInfo.InvariantCulture))
          .Append(" (UTC: ")
          .Append(timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
          .AppendLine(")");
        sb.Append("Category: ").AppendLine(source);
        sb.Append("IsTerminating: ").AppendLine(isTerminating ? "True" : "False");
        sb.Append("Recovered: ").AppendLine(isRecovered ? "True" : "False");
        sb.Append("AppVersion: ").AppendLine(env.AppVersion);
        sb.Append("Platform: ").Append(env.Platform).Append(' ').Append(env.OsVersion).Append(" / ").Append(env.DeviceModel).Append(" (").Append(env.Architecture).AppendLine(")");
        sb.Append("Runtime: ").Append(env.RuntimeDescription).Append(" (NativeAOT=").Append(env.IsNativeAot ? "True" : "False").AppendLine(")");
        sb.Append("Memory: ManagedHeap=").Append(managedMb.ToString("F1", CultureInfo.InvariantCulture)).Append("MB");
        if (env.TotalMemoryMb is { } totalMb)
        {
            sb.Append(", TotalDeviceMemory=").Append(totalMb.ToString(CultureInfo.InvariantCulture)).Append("MB");
        }
        sb.AppendLine();
        sb.Append("AppState: ").Append(s_appState).Append(", ActivePage=").AppendLine(ActivePage);
        sb.Append("PID: ").AppendLine(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        sb.Append("Thread: #").Append(thread.ManagedThreadId.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(thread.Name))
        {
            sb.Append(" (").Append(thread.Name).Append(')');
        }
        sb.Append(" [ThreadPool=").Append(thread.IsThreadPoolThread ? "True" : "False").AppendLine("]");

        if (exceptionObject is Exception ex)
        {
            var summary = LogSanitizer.Sanitize($"{ex.GetType().Name}: {ex.Message}");
            sb.Append("Summary: ").AppendLine(summary);
            sb.Append("ExceptionType: ").AppendLine(ex.GetType().FullName ?? ex.GetType().Name);
            sb.Append("HResult: 0x").AppendLine(ex.HResult.ToString("X8", CultureInfo.InvariantCulture));
            sb.AppendLine();
            sb.AppendLine("--- Exception Details ---");
            sb.AppendLine(LogSanitizer.Sanitize(ex.ToString()));
        }
        else
        {
            var raw = LogSanitizer.Sanitize(exceptionObject?.ToString() ?? "<null>");
            sb.Append("Summary: ").AppendLine(raw);
            sb.Append("ExceptionObject: ").AppendLine(raw);
        }

        AppendBreadcrumbs(sb, breadcrumbs);
        return sb.ToString();
    }

    private static string FormatPlatformCrashReport(
        string reportId,
        string source,
        string exceptionType,
        string message,
        string stackTrace,
        bool isTerminating,
        DateTimeOffset timestamp,
        DeviceEnvironmentInfo env,
        IReadOnlyList<DiagnosticBreadcrumb> breadcrumbs)
    {
        var sb = new StringBuilder(2048);
        sb.AppendLine("=== Octopus Mobile Platform Crash Report ===");
        sb.Append("ReportId: ").AppendLine(reportId);
        sb.Append("Timestamp: ").AppendLine(timestamp.ToString("O", CultureInfo.InvariantCulture));
        sb.Append("Category: ").AppendLine(LogSanitizer.Sanitize(source));
        sb.Append("IsTerminating: ").AppendLine(isTerminating ? "True" : "False");
        sb.Append("AppVersion: ").AppendLine(env.AppVersion);
        sb.Append("Platform: ").Append(env.Platform).Append(' ').Append(env.OsVersion).Append(" / ").Append(env.DeviceModel).Append(" (").Append(env.Architecture).AppendLine(")");
        sb.Append("Runtime: ").Append(env.RuntimeDescription).Append(" (NativeAOT=").Append(env.IsNativeAot ? "True" : "False").AppendLine(")");
        sb.Append("AppState: ").Append(s_appState).Append(", ActivePage=").AppendLine(ActivePage);
        sb.Append("Summary: ").AppendLine(LogSanitizer.Sanitize($"{exceptionType}: {message}"));
        sb.Append("ExceptionType: ").AppendLine(LogSanitizer.Sanitize(exceptionType));
        sb.AppendLine();
        sb.AppendLine("--- Platform StackTrace ---");
        sb.AppendLine(LogSanitizer.Sanitize(stackTrace));
        AppendBreadcrumbs(sb, breadcrumbs);
        return sb.ToString();
    }

    private static void AppendBreadcrumbs(StringBuilder sb, IReadOnlyList<DiagnosticBreadcrumb> breadcrumbs)
    {
        if (breadcrumbs.Count == 0) return;
        sb.AppendLine();
        sb.Append("--- Recent Breadcrumbs (").Append(breadcrumbs.Count.ToString(CultureInfo.InvariantCulture)).AppendLine(") ---");
        foreach (var crumb in breadcrumbs)
        {
            sb.AppendLine(crumb.FormatLine());
        }
    }

    private static DiagnosticRecord? TryParseRecordMetadata(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists) return null;

            var fileName = info.Name;
            var isNativeExit = fileName.StartsWith(NativeExitPrefix, StringComparison.OrdinalIgnoreCase);
            var reportId = Path.GetFileNameWithoutExtension(fileName);
            var category = isNativeExit ? "NativeExit" : "Crash";
            var summary = fileName;
            var isFatal = true;
            var timestamp = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).ToLocalTime();

            using var reader = new StreamReader(filePath, Encoding.UTF8);
            for (var i = 0; i < 20; i++)
            {
                var line = reader.ReadLine();
                if (line is null) break;
                if (line.StartsWith("ReportId: ", StringComparison.Ordinal))
                {
                    reportId = line["ReportId: ".Length..].Trim();
                }
                else if (line.StartsWith("Category: ", StringComparison.Ordinal))
                {
                    category = line["Category: ".Length..].Trim();
                }
                else if (line.StartsWith("Summary: ", StringComparison.Ordinal))
                {
                    summary = line["Summary: ".Length..].Trim();
                }
                else if (line.StartsWith("IsTerminating: ", StringComparison.Ordinal))
                {
                    isFatal = line["IsTerminating: ".Length..].Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
                }
                else if (line.StartsWith("Timestamp: ", StringComparison.Ordinal))
                {
                    var rawTs = line["Timestamp: ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    if (DateTimeOffset.TryParse(rawTs, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                    {
                        timestamp = parsed.ToLocalTime();
                    }
                }
            }

            return new DiagnosticRecord(reportId, fileName, filePath, timestamp, category, summary, isFatal, isNativeExit, info.Length);
        }
        catch
        {
            return null;
        }
    }

    private static string GenerateReportId(DateTimeOffset timestamp)
    {
        Span<byte> bytes = stackalloc byte[3];
        RandomNumberGenerator.Fill(bytes);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"MOB-{timestamp:yyyyMMdd}-{bytes[0]:X2}{bytes[1]:X2}{bytes[2]:X2}");
    }

    private static string AllocatePath(string directory, string prefix, DateTimeOffset timestamp)
    {
        var stamp = timestamp.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var pid = Environment.ProcessId;
        var baseName = $"{prefix}{stamp}-{pid}";
        var candidate = Path.Combine(directory, baseName + LogExtension);
        for (var i = 1; File.Exists(candidate); i++)
        {
            candidate = Path.Combine(directory, $"{baseName}-{i}{LogExtension}");
        }
        return candidate;
    }

    private static void WriteTextFlushToDisk(string path, string content)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static void PruneFilesLocked(string directory, string pattern, int maxFiles)
    {
        try
        {
            var files = Directory.GetFiles(directory, pattern);
            if (files.Length <= maxFiles) return;
            Array.Sort(files, StringComparer.Ordinal);
            var excess = files.Length - maxFiles;
            for (var i = 0; i < excess; i++)
            {
                TryDelete(files[i]);
            }
        }
        catch
        {
        }
    }

    private static void RotateIfOversized(string path, string previousPath, long maxBytes)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length >= maxBytes)
            {
                File.Move(path, previousPath, overwrite: true);
            }
        }
        catch
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }
}
