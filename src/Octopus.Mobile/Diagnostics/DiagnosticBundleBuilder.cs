using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Octopus.Mobile.Account;
using Octopus.Mobile.Common;
using Octopus.Mobile.Workspace;

namespace Octopus.Mobile.Diagnostics;

/// <summary>
/// Builds sanitized <c>.zip</c> diagnostic bundles, concise clipboard summaries, and pre-filled GitHub Issue links
/// so users can share crash and runtime logs with developers in one tap (offline or online).
/// </summary>
public static class DiagnosticBundleBuilder
{
    private const int MaxCrashesInBundle = 5;
    private const int MaxNativeExitsInBundle = 3;
    private const int MaxUnobservedChars = 128 * 1024;
    private const int MaxRuntimeChars = 512 * 1024;
    private const int MaxExportZipFiles = 3;
    private const string GitHubNewIssueBaseUrl = "https://github.com/AIDotNet/Octopus/issues/new";

    /// <summary>
    /// Creates a sanitized <c>octopus-mobile-diag-yyyyMMdd-HHmmss.zip</c> archive in the platform's export cache directory.
    /// </summary>
    public static DiagnosticBundleResult BuildZipBundle(
        IRemoteWorkspace workspace,
        IAccountSession account,
        string? userNote = null,
        bool maskIdentifiers = true,
        DateTimeOffset? now = null)
    {
        var timestamp = now ?? DateTimeOffset.Now;
        var platform = MobileDiagnostics.Platform;
        var env = platform.GetEnvironment();
        var records = MobileDiagnostics.ListRecords();
        var reportId = records.FirstOrDefault()?.ReportId
            ?? string.Create(CultureInfo.InvariantCulture, $"MOB-{timestamp:yyyyMMdd-HHmmss}");

        Directory.CreateDirectory(platform.ExportDirectory);
        PruneOldExports(platform.ExportDirectory);

        var zipName = string.Create(CultureInfo.InvariantCulture, $"octopus-mobile-diag-{timestamp:yyyyMMdd-HHmmss}.zip");
        var zipPath = Path.Combine(platform.ExportDirectory, zipName);
        var includedFiles = new List<string>();

        var crashFiles = records.Where(static r => !r.IsNativeExit).Take(MaxCrashesInBundle).ToList();
        var nativeFiles = records.Where(static r => r.IsNativeExit).Take(MaxNativeExitsInBundle).ToList();

        var manifest = new DiagnosticManifest
        {
            ReportId = reportId,
            GeneratedAtUtc = timestamp.ToUniversalTime(),
            UserNote = string.IsNullOrWhiteSpace(userNote) ? null : LogSanitizer.Sanitize(userNote.Trim(), maskIdentifiers),
            MaskedIdentifiers = maskIdentifiers,
            Platform = env.Platform,
            OsVersion = env.OsVersion,
            DeviceModel = env.DeviceModel,
            Architecture = env.Architecture,
            AppVersion = env.AppVersion,
            RuntimeDescription = env.RuntimeDescription,
            IsNativeAot = env.IsNativeAot,
            TotalMemoryMb = env.TotalMemoryMb,
            Preferences = CapturePreferences(),
            RecentCrashes = records.Take(MaxCrashesInBundle + MaxNativeExitsInBundle).Select(r => new DiagnosticManifestCrashEntry
            {
                ReportId = r.ReportId,
                FileName = r.FileName,
                Timestamp = r.Timestamp,
                Category = r.Category,
                Summary = LogSanitizer.Sanitize(r.Summary, maskIdentifiers),
                IsFatal = r.IsFatal,
            }).ToList(),
        };

        var connectionsSnapshot = CaptureConnectionSnapshot(workspace, account, maskIdentifiers, timestamp);

        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var crash in crashFiles)
            {
                var text = ReadTailSanitized(crash.FilePath, 256 * 1024, maskIdentifiers);
                if (text is null) continue;
                var entryName = "crashes/" + crash.FileName;
                WriteUtf8Entry(zip, entryName, text);
                includedFiles.Add(entryName);
            }

            foreach (var native in nativeFiles)
            {
                var text = ReadTailSanitized(native.FilePath, 256 * 1024, maskIdentifiers);
                if (text is null) continue;
                var entryName = "crashes/" + native.FileName;
                WriteUtf8Entry(zip, entryName, text);
                includedFiles.Add(entryName);
            }

            var unobservedPath = Path.Combine(platform.LogDirectory, "unobserved.log");
            if (ReadTailSanitized(unobservedPath, MaxUnobservedChars, maskIdentifiers) is { Length: > 0 } unobserved)
            {
                WriteUtf8Entry(zip, "crashes/unobserved.log", unobserved);
                includedFiles.Add("crashes/unobserved.log");
            }

