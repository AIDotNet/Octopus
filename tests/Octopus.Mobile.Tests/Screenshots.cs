using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Octopus.Mobile.Account;
using Octopus.Mobile.Conversations;
using Octopus.Mobile.Files;
using Octopus.Mobile.Hosts;
using Octopus.Mobile.Terminals;
using Octopus.Mobile.Workspace;
using TKCode.Arc;
using Xunit;

namespace Octopus.Mobile.Tests;

/// <summary>
/// Renders each page with Skia on the headless platform and saves PNGs to <c>bin/shots</c>, light and dark. The files
/// are looked at by a person: there is nothing to assert but that rendering works.
/// </summary>
public class Screenshots
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_every_page(bool dark)
    {
        ArcTheme.Apply(theme => theme.Appearance(dark ? ArcAppearance.Dark : ArcAppearance.Light));
        var suffix = dark ? "-dark" : "-light";

        using (var phone = new Phone())
        {
            Phone.Settle(300);
            phone.Save("1-hosts" + suffix);

            var mac = phone.Workspace.Hosts[0];
            phone.Shell.ShowHost(mac);
            Phone.Settle();
            phone.Save("2-host" + suffix);

            var host = (HostViewModel)phone.Top<HostPage>().DataContext!;
            host.OpenNewSessionCommand.Execute(null);
            Phone.Settle();
            phone.Save("3-new" + suffix);
            host.NewSession.IsOpen = false;
            Phone.Settle();

            // An agent that waits: looking only, then fitted with the composer and its choices as buttons.
            phone.Shell.ShowSession(mac.Projects[0].Sessions[0]);
            Assert.True(Phone.Until(() => phone.Top<TerminalPage>().ReadScreenText().Trim().Length > 0));
            Phone.Settle();
            phone.Save("4-terminal-viewing" + suffix);
            var agent = (TerminalViewModel)phone.Top<TerminalPage>().DataContext!;
            agent.FitCommand.Execute(null);
            agent.RefreshQuickReplies();
            Phone.Settle(300);
            phone.Save("5-terminal-composer" + suffix);
            phone.Shell.Back();
            Phone.Settle();

            // A shell: the key row, with Ctrl armed.
            phone.Shell.ShowSession(mac.Projects[0].Sessions[2]);
            Assert.True(Phone.Until(() => phone.Top<TerminalPage>().ReadScreenText().Trim().Length > 0));
            var shell = (TerminalViewModel)phone.Top<TerminalPage>().DataContext!;
            shell.FitCommand.Execute(null);
            shell.Keys.Single(key => key.IsControl).PressCommand.Execute(null);
            Phone.Settle();
            phone.Save("6-terminal-keys" + suffix);

            // The same with the keyboard up: the key row sits on it, the terminal keeps what is left.
            var keyboard = new TKCode.Arc.Mobile.KeyboardInsetSource();
            TKCode.Arc.Mobile.Page.SetKeyboardInsetSource(phone.Shell, keyboard);
            keyboard.Set(300, TimeSpan.Zero);
            Phone.Settle(600);
            phone.Save("6b-terminal-keyboard" + suffix);
            keyboard.Set(0, TimeSpan.Zero);
            Phone.Settle(400);

            // The "⋯" menu.
            shell.OpenMenuCommand.Execute(null);
            Phone.Settle();
            phone.Save("6c-terminal-menu" + suffix);
            phone.Shell.Back();
            Phone.Settle();

            var hostPage = phone.Top<HostPage>();
            var hostModel = (HostViewModel)hostPage.DataContext!;
            // "+" (its menu closes as a row is chosen) and the folder picker.
            hostModel.OpenAddMenuCommand.Execute(null);
            Phone.Settle();
            phone.Save("2d-host-add-menu" + suffix);
            hostModel.IsAddMenuOpen = false;
            hostModel.NewProjectCommand.Execute(null);
            Phone.Settle();
            var picker = (FolderPickerViewModel)phone.Top<FolderPickerPage>().DataContext!;
            picker.Folders.Single(folder => folder.Name == "code").OpenCommand.Execute(null);
            Phone.Settle();
            phone.Save("2e-folder-picker" + suffix);
            phone.Shell.Back();
            Phone.Settle();

            // A held row's menu, then the session's details.
            hostModel.ShowMenu(mac.Projects[0].Sessions[1]);
            Phone.Settle();
            phone.Save("2b-host-row-menu" + suffix);
            hostModel.Actions.ShowDetailsCommand.Execute(null);
            Phone.Settle();
            phone.Save("2c-session-details" + suffix);
            phone.Shell.Back();
            Phone.Settle();


            // A GUI conversation on the other computer: first on an older host (details + notice), then the live conversation page.
            var station = phone.Workspace.Hosts.Single(h => h.Id == "station");
            var gui = station.Projects[0].Sessions.Single(session => session.IsGui);
            phone.Shell.ShowHost(station);
            Phone.Settle();
            phone.Save("2f-host-gui-row" + suffix);

            phone.Workspace.ConversationsSupported = false;
            phone.Shell.ShowSession(gui);
            Phone.Settle();
            phone.Save("2g-gui-details" + suffix);
            phone.Shell.Back();
            Phone.Settle();

            phone.Workspace.ConversationsSupported = true;
            phone.Shell.ShowSession(gui);
            Phone.Settle(400);
            var convPage = phone.Top<ConversationPage>();
            var convVm = (ConversationViewModel)convPage.DataContext!;
            // Expand the latest edit tool so its diff is visible in the screenshot.
            var lastAssistant = convVm.Entries.OfType<ConversationAssistantEntry>().Last();
            lastAssistant.Tools[0].IsExpanded = true;
            Phone.Settle(300);
            phone.Save("17-conversation" + suffix);

            // Question card with a selected choice.
            phone.Workspace.Conversations[gui.Id].Ask();
            Phone.Settle(300);
            var question = convVm.Entries.OfType<ConversationQuestionEntry>().Last();
            question.Fields[0].Choices[0].IsSelected = true;
            question.Fields[1].Choices[0].IsSelected = true;
            Phone.Settle(300);
            phone.Save("17b-conversation-question" + suffix);

            // Conversation with keyboard up: the composer stays directly above the keyboard.
            keyboard.Set(300, TimeSpan.Zero);
            Phone.Settle(400);
            phone.Save("17c-conversation-keyboard" + suffix);
            keyboard.Set(0, TimeSpan.Zero);
            Phone.Settle(300);

            // Files, file viewer, changes and diff (Route 2: Files/).
            var octopus = mac.Projects[0];
            phone.Shell.ShowFiles(octopus);
            var files = (FilesViewModel)phone.Top<FilesPage>().DataContext!;
            Assert.True(Phone.Until(() => !files.IsLoading && files.Entries.Count > 0));
            files.Entries.Single(entry => entry.Name == "src").OpenCommand.Execute(null);
            Assert.True(Phone.Until(() => !files.IsLoading && files.Crumbs.Count == 2));
            Phone.Settle();
            phone.Save("18-files" + suffix);

            files.Entries.Single(entry => entry.Name == "Auth").OpenCommand.Execute(null);
            Assert.True(Phone.Until(() => !files.IsLoading && files.Crumbs.Count == 3));
            files.Entries.Single(entry => entry.Name == "TokenIssuer.cs").OpenCommand.Execute(null);
            var viewer = (FileViewerViewModel)phone.Top<FileViewerPage>().DataContext!;
            Assert.True(Phone.Until(() => !viewer.IsLoading && viewer.Lines.Count > 0));
            Phone.Settle();
            phone.Save("19-file-viewer" + suffix);
            phone.Shell.Back();
            phone.Shell.Back();
            Phone.Settle();

            phone.Shell.ShowChanges(octopus);
            var changes = (ChangesViewModel)phone.Top<ChangesPage>().DataContext!;
            Assert.True(Phone.Until(() => !changes.IsLoading && changes.Rows.Count > 0));
            Phone.Settle();
            phone.Save("20-changes" + suffix);

            changes.Rows.Single(row => row.Name == "TokenService.cs").OpenCommand.Execute(null);
            var diff = (DiffViewModel)phone.Top<DiffPage>().DataContext!;
            Assert.True(Phone.Until(() => !diff.IsLoading && diff.Lines.Count > 0));
            Phone.Settle();
            phone.Save("21-diff" + suffix);
        }

        using (var phone = new Phone())
        {
            // A long delay: the pairing page stays for the picture.
            var workspace = new Octopus.Mobile.Demo.DemoWorkspace { PairingDelay = TimeSpan.FromMinutes(1) };
            var shell = new Octopus.Mobile.Shell.MobileShell(workspace, phone.Account);
            phone.Window.Content = shell;
            shell.ShowPairing(workspace.Hosts.Single(host => host.Id == "linux"));
            Phone.Settle();
            phone.Save("7-pairing" + suffix);
        }

        // Signed in: the account's computers on the Computers tab (paired, with the lock, to pair, offline), the
        // folded phones, then the access password page empty, filled and turned away.
        using (var phone = new Phone())
        {
            var hosts = (HostsViewModel)phone.Top<HostsPage>().DataContext!;
            hosts.SelectedTab = HostsViewModel.TabHosts;
            Phone.Settle(300);
            phone.Save("22-hosts-computers" + suffix);
            hosts.IsOtherDevicesOpen = true;
            Phone.Settle(300);
            phone.Top<HostsPage>().FindControl<Control>("OtherDevicesList")!.BringIntoView();
            Phone.Settle(300);
            phone.Save("22b-hosts-other-devices" + suffix);

            phone.Shell.ShowAccessPassword(phone.Workspace.Hosts.Single(host => host.Id == "desk"));
            Phone.Settle();
            phone.Save("23-access-password" + suffix);
            var access = (AccessPasswordViewModel)phone.Top<AccessPasswordPage>().DataContext!;
            access.Password = "demo-2346";
            access.VerifyCommand.Execute(null);
            Assert.True(Phone.Until(() => access.HasError));
            Phone.Settle(300);
            phone.Save("23b-access-password-error" + suffix);

            // The keyboard up: the field and the button stay above it.
            var keyboard = new TKCode.Arc.Mobile.KeyboardInsetSource();
            TKCode.Arc.Mobile.Page.SetKeyboardInsetSource(phone.Shell, keyboard);
            phone.Top<AccessPasswordPage>().Field.Focus();
            keyboard.Set(300, TimeSpan.Zero);
            Phone.Settle(600);
            phone.Save("23c-access-password-keyboard" + suffix);
        }

        // Signed out and nothing chosen: the welcome page, then the code to confirm in the browser.
        using (var phone = new Phone(signedIn: false, signInDelay: TimeSpan.FromMinutes(1)))
        {
            Phone.Settle(300);
            phone.Save("8-welcome" + suffix);
            var welcome = (WelcomeViewModel)phone.Top<WelcomePage>().DataContext!;
            welcome.SignInCommand.Execute(null);
            Assert.True(Phone.Until(() => welcome.SignIn.HasCode));
            Phone.Settle(300);
            phone.Save("9-sign-in-code" + suffix);
            welcome.SignIn.CancelCommand.Execute(null);

            // A service of the person's own: its address.
            welcome.UseCustomCommand.Execute(null);
            Phone.Settle();
            ((CustomServerViewModel)phone.Top<CustomServerPage>().DataContext!).Server = "octopus.example.com";
            Phone.Settle(300);
            phone.Save("10-custom-server" + suffix);
            phone.Shell.Back();
            Phone.Settle();

            // Without an account: the first computer, by its address.
            phone.Shell.ShowAddHost();
            Phone.Settle();
            phone.Top<AddHostPage>();
            phone.Save("11-add-host" + suffix);
        }

        // A build that knows no official service: "Sign in" asks for the address, so there is no row for that.
        using (var phone = new Phone(signedIn: false, official: false))
        {
            Phone.Settle(300);
            phone.Save("12-welcome-no-official" + suffix);
        }

        // Used without an account: the computers the phone paired with itself, and what the account entry opens.
        using (var phone = new Phone(localOnly: true))
        {
            Phone.Settle(300);
            phone.Save("13-hosts-local" + suffix);
            var hosts = (HostsViewModel)phone.Top<HostsPage>().DataContext!;
            hosts.OpenAccountCommand.Execute(null);
            Phone.Settle();
            phone.Save("14-hosts-local-sheet" + suffix);
            hosts.IsLocalOpen = false;
            Phone.Settle();

            // "Sign in" from there: the welcome page with a way back.
            hosts.SignInCommand.Execute(null);
            Phone.Settle();
            phone.Save("15-sign-in-from-local" + suffix);
            phone.Shell.Back();
            Phone.Settle();

            phone.Workspace.Hosts.Clear();
            Phone.Settle(300);
            phone.Save("16-hosts-local-empty" + suffix);
        }
    }
}
