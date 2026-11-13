using System.Diagnostics;
using System.Text.Json;
using Octopus.Core.Updates;
using Octopus.Platform;
using Octopus.Platform.Updates;
using Octopus.Storage;

namespace Octopus.Updates;

/// <summary>Where the updater stands (design/10-自动更新.md §7).</summary>
public enum UpdateStatus
{
    /// <summary>A development build, or a version that cannot be parsed: no checks at all.</summary>
    Disabled,
    /// <summary>Nothing checked yet in this run.</summary>
    Idle,
    UpToDate,
    /// <summary>A newer release was found and is not being downloaded (notify mode, a portable install, a cancelled download).</summary>
    Available,
    Downloading,
    /// <summary>Downloaded and prepared: installs when the app quits.</summary>
    Ready,
    Failed,
}

/// <summary>An immutable picture of the updater for the UI.</summary>
public sealed record UpdateSnapshot
{
    public UpdateStatus Status { get; init; }
    /// <summary>True while a check runs; the status stays what it was.</summary>
    public bool IsChecking { get; init; }
    public AppVersion? CurrentVersion { get; init; }
    public UpdateMode Mode { get; init; }
    public UpdateChannel Channel { get; init; }
    /// <summary>True when no channel is saved and it follows the running version.</summary>
    public bool ChannelFollowsVersion { get; init; }
    public InstallKind InstallKind { get; init; }
    /// <summary>The release offered, downloading, ready or failed.</summary>
    public UpdateOffer? Offer { get; init; }
    public DownloadProgress? Progress { get; init; }
    public UpdateErrorKind? Error { get; init; }
    public string? ErrorDetail { get; init; }
    /// <summary>The failure came from a check nobody asked for: the settings page shows it, the card does not.</summary>
    public bool IsQuietError { get; init; }
    public DateTimeOffset? LastCheckedAt { get; init; }

    /// <summary>This install can download and install the offered release itself.</summary>
    public bool CanInstall => InstallKind == InstallKind.Installed && Offer?.Asset is not null;
}

public interface IUpdateService
{
    UpdateSnapshot Current { get; }

    /// <summary>Raised on any thread after <see cref="Current"/> changed.</summary>
    event EventHandler? Changed;

    /// <summary>Set by <see cref="Start"/> when the update installed at the last quit worked: the version now running.</summary>
    AppVersion? JustUpdatedTo { get; }

    /// <summary>Start the new version after installing it ("restart now"). Reset when the quit is cancelled.</summary>
    bool RelaunchAfterInstall { get; set; }

    /// <summary>Reads what the last run left, then checks on a timer. Call once, when the window is up.</summary>
    void Start();

    /// <summary>Looks for a newer release now. <paramref name="manual"/>: asked for, so failures are shown and skipped versions count.</summary>
    Task CheckAsync(bool manual);

    /// <summary>Downloads the offered release ("download and update").</summary>
    Task DownloadAsync();

    void CancelDownload();

    /// <summary>Stops offering the current release; only a newer one is offered again.</summary>
    void SkipOffered();

    /// <summary>
    /// Called as the app quits: starts the helper that installs a ready update once the app has ended. False when there
    /// is nothing to install or it must not be installed now.
    /// </summary>
    bool InstallOnExit();
}

