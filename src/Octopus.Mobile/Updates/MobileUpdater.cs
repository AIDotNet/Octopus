using System.Globalization;
using System.Net;
using System.Reflection;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Core.Updates;
using Octopus.Mobile.Localization;
using Octopus.Mobile.Remote;
using Octopus.Updates;

namespace Octopus.Mobile.Updates;

/// <summary>
/// What a phone platform does for the updater: who it is in <c>update.json</c>, which version is installed, where a
/// download goes and how a downloaded package is installed. Only Android has one; without it the app has no update row.
/// </summary>
public interface IMobileUpdatePlatform
{
    /// <summary>The key of this platform's package in <c>update.json</c>: <c>android</c>.</summary>
    string Target { get; }

    /// <summary>The installed version as the package states it (Android's versionName); a dev build's "0.1" is no version.</summary>
    string? InstalledVersion { get; }

    /// <summary>A folder of the app's own where downloads are kept (and that the installer may be shown a file of).</summary>
    string DownloadDirectory { get; }

    /// <summary>
    /// Hands the verified package to the system installer, which asks the person. False when the app first has to be
    /// allowed to install apps: the platform then opened that setting, and the person taps "install" again afterwards.
    /// </summary>
    bool Install(string package);
}

/// <summary>Where the updater is.</summary>
public enum MobileUpdateState
{
    /// <summary>Not checked yet in this run.</summary>
    Idle,
    Checking,
    UpToDate,
    /// <summary>A newer release with a package for this phone.</summary>
    Available,
    Downloading,
    /// <summary>Downloaded and verified; the installer was (or is about to be) shown.</summary>
    Ready,
    Failed,
}

/// <summary>
/// The phone's updater (design/10-自动更新.md §15): asks the official service for a newer release of this platform, trusts
/// only an <c>update.json</c> signed by a key in <see cref="UpdateEndpoints.PublicKeys"/>, downloads the package the
/// manifest names, checks its size and SHA-256, and hands it to the system installer. Nothing installs without the
/// person: Android asks before every install. It outlives the pages (signing in or out replaces them).
/// </summary>
public sealed partial class MobileUpdater : ObservableObject
{
    private readonly IMobileUpdatePlatform? _platform;
    private readonly IUpdateFeed? _feed;
    private readonly UpdateDownloader? _downloader;
    private readonly AppVersion? _installed;
    private readonly Action<Uri> _openBrowser;
    private UpdateOffer? _offer;
    private string? _package;
    private bool _checkedOnce;
    private CancellationTokenSource? _downloadCts;
    private (MobileUpdateState State, string? Version)? _dismissedBanner;

    /// <summary>An updater for a platform that has none (iOS, the desktop preview, tests): no row, no checks.</summary>
    public static MobileUpdater Unsupported { get; } = new(null, null, null, _ => { });

    /// <param name="feed">Where releases are found; null when the build knows no official service.</param>
    public MobileUpdater(IMobileUpdatePlatform? platform, IUpdateFeed? feed, UpdateDownloader? downloader, Action<Uri> openBrowser)
    {
        _platform = platform;
        _feed = feed;
        _downloader = downloader;
        _openBrowser = openBrowser;
        _ = AppVersion.TryParse(platform?.InstalledVersion, out _installed);
    }

