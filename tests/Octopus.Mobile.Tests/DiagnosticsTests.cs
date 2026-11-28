using System.IO.Compression;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Octopus.Mobile.Common;
using Octopus.Mobile.Demo;
using Octopus.Mobile.Diagnostics;
using Octopus.Mobile.Hosts;
using Octopus.Mobile.Settings;
using TKCode.Arc;
using TKCode.Arc.Mobile;
using Xunit;

namespace Octopus.Mobile.Tests;

/// <summary>
/// Unit and headless UI tests for the mobile diagnostics subsystem:
/// default-OFF opt-in consent guarantee, sanitizer redaction, crash capture/dedup/burst circuit-breaker,
/// dirty-exit &amp; native-exit detection, sanitized ZIP export, SafeFireAndForget, and native Arc/Arc.Mobile UI.
/// </summary>
public class DiagnosticsTests : IDisposable
{
    private readonly string _tempRoot;

    public DiagnosticsTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "octopus-mobile-diag-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        MobilePreferences.Current.ResetToDefaults();
        MobileDiagnostics.ResetForTests();
        try
        {
            if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void Collection_is_disabled_by_default_and_writes_zero_files_until_user_consents()
    {
        var platform = new FakeDiagnosticsPlatform(_tempRoot)
        {
            NativeExits =
            [
                new NativeExitRecord("exit-ignored", DateTimeOffset.Now, "Android.ExitInfo.Anr", "Should not be imported while disabled"),
            ],
        };

        MobileDiagnostics.Initialize(platform, attachProcessHooks: false);

        Assert.False(MobileDiagnostics.IsCollectionEnabled);
        Assert.False(MobilePreferences.Current.DiagnosticsCollectionEnabled);

        // Attempt every recording API while disabled: nothing may be written or surfaced.
        MobileDiagnostics.Info("NAV", "Should not be recorded");
        MobileDiagnostics.Warn("NET", "Should not be recorded");
        MobileDiagnostics.Error("NET", "Should not be recorded", new InvalidOperationException("nope"));
        Assert.Null(MobileDiagnostics.RecordCrash("Test", new InvalidOperationException("blocked"), isTerminating: true));
        Assert.Null(MobileDiagnostics.RecordPlatformCrash("Java", "RuntimeException", "blocked", "stack", isTerminating: true));
        Assert.Null(MobileDiagnostics.RecordUnobserved(new InvalidOperationException("blocked")));
        Assert.Null(MobileDiagnostics.RecordNativeExit(new NativeExitRecord("mx-1", DateTimeOffset.Now, "iOS.MetricKit.Crash", "blocked")));

        Assert.Null(MobileDiagnostics.PendingNotice);
        Assert.Empty(MobileDiagnostics.ListRecords());
        Assert.Empty(MobileDiagnostics.ReadRecentRuntimeLines());
        Assert.False(Directory.Exists(platform.LogDirectory));

        // Explicit user opt-in enables recording, imports OS exit records, and persists across restart.
        MobileDiagnostics.SetCollectionEnabled(true);
        Assert.True(MobileDiagnostics.IsCollectionEnabled);
        Assert.Single(MobileDiagnostics.ListRecords());

        // Re-init reads persisted consent.
        MobileDiagnostics.Initialize(platform, attachProcessHooks: false);
        Assert.True(MobileDiagnostics.IsCollectionEnabled);

        // Turning off stops recording immediately and clears pending notice.
        MobileDiagnostics.SetCollectionEnabled(false);
        Assert.False(MobileDiagnostics.IsCollectionEnabled);
        Assert.Null(MobileDiagnostics.PendingNotice);
        Assert.Null(MobileDiagnostics.RecordCrash("Test", new InvalidOperationException("after-off"), isTerminating: true));
    }

    [Fact]
    public void Sanitizer_redacts_tokens_secrets_deep_links_emails_paths_and_optional_ips()
    {
        var raw = """
            Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.secretpayload
            refresh_token=rt_super_secret_12345&password="mySecret99"
            Link: octopus://pair?host=192.168.1.20:7420&secret=abcdef123456
            Relay: octopus://relay?url=https://octoterm.si&token=tok_98765
            User: alice.chen@example.com
            MacPath: /Users/alice/code/Octopus/secret.cs
            WinPath: C:\Users\Bob\Documents\Octopus\file.txt
            HostIp: 192.168.1.20:7420
            """;

        var baseRedacted = LogSanitizer.Sanitize(raw, maskIdentifiers: false);
        Assert.DoesNotContain("eyJhbGciOi", baseRedacted);
        Assert.DoesNotContain("rt_super_secret_12345", baseRedacted);
        Assert.DoesNotContain("mySecret99", baseRedacted);
        Assert.DoesNotContain("abcdef123456", baseRedacted);
        Assert.DoesNotContain("tok_98765", baseRedacted);
        Assert.DoesNotContain("alice.chen@example.com", baseRedacted);
        Assert.DoesNotContain("/Users/alice/", baseRedacted);
        Assert.DoesNotContain(@"C:\Users\Bob\", baseRedacted);
        Assert.Contains("Bearer [REDACTED]", baseRedacted);
        Assert.Contains("octopus://pair?[REDACTED]", baseRedacted);
        Assert.Contains("octopus://relay?[REDACTED]", baseRedacted);
        Assert.Contains("a***@example.com", baseRedacted);
        Assert.Contains("/Users/<user>/", baseRedacted);
        Assert.Contains(@"C:\Users\<user>\", baseRedacted);
        Assert.Contains("192.168.1.20", baseRedacted);

        var strictRedacted = LogSanitizer.Sanitize(raw, maskIdentifiers: true);
        Assert.DoesNotContain("192.168.1.20", strictRedacted);
        Assert.Contains("192.168.*.*", strictRedacted);

        var tag1 = LogSanitizer.MaskIdentifier("Ada's MacBook Pro", "host", mask: true);
        var tag2 = LogSanitizer.MaskIdentifier("Ada's MacBook Pro", "host", mask: true);
        Assert.StartsWith("host#", tag1, StringComparison.Ordinal);
        Assert.Equal(tag1, tag2);
        Assert.DoesNotContain("Ada", tag1);
    }

    [Fact]
    public void Crash_capture_deduplicates_enforces_burst_circuit_breaker_and_detects_dirty_exit()
    {
        var platform = new FakeDiagnosticsPlatform(_tempRoot);
        MobileDiagnostics.Initialize(platform, attachProcessHooks: false, initialConsentOverride: true);

        Assert.Null(MobileDiagnostics.PendingNotice);
        Assert.Empty(MobileDiagnostics.ListRecords());

        MobileDiagnostics.Info("NAV", "Opened ConversationPage");
        var boom = new InvalidOperationException("Bearer topsecret_token_abc123 failed");
        var path1 = MobileDiagnostics.RecordCrash("Test.Source", boom, isTerminating: true);
        var path2 = MobileDiagnostics.RecordCrash("AppDomain.UnhandledException", boom, isTerminating: true);

        Assert.NotNull(path1);
        Assert.Null(path2);
        Assert.NotNull(MobileDiagnostics.PendingNotice);
        Assert.DoesNotContain("topsecret_token_abc123", MobileDiagnostics.PendingNotice!.Summary);

        var content = File.ReadAllText(path1!, Encoding.UTF8);
        Assert.Contains("Opened ConversationPage", content);
        Assert.Contains("Bearer [REDACTED]", content);
        Assert.DoesNotContain("topsecret_token_abc123", content);

        // Circuit breaker refuses process-corrupting faults and stops recovering past burst limit.
        Assert.False(MobileDiagnostics.ShouldRecover(new OutOfMemoryException(), 1000));
        Assert.False(MobileDiagnostics.ShouldRecover(new AccessViolationException(), 1000));
        for (var i = 0; i < 12; i++)
        {
            Assert.True(MobileDiagnostics.ShouldRecover(new InvalidOperationException($"err-{i}"), 2000 + i * 10));
        }
        Assert.False(MobileDiagnostics.ShouldRecover(new InvalidOperationException("err-burst"), 2200));

        // Simulate a foreground dirty-exit sentinel when consent was enabled.
        var dirtyRoot = Path.Combine(_tempRoot, "dirty-case");
        var dirtyPlatform = new FakeDiagnosticsPlatform(dirtyRoot);
        MobileDiagnostics.Initialize(dirtyPlatform, attachProcessHooks: false);
        MobileDiagnostics.SetCollectionEnabled(true);
        MobileDiagnostics.NotePage("TerminalPage");

        // Re-initializing without NoteBackgrounded or crash log triggers dirty-exit detection.
        MobileDiagnostics.Initialize(dirtyPlatform, attachProcessHooks: false);
        Assert.NotNull(MobileDiagnostics.PendingNotice);
        Assert.Contains("DirtyExit", MobileDiagnostics.PendingNotice!.Category);
        Assert.Single(MobileDiagnostics.ListRecords());
    }

    [Fact]
    public void Native_exit_records_are_imported_once_and_zip_bundle_contains_sanitized_manifest_and_logs()
    {
        var platform = new FakeDiagnosticsPlatform(_tempRoot)
        {
            NativeExits =
            [
                new NativeExitRecord("exit-42", DateTimeOffset.Now.AddMinutes(-5), "Android.ExitInfo.Anr", "Main thread blocked 5s", "trace /Users/secretuser/app"),
            ],
        };

        MobileDiagnostics.Initialize(platform, attachProcessHooks: false);
        MobileDiagnostics.SetCollectionEnabled(true);
        Assert.Single(MobileDiagnostics.ListRecords());
        // Second init does not duplicate already-imported exit-42.
        MobileDiagnostics.Initialize(platform, attachProcessHooks: false);
        Assert.Single(MobileDiagnostics.ListRecords());

        MobileDiagnostics.Info("NET", "Connected via relay token=abc_secret_xyz");
        var bundle = DiagnosticBundleBuilder.BuildZipBundle(
            new DemoWorkspace(),
            new DemoAccount(),
            userNote: "Crashed when opening octopus://pair?secret=my_pair_secret",
            maskIdentifiers: true);

        Assert.True(File.Exists(bundle.ZipPath));
        Assert.True(bundle.SizeBytes > 0);
        Assert.True(bundle.IncludedFileCount >= 4);

        using var zip = ZipFile.OpenRead(bundle.ZipPath);
        var entryNames = zip.Entries.Select(static e => e.FullName).ToList();
        Assert.Contains("manifest.json", entryNames);
        Assert.Contains("state/connections-snapshot.json", entryNames);
        Assert.Contains("logs/runtime.log", entryNames);
        Assert.Contains(entryNames, static n => n.StartsWith("crashes/native-exit-", StringComparison.Ordinal));

        using var manifestReader = new StreamReader(zip.GetEntry("manifest.json")!.Open());
        var manifestText = manifestReader.ReadToEnd();
        Assert.Contains("octopus://pair?[REDACTED]", manifestText);
        Assert.DoesNotContain("my_pair_secret", manifestText);

        using var runtimeReader = new StreamReader(zip.GetEntry("logs/runtime.log")!.Open());
        var runtimeText = runtimeReader.ReadToEnd();
        Assert.DoesNotContain("abc_secret_xyz", runtimeText);

        using var connReader = new StreamReader(zip.GetEntry("state/connections-snapshot.json")!.Open());
        var connText = connReader.ReadToEnd();
        Assert.Contains("host#", connText);

        var issueUri = DiagnosticBundleBuilder.BuildGitHubIssueUri(new DemoWorkspace(), new DemoAccount(), "user note", maskIdentifiers: true);
        Assert.Equal("https", issueUri.Scheme);
        Assert.Equal("github.com", issueUri.Host);
        Assert.Contains("issues/new", issueUri.AbsolutePath);
    }

    [Fact]
    public async Task SafeFireAndForget_records_faulted_background_task_without_throwing()
    {
        var platform = new FakeDiagnosticsPlatform(_tempRoot);
        MobileDiagnostics.Initialize(platform, attachProcessHooks: false, initialConsentOverride: true);

        var tcs = new TaskCompletionSource();
        var faulted = Task.Run(() => throw new InvalidOperationException("background_boom_123"), TestContext.Current.CancellationToken);
        faulted.SafeFireAndForget("BG_TEST", _ => tcs.TrySetResult());

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var lines = MobileDiagnostics.ReadRecentRuntimeLines(20, maskIdentifiers: false);
        Assert.Contains(lines, static l => l.Contains("[BG_TEST]", StringComparison.Ordinal) && l.Contains("background_boom_123", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Opt_in_consent_flow_native_arc_components_crash_alert_and_export_work_end_to_end()
    {
        var platform = new FakeDiagnosticsPlatform(_tempRoot);
        MobileDiagnostics.Initialize(platform, attachProcessHooks: false);

        using var phone = new Phone();
        var hostsPage = phone.Top<HostsPage>();
        var hostsVm = (HostsViewModel)hostsPage.DataContext!;
        Phone.Pump();

        // Disabled by default: no crash alert shown.
        Assert.False(hostsVm.ShowCrashNotice);

        // Navigate: Hosts -> Settings -> Search "日志" -> DiagnosticsPage.
        hostsVm.OpenSettingsCommand.Execute(null);
        Phone.Settle();
        var settingsPage = phone.Top<SettingsPage>();
        var settingsVm = (SettingsViewModel)settingsPage.DataContext!;
        Assert.IsType<Searchbar>(settingsPage.FindControl<Searchbar>("SettingsSearchField"));
        Assert.Equal(BadgeTone.Neutral, settingsVm.DiagnosticsBadgeTone);

        settingsVm.SearchQuery = "日志";
        Assert.True(settingsVm.ShowAboutSection);
        Assert.False(settingsVm.ShowAppearanceSection);

        settingsVm.OpenDiagnosticsCommand.Execute(null);
        Phone.Settle();
        var diagPage = phone.Top<DiagnosticsPage>();
        var diagVm = (DiagnosticsViewModel)diagPage.DataContext!;

        // Initially disabled: native Arc EmptyState prompts opt-in.
        Assert.False(diagVm.IsCollectionEnabled);
        Assert.True(diagPage.FindControl<EmptyState>("DiagConsentEmptyState")!.IsEffectivelyVisible);

        // Requesting enable opens native Arc.Mobile consent Dialog before turning collection on.
        diagVm.RequestEnableCollectionCommand.Execute(null);
        Assert.True(diagVm.IsConsentDialogOpen);
        Assert.False(MobileDiagnostics.IsCollectionEnabled);

        diagVm.ConfirmEnableCollectionCommand.Execute(null);
        Phone.Pump();
        Assert.False(diagVm.IsConsentDialogOpen);
        Assert.True(MobileDiagnostics.IsCollectionEnabled);
        Assert.True(diagVm.IsCollectionEnabled);
        Assert.Equal(BadgeTone.Success, settingsVm.DiagnosticsBadgeTone);

        // Now record a crash: native Arc Alert appears on HostsPage, record appears with Arc Badge on DiagnosticsPage.
        MobileDiagnostics.RecordCrash("UI.Test", new InvalidOperationException("sample ui crash"), isTerminating: false, isRecovered: true);
        Phone.Pump();

        Assert.True(hostsVm.ShowCrashNotice);
        Assert.Equal(AlertTone.Warning, hostsVm.CrashNoticeTone);
        Assert.IsType<Alert>(hostsPage.FindControl<Alert>("CrashNoticeBanner"));
        Assert.True(diagVm.HasRecords);
        Assert.Single(diagVm.Records);
        Assert.Equal(BadgeTone.Warning, diagVm.Records[0].BadgeTone);
        Assert.Contains("sample ui crash", diagVm.PreviewText);
        Assert.Contains("sample ui crash", diagVm.CopyableSummary);

        // Exporting creates a .zip bundle, invokes platform share, and dismisses the pending crash alert.
        await diagVm.ShareBundleCommand.ExecuteAsync(null);
        Assert.NotNull(diagVm.LastExportPath);
        Assert.True(File.Exists(diagVm.LastExportPath!));
        Assert.Equal(diagVm.LastExportPath, platform.LastSharedFile);
        Assert.Null(MobileDiagnostics.PendingNotice);
        Assert.False(hostsVm.ShowCrashNotice);

        // Clear logs uses confirmation dialog.
        diagVm.RequestClearLogsCommand.Execute(null);
        Assert.True(diagVm.IsClearDialogOpen);
        diagVm.ConfirmClearLogsCommand.Execute(null);
        Phone.Pump();
        Assert.True(diagVm.IsEmpty);
        Assert.Empty(diagVm.Records);
    }

    private sealed class FakeDiagnosticsPlatform(string root) : IMobileDiagnosticsPlatform
    {
        public string LogDirectory { get; } = Path.Combine(root, "logs");
        public string ExportDirectory { get; } = Path.Combine(root, "exports");
        public bool CanShareFiles => true;
        public string? LastSharedFile { get; private set; }
        public IReadOnlyList<NativeExitRecord> NativeExits { get; init; } = [];

        public DeviceEnvironmentInfo GetEnvironment() => new(
            Platform: "TestOS",
            OsVersion: "1.0",
            DeviceModel: "UnitTestPhone",
            Architecture: "Arm64",
            AppVersion: "1.0.9999",
            RuntimeDescription: ".NET 10.0",
            IsNativeAot: false,
            TotalMemoryMb: 4096);

        public Task<bool> ShareFileAsync(string filePath, string title, string mimeType)
        {
            LastSharedFile = filePath;
            return Task.FromResult(File.Exists(filePath));
        }

        public IReadOnlyList<NativeExitRecord> CollectNativeExitRecords() => NativeExits;
    }
}
