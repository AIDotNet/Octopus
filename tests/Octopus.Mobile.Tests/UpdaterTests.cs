using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Octopus.Core.Updates;
using Octopus.Mobile.Common;
using Octopus.Mobile.Hosts;
using Octopus.Mobile.Updates;
using Octopus.Mobile.Workspace;
using Octopus.Updates;
using TKCode.Arc.Mobile;
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

    [AvaloniaFact]
    public void Dashboard_update_banner_shows_on_activity_and_computers_tabs_hides_on_me_tab_and_reappears_on_ready()
    {
        var platform = new FakePlatform("1.3.1120", _folder);
        var feed = new FakeFeed { Offer = Offer("1.4.1124", AndroidAsset(Apk)) };
        var updater = new MobileUpdater(platform, feed, new UpdateDownloader(Serving(Apk)), _ => { });
        using var phone = new Phone(updater: updater);
        var page = phone.Top<HostsPage>();
        var hosts = (HostsViewModel)page.DataContext!;
        var banner = page.FindControl<Control>("UpdateReminderBanner")!;

        Assert.False(banner.IsVisible);

        Assert.True(Phone.Until(() => { _ = updater.CheckQuietlyAsync(); return updater.State == MobileUpdateState.Available; }));
        Phone.Pump();
        Assert.True(hosts.ShowUpdateBanner);
        Assert.True(banner.IsVisible);
        Assert.Contains("1.4.1124", updater.BannerTitle);
        Assert.Contains("正式版", updater.OfferedMetaText);

        // Switching to Computers tab keeps the reminder banner visible; Me tab hides it to avoid duplicating MeAboutGroup.
        hosts.SelectTabCommand.Execute(HostsViewModel.TabHosts);
        Phone.Pump();
        Assert.True(hosts.ShowUpdateBanner);
        Assert.True(banner.IsVisible);

        hosts.SelectTabCommand.Execute(HostsViewModel.TabAccount);
        Phone.Pump();
        Assert.False(hosts.ShowUpdateBanner);
        Assert.False(banner.IsVisible);

        // Dismissing on Activity hides the Available banner, but transitioning to Ready surfaces it again.
        hosts.SelectTabCommand.Execute(HostsViewModel.TabActivity);
        Phone.Pump();
        updater.DismissBannerCommand.Execute(null);
        Phone.Pump();
        Assert.False(updater.IsBannerVisible);
        Assert.False(banner.IsVisible);

        updater.ActCommand.Execute(null);
        Assert.True(Phone.Until(() => updater.State == MobileUpdateState.Ready));
        Phone.Pump();
        Assert.True(updater.IsBannerVisible);
        Assert.True(banner.IsVisible);
        Assert.Equal("1.4.1124 已就绪", updater.BannerTitle);
    }

    [AvaloniaFact]
    public void Opening_release_notes_shows_in_app_markdown_sheet_and_links_open_in_browser()
    {
        Uri? opened = null;
        var platform = new FakePlatform("1.3.1120", _folder);
        const string markdown = """
            ## 1.4.1124

            ### 新功能

            - **应用内更新弹窗**：支持在移动端直接用 `Markdown` 渲染更新说明 ([#218](https://github.com/AIDotNet/Octopus/pull/218))
              - 支持嵌套列表与 *斜体*
            - [x] 已完成状态优化

            > 提示：下载完成后由系统安装器确认安装。

            ```sh
            dotnet test tests/Octopus.Mobile.Tests
            ```

            ---

            [Full changelog](https://github.com/AIDotNet/Octopus/compare/v1.3.1120...v1.4.1124)
            """;
        var feed = new FakeFeed { Offer = Offer("1.4.1124", AndroidAsset(Apk), markdown) };
        var updater = new MobileUpdater(platform, feed, new UpdateDownloader(Serving(Apk)), uri => opened = uri);
        using var phone = new Phone(updater: updater);
        var page = phone.Top<HostsPage>();

        Assert.True(Phone.Until(() => { _ = updater.CheckQuietlyAsync(); return updater.State == MobileUpdateState.Available; }));
        Phone.Pump();

        // Opening notes opens the in-app sheet instead of kicking straight out to the browser.
        updater.OpenNotesCommand.Execute(null);
        Phone.Pump();
        Assert.Null(opened);
        Assert.True(updater.IsNotesOpen);
        var sheet = page.FindControl<Sheet>("UpdateNotesSheet")!;
        Assert.True(sheet.IsOpen);
        Assert.Equal("1.4.1124 更新说明", updater.NotesModalTitle);
        Assert.Equal("1.3.1120 → 1.4.1124", updater.VersionTransitionText);

        var mdView = page.FindControl<MobileMarkdownView>("UpdateNotesMarkdown")!;
        Assert.True(mdView.IsVisible);
        Assert.Contains(mdView.Blocks, b => b.Kind == MarkdownBlockKind.Heading && b.Level == 2 && b.RawText == "1.4.1124");
        Assert.Contains(mdView.Blocks, b => b.Kind == MarkdownBlockKind.Heading && b.Level == 3 && b.RawText == "新功能");
        Assert.Contains(mdView.Blocks, b => b.Kind == MarkdownBlockKind.ListItem && b.Level == 1);
        Assert.Contains(mdView.Blocks, b => b.Kind == MarkdownBlockKind.ListItem && b.Marker == "☑");
        Assert.Contains(mdView.Blocks, b => b.Kind == MarkdownBlockKind.Quote);
        Assert.Contains(mdView.Blocks, b => b.Kind == MarkdownBlockKind.Code && b.Marker == "sh");
        Assert.Contains(mdView.Blocks, b => b.Kind == MarkdownBlockKind.Divider);

        // Tapping an inline Markdown link and the "Open in browser" button both forward to the platform browser opener.
        var prLink = mdView.Blocks.SelectMany(b => b.Inlines).First(i => i.Kind == MarkdownInlineKind.Link && i.Text == "#218");
        updater.OpenLinkCommand.Execute(new Uri(prLink.Url!));
        Assert.Equal("https://github.com/AIDotNet/Octopus/pull/218", opened?.ToString());

        updater.OpenNotesInBrowserCommand.Execute(null);
        Assert.Equal("https://github.com/AIDotNet/Octopus/releases/tag/v1.4.1124", opened?.ToString());

        updater.CloseNotesCommand.Execute(null);
        Phone.Pump();
        Assert.False(sheet.IsOpen);
    }

    [AvaloniaFact]
    public void Release_without_notes_still_opens_sheet_with_empty_fallback()
    {
        var platform = new FakePlatform("1.3.1120", _folder);
        var feed = new FakeFeed { Offer = Offer("1.4.1124", AndroidAsset(Apk), notes: null) };
        var updater = new MobileUpdater(platform, feed, new UpdateDownloader(Serving(Apk)), _ => { });
        using var phone = new Phone(updater: updater);
        var page = phone.Top<HostsPage>();

        Assert.True(Phone.Until(() => { _ = updater.CheckQuietlyAsync(); return updater.State == MobileUpdateState.Available; }));
        updater.OpenNotesCommand.Execute(null);
        Phone.Pump();

        Assert.True(updater.IsNotesOpen);
        Assert.False(updater.HasOfferedNotes);
        Assert.False(page.FindControl<Control>("UpdateNotesMarkdown")!.IsVisible);
        Assert.True(page.FindControl<Control>("UpdateNotesEmptyCard")!.IsVisible);
    }

    [AvaloniaFact]
    public void Cancelling_download_returns_cleanly_to_available_state()
    {
        var platform = new FakePlatform("1.3.1120", _folder);
        var feed = new FakeFeed { Offer = Offer("1.4.1124", AndroidAsset(Apk)) };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updater = new MobileUpdater(platform, feed, new UpdateDownloader(new HttpClient(new HangingHandler(started))), _ => { });

        updater.ActCommand.Execute(null);
        Assert.True(Phone.Until(() => updater.State == MobileUpdateState.Available));

        updater.ActCommand.Execute(null);
        Assert.True(Phone.Until(() => started.Task.IsCompleted && updater.State == MobileUpdateState.Downloading));

        updater.CancelDownloadCommand.Execute(null);
        Assert.True(Phone.Until(() => updater.State == MobileUpdateState.Available));
        Assert.Null(updater.Error);
        Assert.Empty(platform.Installed);
    }

    [AvaloniaFact]
    public void Network_drop_mid_download_shows_retryable_error_without_crashing_and_resumes()
    {
        var payload = new byte[128_000];
        RandomNumberGenerator.Fill(payload);
        var platform = new FakePlatform("1.3.1120", _folder);
        var feed = new FakeFeed { Offer = Offer("1.4.1124", AndroidAsset(payload)) };
        var handler = new DropOnceThenResumeHandler(payload, dropAfterBytes: 40_000);
        var updater = new MobileUpdater(platform, feed, new UpdateDownloader(new HttpClient(handler)), _ => { });

        updater.ActCommand.Execute(null);
        Assert.True(Phone.Until(() => updater.State == MobileUpdateState.Available));

        // First download attempt drops mid-stream with a SocketException: must transition cleanly to Failed (no crash) and keep .part.
        updater.ActCommand.Execute(null);
        Assert.True(Phone.Until(() => updater.State == MobileUpdateState.Failed));
        Assert.Contains("重试", updater.Error);
        Assert.Contains("重试", updater.StatusText);
        Assert.Empty(platform.Installed);

        // Retrying resumes via Range and installs the verified package.
        updater.ActCommand.Execute(null);
        Assert.True(Phone.Until(() => updater.State == MobileUpdateState.Ready));
        Assert.True(handler.ResumedFromOffset > 0);
        var installed = Assert.Single(platform.Installed);
        Assert.Equal(payload, File.ReadAllBytes(installed));
    }

    [AvaloniaFact]
    public void Activity_session_rows_keep_long_titles_within_row_bounds_and_put_project_first()
    {
        using var phone = new Phone();
        var host = phone.Workspace.Hosts[0];
        var project = host.Projects[0];
        const string longTitle = "GitHub Issue 205 Compact Style Reply With Extra Long Session Title For Mobile Overflow Check";
        project.Sessions.Add(new RemoteSession(project, "overflow-test", SessionKind.Agent, longTitle)
        {
            AgentId = "claude",
            Activity = SessionActivity.Working,
        });
        Phone.Pump();

        var page = phone.Top<HostsPage>();
        var hosts = (HostsViewModel)page.DataContext!;
        var rowVm = hosts.WorkingSessions.First(s => s.Title == longTitle);
        Assert.StartsWith(project.Name, rowVm.ContextLine);
        Assert.Contains(host.Name, rowVm.ContextLine);

        var workingGroup = page.FindControl<Control>("WorkingGroup")!;
        var titleBlock = workingGroup.GetVisualDescendants()
            .OfType<TextBlock>()
            .First(tb => tb.Text == longTitle);
        var listItem = titleBlock.GetVisualAncestors().OfType<ListItem>().First();

        var titleRight = titleBlock.TranslatePoint(new Point(titleBlock.Bounds.Width, 0), listItem)!.Value.X;
        Assert.True(titleRight <= listItem.Bounds.Width, $"Title right edge {titleRight:F1} exceeded ListItem width {listItem.Bounds.Width:F1}");
    }

    [Fact]
    public void Mobile_markdown_parser_handles_inline_styles_escaped_chars_and_snake_case()
    {
        var blocks = MobileMarkdownParser.Parse("Fix `foo_bar` and **bold** + *italic* + ~~old~~ + snake_case_name + \\*literal\\*");
        var para = Assert.Single(blocks);
        Assert.Equal(MarkdownBlockKind.Paragraph, para.Kind);
        Assert.Contains(para.Inlines, i => i is { Kind: MarkdownInlineKind.Code, Text: "foo_bar" });
        Assert.Contains(para.Inlines, i => i is { Kind: MarkdownInlineKind.Bold, Text: "bold" });
        Assert.Contains(para.Inlines, i => i is { Kind: MarkdownInlineKind.Italic, Text: "italic" });
        Assert.Contains(para.Inlines, i => i is { Kind: MarkdownInlineKind.Strikethrough, Text: "old" });
        Assert.Contains(para.Inlines, i => i.Kind == MarkdownInlineKind.Text && i.Text.Contains("snake_case_name") && i.Text.Contains("*literal*"));
    }

    private static UpdateAsset AndroidAsset(byte[] content) => new()
    {
        Name = "Octopus-1.4.1124-android.apk",
        Url = "https://octopus.example/releases/download/v1.4.1124/Octopus-1.4.1124-android.apk",
        Size = content.Length,
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(content)),
    };

    private static UpdateOffer Offer(string version, UpdateAsset? asset, string? notes = "- 移动端支持应用内更新")
    {
        var manifest = new UpdateManifest
        {
            Version = version,
            PublishedAt = new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero),
            NotesUrl = $"https://github.com/AIDotNet/Octopus/releases/tag/v{version}",
        };
        if (asset is not null) manifest.Assets["android"] = asset;
        return new UpdateOffer(AppVersion.Parse(version), manifest, asset) { Notes = notes };
    }

    private static HttpClient Serving(byte[] content) => new(new StaticHandler(content));

    private sealed class HangingHandler(TaskCompletionSource started) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class StaticHandler(byte[] content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
    }

    private sealed class DropOnceThenResumeHandler(byte[] content, int dropAfterBytes) : HttpMessageHandler
    {
        private int _attempt;
        public long ResumedFromOffset { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _attempt++;
            var from = (int)(request.Headers.Range?.Ranges.FirstOrDefault()?.From ?? 0);
            if (_attempt == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new DroppingStream(content, dropAfterBytes)),
                };
                response.Content.Headers.ContentLength = content.Length;
                return Task.FromResult(response);
            }
            ResumedFromOffset = from;
            var remaining = content.Length - from;
            var partial = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(content, from, remaining),
            };
            partial.Content.Headers.ContentLength = remaining;
            partial.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, content.Length - 1, content.Length);
            return Task.FromResult(partial);
        }

        private sealed class DroppingStream(byte[] data, int dropAfter) : MemoryStream(data)
        {
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (Position >= dropAfter) throw new SocketException((int)SocketError.NetworkDown);
                var count = (int)Math.Min(Math.Min(buffer.Length, 16_384), dropAfter - Position);
                return base.ReadAsync(buffer[..count], cancellationToken);
            }
        }
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
