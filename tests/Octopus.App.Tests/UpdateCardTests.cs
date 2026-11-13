using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Octopus.App.Composition;
using Octopus.App.Settings;
using Octopus.App.Settings.General;
using Octopus.App.Shell;
using Octopus.App.Updates;
using Octopus.Core.Updates;
using Octopus.Platform.Updates;
using Octopus.Storage;
using Octopus.Updates;
using TKCode.Arc;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>An updater whose state the test sets.</summary>
public sealed class FakeUpdateService : IUpdateService
{
    public UpdateSnapshot Current { get; private set; } = new()
    {
        Status = UpdateStatus.Idle,
        CurrentVersion = AppVersion.Parse("1.2.1112"),
        InstallKind = InstallKind.Installed,
    };

    public event EventHandler? Changed;
    public AppVersion? JustUpdatedTo => null;
    public bool RelaunchAfterInstall { get; set; }
    public List<string> Calls { get; } = [];

    public void Set(Func<UpdateSnapshot, UpdateSnapshot> change)
    {
        Current = change(Current);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public static UpdateOffer Offer(string version, bool withPackage = true, string? notes = null) => new(
        AppVersion.Parse(version),
        new UpdateManifest
        {
            Version = version,
            PublishedAt = new DateTimeOffset(2026, 12, 1, 8, 0, 0, TimeSpan.Zero),
            NotesUrl = $"https://github.com/AIDotNet/Octopus/releases/tag/v{version}",
        },
        withPackage ? new UpdateAsset { Name = $"Octopus-{version}-osx-arm64.zip", Url = "https://example.invalid/x.zip", Size = 64_000_000, Sha256 = new string('a', 64) } : null) { Notes = notes };

    public void Start() => Calls.Add("start");
    public Task CheckAsync(bool manual) { Calls.Add($"check:{manual}"); return Task.CompletedTask; }
    public Task DownloadAsync() { Calls.Add("download"); return Task.CompletedTask; }
    public void CancelDownload() => Calls.Add("cancel");
    public void SkipOffered() => Calls.Add("skip");
    public bool InstallOnExit() { Calls.Add("install"); return true; }
}

public class UpdateCardTests
{
    private static FakeUpdateService UseFake()
    {
        var fake = new FakeUpdateService();
        var original = AppHost.ConfigureOverrides;
        AppHost.ConfigureOverrides = services =>
        {
            original?.Invoke(services);
            services.Replace(ServiceDescriptor.Singleton<IUpdateService>(fake));
        };
        // A new container built while the override is in place (as TestApp.Fresh builds one), then the override goes.
        AppHost.Reset();
        AppHost.Get<Octopus.App.Appearance.ThemeService>().Track();
        AppHost.ConfigureOverrides = original;
        return fake;
    }

    [AvaloniaFact]
    public void TheRealUpdaterIsQuietInTests()
    {
        // The test host turns the updater off (TestApp): never a request, never a card.
        var updates = AppHost.Get<IUpdateService>();
        updates.Start();
        Assert.Equal(UpdateStatus.Disabled, updates.Current.Status);
        Assert.False(AppHost.Get<UpdateCardViewModel>().IsOpen);
    }

    [AvaloniaFact]
    public void NothingToTellShowsNoCard()
    {
        var fake = UseFake();
        var card = AppHost.Get<UpdateCardViewModel>();
        Assert.False(card.IsOpen);
        fake.Set(s => s with { Status = UpdateStatus.UpToDate });
        Assert.False(card.IsOpen);
        fake.Set(s => s with { IsChecking = true });
        Assert.False(card.IsOpen);
    }

    [AvaloniaFact]
    public void AnOfferHasDownloadSkipAndLater()
    {
        var fake = UseFake();
        var card = AppHost.Get<UpdateCardViewModel>();
        fake.Set(s => s with { Status = UpdateStatus.Available, Offer = FakeUpdateService.Offer("1.3.1201-preview.2") });
        Assert.True(card.IsOpen);
        Assert.Equal(UpdateCardState.Available, card.State);
        Assert.Contains("1.3.1201-preview.2", card.Title);
        Assert.True(card.HasNotes);
        Assert.True(card.ShowDownload && card.ShowSkip && card.ShowLater);
        Assert.False(card.ShowRestart || card.ShowCancel || card.ShowOpenPage);

        card.DownloadCommand.Execute(null);
        card.SkipCommand.Execute(null);
        Assert.Equal(["download", "skip"], fake.Calls);
    }