/// <summary>The settings of the updater that come from the build, not from settings.json.</summary>
public sealed class UpdateOptions
{
    /// <summary>The running version; null (unparsable) disables updates.</summary>
    public AppVersion? CurrentVersion { get; init; }
    /// <summary>A debug build: no checks unless a feed was given for testing.</summary>
    public bool IsDevelopment { get; init; }
    /// <summary>A few seconds after the window is up: the check is one small request, and it is what tells a person who opens the app that there is a new version.</summary>
    public TimeSpan FirstCheckDelay { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromHours(6);
    /// <summary>After failed automatic checks, then <see cref="CheckInterval"/> again.</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30)];
    /// <summary>True when another copy of the app runs from this install: nothing is installed under it then.</summary>
    public Func<string, bool> OtherInstancesRunning { get; init; } = DefaultOtherInstancesRunning;

    private static bool DefaultOtherInstancesRunning(string location)
    {
        try
        {
            using var me = Process.GetCurrentProcess();
            foreach (var process in Process.GetProcessesByName(me.ProcessName))
            {
                using (process)
                {
                    if (process.Id == me.Id) continue;
                    string? path = null;
                    try { path = process.MainModule?.FileName; } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
                    // A copy whose path cannot be read may well be this install's: leave it alone.
                    if (path is null || path.StartsWith(location, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        return false;
    }
}

/// <summary>
/// The updater: checks on a timer, downloads, prepares, and hands the install to the platform when the app quits
/// (design/10-自动更新.md §7). Safe to call from any thread; <see cref="Changed"/> is raised outside its lock.
/// </summary>
public sealed class UpdateService : IUpdateService, IDisposable
{
    public const string PendingFileName = "pending.json";
    public const string StateFileName = "state.json";
    public const string InstallLogName = "install.log";
    /// <summary>After this many failed installs of one version it is no longer installed automatically.</summary>
    public const int MaxInstallFailures = 2;

    private readonly UpdateOptions _options;
    private readonly ISettingsStore _settings;
    private readonly IUpdateFeed _feed;
    private readonly UpdateDownloader _downloader;
    private readonly IUpdateInstaller _installer;
    private readonly IAppPaths _paths;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _life = new();
    /// <summary>Versions whose download the person cancelled in this run: not downloaded again on their own.</summary>
    private readonly HashSet<AppVersion> _declined = [];

    private UpdateSnapshot _snapshot;
    private PendingUpdate? _pending;
    private bool _installBlocked;
    private bool _started;
    private CancellationTokenSource? _download;
    private string? _lastMode;
    private string? _lastChannel;
    private Task? _loop;

    public UpdateService(
        UpdateOptions options,
        ISettingsStore settings,
        IUpdateFeed feed,
        UpdateDownloader downloader,
        IUpdateInstaller installer,
        IAppPaths paths,
        TimeProvider? time = null)
    {
        _options = options;
        _settings = settings;
        _feed = feed;
        _downloader = downloader;
        _installer = installer;
        _paths = paths;
        _time = time ?? TimeProvider.System;
        _snapshot = new UpdateSnapshot
        {
            Status = UpdateStatus.Disabled,
            CurrentVersion = options.CurrentVersion,
            Mode = UpdateSettingValues.ParseMode(settings.Current.Updates.Mode),
            Channel = UpdateSettingValues.ResolveChannel(settings.Current.Updates.Channel, options.CurrentVersion),
            ChannelFollowsVersion = settings.Current.Updates.Channel is null,
            InstallKind = InstallKind.Portable,
        };
    }

    public UpdateSnapshot Current
    {
        get { lock (_lock) return _snapshot; }
    }

    public event EventHandler? Changed;

    public AppVersion? JustUpdatedTo { get; private set; }

    public bool RelaunchAfterInstall { get; set; }

    /// <summary>The updater's folder in the cache: downloads, pending.json, state.json, install.log.</summary>
    public string Root => Path.Combine(_paths.CacheDirectory, "updates");

    private bool IsEnabled => _options.CurrentVersion is not null && !_options.IsDevelopment;

    public void Start()
    {
        lock (_lock)
        {
            if (_started) return;
            _started = true;
        }
        if (!IsEnabled) return;
        var kind = _installer.Detect();
        var state = Read(Path.Combine(Root, StateFileName), UpdatesJsonContext.Default.UpdateCheckState);
        Set(s => s with { Status = UpdateStatus.Idle, InstallKind = kind, LastCheckedAt = state?.LastCheckedAt });
        RecoverPending();
        _lastMode = _settings.Current.Updates.Mode;
        _lastChannel = _settings.Current.Updates.Channel;
        _settings.Changed += OnSettingsChanged;
        _loop = Task.Run(() => LoopAsync(_life.Token));
    }

    public Task CheckAsync(bool manual) => CheckCoreAsync(manual);

    public Task DownloadAsync() => DownloadCoreAsync(approved: true);

    public void CancelDownload()
    {
        lock (_lock) _download?.Cancel();
    }

    public void SkipOffered()
    {
        var offer = Current.Offer;
        if (offer is null) return;
        _settings.Update(s => s.Updates.SkippedVersion = offer.Version.ToString());
        Set(s => s.Status == UpdateStatus.Available && s.Offer == offer ? s with { Status = UpdateStatus.UpToDate, Offer = null } : s);
    }

    public bool InstallOnExit()
    {
        PendingUpdate pending;
        lock (_lock)
        {
            if (_pending is null || _installBlocked || _snapshot.InstallKind != InstallKind.Installed) return false;
            // Downloaded on its own in auto mode, then auto mode was turned off: not installed unless asked for.
            if (!_pending.Approved && !RelaunchAfterInstall && UpdateSettingValues.ParseMode(_settings.Current.Updates.Mode) != UpdateMode.Auto) return false;
            pending = _pending;
        }
        if (_options.OtherInstancesRunning(_installer.Location)) return false;
        try
        {
            var log = Path.Combine(Root, InstallLogName);
            File.Delete(log);
            var work = Path.GetDirectoryName(pending.Package) ?? Root;
            _installer.LaunchInstall(pending.Prepared, new InstallRequest(work, log, Environment.ProcessId, RelaunchAfterInstall));
            pending.Launched = true;
            Write(Path.Combine(Root, PendingFileName), pending, UpdatesJsonContext.Default.PendingUpdate);
            return true;
        }
        catch (Exception e) when (e is UpdateInstallException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _life.Cancel();
        lock (_lock) _download?.Cancel();
    }

    // ------------------------------------------------------------------ checking

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        var delay = _options.FirstCheckDelay;
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            var ok = true;
            if (Current.Mode != UpdateMode.Off) ok = await CheckCoreAsync(manual: false).ConfigureAwait(false);
            failures = ok ? 0 : failures + 1;
            delay = ok || failures > _options.RetryDelays.Count
                ? _options.CheckInterval
                : _options.RetryDelays[failures - 1];
        }
    }

    /// <summary>False when the feed could not be read.</summary>
    private async Task<bool> CheckCoreAsync(bool manual)
    {
        AppVersion current;
        AppVersion floor;
        UpdateChannel channel;
        lock (_lock)
        {
            if (!_started || !IsEnabled || _snapshot.IsChecking || _snapshot.Status == UpdateStatus.Downloading) return true;
            current = _options.CurrentVersion!;
            channel = UpdateSettingValues.ResolveChannel(_settings.Current.Updates.Channel, current);
            floor = current;
            if (!manual && AppVersion.TryParse(_settings.Current.Updates.SkippedVersion, out var skipped) && skipped > floor) floor = skipped;
            // What is already downloaded is not offered again, nor is a version whose install failed too often.
            if (_pending is not null && AppVersion.TryParse(_pending.Version, out var ready) && ready > floor) floor = ready;
        }
        Set(s => s with { IsChecking = true, Channel = channel });

        UpdateOffer? offer;
        try
        {
            offer = await _feed.FindAsync(floor, channel, _installer.Target, _life.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Set(s => s with { IsChecking = false });
            return true;
        }
        catch (UpdateException e)
        {
            Set(s => s.Status is UpdateStatus.Ready or UpdateStatus.Available
                ? s with { IsChecking = false, Error = e.Kind, ErrorDetail = e.Message, IsQuietError = true }
                : s with { IsChecking = false, Status = UpdateStatus.Failed, Offer = null, Error = e.Kind, ErrorDetail = e.Message, IsQuietError = !manual });
            return false;
        }

        var now = _time.GetUtcNow();
        Write(Path.Combine(Root, StateFileName), new UpdateCheckState { LastCheckedAt = now }, UpdatesJsonContext.Default.UpdateCheckState);
        if (offer is null)
        {
            // A ready update and a failed install stay what they are: nothing newer replaces them.
            Set(s => s.Status == UpdateStatus.Ready || (s.Status == UpdateStatus.Failed && s.Error == UpdateErrorKind.Install)
                ? s with { IsChecking = false, LastCheckedAt = now }
                : s with
                {
                    IsChecking = false,
                    LastCheckedAt = now,
                    Error = null,
                    ErrorDetail = null,
                    IsQuietError = false,
                    Status = UpdateStatus.UpToDate,
                    Offer = null,
                });
            return true;
        }

        Set(s => s with
        {
            IsChecking = false,
            LastCheckedAt = now,
            Status = UpdateStatus.Available,
            Offer = offer,
            Progress = null,
            Error = null,
            ErrorDetail = null,
            IsQuietError = false,
        });
        bool download;
        lock (_lock)
        {
            download = _snapshot.Mode == UpdateMode.Auto && _snapshot.CanInstall && !_declined.Contains(offer.Version);
        }
        if (download) _ = DownloadCoreAsync(approved: false);
        return true;
    }

    // ------------------------------------------------------------------ downloading

    private async Task DownloadCoreAsync(bool approved)
    {
        UpdateOffer offer;
        CancellationTokenSource cancel;
        lock (_lock)
        {
            if (_snapshot.Status is not (UpdateStatus.Available or UpdateStatus.Failed) || !_snapshot.CanInstall) return;
            offer = _snapshot.Offer!;
            _download?.Dispose();
            _download = cancel = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        }
        var asset = offer.Asset!;
        Set(s => s with
        {
            Status = UpdateStatus.Downloading,
            Progress = new DownloadProgress(0, asset.Size, 0),
            Error = null,
            ErrorDetail = null,
            IsQuietError = false,
        });
        try
        {
            var directory = Path.Combine(Root, offer.Version.ToString());
            var progress = new ProgressSink(p => Set(s => s.Status == UpdateStatus.Downloading ? s with { Progress = p } : s));
            var package = await _downloader.DownloadAsync(asset, directory, progress, cancel.Token).ConfigureAwait(false);
            var prepared = await _installer.PrepareAsync(package, directory, cancel.Token).ConfigureAwait(false);
            var pending = new PendingUpdate
            {
                Version = offer.Version.ToString(),
                Package = package,
                Prepared = prepared,
                NotesUrl = offer.NotesUrl,
                Notes = offer.Notes,
                PublishedAt = offer.PublishedAt,
                Approved = approved,
            };
            Write(Path.Combine(Root, PendingFileName), pending, UpdatesJsonContext.Default.PendingUpdate);
            lock (_lock)
            {
                _pending = pending;
                _installBlocked = false;
            }
            DeleteVersionFolders(keep: offer.Version);
            Set(s => s with { Status = UpdateStatus.Ready, Offer = offer, Progress = null });
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            lock (_lock) _declined.Add(offer.Version);
            Set(s => s with { Status = UpdateStatus.Available, Progress = null });
        }
        catch (UpdateException e)
        {
            Fail(e.Kind, e.Message);
        }
        catch (UpdateInstallException e)
        {
            Fail(UpdateErrorKind.Package, e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Fail(UpdateErrorKind.Disk, e.Message);
        }
        finally
        {
            lock (_lock)
            {
                if (_download == cancel) _download = null;
            }
            cancel.Dispose();
        }

        void Fail(UpdateErrorKind kind, string detail) =>
            Set(s => s with { Status = UpdateStatus.Failed, Progress = null, Error = kind, ErrorDetail = detail, IsQuietError = false });
    }

    private sealed class ProgressSink(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }

    // ------------------------------------------------------------------ what the last run left

    /// <summary>
    /// pending.json says what was downloaded and whether its install was started. The running version tells whether that
    /// install worked.
    /// </summary>
    private void RecoverPending()
    {
        var current = _options.CurrentVersion!;
        var path = Path.Combine(Root, PendingFileName);
        var pending = Read(path, UpdatesJsonContext.Default.PendingUpdate);
        if (pending is null || !AppVersion.TryParse(pending.Version, out var version))
        {
            TryDelete(path);
            DeleteVersionFolders(keep: null, olderThanOrEqual: current);
            return;
        }
        if (current >= version)
        {
            if (pending.Launched) JustUpdatedTo = current;
            TryDelete(path);
            DeleteVersionFolders(keep: null, olderThanOrEqual: current);
            return;
        }
        var offer = new UpdateOffer(version, new UpdateManifest { Version = pending.Version, NotesUrl = pending.NotesUrl, PublishedAt = pending.PublishedAt }, null) { Notes = pending.Notes };
        if (pending.Launched)
        {
            // The helper ran and this is still the old version.
            pending.Launched = false;
            pending.Failures++;
            Write(path, pending, UpdatesJsonContext.Default.PendingUpdate);
            if (pending.Failures >= MaxInstallFailures)
            {
                lock (_lock)
                {
                    _pending = pending;
                    _installBlocked = true;
                }
                Set(s => s with { Status = UpdateStatus.Failed, Offer = offer, Error = UpdateErrorKind.Install, ErrorDetail = InstallResult(), IsQuietError = false });
                return;
            }
        }
        if (!File.Exists(pending.Prepared) && !Directory.Exists(pending.Prepared))
        {
            TryDelete(path);
            return;
        }
        lock (_lock) _pending = pending;
        Set(s => s with { Status = UpdateStatus.Ready, Offer = offer });
    }

    /// <summary>The helper's last <c>result=failed: …</c> line, or a generic note when it left none.</summary>
    private string InstallResult()
    {
        try
        {
            var line = File.ReadLines(Path.Combine(Root, InstallLogName)).LastOrDefault(l => l.Contains("result=", StringComparison.Ordinal));
            if (line is not null)
            {
                var result = line[(line.IndexOf("result=", StringComparison.Ordinal) + "result=".Length)..].Trim();
                return result.StartsWith("failed:", StringComparison.Ordinal) ? result["failed:".Length..].Trim() : result;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return "The installer left no result.";
    }

    // ------------------------------------------------------------------ settings

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        var updates = _settings.Current.Updates;
        string? previousMode, previousChannel;
        lock (_lock)
        {
            previousMode = _lastMode;
            previousChannel = _lastChannel;
            _lastMode = updates.Mode;
            _lastChannel = updates.Channel;
        }
        if (previousMode == updates.Mode && previousChannel == updates.Channel) return;
        var mode = UpdateSettingValues.ParseMode(updates.Mode);
        var channel = UpdateSettingValues.ResolveChannel(updates.Channel, _options.CurrentVersion);
        Set(s => s with { Mode = mode, Channel = channel, ChannelFollowsVersion = updates.Channel is null });
        if (mode == UpdateMode.Off) return;
        var snapshot = Current;
        if (previousChannel != updates.Channel || previousMode == UpdateSettingValues.Off)
        {
            _ = CheckCoreAsync(manual: false);
        }
        else if (mode == UpdateMode.Auto && snapshot.Status == UpdateStatus.Available && snapshot.CanInstall)
        {
            _ = DownloadCoreAsync(approved: false);
        }
    }

    // ------------------------------------------------------------------ helpers

    private void Set(Func<UpdateSnapshot, UpdateSnapshot> change)
    {
        bool changed;
        lock (_lock)
        {
            var next = change(_snapshot);
            changed = !ReferenceEquals(next, _snapshot) && next != _snapshot;
            _snapshot = next;
        }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes downloads of other versions (all but <paramref name="keep"/>, or those up to the running version).</summary>
    private void DeleteVersionFolders(AppVersion? keep, AppVersion? olderThanOrEqual = null)
    {
        try
        {
            if (!Directory.Exists(Root)) return;
            foreach (var directory in Directory.EnumerateDirectories(Root))
            {
                if (!AppVersion.TryParse(Path.GetFileName(directory), out var version)) continue;
                var remove = olderThanOrEqual is not null ? version <= olderThanOrEqual : version != keep;
                if (remove) Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static T? Read<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllBytes(path), type) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void Write<T>(string path, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        try
        {
            // Written beside the file, then renamed over it: a crash leaves the old file or the new one, never half of one.
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + $".{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(value, type));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
