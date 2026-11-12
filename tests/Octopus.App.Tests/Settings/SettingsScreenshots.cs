using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Octopus.Agents;
using Octopus.App.Composition;
using Octopus.App.Settings;
using Octopus.App.Settings.Agents;
using TKCode.Arc;
using Xunit;

namespace Octopus.App.Tests.Settings;

/// <summary>
/// Renders the settings page with Skia (TestApp draws for real) and saves PNGs to <c>bin/shots</c>, so the result can be
/// looked at. The host has a scanner that "found" four agents, so the installed group has content.
/// </summary>
public class SettingsScreenshots
{
    private static readonly string s_dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "shots"));

    [AvaloniaFact]
    public void Render_the_settings_pages()
    {
        Directory.CreateDirectory(s_dir);
        var original = AppHost.ConfigureOverrides;
        AppHost.ConfigureOverrides = services =>
        {
            original?.Invoke(services);
            services.Replace(ServiceDescriptor.Singleton<IAgentScanner>(new SettingsTestSupport.FakeScanner("claude", "codex", "opencode", "cursor")));
        };
        try
        {
            foreach (var dark in new[] { false, true })
            {
                ArcTheme.Apply(t => t.Appearance(dark ? ArcAppearance.Dark : ArcAppearance.Light));
                var theme = dark ? "dark" : "light";
                AppHost.Reset();
                Shot("agents-claude", theme, "agents", rows => Expand(rows, "claude"));
                Shot("agents-opencode", theme, "agents", rows => Expand(rows, "opencode"));
                Shot("agents-collapsed", theme, "agents", rows => Expand(rows));
                Shot("agents-full-access", theme, "agents", rows =>
                {
                    AppHost.Get<Octopus.Storage.ISettingsStore>().Update(s => s.Agents.DefaultPermission = "full");
                    Expand(rows, "codex");
                });
                Shot("general", theme, "general", null);
                Shot("terminal", theme, "terminal", null);
                Shot("appearance", theme, "appearance", null);
                Shot("shortcuts", theme, "shortcuts", null);
                Shot("agents-override", theme, "agents", rows =>
                {
                    Expand(rows, "claude");
                    var claude = rows.First(r => r.Id == "claude");
                    claude.Field<CommandFieldViewModel>("command").Text = "rec --profile work";
                    claude.FlushPending();
                    for (var i = 0; i < 20; i++)
                    {
                        Thread.Sleep(30);
                        Pump();
                    }
                });
            }
        }
        finally
        {
            AppHost.ConfigureOverrides = original;
            AppHost.Reset();
        }
    }

    private static void Expand(IEnumerable<AgentRowViewModel> rows, string? id = null)
    {
        foreach (var row in rows)
        {
            row.IsExpanded = row.Id == id;
        }
    }

    private static void Shot(string name, string theme, string section, Action<IEnumerable<AgentRowViewModel>>? arrange)
    {
        var view = new SettingsView { InitialSection = section };
        var window = new Window { Width = 1280, Height = 800, Content = view };
        window.Show();
        Pump();
        if (arrange is not null && ((SettingsViewModel)view.DataContext!).Page is AgentsSettingsViewModel agents)
        {
            arrange(agents.Installed);
        }
        Pump();
        Frames.Save(window, Path.Combine(s_dir, $"settings-{name}-{theme}.png"));
        window.Close();
    }

    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }
}
