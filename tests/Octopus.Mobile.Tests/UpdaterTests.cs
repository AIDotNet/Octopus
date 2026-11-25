using System.Net;
using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Octopus.Core.Updates;
using Octopus.Mobile.Hosts;
using Octopus.Mobile.Updates;
using Octopus.Updates;
using Xunit;

namespace Octopus.Mobile.Tests;

/// <summary>
/// The phone's updater: a newer signed release for "android" is offered in the "Me" tab, its package is downloaded and
/// checked against the manifest, and only a verified file reaches the system installer. No network: the feed and the
/// download server are stand-ins.
/// </summary>
public sealed class UpdaterTests : IDisposable
{
    private static readonly byte[] Apk = "PK\x03\x04 not really an apk, but bytes with a hash"u8.ToArray();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "octopus-mobile-updates-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [AvaloniaFact]
    public void A_newer_release_is_offered_in_the_me_tab_downloaded_verified_and_handed_to_the_installer()
    {
        var platform = new FakePlatform("1.3.1120", _folder);
        var feed = new FakeFeed { Offer = Offer("1.4.1124", AndroidAsset(Apk)) };
        var updater = new MobileUpdater(platform, feed, new UpdateDownloader(Serving(Apk)), _ => { });
        using var phone = new Phone(updater: updater);
        var page = phone.Top<HostsPage>();
        var hosts = (HostsViewModel)page.DataContext!;
        hosts.SelectTabCommand.Execute(HostsViewModel.TabAccount);
        Phone.Pump();
        Assert.True(page.FindControl<Control>("MeAboutGroup")!.IsVisible);
        Assert.Equal("当前版本 1.3.1120", updater.StatusText);

        // The quiet check at start: a release install asks the stable channel, for the android package.
        Assert.True(Phone.Until(() => { _ = updater.CheckQuietlyAsync(); return updater.State == MobileUpdateState.Available; }));
        Assert.Equal((UpdateChannel.Stable, "android"), (feed.Channel, feed.Target));
        Assert.Equal("发现新版本 1.4.1124", updater.StatusText);
        Assert.Equal("更新", updater.ActionText);
        Assert.Equal("我的 •", hosts.MeTabLabel);
        Assert.True(page.FindControl<Control>("MeUpdateNotesRow")!.IsVisible);

        updater.ActCommand.Execute(null);
        Assert.True(Phone.Until(() => updater.State == MobileUpdateState.Ready));
        var installed = Assert.Single(platform.Installed);
        Assert.Equal(Apk, File.ReadAllBytes(installed));
        Assert.Equal("安装", updater.ActionText);

        // Tapping again shows the installer again (the person may have backed out of it).
        updater.ActCommand.Execute(null);
        Assert.Equal(2, platform.Installed.Count);
    }

    [AvaloniaFact]
    public void A_download_that_does_not_match_the_signed_manifest_never_reaches_the_installer()
    {
        var platform = new FakePlatform("1.3.1120", _folder);
        var feed = new FakeFeed { Offer = Offer("1.4.1124", AndroidAsset(Apk)) };
        var tampered = Apk.Select(b => (byte)(b ^ 1)).ToArray();
        var updater = new MobileUpdater(platform, feed, new UpdateDownloader(Serving(tampered)), _ => { });

        updater.ActCommand.Execute(null);
        Assert.True(Phone.Until(() => updater.State == MobileUpdateState.Available));
        updater.ActCommand.Execute(null);
        Assert.True(Phone.Until(() => updater.State == MobileUpdateState.Failed));
        Assert.Empty(platform.Installed);
        Assert.Contains("签名", updater.StatusText);
        Assert.Empty(Directory.Exists(_folder) ? Directory.GetFiles(_folder) : []);
    }

    [AvaloniaFact]
    public void A_release_without_an_android_package_is_no_update_for_the_phone()
    {
        var feed = new FakeFeed { Offer = Offer("1.4.1124", asset: null) };
        var updater = new MobileUpdater(new FakePlatform("1.3.1120", _folder), feed, new UpdateDownloader(Serving(Apk)), _ => { });

        updater.ActCommand.Execute(null);
        Assert.True(Phone.Until(() => updater.State == MobileUpdateState.UpToDate));
        Assert.Equal("1.3.1120 已是最新版本", updater.StatusText);
        Assert.False(updater.HasNews);
    }

