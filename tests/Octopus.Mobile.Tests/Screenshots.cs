using Avalonia.Headless.XUnit;
using Octopus.Mobile.Account;
using Octopus.Mobile.Hosts;
using Octopus.Mobile.Terminals;
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
        }

        using (var phone = new Phone())
        {
            // A long delay: the pairing page stays for the picture.
            var workspace = new Octopus.Mobile.Demo.DemoWorkspace { PairingDelay = TimeSpan.FromMinutes(1) };
            var shell = new Octopus.Mobile.Shell.MobileShell(workspace, phone.Account);
            phone.Window.Content = shell;
            shell.ShowPairing(workspace.Hosts.Single(host => !host.IsPaired));
            Phone.Settle();
            phone.Save("7-pairing" + suffix);
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
