using System.Text.Json;
using Octopus.Core.Updates;
using Octopus.Platform.Updates;
using Octopus.Storage;
using static Octopus.Updates.Tests.Versions;

namespace Octopus.Updates.Tests;

public sealed class UpdateServiceTests : IDisposable
{
    private readonly TestKey _key = new();
    private readonly FakeHttp _http = new();
    private readonly FakeReleases _releases;
    private readonly TempPaths _paths = new();
    private readonly InMemorySettingsStore _settings = new();
    private readonly FakeInstaller _installer = new();
    private readonly List<UpdateService> _services = [];
    private bool _otherInstances;

    public UpdateServiceTests() => _releases = new FakeReleases(_http, _key);

    public void Dispose()
    {
        foreach (var service in _services) service.Dispose();
        _paths.Dispose();
        _key.Dispose();
    }

    /// <summary>A service as the app builds it, with the timer far away: tests check by hand.</summary>
    private UpdateService Service(string? current = "1.0.0", bool development = false, TimeSpan? firstCheck = null)
    {
        var http = new HttpClient(_http);
        var service = new UpdateService(
            new UpdateOptions
            {
                CurrentVersion = AppVersion.TryParse(current, out var v) ? v : null,
                IsDevelopment = development,
                FirstCheckDelay = firstCheck ?? TimeSpan.FromHours(1),
                OtherInstancesRunning = _ => _otherInstances,
            },
            _settings,
            new GitHubReleasesFeed(http, new ManifestVerifier([_key.PublicKey]), FakeReleases.ListUrl),
            new UpdateDownloader(http),
            _installer,
            _paths);
        _services.Add(service);
        service.Start();
        return service;
    }

    private void Mode(string mode) => _settings.Update(s => s.Updates.Mode = mode);