    [AvaloniaFact]
    public void ReleaseNotesAreAskedForInTheApp()
    {
        var fake = UseFake();
        var card = AppHost.Get<UpdateCardViewModel>();
        var asked = 0;
        card.NotesRequested += (_, _) => asked++;
        fake.Set(s => s with { Status = UpdateStatus.Available, Offer = FakeUpdateService.Offer("1.3.1201", notes: "### New\n\n- a thing") });
        card.OpenNotesCommand.Execute(null);
        Assert.Equal(1, asked);
    }

    [AvaloniaFact]
    public void TheNotesDialogRendersMarkdown()
    {
        var view = new UpdateNotesView("### New\n\n- **a** thing");
        var markdown = view.FindControl<TKCode.Arc.Markdown>("Notes");
        Assert.NotNull(markdown);
        Assert.Equal("### New\n\n- **a** thing", markdown.Text);
    }

    [AvaloniaFact]
    public void TheDownloadShowsItsProgress()
    {
        var fake = UseFake();
        var card = AppHost.Get<UpdateCardViewModel>();
        fake.Set(s => s with
        {
            Status = UpdateStatus.Downloading,
            Offer = FakeUpdateService.Offer("1.3.1201"),
            Progress = new DownloadProgress(40_000_000, 64_000_000, 4_400_000),
        });
        Assert.True(card.IsDownloading);
        Assert.Equal(62.5, card.ProgressValue);
        Assert.Contains("38.1", card.ProgressText);
        Assert.Contains("61.0", card.ProgressText);
        Assert.Contains("4.2 MB/s", card.SpeedText);
        Assert.True(card.ShowCancel);
        card.CancelCommand.Execute(null);
        Assert.Equal(["cancel"], fake.Calls);
    }

    [AvaloniaFact]
    public void ClosingHidesTheStateUntilTheNextOne()
    {
        var fake = UseFake();
        var card = AppHost.Get<UpdateCardViewModel>();
        var offer = FakeUpdateService.Offer("1.3.1201");
        fake.Set(s => s with { Status = UpdateStatus.Downloading, Offer = offer, Progress = new DownloadProgress(1, 64_000_000, 0) });
        card.CloseCommand.Execute(null);
        Assert.False(card.IsOpen);
        // More progress: still closed.
        fake.Set(s => s with { Progress = new DownloadProgress(2_000_000, 64_000_000, 1_000_000) });
        Assert.False(card.IsOpen);
        // Downloaded: shown again.
        fake.Set(s => s with { Status = UpdateStatus.Ready, Progress = null });
        Assert.True(card.IsOpen);
        Assert.True(card.ShowRestart && card.ShowLater);
        card.LaterCommand.Execute(null);
        Assert.False(card.IsOpen);
    }

    [AvaloniaFact]
    public void AnInstallThatCannotUpdateItselfOffersTheDownloadPage()
    {
        var fake = UseFake();
        var card = AppHost.Get<UpdateCardViewModel>();
        fake.Set(s => s with { Status = UpdateStatus.Available, InstallKind = InstallKind.Portable, Offer = FakeUpdateService.Offer("1.3.1201") });
        Assert.Equal(UpdateCardState.Unsupported, card.State);
        Assert.True(card.ShowOpenPage);
        Assert.False(card.ShowDownload);
    }

    [AvaloniaFact]
    public void QuietFailuresStayOutOfTheWay()
    {
        var fake = UseFake();
        var card = AppHost.Get<UpdateCardViewModel>();
        fake.Set(s => s with { Status = UpdateStatus.Failed, Error = UpdateErrorKind.Network, IsQuietError = true });
        Assert.False(card.IsOpen);
        fake.Set(s => s with { Status = UpdateStatus.Failed, Error = UpdateErrorKind.Integrity, IsQuietError = false, Offer = FakeUpdateService.Offer("1.3.1201") });
        Assert.True(card.IsOpen);
        Assert.True(card.IsFailed);
        Assert.True(card.ShowRetry);
        fake.Set(s => s with { Error = UpdateErrorKind.Install, ErrorDetail = "setup exited with 5" });
        Assert.False(card.ShowRetry);
        Assert.Contains("setup exited with 5", card.Detail);
    }

