using System.Net;
using System.Reflection;
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

    /// <summary>An updater for a platform that has none (iOS, the desktop preview, tests): no row, no checks.</summary>
    public static MobileUpdater Unsupported { get; } = new(null, null, null, _ => { });

    /// <param name="feed">Where releases are found; null when the build knows no official service.</param>
    public MobileUpdater(IMobileUpdatePlatform? platform, IUpdateFeed? feed, UpdateDownloader? downloader, Action<Uri> openBrowser)
    {
        _platform = platform;
        _feed = feed;
        _downloader = downloader;
        _openBrowser = openBrowser;
        AppVersion.TryParse(platform?.InstalledVersion, out _installed);
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
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(HasNews), nameof(IsBusy), nameof(ActionText))]
    public partial MobileUpdateState State { get; private set; } = MobileUpdateState.Idle;

    /// <summary>0..1 while downloading.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial double Progress { get; private set; }

    /// <summary>Why the last check or download failed, for the status line.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string? Error { get; private set; }

    /// <summary>The release offered, once one was found.</summary>
    public string? OfferedVersion => _offer?.Version.ToString();

    /// <summary>The release notes (Markdown) of the offered release, when it has any.</summary>
    public string? OfferedNotes => _offer?.Notes;

    /// <summary>A newer release waits: the row shows a pill and the "Me" tab a dot.</summary>
    public bool HasNews => State is MobileUpdateState.Available or MobileUpdateState.Downloading or MobileUpdateState.Ready;

    public bool IsBusy => State is MobileUpdateState.Checking or MobileUpdateState.Downloading;

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

    /// <summary>Opens the offered release's page (its notes and downloads).</summary>
    [RelayCommand]
    private void OpenNotes()
    {
        var address = _offer?.NotesUrl ?? (_offer is { } offer ? UpdateEndpoints.ReleasePage(offer.Version) : null);
        if (address is not null && Uri.TryCreate(address, UriKind.Absolute, out var uri)) _openBrowser(uri);
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
            OnPropertyChanged(nameof(OfferedVersion));
            OnPropertyChanged(nameof(OfferedNotes));
            State = _offer is null ? MobileUpdateState.UpToDate : MobileUpdateState.Available;
        }
        catch (Exception e) when (e is UpdateException or HttpRequestException or IOException or OperationCanceledException)
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
        State = MobileUpdateState.Downloading;
        Progress = 0;
        Error = null;
        try
        {
            var progress = new Progress<DownloadProgress>(p => Progress = p.Fraction);
            _package = await _downloader!.DownloadAsync(asset, _platform!.DownloadDirectory, progress, CancellationToken.None).ConfigureAwait(true);
            DeleteOlderPackages(_package);
            State = MobileUpdateState.Ready;
            Install();
        }
        catch (Exception e) when (e is UpdateException or HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Error = Describe(e);
            State = MobileUpdateState.Failed;
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

    private static string Describe(Exception e) => e switch
    {
        UpdateException { Kind: UpdateErrorKind.Integrity } => MobileLangResources.Get(MobileLangResourceKind.UpdateFailedIntegrity),
        UpdateException { Kind: UpdateErrorKind.Disk } or IOException or UnauthorizedAccessException => MobileLangResources.Get(MobileLangResourceKind.UpdateFailedDisk),
        UpdateException { Kind: UpdateErrorKind.RateLimited } => MobileLangResources.Get(MobileLangResourceKind.UpdateFailedBusy),
        _ => MobileLangResources.Get(MobileLangResourceKind.UpdateFailedNetwork),
    };

    private static string AssemblyVersion() =>
        typeof(MobileUpdater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "";
}