    private PendingUpdate? Pending(UpdateService service)
    {
        var path = Path.Combine(service.Root, UpdateService.PendingFileName);
        return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllBytes(path), UpdatesJsonContext.Default.PendingUpdate) : null;
    }

    [Theory]
    [InlineData("0.0.0-dev", false)]
    [InlineData("1.0.0", true)]
    public async Task DevelopmentBuildsNeverCheck(string current, bool development)
    {
        _releases.Add("1.1.0");
        var service = Service(current, development);
        await service.CheckAsync(manual: true);
        Assert.Equal(UpdateStatus.Disabled, service.Current.Status);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task AutoModeDownloadsAndInstallsOnQuit()
    {
        var package = _releases.Add("1.1.0");
        var service = Service();
        Assert.Equal(UpdateStatus.Idle, service.Current.Status);

        await service.CheckAsync(manual: false);
        await Wait.ForAsync(service, s => s.Status == UpdateStatus.Ready);
        Assert.Equal(V("1.1.0"), service.Current.Offer!.Version);
        Assert.NotNull(service.Current.LastCheckedAt);
        var pending = Pending(service)!;
        Assert.Equal("1.1.0", pending.Version);
        Assert.False(pending.Approved);
        Assert.Equal(package, File.ReadAllBytes(pending.Package));

        Assert.True(service.InstallOnExit());
        var (prepared, request) = Assert.Single(_installer.Launched);
        Assert.Equal(pending.Prepared, prepared);
        Assert.False(request.Relaunch);
        Assert.Equal(Environment.ProcessId, request.ProcessId);
        Assert.True(Pending(service)!.Launched);
    }

    [Fact]
    public async Task TheNextStartSeesTheInstallWorked()
    {
        _releases.Add("1.1.0");
        var before = Service();
        await before.CheckAsync(manual: false);
        await Wait.ForAsync(before, s => s.Status == UpdateStatus.Ready);
        before.InstallOnExit();
        before.Dispose();

        var after = Service("1.1.0");
        Assert.Equal(V("1.1.0"), after.JustUpdatedTo);
        Assert.Null(Pending(after));
        Assert.False(Directory.Exists(Path.Combine(after.Root, "1.1.0")));
        Assert.Equal(UpdateStatus.Idle, after.Current.Status);
    }

    [Fact]
    public async Task AFailedInstallIsRetriedOnceThenReported()
    {
        _releases.Add("1.1.0");
        var first = Service();
        await first.CheckAsync(manual: false);
        await Wait.ForAsync(first, s => s.Status == UpdateStatus.Ready);
        first.InstallOnExit();
        File.WriteAllText(Path.Combine(first.Root, UpdateService.InstallLogName), "waiting\nresult=failed: the installer exited with 5\n");
        first.Dispose();

        // Still the old version: the update stays ready and is tried again at the next quit.
        var second = Service();
        Assert.Null(second.JustUpdatedTo);
        Assert.Equal(UpdateStatus.Ready, second.Current.Status);
        Assert.True(second.InstallOnExit());
        File.WriteAllText(Path.Combine(second.Root, UpdateService.InstallLogName), "result=failed: the installer exited with 5\n");
        second.Dispose();

        var third = Service();
        Assert.Equal(UpdateStatus.Failed, third.Current.Status);
        Assert.Equal(UpdateErrorKind.Install, third.Current.Error);
        Assert.Equal("the installer exited with 5", third.Current.ErrorDetail);
        Assert.False(third.Current.IsQuietError);
        Assert.False(third.InstallOnExit());

        // The same version is not downloaded again behind the person's back; the failure stays on show.
        await third.CheckAsync(manual: false);
        Assert.Equal(UpdateStatus.Failed, third.Current.Status);
        Assert.Equal(UpdateErrorKind.Install, third.Current.Error);
        Assert.False(third.InstallOnExit());

        // A newer release starts over.
        _releases.Add("1.2.0");
        await third.CheckAsync(manual: false);
        await Wait.ForAsync(third, s => s.Status == UpdateStatus.Ready && s.Offer!.Version == V("1.2.0"));
        Assert.True(third.InstallOnExit());
    }

    [Fact]
    public async Task NotifyModeOffersAndDownloadsWhenAsked()
    {
        Mode("notify");
        _releases.Add("1.1.0");
        var service = Service();
        await service.CheckAsync(manual: false);
        Assert.Equal(UpdateStatus.Available, service.Current.Status);
        Assert.True(service.Current.CanInstall);
        Assert.DoesNotContain(_http.Requests, r => r.RequestUri!.ToString().EndsWith(".zip"));

        await service.DownloadAsync();
        Assert.Equal(UpdateStatus.Ready, service.Current.Status);
        Assert.True(Pending(service)!.Approved);
        // Asked for: installed even though nothing is automatic.
        Mode("off");
        Assert.True(service.InstallOnExit());
    }

    [Fact]
    public async Task AnAutomaticDownloadIsNotInstalledOnceAutoModeIsOff()
    {
        _releases.Add("1.1.0");
        var service = Service();
        await service.CheckAsync(manual: false);
        await Wait.ForAsync(service, s => s.Status == UpdateStatus.Ready);
        Mode("notify");
        Assert.False(service.InstallOnExit());
        service.RelaunchAfterInstall = true;
        Assert.True(service.InstallOnExit());
        Assert.True(_installer.Launched.Single().Request.Relaunch);
    }

    [Theory]
    [InlineData(InstallKind.Portable)]
    [InlineData(InstallKind.ReadOnly)]
    public async Task InstallsThatCannotUpdateThemselvesOnlyOffer(InstallKind kind)
    {
        _installer.Kind = kind;
        _releases.Add("1.1.0");
        var service = Service();
        await service.CheckAsync(manual: false);
        Assert.Equal(UpdateStatus.Available, service.Current.Status);
        Assert.False(service.Current.CanInstall);
        await service.DownloadAsync();
        Assert.Equal(UpdateStatus.Available, service.Current.Status);
        Assert.False(service.InstallOnExit());
    }

    [Fact]
    public async Task ASkippedVersionIsOfferedOnlyWhenAskedOrWhenANewerOneComes()
    {
        Mode("notify");
        _releases.Add("1.1.0");
        var service = Service();
        await service.CheckAsync(manual: false);
        service.SkipOffered();
        Assert.Equal("1.1.0", _settings.Current.Updates.SkippedVersion);
        Assert.Equal(UpdateStatus.UpToDate, service.Current.Status);

        await service.CheckAsync(manual: false);
        Assert.Equal(UpdateStatus.UpToDate, service.Current.Status);
        await service.CheckAsync(manual: true);
        Assert.Equal(UpdateStatus.Available, service.Current.Status);

        _releases.Add("1.2.0");
        await service.CheckAsync(manual: false);
        Assert.Equal(V("1.2.0"), service.Current.Offer!.Version);
    }

    [Fact]
    public async Task FailedAutomaticChecksStayQuiet()
    {
        _http.Status(FakeReleases.ListUrl, System.Net.HttpStatusCode.BadGateway);
        var service = Service();
        await service.CheckAsync(manual: false);
        Assert.Equal(UpdateStatus.Failed, service.Current.Status);
        Assert.Equal(UpdateErrorKind.Network, service.Current.Error);
        Assert.True(service.Current.IsQuietError);
        await service.CheckAsync(manual: true);
        Assert.False(service.Current.IsQuietError);
    }

    [Fact]
    public async Task AFailedCheckKeepsAReadyUpdate()
    {
        _releases.Add("1.1.0");
        var service = Service();
        await service.CheckAsync(manual: false);
        await Wait.ForAsync(service, s => s.Status == UpdateStatus.Ready);
        _http.Status(FakeReleases.ListUrl, System.Net.HttpStatusCode.BadGateway);
        await service.CheckAsync(manual: true);
        Assert.Equal(UpdateStatus.Ready, service.Current.Status);
    }

    [Fact]
    public async Task ATamperedPackageFails()
    {
        _releases.Add("1.1.0");
        _http.File("https://dl.test/1.1.0/Octopus-1.1.0-osx-arm64.zip", "something else"u8.ToArray());
        var service = Service();
        await service.CheckAsync(manual: false);
        await Wait.ForAsync(service, s => s.Status == UpdateStatus.Failed);
        Assert.Equal(UpdateErrorKind.Integrity, service.Current.Error);
        Assert.False(service.Current.IsQuietError);
        Assert.Null(Pending(service));
        Assert.False(service.InstallOnExit());
    }

    [Fact]
    public async Task APackageThePlatformRefusesFails()
    {
        _installer.PrepareFails = new UpdateInstallException("bad signature");
        _releases.Add("1.1.0");
        var service = Service();
        await service.CheckAsync(manual: false);
        await Wait.ForAsync(service, s => s.Status == UpdateStatus.Failed);
        Assert.Equal(UpdateErrorKind.Package, service.Current.Error);
        Assert.Equal("bad signature", service.Current.ErrorDetail);
    }

    [Fact]
    public async Task ACancelledDownloadIsNotRestartedOnItsOwn()
    {
        _http.Hold = new TaskCompletionSource();
        _releases.Add("1.1.0");
        var service = Service();
        await service.CheckAsync(manual: false);
        await Wait.ForAsync(service, s => s.Status == UpdateStatus.Downloading);
        Assert.Equal(0, service.Current.Progress!.Value.Received);
        service.CancelDownload();
        await Wait.ForAsync(service, s => s.Status == UpdateStatus.Available);

        _http.Hold = null;
        await service.CheckAsync(manual: false);
        Assert.Equal(UpdateStatus.Available, service.Current.Status);
        await service.DownloadAsync();
        Assert.Equal(UpdateStatus.Ready, service.Current.Status);
    }

    [Fact]
    public async Task ANewerReleaseReplacesAReadyOne()
    {
        _releases.Add("1.1.0");
        var service = Service();
        await service.CheckAsync(manual: false);
        await Wait.ForAsync(service, s => s.Status == UpdateStatus.Ready);
        await service.CheckAsync(manual: false);
        Assert.Equal(UpdateStatus.Ready, service.Current.Status); // the same release is not offered again

        _releases.Add("1.2.0");
        await service.CheckAsync(manual: false);
        await Wait.ForAsync(service, s => s.Status == UpdateStatus.Ready && s.Offer!.Version == V("1.2.0"));
        Assert.Equal("1.2.0", Pending(service)!.Version);
        Assert.False(Directory.Exists(Path.Combine(service.Root, "1.1.0")));
    }

    [Fact]
    public async Task ChoosingThePreviewChannelChecksAtOnce()
    {
        Mode("notify");
        _releases.Add("1.1.0-preview.1");
        var service = Service();
        await service.CheckAsync(manual: false);
        Assert.Equal(UpdateStatus.UpToDate, service.Current.Status);
        Assert.Equal(UpdateChannel.Stable, service.Current.Channel);
        Assert.True(service.Current.ChannelFollowsVersion);

        _settings.Update(s => s.Updates.Channel = UpdateSettingValues.Preview);
        await Wait.ForAsync(service, s => s.Status == UpdateStatus.Available);
        Assert.Equal(UpdateChannel.Preview, service.Current.Channel);
        Assert.False(service.Current.ChannelFollowsVersion);
    }

    [Fact]
    public async Task APreviewBuildFollowsPreviews()
    {
        Mode("notify");
        _releases.Add("1.1.0-preview.2");
        var service = Service("1.1.0-preview.1");
        await service.CheckAsync(manual: false);
        Assert.Equal(UpdateChannel.Preview, service.Current.Channel);
        Assert.Equal(V("1.1.0-preview.2"), service.Current.Offer!.Version);
    }

    [Fact]
    public async Task NothingIsInstalledUnderAnotherRunningCopy()
    {
        _releases.Add("1.1.0");
        var service = Service();
        await service.CheckAsync(manual: false);
        await Wait.ForAsync(service, s => s.Status == UpdateStatus.Ready);
        _otherInstances = true;
        Assert.False(service.InstallOnExit());
        Assert.Empty(_installer.Launched);
    }

    [Fact]
    public async Task TheTimerChecksOnItsOwn()
    {
        _releases.Add("1.1.0");
        var service = Service(firstCheck: TimeSpan.FromMilliseconds(10));
        await Wait.ForAsync(service, s => s.Status == UpdateStatus.Ready);
    }

    [Fact]
    public async Task OffModeDoesNotCheckOnItsOwn()
    {
        Mode("off");
        _releases.Add("1.1.0");
        var service = Service(firstCheck: TimeSpan.FromMilliseconds(10));
        await Task.Delay(200);
        Assert.Equal(UpdateStatus.Idle, service.Current.Status);
        await service.CheckAsync(manual: true);
        Assert.Equal(UpdateStatus.Available, service.Current.Status);
    }
}
