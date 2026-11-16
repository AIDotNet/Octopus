using Avalonia.Headless.XUnit;
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

        using (var phone = new Phone())
        {
            var account = new Octopus.Mobile.Demo.DemoAccount { SignInDelay = TimeSpan.FromMinutes(1) };
            account.SignOut();
            var shell = new Octopus.Mobile.Shell.MobileShell(phone.Workspace, account);
            phone.Window.Content = shell;
            Phone.Settle(300);
            // First the service's address, then the code to confirm in the browser.
            phone.Save("8-sign-in" + suffix);
            var signIn = (Octopus.Mobile.Account.SignInViewModel)shell.Top!.DataContext!;
            signIn.StartCommand.Execute(null);
            Assert.True(Phone.Until(() => signIn.HasCode));
            Phone.Settle(300);
            phone.Save("9-sign-in-code" + suffix);
        }
    }
}
