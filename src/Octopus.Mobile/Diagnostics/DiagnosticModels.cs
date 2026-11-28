using System.Text.Json.Serialization;

namespace Octopus.Mobile.Diagnostics;

/// <summary>Metadata for one persisted crash, native exit, or unobserved-error log file.</summary>
public sealed record DiagnosticRecord(
    string ReportId,
    string FileName,
    string FilePath,
    DateTimeOffset Timestamp,
    string Category,
    string Summary,
    bool IsFatal,
    bool IsNativeExit,
    long SizeBytes);

/// <summary>Result of exporting a sanitized diagnostic <c>.zip</c> bundle for sharing with developers.</summary>
public sealed record DiagnosticBundleResult(
    string ZipPath,
    string ReportId,
    string SummaryText,
    long SizeBytes,
    int IncludedFileCount);

/// <summary>Notice shown on next launch (or after a recovered UI crash) so the user can export logs in one tap.</summary>
public sealed class PendingCrashNotice
{
    public string ReportId { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Category { get; set; } = "";
    public string Summary { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; }
    public bool IsRecovered { get; set; }
}

/// <summary>Dirty-exit sentinel persisted in <c>session-state.json</c> to detect silent OS/native kills on next start.</summary>
internal sealed class SessionStateMarker
{
    public int Pid { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string State { get; set; } = "foreground";
    public string LastPage { get; set; } = "";
    public string AppVersion { get; set; } = "";
}

/// <summary>Persisted set of OS native exit record IDs that were already imported into <c>native-exit-*.log</c>.</summary>
internal sealed class SeenNativeExitsState
{
    public List<string> Ids { get; set; } = [];
}

/// <summary>Top-level manifest included inside exported <c>octopus-mobile-diag-*.zip</c> bundles.</summary>
internal sealed class DiagnosticManifest
{
    public string ReportId { get; set; } = "";
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public string? UserNote { get; set; }
    public bool MaskedIdentifiers { get; set; }
    public string Platform { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public string DeviceModel { get; set; } = "";
    public string Architecture { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public string RuntimeDescription { get; set; } = "";
    public bool IsNativeAot { get; set; }
    public long? TotalMemoryMb { get; set; }
    public DiagnosticPreferencesSnapshot Preferences { get; set; } = new();
    public List<string> IncludedFiles { get; set; } = [];
    public List<DiagnosticManifestCrashEntry> RecentCrashes { get; set; } = [];
}

internal sealed class DiagnosticPreferencesSnapshot
{
    public string ThemeMode { get; set; } = "";
    public bool CompactDensity { get; set; }
    public bool SuspendStreamInBackground { get; set; }
    public bool SnapshotResyncOnResume { get; set; }
    public double TerminalFontSize { get; set; }
    public bool TerminalPasteConfirm { get; set; }
}

internal sealed class DiagnosticManifestCrashEntry
{
    public string ReportId { get; set; } = "";
    public string FileName { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; }
    public string Category { get; set; } = "";
    public string Summary { get; set; } = "";
    public bool IsFatal { get; set; }
}

internal sealed class DiagnosticConnectionSnapshot
{
    public DateTimeOffset CapturedAtUtc { get; set; }
    public string AccountMode { get; set; } = "";
    public string? ServerHost { get; set; }
    public bool IsResuming { get; set; }
    public bool IsBackgrounded { get; set; }
    public string ActivePage { get; set; } = "";
    public int TotalHosts { get; set; }
    public int OnlineHosts { get; set; }
    public int PairedHosts { get; set; }
    public List<DiagnosticHostSnapshot> Hosts { get; set; } = [];
}

internal sealed class DiagnosticHostSnapshot
{
    public string Host { get; set; } = "";
    public string Link { get; set; } = "";
    public bool IsPaired { get; set; }
    public bool IsOnline { get; set; }
    public bool SupportsConversations { get; set; }
    public bool SupportsProjectGroups { get; set; }
    public int ProjectCount { get; set; }
    public int SessionCount { get; set; }
    public int AttentionSessions { get; set; }
    public int WorkingSessions { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(PendingCrashNotice))]
[JsonSerializable(typeof(SessionStateMarker))]
[JsonSerializable(typeof(SeenNativeExitsState))]
[JsonSerializable(typeof(DiagnosticManifest))]
[JsonSerializable(typeof(DiagnosticConnectionSnapshot))]
internal sealed partial class MobileDiagnosticsJsonContext : JsonSerializerContext;