    [AvaloniaFact]
    public async Task RestartQuitsThroughTheNormalPathAndACancelledQuitForgetsIt()
    {
        var fake = UseFake();
        var terminals = AppHost.Get<FakeTerminalConnectionFactory>();
        terminals.Foreground = "vim"; // a running program: quitting asks first
        var window = new MainWindow();
        window.Show();
        AppHost.Get<Octopus.App.Services.IProjectService>().AddProject(Directory.CreateTempSubdirectory("octopus-update-").FullName);
        AppHost.Get<Octopus.App.Services.IItemOpener>().OpenTerminal();
        await PumpUntil(() => terminals.Connections.Count > 0);
        var card = AppHost.Get<UpdateCardViewModel>();
        fake.Set(s => s with { Status = UpdateStatus.Ready, Offer = FakeUpdateService.Offer("1.3.1201") });
        var dialogs = AppHost.Get<FakeDialogService>();

        dialogs.Answer = false;
        card.RestartCommand.Execute(null);
        await PumpUntil(() => dialogs.Asked == 1);
        await PumpUntil(() => !fake.RelaunchAfterInstall);
        Assert.True(window.IsVisible);

        dialogs.Answer = true;
        card.RestartCommand.Execute(null);
        await PumpUntil(() => !window.IsVisible);
        Assert.True(fake.RelaunchAfterInstall);
    }

    private static async Task PumpUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(20);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
        Assert.True(condition(), "the condition did not become true in time");
    }

    [AvaloniaFact]
    public void TheCardSitsAtTheBottomRightOfTheWindow()
    {
        var fake = UseFake();
        var window = new MainWindow();
        window.Show();
        fake.Set(s => s with { Status = UpdateStatus.Ready, Offer = FakeUpdateService.Offer("1.3.1201") });
        Frames.Pump();
        var view = window.GetVisualDescendants().OfType<UpdateCardView>().Single();
        Assert.True(view.IsVisible);
        var corner = view.TranslatePoint(new Avalonia.Point(view.Bounds.Width, view.Bounds.Height), window)!.Value;
        Assert.Equal(window.Bounds.Width - 24, corner.X, 1);
        Assert.Equal(window.Bounds.Height - 24, corner.Y, 1);
        // Toasts move above the card.
        Assert.Equal(view.Bounds.Height + 12, ToastHost.GetStack(view)!.Margin.Bottom, 1);
        fake.Set(s => s with { Status = UpdateStatus.UpToDate, Offer = null });
        Frames.Pump();
        Assert.Equal(0, ToastHost.GetStack(view)!.Margin.Bottom);
        window.Close();
    }

    [AvaloniaFact]
    public void TheSettingsGroupSavesTheChoices()
    {
        var fake = UseFake();
        var settings = AppHost.Get<ISettingsStore>();
        var general = AppHost.Get<GeneralSettingsViewModel>();
        var updates = general.Updates;
        Assert.Equal("auto", updates.Mode);
        Assert.Equal("follow", updates.Channel);
        Assert.Equal("1.2.1112", updates.VersionText);

        updates.Mode = "notify";
        updates.Channel = "preview";
        Assert.Equal("notify", settings.Current.Updates.Mode);
        Assert.Equal("preview", settings.Current.Updates.Channel);
        updates.Channel = "follow";
        Assert.Null(settings.Current.Updates.Channel);
        updates.Mode = "nonsense";
        Assert.Equal("notify", settings.Current.Updates.Mode);

        updates.CheckCommand.Execute(null);
        Assert.Equal(["check:True"], fake.Calls);

        fake.Set(s => s with { Status = UpdateStatus.Ready, Offer = FakeUpdateService.Offer("1.3.1201") });
        Assert.True(updates.IsReady);
        Assert.Contains("1.3.1201", updates.StatusText);
        fake.Set(s => s with { Status = UpdateStatus.UpToDate, InstallKind = InstallKind.Portable, LastCheckedAt = DateTimeOffset.Now.AddMinutes(-3) });
        Assert.True(updates.HasInstallNote);
        Assert.Contains("3", updates.StatusText);
        updates.Dispose();
    }
}
