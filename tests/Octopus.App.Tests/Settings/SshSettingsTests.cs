using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Octopus.App.Composition;
using Octopus.App.Settings;
using Octopus.App.Services;
using Octopus.App.Settings.Ssh;
using Octopus.Platform;
using Octopus.Ssh;
using TKCode.Arc;
using Xunit;

namespace Octopus.App.Tests.Settings;

/// <summary>The SSH page lists the hosts of ~/.ssh/config and reads the file again on a rescan.</summary>
public class SshSettingsTests
{
    private static (string Home, SshHostCatalog Catalog) HomeWith(string? config)
    {
        var home = Directory.CreateTempSubdirectory("octopus-ssh-page-").FullName;
        if (config is not null)
        {
            Directory.CreateDirectory(Path.Combine(home, ".ssh"));
            File.WriteAllText(Path.Combine(home, ".ssh", "config"), config);
        }
        return (home, new SshHostCatalog(home));
    }

    [AvaloniaFact]
    public void Each_host_shows_where_it_connects_and_what_else_the_file_says()
    {
        var (_, catalog) = HomeWith("""
            Host web
              HostName web.example
              User ops
              Port 2222
              ProxyJump bastion
              IdentityFile ~/.ssh/id_ops
            Host db
            """);

        var page = new SshSettingsViewModel(catalog, AppHost.Get<IFileOpener>(), AppHost.Get<IDialogService>());

        Assert.Equal(["web", "db"], page.Hosts.Select(h => h.Alias));
        Assert.Equal("ops@web.example:2222", page.Hosts[0].Address);
        Assert.True(page.Hosts[0].HasDetails);
        Assert.Contains("bastion", page.Hosts[0].Details);
        Assert.Contains("~/.ssh/id_ops", page.Hosts[0].Details);
        Assert.False(page.Hosts[1].HasAddress);
        Assert.False(page.Hosts[1].HasDetails);
        Assert.True(page.HasHosts);
        page.Dispose();
    }

    [AvaloniaFact]
    public void Rescan_picks_up_a_host_added_to_the_file()
    {
        var (home, catalog) = HomeWith("Host one\n");
        var page = new SshSettingsViewModel(catalog, AppHost.Get<IFileOpener>(), AppHost.Get<IDialogService>());
        Assert.Single(page.Hosts);

        File.AppendAllText(Path.Combine(home, ".ssh", "config"), "Host two\n");
        page.RescanCommand.Execute(null);

        Assert.Equal(["one", "two"], page.Hosts.Select(h => h.Alias));
        page.Dispose();
    }

    [AvaloniaFact]
    public void Without_a_file_the_page_says_so_and_offers_no_host()
    {
        var (_, catalog) = HomeWith(null);
        var page = new SshSettingsViewModel(catalog, AppHost.Get<IFileOpener>(), AppHost.Get<IDialogService>());

        Assert.False(page.HasHosts);
        Assert.False(page.ConfigExists);
        Assert.EndsWith(Path.Combine(".ssh", "config"), page.ConfigPath);
        page.Dispose();
    }

    [AvaloniaFact]
    public async Task Adding_a_host_writes_it_to_the_file_and_lists_it()
    {
        var (home, catalog) = HomeWith("Host one\n");
        var dialogs = (FakeDialogService)AppHost.Get<IDialogService>();
        var page = new SshSettingsViewModel(catalog, AppHost.Get<IFileOpener>(), dialogs);
        dialogs.NewSshHost = new SshHostDraft("two", HostName: "10.0.0.2", User: "ops");
        try
        {
            await page.AddHostCommand.ExecuteAsync(null);
        }
        finally
        {
            dialogs.NewSshHost = null;
        }

        Assert.Equal(["one", "two"], page.Hosts.Select(h => h.Alias));
        Assert.Contains("Host two\n    HostName 10.0.0.2\n    User ops\n", File.ReadAllText(Path.Combine(home, ".ssh", "config")));
        Assert.False(page.HasError);
        page.Dispose();
    }

    [AvaloniaFact]
    public async Task A_dismissed_dialog_adds_nothing()
    {
        var (home, catalog) = HomeWith("Host one\n");
        var dialogs = (FakeDialogService)AppHost.Get<IDialogService>();
        var page = new SshSettingsViewModel(catalog, AppHost.Get<IFileOpener>(), dialogs);
        dialogs.NewSshHost = null;

        await page.AddHostCommand.ExecuteAsync(null);

        Assert.Equal("Host one\n", File.ReadAllText(Path.Combine(home, ".ssh", "config")));
        Assert.Single(page.Hosts);
        page.Dispose();
    }

    [AvaloniaFact]
    public async Task A_write_that_fails_is_said_on_the_page_and_nothing_is_listed()
    {
        var home = Directory.CreateTempSubdirectory("octopus-ssh-page-fail-").FullName;
        File.WriteAllText(Path.Combine(home, ".ssh"), "not a folder");
        var dialogs = (FakeDialogService)AppHost.Get<IDialogService>();
        var page = new SshSettingsViewModel(new SshHostCatalog(home), AppHost.Get<IFileOpener>(), dialogs);
        dialogs.NewSshHost = new SshHostDraft("box");
        try
        {
            await page.AddHostCommand.ExecuteAsync(null);
        }
        finally
        {
            dialogs.NewSshHost = null;
        }

        Assert.True(page.HasError);
        Assert.NotNull(page.ErrorText);
        Assert.False(page.HasHosts);
        page.Dispose();
    }

    [AvaloniaFact]
    public void The_page_opens_in_the_settings_with_an_add_button_bound_to_the_command()
    {
        var view = new SettingsView { InitialSection = "ssh" };
        var window = new Window { Width = 1280, Height = 800, Content = view };
        window.Show();
        Frames.Pump();

        var page = Assert.IsType<SshSettingsViewModel>(((SettingsViewModel)view.DataContext!).Page);
        var add = window.GetVisualDescendants().OfType<TKCode.Arc.Button>().Single(b => ReferenceEquals(b.Command, page.AddHostCommand));
        Assert.True(add.IsVisible);
        window.Close();
    }
}
