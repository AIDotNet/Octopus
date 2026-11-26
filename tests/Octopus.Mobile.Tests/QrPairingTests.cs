using Avalonia.Headless.XUnit;
using Octopus.Mobile.Account;
using Octopus.Mobile.Hosts;
using Xunit;

namespace Octopus.Mobile.Tests;

/// <summary>
/// Plan B end-to-end verification: in-app QR scanning (AddHost, Welcome, CustomServer), system-camera deep links
/// (<c>octopus://pair?…</c> and <c>octopus://relay?…</c> in both hot-start and cold-start flows), and multi-address
/// concurrent pairing race so dead virtual NICs never stall QR pairing.
/// </summary>
public class QrPairingTests
{
    private static readonly TimeSpan Open = TimeSpan.FromMinutes(1);

    [AvaloniaFact]
    public async Task Scanning_pairing_qr_from_add_host_pairs_immediately_and_connects()
    {
        var previousScanner = MobileHost.ScanQrCode;
        try
        {
            await using var den = Computer.StartLocal();
            den.Workspace.AddProject("Octopus", "~/code/Octopus");
            den.Service.OpenPairing(Open);
            var link = den.Service.CreatePairingLink([$"ws://{den.Address}"])!.ToString();

            MobileHost.ScanQrCode = () => Task.FromResult<string?>(link);

            using var phone = new RealPhone(null);
            phone.Model<WelcomeViewModel>().UseLocalCommand.Execute(null);
            phone.Top<AddHostPage>();
            var add = phone.Model<AddHostViewModel>();
            Assert.True(add.CanScanQr);
            Assert.True(add.ScanQrCommand.CanExecute(null));

            add.ScanQrCommand.Execute(null);

            Assert.True(Phone.Until(() => phone.Shell.Top is HostsPage), "QR scan pairs without comparing digits: " + add.ErrorText);
            var hosts = phone.Model<HostsViewModel>();
            Assert.True(Phone.Until(() => hosts.Online.Count == 1), "scanned computer connects");
            Assert.Equal("Den", hosts.Online[0].Name);
            Assert.Empty(den.Service.PairingRequests);
            // One-time token was consumed; host can issue a fresh one for a second device.
            Assert.Null(den.Service.CreatePairingLink([$"ws://{den.Address}"]));
            Assert.True(den.Service.RefreshPairingToken());
            Assert.NotNull(den.Service.CreatePairingLink([$"ws://{den.Address}"]));
        }
        finally
        {
            MobileHost.ScanQrCode = previousScanner;
        }
    }

    [AvaloniaFact]
    public async Task Scanning_pairing_qr_from_welcome_page_pairs_and_enters_local_mode()
    {
        var previousScanner = MobileHost.ScanQrCode;
        try
        {
            await using var den = Computer.StartLocal();
            den.Service.OpenPairing(Open);
            var link = den.Service.CreatePairingLink([$"ws://{den.Address}"])!.ToString();

            MobileHost.ScanQrCode = () => Task.FromResult<string?>(link);

            using var phone = new RealPhone(null);
            phone.Top<WelcomePage>();
            var welcome = phone.Model<WelcomeViewModel>();
            Assert.True(welcome.ShowsScanQr);

            welcome.ScanQrCommand.Execute(null);

            Assert.True(Phone.Until(() => phone.Shell.Top is HostsPage), "scanning from welcome page goes straight to hosts");
            Assert.True(phone.Remote.Account.IsLocalOnly);
            var hosts = phone.Model<HostsViewModel>();
            Assert.True(Phone.Until(() => hosts.Online.Count == 1));
            Assert.Equal("Den", hosts.Online[0].Name);
        }
        finally
        {
            MobileHost.ScanQrCode = previousScanner;
        }
    }

    [AvaloniaFact]
    public async Task Scanning_relay_qr_from_add_host_routes_to_custom_server_with_key_filled()
    {
        var previousScanner = MobileHost.ScanQrCode;
        try
        {
            const string relayQr = "octopus://relay?v=1&url=https%3A%2F%2Frelay.example.com&key=my-access-key-01";
            MobileHost.ScanQrCode = () => Task.FromResult<string?>(relayQr);

            using var phone = new RealPhone(null);
            phone.Model<WelcomeViewModel>().UseLocalCommand.Execute(null);
            phone.Top<AddHostPage>();
            phone.Model<AddHostViewModel>().ScanQrCommand.Execute(null);

            Assert.True(Phone.Until(() => phone.Shell.Top is CustomServerPage));
            var custom = phone.Model<CustomServerViewModel>();
            Assert.Equal("https://relay.example.com", custom.Server);
            Assert.Equal("my-access-key-01", custom.AccessKey);
        }
        finally
        {
            MobileHost.ScanQrCode = previousScanner;
        }
    }

    [AvaloniaFact]
    public async Task Deep_link_pair_and_relay_work_on_hot_start_and_cold_start()
    {
        await using var den = Computer.StartLocal();
        den.Service.OpenPairing(Open);
        var pairLink = den.Service.CreatePairingLink([$"ws://{den.Address}"])!.ToString();

        // Hot-start: shell is already attached when system camera opens octopus://pair?...
        using (var phone = new RealPhone(null))
        {
            phone.Top<WelcomePage>();
            MobileHost.HandleDeepLink(new Uri(pairLink));
            Assert.True(Phone.Until(() => phone.Shell.Top is HostsPage), "hot-start deep link pairs automatically");
            var hosts = phone.Model<HostsViewModel>();
            Assert.True(Phone.Until(() => hosts.Online.Count == 1));
            Assert.Equal("Den", hosts.Online[0].Name);
        }

        // Cold-start: octopus://relay?... arrives before MobileShell is attached.
        MobileHost.HandleDeepLink(new Uri("octopus://relay?v=1&url=https%3A%2F%2Fself.example.com&key=cold-key"));
        using (var phone = new RealPhone(null))
        {
            Assert.True(Phone.Until(() => phone.Shell.Top is CustomServerPage), "cold-start deep link replays on attach");
            var custom = phone.Model<CustomServerViewModel>();
            Assert.Equal("https://self.example.com", custom.Server);
            Assert.Equal("cold-key", custom.AccessKey);
        }
    }

    [AvaloniaFact]
    public async Task Pairing_link_with_unreachable_first_address_races_and_connects_via_live_address()
    {
        await using var den = Computer.StartLocal();
        den.Service.OpenPairing(Open);
        // Simulate a stale/dead virtual NIC listed ahead of the real LAN address.
        var link = den.Service.CreatePairingLink(["ws://127.0.0.1:9", $"ws://{den.Address}"])!.ToString();

        using var phone = new RealPhone(null);
        phone.Model<WelcomeViewModel>().UseLocalCommand.Execute(null);
        var add = phone.Model<AddHostViewModel>();
        add.Target = link;
        add.ConnectCommand.Execute(null);

        Assert.True(Phone.Until(() => phone.Shell.Top is HostsPage, 5_000), "concurrent dial race connects quickly: " + add.ErrorText);
        var hosts = phone.Model<HostsViewModel>();
        Assert.True(Phone.Until(() => hosts.Online.Count == 1));
        Assert.Equal("Den", hosts.Online[0].Name);
    }
}
