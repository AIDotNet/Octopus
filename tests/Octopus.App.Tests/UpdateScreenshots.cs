using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Octopus.App.Composition;
using Octopus.App.Settings;
using Octopus.App.Shell;
using Octopus.Platform.Updates;
using Octopus.Updates;
using TKCode.Arc;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>
/// Renders the update card in each state and the settings group, light and dark, to <c>bin/shots</c> (design/10 §10),
/// for a person to look at.
/// </summary>
public class UpdateScreenshots
{
    private static readonly string s_dir = Path.Combine(AppContext.BaseDirectory, "shots");

    [AvaloniaFact]
    public void Render_the_update_card()
    {
        Directory.CreateDirectory(s_dir);
        var offer = FakeUpdateService.Offer("1.3.1201-preview.2");
        (string Name, Func<UpdateSnapshot, UpdateSnapshot> State)[] states =
        [
            ("available", s => s with { Status = UpdateStatus.Available, Offer = offer }),
            ("downloading", s => s with { Status = UpdateStatus.Downloading, Offer = offer, Progress = new DownloadProgress(40_000_000, 64_000_000, 4_400_000) }),
            ("ready", s => s with { Status = UpdateStatus.Ready, Offer = offer }),
            ("portable", s => s with { Status = UpdateStatus.Available, Offer = offer, InstallKind = InstallKind.Portable }),
            ("failed", s => s with { Status = UpdateStatus.Failed, Offer = offer, Error = UpdateErrorKind.Integrity }),
        ];
        var original = AppHost.ConfigureOverrides;
        try
        {
            foreach (var dark in new[] { false, true })
            {
                ArcTheme.Apply(t => t.Appearance(dark ? ArcAppearance.Dark : ArcAppearance.Light));
                var theme = dark ? "dark" : "light";
                foreach (var (name, state) in states)
                {
                    var fake = Use(original);
                    var window = new MainWindow();
                    window.Show();
                    fake.Set(state);
                    Frames.Until(() => false, milliseconds: 600); // the progress bar springs to its value
                    Frames.Save(window, Path.Combine(s_dir, $"update-{name}-{theme}.png"));
                    window.Close();
                }

                var settingsFake = Use(original);
                settingsFake.Set(s => s with { Status = UpdateStatus.Ready, Offer = offer, LastCheckedAt = DateTimeOffset.Now.AddMinutes(-3) });
                var view = new SettingsView { InitialSection = "general" };
                var settingsWindow = new Window { Width = 1280, Height = 800, Content = view };
                settingsWindow.Show();
                Frames.Until(() => false, milliseconds: 300);
                Frames.Save(settingsWindow, Path.Combine(s_dir, $"update-settings-{theme}.png"));
                settingsWindow.Close();
            }
        }
        finally
        {
            AppHost.ConfigureOverrides = original;
            ArcTheme.Apply(t => t.Appearance(ArcAppearance.Light));
        }
    }

    private static FakeUpdateService Use(Action<IServiceCollection>? original)
    {
        var fake = new FakeUpdateService();
        AppHost.ConfigureOverrides = services =>
        {
            original?.Invoke(services);
            services.Replace(ServiceDescriptor.Singleton<IUpdateService>(fake));
        };
        AppHost.Reset();
        AppHost.Get<Octopus.App.Appearance.ThemeService>().Track();
        return fake;
    }
}
