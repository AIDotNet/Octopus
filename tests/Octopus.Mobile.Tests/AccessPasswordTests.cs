using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Octopus.Mobile.Account;
using Octopus.Mobile.Hosts;
using Octopus.Remote.Security;
using Octopus.Remote.Tests;
using TKCode.Arc.Mobile;
using Xunit;

namespace Octopus.Mobile.Tests;

/// <summary>
/// Signed in, the account's computers that are not paired yet (design/16-账号设备与访问密码.md): one that takes its
/// access password is got into with it once and then remembered; one that does not still pairs; an offline one is only
/// named; phones are listed apart and never opened. First on made-up hosts (the page itself), then on the real
/// protocol against a host service and the stand-in relay.
/// </summary>
public class AccessPasswordTests
{
    /// <summary>Long enough for both sides' key derivation (600 000 rounds of PBKDF2 each) on a slow machine.</summary>
    private const int Derivation = 20_000;

    // ---- the page, on made-up hosts ----

    [AvaloniaFact]
    public void The_field_formats_what_is_typed_and_the_button_waits_for_eight_characters()
    {
        using var phone = new Phone();
        var desk = phone.Workspace.Hosts.Single(host => host.Id == "desk");
        phone.Shell.ShowAccessPassword(desk);
        Phone.Settle(400);
        var page = phone.Top<AccessPasswordPage>();
        var model = (AccessPasswordViewModel)page.DataContext!;
        Assert.Equal("Windows 台式机", model.HostName);
        Assert.Equal("Windows 台式机", ((Navbar)page.Header!).Title);
        Assert.False(model.VerifyCommand.CanExecute(null));
        Assert.Equal("连接", model.VerifyText);

        // Typed key by key, in lower case: upper case, and a dash after the fourth, with the caret after what was typed.
        page.Field.Focus();
        phone.Window.KeyTextInput("demo");
        Assert.Equal("DEMO", page.Field.Text);
        Assert.False(model.VerifyCommand.CanExecute(null));
        phone.Window.KeyTextInput("2");
        Assert.Equal("DEMO-2", page.Field.Text);
        Assert.Equal(6, page.Field.CaretIndex);
        phone.Window.KeyTextInput("34");
        Assert.Equal("DEMO-234", page.Field.Text);
        Assert.False(model.VerifyCommand.CanExecute(null));
        phone.Window.KeyTextInput("5");
        Assert.Equal("DEMO-2345", page.Field.Text);
        Assert.Equal("DEMO-2345", model.Password);
        Assert.True(model.VerifyCommand.CanExecute(null));
        // A ninth character is not taken.
        phone.Window.KeyTextInput("6");
        Assert.Equal("DEMO-2345", page.Field.Text);
        // Erasing goes back over the dash as if it were not there.
        phone.Window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
        Assert.Equal("DEMO-234", page.Field.Text);
        Assert.False(model.VerifyCommand.CanExecute(null));

        // Pasted with spaces, dashes and in any case: the same form.
        page.Field.Text = " abcd efgh ";
        Phone.Pump();
        Assert.Equal("ABCD-EFGH", page.Field.Text);
        Assert.Equal("ABCD-EFGH", model.Password);
        page.Field.Text = "ab-cd-23-45";
        Phone.Pump();
        Assert.Equal("ABCD-2345", model.Password);
        Assert.True(model.VerifyCommand.CanExecute(null));
        Assert.Equal("ABCD-EFGH", AccessPasswordViewModel.FormatTyped("abcd_efgh!!"));
    }