    /// <summary>The updater of a real phone: the official service's update check, over the system proxy.</summary>
    public static MobileUpdater Create(IMobileUpdatePlatform platform, Action<Uri> openBrowser)
    {
        if (MobileBuild.ServiceUrl is not { Length: > 0 } service) return new MobileUpdater(platform, null, null, openBrowser);
        var http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(20),
        })
        {
            // Downloads are long; each read has its own idle timeout (UpdateDownloader), each check its 30 s.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Octopus-Mobile-Updater");
        var feed = new ServerUpdateFeed(http, new ManifestVerifier(UpdateEndpoints.PublicKeys), $"{service.TrimEnd('/')}/v1/updates/check");
        return new MobileUpdater(platform, feed, new UpdateDownloader(http), openBrowser);
    }

    /// <summary>True on a platform that can update itself: the "Me" tab then shows the update row.</summary>
    public bool IsSupported => _platform is not null;

    /// <summary>A real release version to compare with, and a service to ask: a dev build ("0.1") checks nothing.</summary>
    public bool CanCheck => _platform is not null && _feed is not null && _installed is not null;

    /// <summary>"1.3.2", or what the package says when it is no release version.</summary>
    public string InstalledVersionText => _installed?.ToString() ?? _platform?.InstalledVersion ?? AssemblyVersion();

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(StatusText),
        nameof(HasNews),
        nameof(IsBusy),
        nameof(IsAvailable),
        nameof(IsDownloading),
        nameof(IsReady),
        nameof(IsFailed),
        nameof(ActionText),
        nameof(PrimaryModalActionText),
        nameof(HasPrimaryModalAction),
        nameof(BannerTitle),
        nameof(BannerSubtitle),
        nameof(IsBannerVisible),
        nameof(CanDismissBanner),
        nameof(ProgressDetailText))]
    public partial MobileUpdateState State { get; private set; } = MobileUpdateState.Idle;

    /// <summary>0..1 while downloading.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(ProgressPercent), nameof(ProgressDetailText), nameof(BannerSubtitle))]
    public partial double Progress { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressDetailText), nameof(BannerSubtitle))]
    public partial long DownloadedBytes { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressDetailText), nameof(BannerSubtitle))]
    public partial long TotalBytes { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressDetailText), nameof(BannerSubtitle))]
    public partial double BytesPerSecond { get; private set; }

    /// <summary>Why the last check or download failed, for the status line.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(BannerSubtitle))]
    public partial string? Error { get; private set; }

    /// <summary>True while the in-app Markdown release-notes sheet is open.</summary>
    [ObservableProperty]
    public partial bool IsNotesOpen { get; set; }

    /// <summary>The release offered, once one was found.</summary>
    public string? OfferedVersion => _offer?.Version.ToString();

    /// <summary>The release notes (Markdown) of the offered release, when it has any.</summary>
    public string? OfferedNotes => _offer?.Notes;

    public bool HasOffer => _offer is not null;

    public bool HasOfferedNotes => !string.IsNullOrWhiteSpace(_offer?.Notes);

    public bool IsOfferedPreview => _offer?.Version.IsPreview == true;

    public string OfferedKindText => _offer is null
        ? ""
        : MobileLangResources.Get(_offer.Version.IsPreview ? MobileLangResourceKind.UpdateKindPreview : MobileLangResourceKind.UpdateKindRelease);

    /// <summary>"1.3.1120 → 1.4.1124" shown in the update notes modal header.</summary>
    public string VersionTransitionText => _offer is null ? InstalledVersionText : $"{InstalledVersionText} → {OfferedVersion}";

    /// <summary>Release channel, publish date and package size joined with middots (e.g. "正式版 · 11-25 · 24.8 MB").</summary>
    public string OfferedMetaText
    {
        get
        {
            if (_offer is null) return "";
            var parts = new List<string>(3) { OfferedKindText };
            if (_offer.PublishedAt is { } published)
            {
                var local = published.ToLocalTime();
                var fmt = local.Year == DateTimeOffset.Now.Year ? "MM-dd" : "yyyy-MM-dd";
                parts.Add(local.ToString(fmt, CultureInfo.InvariantCulture));
            }
            if (_offer.Asset is { Size: > 0 } asset)
            {
                parts.Add(FormatSize(asset.Size));
            }
            return string.Join(" · ", parts);
        }
    }

    public Uri? OfferedNotesUri
    {
        get
        {
            var address = _offer?.NotesUrl ?? (_offer is { } offer ? UpdateEndpoints.ReleasePage(offer.Version) : null);
            return address is not null && Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? uri : null;
        }
    }

    public bool HasNotesUrl => OfferedNotesUri is not null;

    /// <summary>Title of the in-app Markdown release-notes sheet.</summary>
    public string NotesModalTitle => _offer is null
        ? MobileLangResources.Get(MobileLangResourceKind.UpdateNotes)
        : MobileLangResources.Format(MobileLangResourceKind.UpdateNotesTitle, OfferedVersion ?? "");

    /// <summary>A newer release waits: the row shows a pill and the "Me" tab a dot.</summary>
    public bool HasNews => State is MobileUpdateState.Available or MobileUpdateState.Downloading or MobileUpdateState.Ready;

    public bool IsBusy => State is MobileUpdateState.Checking or MobileUpdateState.Downloading;
    public bool IsAvailable => State == MobileUpdateState.Available;
    public bool IsDownloading => State == MobileUpdateState.Downloading;
    public bool IsReady => State == MobileUpdateState.Ready;
    public bool IsFailed => State == MobileUpdateState.Failed;

    /// <summary>0..100 for a visual progress bar.</summary>
    public double ProgressPercent => Math.Clamp(Progress * 100.0, 0, 100);

    /// <summary>"12.4 / 24.8 MB · 2.1 MB/s" while downloading, or "45%" when total size is unknown.</summary>
    public string ProgressDetailText
    {
        get
        {
            if (State != MobileUpdateState.Downloading) return "";
            var pct = (int)Math.Round(ProgressPercent);
            if (TotalBytes > 0)
            {
                var size = MobileLangResources.Format(MobileLangResourceKind.UpdateProgressSize, Megabytes(DownloadedBytes), Megabytes(TotalBytes));
                if (BytesPerSecond > 0)
                {
                    return $"{pct}% · {size} · {Megabytes((long)BytesPerSecond)} MB/s";
                }
                return $"{pct}% · {size}";
            }
            return MobileLangResources.Format(MobileLangResourceKind.UpdateDownloading, pct);
        }
    }

    /// <summary>
    /// True when the top reminder banner should show on dashboard tabs: visible for a newer release, active download,
    /// ready-to-install package, or a download/install failure when an offer exists; hidden if dismissed for this state &amp; version.
    /// </summary>
    public bool IsBannerVisible =>
        (State is MobileUpdateState.Available or MobileUpdateState.Downloading or MobileUpdateState.Ready
            || (State == MobileUpdateState.Failed && _offer is not null))
        && _dismissedBanner != (State, OfferedVersion);

    public bool CanDismissBanner => State != MobileUpdateState.Downloading;

    public string BannerTitle => State switch
    {
        MobileUpdateState.Available => MobileLangResources.Format(MobileLangResourceKind.UpdateAvailable, OfferedVersion ?? ""),
        MobileUpdateState.Downloading => MobileLangResources.Format(MobileLangResourceKind.UpdateDownloadingTitle, OfferedVersion ?? ""),
        MobileUpdateState.Ready => MobileLangResources.Format(MobileLangResourceKind.UpdateReadyTitle, OfferedVersion ?? ""),
        MobileUpdateState.Failed => MobileLangResources.Get(MobileLangResourceKind.UpdateFailedTitle),
        _ => "",
    };

    public string BannerSubtitle => State switch
    {
        MobileUpdateState.Available => string.IsNullOrEmpty(OfferedMetaText)
            ? MobileLangResources.Get(MobileLangResourceKind.UpdateBannerHintAvailable)
            : OfferedMetaText,
        MobileUpdateState.Downloading => ProgressDetailText,
        MobileUpdateState.Ready => MobileLangResources.Get(MobileLangResourceKind.UpdateReadyHint),
        MobileUpdateState.Failed => Error ?? MobileLangResources.Get(MobileLangResourceKind.UpdateFailedNetwork),
        _ => "",
    };

    /// <summary>The subtitle of the update row.</summary>
    public string StatusText => State switch
    {
        _ when _installed is null => MobileLangResources.Format(MobileLangResourceKind.UpdateDevBuild, InstalledVersionText),
        _ when !CanCheck => MobileLangResources.Format(MobileLangResourceKind.UpdateCurrent, InstalledVersionText),
        MobileUpdateState.Checking => MobileLangResources.Get(MobileLangResourceKind.UpdateChecking),
        MobileUpdateState.UpToDate => MobileLangResources.Format(MobileLangResourceKind.UpdateUpToDate, InstalledVersionText),
        MobileUpdateState.Available => MobileLangResources.Format(MobileLangResourceKind.UpdateAvailable, OfferedVersion ?? ""),
        MobileUpdateState.Downloading => MobileLangResources.Format(MobileLangResourceKind.UpdateDownloading, (int)Math.Round(Progress * 100)),
        MobileUpdateState.Ready => MobileLangResources.Format(MobileLangResourceKind.UpdateReady, OfferedVersion ?? ""),
        MobileUpdateState.Failed => Error ?? MobileLangResources.Get(MobileLangResourceKind.UpdateFailedNetwork),
        _ => MobileLangResources.Format(MobileLangResourceKind.UpdateCurrent, InstalledVersionText),
    };

    /// <summary>The pill on the update row: what tapping it does next.</summary>
    public string? ActionText => State switch
    {
        MobileUpdateState.Available => MobileLangResources.Get(MobileLangResourceKind.UpdateActionDownload),
        MobileUpdateState.Ready => MobileLangResources.Get(MobileLangResourceKind.UpdateActionInstall),
        _ => null,
    };

    /// <summary>Primary CTA button label inside the reminder banner and release-notes modal.</summary>
    public string? PrimaryModalActionText => State switch
    {
        MobileUpdateState.Available => MobileLangResources.Get(MobileLangResourceKind.UpdateActionDownloadNow),
        MobileUpdateState.Ready => MobileLangResources.Get(MobileLangResourceKind.UpdateActionInstallNow),
        MobileUpdateState.Failed when _offer?.Asset is not null => MobileLangResources.Get(MobileLangResourceKind.UpdateActionRetry),
        _ => null,
    };

    public bool HasPrimaryModalAction => PrimaryModalActionText is not null;

    /// <summary>
    /// Checks once per run, quietly: called when the app starts. A failure leaves the row as it was (the person can tap
    /// it to try again), so a phone without a connection does not greet anyone with an error.
    /// </summary>
    public async Task CheckQuietlyAsync()
    {
        if (_checkedOnce || !CanCheck || State != MobileUpdateState.Idle) return;
        _checkedOnce = true;
        await CheckCoreAsync(quiet: true).ConfigureAwait(true);
    }

    /// <summary>What tapping the update row does: check, download and install, or show the installer again.</summary>
    [RelayCommand]
    private async Task Act()
    {
        if (!CanCheck) return;
        switch (State)
        {
            case MobileUpdateState.Idle or MobileUpdateState.UpToDate or MobileUpdateState.Failed when _offer?.Asset is null:
                await CheckCoreAsync(quiet: false).ConfigureAwait(true);
                break;
            case MobileUpdateState.Available or MobileUpdateState.Failed:
                await DownloadAndInstallAsync().ConfigureAwait(true);
                break;
            case MobileUpdateState.Ready:
                Install();
                break;
        }
    }

    /// <summary>Cancels an in-flight download and returns cleanly to <see cref="MobileUpdateState.Available"/>.</summary>
    [RelayCommand]
    private void CancelDownload()
    {
        try
        {
            _downloadCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Hides the dashboard update reminder banner for the current state and version.</summary>
    [RelayCommand]
    private void DismissBanner()
    {
        if (!CanDismissBanner) return;
        _dismissedBanner = (State, OfferedVersion);
        OnPropertyChanged(nameof(IsBannerVisible));
    }

    /// <summary>Opens the in-app Markdown release-notes sheet for the offered release.</summary>
    [RelayCommand]
    private void OpenNotes()
    {
        if (_offer is null) return;
        IsNotesOpen = true;
    }

    [RelayCommand]
    private void CloseNotes() => IsNotesOpen = false;

    /// <summary>Opens the offered release page in the system browser (fallback / external link inside the notes sheet).</summary>
    [RelayCommand]
    private void OpenNotesInBrowser()
    {
        if (OfferedNotesUri is { } uri) _openBrowser(uri);
    }

    /// <summary>Opens an HTTP(S) link tapped inside the Markdown release notes.</summary>
    [RelayCommand]
    private void OpenLink(Uri? uri)
    {
        if (uri is not null && uri.Scheme is "https" or "http") _openBrowser(uri);
    }

    private async Task CheckCoreAsync(bool quiet)
    {
        var before = State;
        State = MobileUpdateState.Checking;
        Error = null;
        try
        {
            // A preview install follows previews; a release install only releases.
            var channel = _installed!.IsPreview ? UpdateChannel.Preview : UpdateChannel.Stable;
            var offer = await _feed!.FindAsync(_installed, channel, _platform!.Target, CancellationToken.None).ConfigureAwait(true);
            // A newer release without a package for this phone (only desktop ones were signed) is no update here.
            _offer = offer?.Asset is not null ? offer : null;
            _package = null;
            NotifyOfferPropertiesChanged();
            State = _offer is null ? MobileUpdateState.UpToDate : MobileUpdateState.Available;
        }
        catch (Exception e)
        {
            if (quiet)
            {
                State = before;
                return;
            }
            Error = Describe(e);
            State = MobileUpdateState.Failed;
        }
    }

    private async Task DownloadAndInstallAsync()
    {
        if (_offer?.Asset is not { } asset) return;
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _downloadCts, cts);
        try { previous?.Dispose(); }
        catch (ObjectDisposedException) { }
        var ct = cts.Token;

        State = MobileUpdateState.Downloading;
        Progress = 0;
        DownloadedBytes = 0;
        TotalBytes = asset.Size;
        BytesPerSecond = 0;
        Error = null;
        try
        {
            var progress = new UiProgress<DownloadProgress>(p =>
            {
                if (State != MobileUpdateState.Downloading) return;
                Progress = p.Fraction;
                DownloadedBytes = p.Received;
                TotalBytes = p.Total > 0 ? p.Total : asset.Size;
                BytesPerSecond = p.BytesPerSecond;
            });
            _package = await _downloader!.DownloadAsync(asset, _platform!.DownloadDirectory, progress, ct).ConfigureAwait(true);
            DeleteOlderPackages(_package);
            Progress = 1;
            DownloadedBytes = TotalBytes > 0 ? TotalBytes : asset.Size;
            State = MobileUpdateState.Ready;
            Install();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Progress = 0;
            DownloadedBytes = 0;
            BytesPerSecond = 0;
            Error = null;
            State = MobileUpdateState.Available;
        }
        catch (Exception e)
        {
            BytesPerSecond = 0;
            Error = Describe(e);
            State = MobileUpdateState.Failed;
        }
        finally
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _downloadCts, null, cts), cts))
            {
                try { cts.Dispose(); }
                catch (ObjectDisposedException) { }
            }
        }
    }

    private void Install()
    {
        if (_package is null || !File.Exists(_package))
        {
            // The cache was cleared meanwhile: fetch it again.
            State = MobileUpdateState.Available;
            return;
        }
        try
        {
            _platform!.Install(_package);
        }
        catch (Exception e)
        {
            Error = MobileLangResources.Format(MobileLangResourceKind.UpdateFailedInstall, e.Message);
            State = MobileUpdateState.Failed;
        }
    }

    private void NotifyOfferPropertiesChanged()
    {
        OnPropertyChanged(nameof(OfferedVersion));
        OnPropertyChanged(nameof(OfferedNotes));
        OnPropertyChanged(nameof(HasOffer));
        OnPropertyChanged(nameof(HasOfferedNotes));
        OnPropertyChanged(nameof(IsOfferedPreview));
        OnPropertyChanged(nameof(OfferedKindText));
        OnPropertyChanged(nameof(VersionTransitionText));
        OnPropertyChanged(nameof(OfferedMetaText));
        OnPropertyChanged(nameof(OfferedNotesUri));
        OnPropertyChanged(nameof(HasNotesUrl));
        OnPropertyChanged(nameof(NotesModalTitle));
        OnPropertyChanged(nameof(PrimaryModalActionText));
        OnPropertyChanged(nameof(HasPrimaryModalAction));
        OnPropertyChanged(nameof(BannerTitle));
        OnPropertyChanged(nameof(BannerSubtitle));
        OnPropertyChanged(nameof(IsBannerVisible));
    }

    /// <summary>Only the package just downloaded stays: earlier ones (and their partial downloads) take room for nothing.</summary>
    private void DeleteOlderPackages(string keep)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_platform!.DownloadDirectory))
            {
                if (!string.Equals(file, keep, StringComparison.Ordinal)) File.Delete(file);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? $"{Megabytes(bytes)} MB"
        : $"{Math.Max(1, bytes / 1024)} KB";

    private static string Megabytes(long bytes) => (bytes / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture);

    private static string Describe(Exception e) => e switch
    {
        UpdateException { Kind: UpdateErrorKind.Integrity } => MobileLangResources.Get(MobileLangResourceKind.UpdateFailedIntegrity),
        UpdateException { Kind: UpdateErrorKind.Disk } or IOException or UnauthorizedAccessException => MobileLangResources.Get(MobileLangResourceKind.UpdateFailedDisk),
        UpdateException { Kind: UpdateErrorKind.RateLimited } => MobileLangResources.Get(MobileLangResourceKind.UpdateFailedBusy),
        _ => MobileLangResources.Get(MobileLangResourceKind.UpdateFailedNetwork),
    };

    private static string AssemblyVersion() =>
        typeof(MobileUpdater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "";

    /// <summary>
    /// Marshals download progress callbacks onto Avalonia's UI thread even when <see cref="SynchronizationContext.Current"/>
    /// is null (e.g. on Android or background continuations), preventing cross-thread UI binding crashes.
    /// </summary>
    private sealed class UiProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value)
        {
            if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
            {
                handler(value);
                return;
            }
            Dispatcher.UIThread.Post(() => handler(value), DispatcherPriority.Background);
        }
    }
}