            var runtimePath = Path.Combine(platform.LogDirectory, MobileLogRing.RuntimeFileName);
            if (ReadTailSanitized(runtimePath, MaxRuntimeChars, maskIdentifiers) is { Length: > 0 } runtime)
            {
                WriteUtf8Entry(zip, "logs/runtime.log", runtime);
                includedFiles.Add("logs/runtime.log");
            }

            var prevRuntimePath = Path.Combine(platform.LogDirectory, MobileLogRing.RuntimePreviousFileName);
            if (ReadTailSanitized(prevRuntimePath, MaxRuntimeChars / 2, maskIdentifiers) is { Length: > 0 } prevRuntime)
            {
                WriteUtf8Entry(zip, "logs/runtime.prev.log", prevRuntime);
                includedFiles.Add("logs/runtime.prev.log");
            }

            var connJson = JsonSerializer.Serialize(connectionsSnapshot, MobileDiagnosticsJsonContext.Default.DiagnosticConnectionSnapshot);
            WriteUtf8Entry(zip, "state/connections-snapshot.json", connJson);
            includedFiles.Add("state/connections-snapshot.json");

            manifest.IncludedFiles = includedFiles;
            var manifestJson = JsonSerializer.Serialize(manifest, MobileDiagnosticsJsonContext.Default.DiagnosticManifest);
            WriteUtf8Entry(zip, "manifest.json", manifestJson);
        }

        var fileSize = new FileInfo(zipPath).Length;
        var summary = BuildTextSummary(workspace, account, userNote, maskIdentifiers, records.FirstOrDefault());
        return new DiagnosticBundleResult(zipPath, reportId, summary, fileSize, includedFiles.Count + 1);
    }

    /// <summary>Builds a concise, sanitized plain-text diagnostic summary suitable for clipboard or chat pasting.</summary>
    public static string BuildTextSummary(
        IRemoteWorkspace workspace,
        IAccountSession account,
        string? userNote = null,
        bool maskIdentifiers = true,
        DiagnosticRecord? focusRecord = null)
    {
        var env = MobileDiagnostics.Platform.GetEnvironment();
        var record = focusRecord ?? MobileDiagnostics.ListRecords().FirstOrDefault();
        var sb = new StringBuilder(1024);

        sb.AppendLine("Octopus Mobile Diagnostics Summary");
        sb.Append("Version: ").AppendLine(env.AppVersion);
        sb.Append("Device: ").Append(env.Platform).Append(' ').Append(env.OsVersion).Append(" / ").Append(env.DeviceModel).Append(" (").Append(env.Architecture).AppendLine(")");
        sb.Append("Runtime: ").Append(env.RuntimeDescription).Append(" (NativeAOT=").Append(env.IsNativeAot ? "True" : "False").AppendLine(")");
        sb.Append("Mode: ").Append(DescribeAccountMode(account))
          .Append(", Hosts=").Append(workspace.Hosts.Count.ToString(CultureInfo.InvariantCulture))
          .Append(" (Online=").Append(workspace.Hosts.Count(static h => h.IsOnline && h.IsPaired).ToString(CultureInfo.InvariantCulture))
          .AppendLine(")");

        if (!string.IsNullOrWhiteSpace(userNote))
        {
            sb.Append("User Note: ").AppendLine(LogSanitizer.Sanitize(userNote.Trim(), maskIdentifiers));
        }

        if (record is not null)
        {
            sb.AppendLine();
            sb.Append("Latest Incident: ").Append(record.ReportId).Append(" (").Append(record.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).AppendLine(")");
            sb.Append("Category: ").AppendLine(record.Category);
            sb.Append("Summary: ").AppendLine(LogSanitizer.Sanitize(record.Summary, maskIdentifiers));

            var detail = MobileDiagnostics.ReadRecordContent(record, maskIdentifiers, maxChars: 2400);
            if (!string.IsNullOrWhiteSpace(detail))
            {
                sb.AppendLine();
                sb.AppendLine("```");
                sb.AppendLine(detail.Trim());
                sb.AppendLine("```");
            }
        }
        else
        {
            var recent = MobileDiagnostics.ReadRecentRuntimeLines(20, maskIdentifiers);
            if (recent.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Recent Logs:");
                sb.AppendLine("```");
                foreach (var line in recent) sb.AppendLine(line);
                sb.AppendLine("```");
            }
        }

        return sb.ToString();
    }

    /// <summary>Builds a URL-length-safe pre-filled GitHub Issue link with sanitized diagnostics metadata.</summary>
    public static Uri BuildGitHubIssueUri(
        IRemoteWorkspace workspace,
        IAccountSession account,
        string? userNote = null,
        bool maskIdentifiers = true,
        DiagnosticRecord? focusRecord = null)
    {
        var record = focusRecord ?? MobileDiagnostics.ListRecords().FirstOrDefault();
        var title = record is not null
            ? $"[Mobile] {record.Category}: {Truncate(LogSanitizer.Sanitize(record.Summary, maskIdentifiers), 72)}"
            : "[Mobile] Diagnostic report";

        var body = BuildTextSummary(workspace, account, userNote, maskIdentifiers, record);
        if (body.Length > 2800)
        {
            body = body[..2800] + "\n\n_(Truncated — please attach the exported octopus-mobile-diag .zip file)_";
        }

        var url = $"{GitHubNewIssueBaseUrl}?title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}";
        return new Uri(url, UriKind.Absolute);
    }

    private static DiagnosticPreferencesSnapshot CapturePreferences()
    {
        var prefs = MobilePreferences.Current;
        return new DiagnosticPreferencesSnapshot
        {
            ThemeMode = prefs.ThemeMode.ToString(),
            CompactDensity = prefs.CompactDensity,
            SuspendStreamInBackground = prefs.SuspendStreamInBackground,
            SnapshotResyncOnResume = prefs.SnapshotResyncOnResume,
            TerminalFontSize = prefs.TerminalFontSize,
            TerminalPasteConfirm = prefs.TerminalPasteConfirm,
        };
    }

    private static DiagnosticConnectionSnapshot CaptureConnectionSnapshot(
        IRemoteWorkspace workspace,
        IAccountSession account,
        bool maskIdentifiers,
        DateTimeOffset timestamp)
    {
        var activeServer = !string.IsNullOrWhiteSpace(account.Server) ? account.Server : account.OfficialServer;
        var serverHost = WelcomeViewModel.FormatHost(activeServer);
        var hostSnapshots = new List<DiagnosticHostSnapshot>();

        foreach (var host in workspace.Hosts)
        {
            var sessions = host.Projects.SelectMany(static p => p.Sessions).ToList();
            hostSnapshots.Add(new DiagnosticHostSnapshot
            {
                Host = LogSanitizer.MaskIdentifier(host.Name, "host", maskIdentifiers),
                Link = host.Link.ToString(),
                IsPaired = host.IsPaired,
                IsOnline = host.IsOnline,
                SupportsConversations = workspace.SupportsConversations(host),
                SupportsProjectGroups = workspace.SupportsProjectGroups(host),
                ProjectCount = host.Projects.Count,
                SessionCount = sessions.Count,
                AttentionSessions = sessions.Count(static s => s.Activity is SessionActivity.Waiting or SessionActivity.Error),
                WorkingSessions = sessions.Count(static s => s.Activity == SessionActivity.Working),
            });
        }

        return new DiagnosticConnectionSnapshot
        {
            CapturedAtUtc = timestamp.ToUniversalTime(),
            AccountMode = DescribeAccountMode(account),
            ServerHost = string.IsNullOrWhiteSpace(serverHost) ? null : LogSanitizer.MaskIdentifier(serverHost, "server", maskIdentifiers),
            IsResuming = workspace.IsResuming,
            IsBackgrounded = MobileHost.IsBackgrounded,
            ActivePage = MobileDiagnostics.ActivePage,
            TotalHosts = workspace.Hosts.Count,
            OnlineHosts = workspace.Hosts.Count(static h => h.IsOnline),
            PairedHosts = workspace.Hosts.Count(static h => h.IsPaired),
            Hosts = hostSnapshots,
        };
    }

    private static string DescribeAccountMode(IAccountSession account) =>
        account.IsSignedIn
            ? string.IsNullOrWhiteSpace(account.Server) ? "OfficialRelay" : "CustomServer"
            : account.IsLocalOnly ? "LocalOnly" : "SignedOut";

    private static string? ReadTailSanitized(string path, int maxChars, bool maskIdentifiers)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path, Encoding.UTF8);
            if (text.Length > maxChars)
            {
                text = "… (earlier lines truncated)\n" + text[^maxChars..];
            }
            return LogSanitizer.Sanitize(text, maskIdentifiers);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteUtf8Entry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static void PruneOldExports(string exportDirectory)
    {
        try
        {
            var files = Directory.GetFiles(exportDirectory, "octopus-mobile-diag-*.zip");
            if (files.Length < MaxExportZipFiles) return;
            Array.Sort(files, StringComparer.Ordinal);
            var removeCount = files.Length - (MaxExportZipFiles - 1);
            for (var i = 0; i < removeCount; i++)
            {
                try { File.Delete(files[i]); } catch { }
            }
        }
        catch
        {
        }
    }

    private static string Truncate(string value, int maxLen) =>
        value.Length <= maxLen ? value : value[..maxLen] + "…";
}