    [AvaloniaFact]
    public void A_wrong_password_says_so_and_the_right_one_opens_the_computer()
    {
        using var phone = new Phone();
        var hosts = (HostsViewModel)phone.Top<HostsPage>().DataContext!;
        var row = hosts.Unpaired.Single(row => row.Name == "Windows 台式机");
        Assert.True(row.IsPasswordAction);

        // The row with the lock opens the access password page, not pairing.
        row.OpenCommand.Execute(null);
        Phone.Settle(400);
        var model = (AccessPasswordViewModel)phone.Top<AccessPasswordPage>().DataContext!;

        model.Password = "wxyz-2345";
        model.VerifyCommand.Execute(null);
        Assert.True(model.IsVerifying);
        Assert.Equal("验证中…", model.VerifyText);
        Assert.False(model.VerifyCommand.CanExecute(null));
        Assert.True(Phone.Until(() => model.HasError));
        Assert.Equal("密码不对。请对照电脑上显示的访问密码再试一次。", model.ErrorText);
        Assert.False(row.Host.IsPaired);
        // Typing again clears the old answer.
        model.Password = "demo2345";
        Assert.False(model.HasError);

        model.VerifyCommand.Execute(null);
        Assert.True(Phone.Until(() => phone.Shell.Top is HostPage), "the computer's page opens");
        Assert.Equal(2, phone.Shell.Stack!.Depth);
        Assert.Same(row.Host, ((HostViewModel)phone.Shell.Top!.DataContext!).Host);
        Assert.True(row.Host.IsPaired);
        Assert.Contains(hosts.Online, online => online.Name == "Windows 台式机");
        Assert.DoesNotContain(hosts.Unpaired, unpaired => unpaired.Name == "Windows 台式机");
    }

    [AvaloniaFact]
    public void Other_devices_are_folded_and_name_this_phone()
    {
        using var phone = new Phone();
        var page = phone.Top<HostsPage>();
        var hosts = (HostsViewModel)page.DataContext!;
        hosts.SelectedTab = HostsViewModel.TabHosts;
        Phone.Pump();

        Assert.True(hosts.HasOtherDevices);
        Assert.Equal("其他设备（2）", hosts.OtherDevicesTitle);
        Assert.Equal([("Demo 的 iPhone", true), ("Pixel 9", false)], hosts.OtherDevices.Select(device => (device.Name, device.IsCurrent)));
        Assert.Equal("iOS · 正在使用的这台手机", hosts.OtherDevices[0].Summary);
        Assert.Equal("Android · 2 天前在线", hosts.OtherDevices[1].Summary);
        // Folded: only its header row is there; a tap unfolds it.
        Assert.False(hosts.IsOtherDevicesOpen);
        Assert.False(page.FindControl<ListGroup>("OtherDevicesList")!.IsVisible);
        hosts.ToggleOtherDevicesCommand.Execute(null);
        Phone.Pump();
        Assert.True(page.FindControl<ListGroup>("OtherDevicesList")!.IsEffectivelyVisible);
        // Phones are never computers to open.
        Assert.DoesNotContain(phone.Workspace.Hosts, host => host.Name is "Demo 的 iPhone" or "Pixel 9");

        // Without an account there is no such section (nor computers of an account).
        using var local = new Phone(localOnly: true);
        var localHosts = (HostsViewModel)local.Top<HostsPage>().DataContext!;
        Assert.False(localHosts.HasOtherDevices);
        Assert.Empty(localHosts.Unpaired);
    }

    // ---- on the real protocol ----