    [AvaloniaFact]
    public void A_preview_install_follows_previews()
    {
        var feed = new FakeFeed();
        var updater = new MobileUpdater(new FakePlatform("1.4.1124-preview.2", _folder), feed, new UpdateDownloader(Serving(Apk)), _ => { });

        updater.ActCommand.Execute(null);
        Assert.True(Phone.Until(() => updater.State == MobileUpdateState.UpToDate));
        Assert.Equal(UpdateChannel.Preview, feed.Channel);
    }

    [AvaloniaFact]
    public void The_quiet_check_at_start_says_nothing_when_offline_and_a_tap_says_why()
    {
        var feed = new FakeFeed { Failure = new UpdateException(UpdateErrorKind.Network, "offline") };
        var updater = new MobileUpdater(new FakePlatform("1.3.1120", _folder), feed, new UpdateDownloader(Serving(Apk)), _ => { });

        _ = updater.CheckQuietlyAsync();
        Phone.Pump();
        Assert.Equal(MobileUpdateState.Idle, updater.State);

        updater.ActCommand.Execute(null);
        Assert.True(Phone.Until(() => updater.State == MobileUpdateState.Failed));
        Assert.Equal("连不上更新服务，点按重试。", updater.StatusText);
    }

    [AvaloniaFact]
    public void A_development_build_checks_nothing()
    {
        var feed = new FakeFeed();
        var updater = new MobileUpdater(new FakePlatform("0.1", _folder), feed, new UpdateDownloader(Serving(Apk)), _ => { });

        Assert.False(updater.CanCheck);
        updater.ActCommand.Execute(null);
        _ = updater.CheckQuietlyAsync();
        Phone.Pump();
        Assert.Equal(0, feed.Calls);
        Assert.Equal("当前版本 0.1（开发版本，不检查更新）", updater.StatusText);
    }

    [AvaloniaFact]
    public void Without_an_updater_the_me_tab_has_no_update_row()
    {
        using var phone = new Phone();
        var page = phone.Top<HostsPage>();
        ((HostsViewModel)page.DataContext!).SelectTabCommand.Execute(HostsViewModel.TabAccount);
        Phone.Pump();
        Assert.False(page.FindControl<Control>("MeAboutGroup")!.IsVisible);
        Assert.Equal("我的", ((HostsViewModel)page.DataContext!).MeTabLabel);
    }

    private static UpdateAsset AndroidAsset(byte[] content) => new()
    {
        Name = "Octopus-1.4.1124-android.apk",
        Url = "https://octopus.example/releases/download/v1.4.1124/Octopus-1.4.1124-android.apk",
        Size = content.Length,
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(content)),
    };

    private static UpdateOffer Offer(string version, UpdateAsset? asset)
    {
        var manifest = new UpdateManifest { Version = version, NotesUrl = $"https://github.com/AIDotNet/Octopus/releases/tag/v{version}" };
        if (asset is not null) manifest.Assets["android"] = asset;
        return new UpdateOffer(AppVersion.Parse(version), manifest, asset) { Notes = "- 移动端支持应用内更新" };
    }

    private static HttpClient Serving(byte[] content) => new(new StaticHandler(content));

    private sealed class StaticHandler(byte[] content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
    }

    private sealed class FakeFeed : IUpdateFeed
    {
        public UpdateOffer? Offer { get; init; }
        public Exception? Failure { get; init; }
        public int Calls { get; private set; }
        public UpdateChannel? Channel { get; private set; }
        public string? Target { get; private set; }

        public Task<UpdateOffer?> FindAsync(AppVersion floor, UpdateChannel channel, string target, CancellationToken cancellationToken)
        {
            Calls++;
            Channel = channel;
            Target = target;
            if (Failure is not null) return Task.FromException<UpdateOffer?>(Failure);
            return Task.FromResult(Offer is { } offer && offer.Version > floor ? offer : null);
        }
    }

    private sealed class FakePlatform(string? version, string folder) : IMobileUpdatePlatform
    {
        public List<string> Installed { get; } = [];
        public string Target => "android";
        public string? InstalledVersion => version;
        public string DownloadDirectory => folder;

        public bool Install(string package)
        {
            Installed.Add(package);
            return true;
        }
    }
}
