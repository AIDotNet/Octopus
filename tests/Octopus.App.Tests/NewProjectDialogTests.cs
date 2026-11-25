using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Octopus.App.Composition;
using Octopus.App.Dialogs;
using Octopus.App.Services;
using Octopus.App.Shell;
using Octopus.Ssh;
using TKCode.Arc;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>New project: a dialog first (folder and name), not the system folder picker straight away.</summary>
public class NewProjectDialogTests
{
    [AvaloniaFact]
    public void The_name_follows_the_folder_until_it_is_edited()
    {
        var form = new NewProjectView(null, null);
        var path = form.FindControl<Input>("PathInput")!;
        var name = form.FindControl<Input>("NameInput")!;

        path.Text = "/tmp/alpha";
        Assert.Equal("alpha", name.Text);
        path.Text = "/tmp/beta/";
        Assert.Equal("beta", name.Text);

        name.Text = "My project";
        path.Text = "/tmp/gamma";
        Assert.Equal("My project", form.ProjectName);
        Assert.Equal("/tmp/gamma", form.FolderPath);
    }

    [AvaloniaFact]
    public void A_folder_must_be_given_and_must_exist()
    {
        var dir = Directory.CreateTempSubdirectory("octopus-new-").FullName;
        Assert.NotNull(NewProjectView.Validate(""));
        Assert.NotNull(NewProjectView.Validate(Path.Combine(dir, "missing")));
        Assert.Null(NewProjectView.Validate(dir));
        Assert.Null(NewProjectView.Validate("~"));
    }

    [AvaloniaFact]
    public async Task Adding_a_project_asks_the_dialog_and_uses_its_folder_and_name()
    {
        var dir = Directory.CreateTempSubdirectory("octopus-new-").FullName;
        var dialogs = (FakeDialogService)AppHost.Get<IDialogService>();
        var opener = AppHost.Get<IItemOpener>();

        dialogs.NewProject = null;
        Assert.Null(await opener.PickAndAddProjectAsync());
        Assert.Empty(AppHost.Get<IProjectService>().Projects);

        dialogs.NewProject = new NewProjectChoice(dir, "Named");
        var project = await opener.PickAndAddProjectAsync();
        Assert.Equal("Named", project!.Name);

        dialogs.NewProject = new NewProjectChoice(Directory.CreateTempSubdirectory("octopus-new-").FullName, "");
        project = await opener.PickAndAddProjectAsync();
        Assert.StartsWith("octopus-new-", project!.Name);
    }

    [AvaloniaFact]
    public void A_folder_on_a_host_is_not_looked_for_on_this_computer()
    {
        Assert.NotNull(NewProjectView.Validate("", "prod"));
        Assert.Null(NewProjectView.Validate("~/code/app", "prod"));
        Assert.Null(NewProjectView.Validate("/no/such/folder/on/the/host", "prod"));
        Assert.NotNull(NewProjectView.Validate("/no/such/folder/on/the/host"));
    }

    [AvaloniaFact]
    public void The_location_is_offered_only_with_hosts_and_a_host_makes_the_folder_remote()
    {
        var none = new NewProjectView(null, null, []);
        Assert.False(none.FindControl<Select>("LocationSelect")!.IsVisible);
        Assert.Null(none.SshHost);

        var hosts = new[] { new SshHost("prod", "10.0.0.5", "ops", 2222, null, null) };
        var form = new NewProjectView(null, null, hosts);
        var location = form.FindControl<Select>("LocationSelect")!;
        var browse = form.FindControl<TKCode.Arc.Button>("BrowseButton")!;
        Assert.True(location.IsVisible);
        Assert.Null(form.SshHost);
        Assert.True(browse.IsVisible);

        location.SelectedValue = "ssh:prod";
        Assert.Equal("prod", form.SshHost);
        Assert.True(browse.IsVisible);

        location.SelectedValue = "local";
        Assert.Null(form.SshHost);
        Assert.True(browse.IsVisible);
    }

    [AvaloniaFact]
    public void Windows_paths_also_suggest_only_the_last_folder_name()
    {
        var form = new NewProjectView(null, new NewProjectChoice("", "", RemoteHost: "win1"), [], [("win1", "Win")]);
        var path = form.FindControl<Input>("PathInput")!;
        var name = form.FindControl<Input>("NameInput")!;

        path.Text = @"C:\Users\token\code\Octopus";
        Assert.Equal("Octopus", name.Text);

        path.Text = @"D:\work\api\";
        Assert.Equal("api", name.Text);
    }

    [AvaloniaFact]
    public async Task In_dialog_folder_browser_picks_and_creates_folders_and_returns_to_form()
    {
        var root = Directory.CreateTempSubdirectory("octopus-browse-ui-").FullName;
        var alpha = Path.Combine(root, "alpha");
        var hidden = Path.Combine(root, ".secret");
        Directory.CreateDirectory(alpha);
        Directory.CreateDirectory(hidden);

        try
        {
            var browser = new Octopus.FileSystem.LocalFolderBrowser(
                "This computer",
                () => [new Octopus.FileSystem.FolderBrowseProject("Alpha", alpha)]);
            var form = new NewProjectView(null, new NewProjectChoice(root, ""), browserFactory: _ => browser);
            var window = new Window { Width = 640, Height = 560, Content = form };
            window.Show();
            Frames.Pump();

            Assert.False(form.IsBrowsing);
            await form.OpenBrowseAsync();
            Frames.Pump();

            Assert.True(form.IsBrowsing);
            var browserView = form.FindControl<FolderBrowserView>("BrowserView")!;
            var list = browserView.FindControl<ListBox>("FolderList")!;
            // Hidden folder is excluded by default.
            var visibleRows = list.Items.Cast<FolderBrowserRow>().ToList();
            Assert.Single(visibleRows);
            Assert.Equal("alpha", visibleRows[0].Name);
            Assert.True(visibleRows[0].IsExistingProject);

            // Create a new subfolder inline.
            browserView.FindControl<Input>("NewFolderNameBox")!.Text = "created-here";
            browserView.FindControl<Border>("NewFolderBar")!.IsVisible = true;
            await browserView.CommitNewFolderAsync();
            Frames.Pump();

            var created = Path.Combine(root, "created-here");
            Assert.True(Directory.Exists(created));
            Assert.Equal(Path.GetFullPath(created), Path.GetFullPath(browserView.SelectedPath!));

            // Confirming returns to the form and populates PathInput + suggested NameInput.
            form.ConfirmBrowse();
            Frames.Pump();

            Assert.False(form.IsBrowsing);
            Assert.Equal(Path.GetFullPath(created), Path.GetFullPath(form.FolderPath));
            Assert.Equal("created-here", form.ProjectName);
            window.Close();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