    [AvaloniaFact]
    public async Task A_computer_that_takes_its_access_password_is_remembered_after_the_right_one()
    {
        await using var relay = new FakeRelay();
        await using var studio = await Computer.StartAsync(relay);
        await studio.SetAccessPasswordAsync("K7M2-Q9XP", relay);
        using var phone = new RealPhone(relay);
        phone.SignIn(relay);
        var hosts = phone.Model<HostsViewModel>();

        // The account's computer, not paired, with the lock.
        Assert.True(Phone.Until(() => hosts.Unpaired.Any(row => row.IsPasswordAction)), "the computer is listed with its lock");
        var row = hosts.Unpaired.Single();
        Assert.Equal(("Studio", "密码", true), (row.Name, row.PillText, row.CanOpen));
        Assert.Empty(studio.Service.Connections);

        row.OpenCommand.Execute(null);
        Assert.True(Phone.Until(() => phone.Shell.Top is AccessPasswordPage));
        var page = phone.Model<AccessPasswordViewModel>();
        Assert.Equal("Studio", page.HostName);

        // Wrong: said so, and nothing is remembered on either side.
        page.Password = "k7m2q9xx";
        page.VerifyCommand.Execute(null);
        Assert.True(Phone.Until(() => page.HasError && !page.IsVerifying, Derivation), "the computer answers");
        Assert.Equal("密码不对。请对照电脑上显示的访问密码再试一次。", page.ErrorText);
        Assert.Empty(phone.Remote.Connections.Hosts.Peers);
        Assert.Empty(studio.Service.Devices.Peers);
        Assert.Contains(hosts.Unpaired, unpaired => unpaired.Host.Id == studio.Identity.Id);

        // Right, typed as a person might: in, remembered as come in by password, connected, and on the computer's page.
        page.Password = "k7m2 q9xp";
        page.VerifyCommand.Execute(null);
        Assert.True(Phone.Until(() => phone.Shell.Top is HostPage, Derivation), "the computer's page opens: " + page.ErrorText);
        Assert.Equal(2, phone.Shell.Stack!.Depth);
        Assert.Equal(studio.Identity.Id, phone.Model<HostViewModel>().Host.Id);
        var remembered = Assert.Single(phone.Remote.Connections.Hosts.Peers);
        Assert.Equal((studio.Identity.Id, PairedVia.Password), (remembered.Id, remembered.Via));
        var phoneOnHost = Assert.Single(studio.Service.Devices.Peers);
        Assert.Equal((DeviceKind.Phone, PairedVia.Password), (phoneOnHost.Kind, phoneOnHost.Via));
        Assert.True(Phone.Until(() => hosts.Online.Any(online => online.Host.Id == studio.Identity.Id)), "the computer is listed as connected");
        Assert.Empty(hosts.Unpaired);
        Assert.StartsWith("中转", hosts.Online.Single().Summary);

        // Back at the list: the one connection the password made is the one in use, and the page is gone.
        phone.Shell.Back();
        Assert.True(Phone.Until(() => phone.Shell.Top is HostsPage));
        Assert.Single(studio.Service.Connections);
    }

    [AvaloniaFact]
    public async Task Five_wrong_passwords_make_the_phone_wait()
    {
        await using var relay = new FakeRelay();
        await using var studio = await Computer.StartAsync(relay);
        await studio.SetAccessPasswordAsync("K7M2-Q9XP", relay);
        using var phone = new RealPhone(relay);
        phone.SignIn(relay);
        var hosts = phone.Model<HostsViewModel>();
        Assert.True(Phone.Until(() => hosts.Unpaired.Any(row => row.IsPasswordAction)));
        hosts.Unpaired.Single().OpenCommand.Execute(null);
        Assert.True(Phone.Until(() => phone.Shell.Top is AccessPasswordPage));
        var page = phone.Model<AccessPasswordViewModel>();

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            page.Password = "WRNG-000" + attempt;
            page.VerifyCommand.Execute(null);
            Assert.True(Phone.Until(() => page.HasError && !page.IsVerifying, Derivation), $"attempt {attempt} is answered");
            if (attempt < 5) Assert.Equal("密码不对。请对照电脑上显示的访问密码再试一次。", page.ErrorText);
        }
        Assert.Equal("错误次数太多，稍后再试，约 15 分钟。", page.ErrorText);
        Assert.False(page.CanPairInstead);

        // Even the right one waits now.
        page.Password = "K7M2-Q9XP";
        page.VerifyCommand.Execute(null);
        Assert.True(Phone.Until(() => page.HasError && !page.IsVerifying, Derivation));
        Assert.Contains("15 分钟", page.ErrorText);
        Assert.Empty(phone.Remote.Connections.Hosts.Peers);
        Assert.Empty(studio.Service.Devices.Peers);
    }

    [AvaloniaFact]
    public async Task A_computer_with_password_access_off_offers_pairing_instead()
    {
        await using var relay = new FakeRelay();
        await using var studio = await Computer.StartAsync(relay);
        await studio.SetAccessPasswordAsync("K7M2-Q9XP", relay);
        using var phone = new RealPhone(relay);
        phone.SignIn(relay);
        var hosts = phone.Model<HostsViewModel>();
        Assert.True(Phone.Until(() => hosts.Unpaired.Any(row => row.IsPasswordAction)));
        hosts.Unpaired.Single().OpenCommand.Execute(null);
        Assert.True(Phone.Until(() => phone.Shell.Top is AccessPasswordPage));
        var page = phone.Model<AccessPasswordViewModel>();

        // Turned off on the computer while the page was open: the computer says no, the page says where to turn it on.
        studio.Service.SetAccessPassword(null);
        page.Password = "K7M2-Q9XP";
        page.VerifyCommand.Execute(null);
        Assert.True(Phone.Until(() => page.HasError && !page.IsVerifying, Derivation), "the computer answers");
        Assert.Equal("这台电脑现在不接受访问密码。可以改用配对，或在这台电脑的 Octopus 设置 → 账号与设备 里打开访问密码。", page.ErrorText);
        Assert.True(page.CanPairInstead);
        Assert.Empty(phone.Remote.Connections.Hosts.Peers);

        // "Pair instead": the six digits, as for a computer that never offered a password.
        page.PairInsteadCommand.Execute(null);
        Assert.True(Phone.Until(() => phone.Shell.Top is PairingPage), "the pairing page opens");
        Assert.Equal(2, phone.Shell.Stack!.Depth);
        phone.Model<PairingViewModel>().CancelCommand.Execute(null);
        Assert.True(Phone.Until(() => phone.Shell.Top is HostsPage));

        // Once the relay heard it, the row has no lock any more and opens pairing directly.
        await studio.Link!.AnnounceFeaturesAsync();
        Assert.True(Phone.Until(() => hosts.Unpaired.Any(row => !row.IsPasswordAction && row.PillText == "配对")), "the lock is gone");
        hosts.Unpaired.Single().OpenCommand.Execute(null);
        Assert.True(Phone.Until(() => phone.Shell.Top is PairingPage));
    }

    [AvaloniaFact]
    public async Task Offline_computers_are_only_named_and_phones_are_listed_apart()
    {
        await using var relay = new FakeRelay();
        await using var studio = await Computer.StartAsync(relay);
        var oldLaptop = NewDeviceId();
        var pixel = NewDeviceId();
        relay.AddDevice(oldLaptop, "旧笔记本");
        relay.AddDevice(pixel, "Pixel 9", kind: "phone");
        using var phone = new RealPhone(relay);
        phone.SignIn(relay);
        var hosts = phone.Model<HostsViewModel>();

        Assert.True(Phone.Until(() => hosts.Offline.Count == 1 && hosts.Unpaired.Count == 1 && hosts.OtherDevices.Count == 2), "the account's devices are listed");
        // The offline computer: faded, says when it was there, and does nothing.
        var offline = hosts.Offline.Single();
        Assert.Equal("旧笔记本", offline.Name);
        Assert.True(offline.IsDimmed);
        Assert.False(offline.CanOpen);
        Assert.False(offline.OpenCommand.CanExecute(null));
        Assert.False(offline.HasPill);
        Assert.Equal("刚刚在线", offline.Summary);
        // Phones are not computers: this one first as "This phone", then the other; none of them is a host row.
        Assert.Equal([("Ada 的 iPhone", true), ("Pixel 9", false)], hosts.OtherDevices.Select(device => (device.Name, device.IsCurrent)));
        Assert.Equal("正在使用的这台手机", hosts.OtherDevices[0].Summary);
        Assert.DoesNotContain(phone.Remote.Workspace.Hosts, host => host.Id == pixel || host.Name == "Ada 的 iPhone");
        Assert.False(hosts.IsOtherDevicesOpen);

        // Never connected to: refreshing a few times tries nothing for it, and the online one is still only listed.
        await phone.Remote.Workspace.RefreshAsync();
        await phone.Remote.Workspace.RefreshAsync();
        Phone.Settle(300);
        Assert.Equal(Octopus.Remote.Client.RemoteHostState.Disconnected, phone.Remote.Connections.StateOf(oldLaptop));
        Assert.Empty(studio.Service.Connections);
        Assert.Empty(phone.Remote.Connections.Hosts.Peers);

        // Signed out: none of the account's devices stays on the screen.
        phone.Remote.Account.SignOut();
        Assert.True(Phone.Until(() => phone.Shell.Top is WelcomePage));
        Assert.True(Phone.Until(() => phone.Remote.Workspace.OtherDevices.Count == 0));
    }

    private static string NewDeviceId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(13));
}
