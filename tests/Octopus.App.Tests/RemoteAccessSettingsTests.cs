using System.Net;
using System.Net.Sockets;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Octopus.App.Composition;
using Octopus.App.Remote;
using Octopus.App.Settings.Remote;
using Octopus.Platform;
using Octopus.Remote.Client;
using Octopus.Remote.Security;
using Octopus.Remote.Transport;
using Octopus.Storage;
using TKCode.Arc;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>The "Remote access" settings page driving the real host service, with a real device over a local socket (design/12-远程操作.md §7.1).</summary>
public class RemoteAccessSettingsTests
{
    private static readonly DeviceDescription Laptop = new("Laptop", DeviceKind.Desktop);

    private static RemoteAccessSettingsViewModel Page()
    {
        // Any free port: the default one may be taken on the machine that runs the tests.
        AppHost.Get<ISettingsStore>().Update(s => s.Remote.LanPort = 0);
        return ActivatorUtilities.CreateInstance<RemoteAccessSettingsViewModel>(AppHost.Services);
    }

    private static async Task<T> Wait<T>(Task<T> task, int milliseconds = 8000)
    {
        var end = Environment.TickCount64 + milliseconds;
        while (!task.IsCompleted && Environment.TickCount64 < end)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Assert.True(task.IsCompleted, "The operation did not finish in time.");
        return await task;
    }

    private static Task Wait(Task task) => Wait(task.ContinueWith(t => { t.GetAwaiter().GetResult(); return true; }));

    private static async Task Until(Func<bool> done, string what)
    {
        var end = Environment.TickCount64 + 8000;
        while (!done() && Environment.TickCount64 < end)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Assert.True(done(), what);
    }

    private static async Task<IMessageTransport> Dial(RemoteHostRuntime runtime) =>
        await Wait(WebSocketTransport.ConnectAsync(new Uri($"ws://127.0.0.1:{runtime.Service!.LanPort}")));

    [AvaloniaFact]
    public async Task Nothing_is_made_until_remote_access_is_turned_on_and_turning_it_off_stops_listening()
    {
        var runtime = AppHost.Get<RemoteHostRuntime>();
        using var page = Page();

        // Off by default: no service, no key, nothing listening.
        Assert.False(page.LanEnabled);
        Assert.False(page.IsListening);
        Assert.Null(runtime.Service);
        Assert.Null(await AppHost.Get<ISecretStore>().GetAsync("Octopus/remote", DeviceIdentityStore.KeyName));
        Assert.Equal("没有监听", page.AddressesText);
        Assert.False(page.ShowTrayHint);

        page.LanEnabled = true;
        await Wait(page.Applied);
        Assert.True(page.IsListening);
        Assert.True(AppHost.Get<ISettingsStore>().Current.Remote.LanEnabled);
        Assert.StartsWith("ws://", page.AddressesText);
        Assert.EndsWith(":" + runtime.Service!.LanPort, page.AddressesText.Split('\n')[0]);
        Assert.False(page.HasError);
        // Closing the window would quit the app: the page says how to keep it reachable.
        Assert.True(page.ShowTrayHint);
        // The device key went to the keystore, and it is the same one the next time.
        var stored = await AppHost.Get<ISecretStore>().GetAsync("Octopus/remote", DeviceIdentityStore.KeyName);
        Assert.NotNull(stored);
        using (var again = await DeviceIdentityStore.LoadOrCreateAsync(null, Path.Combine(AppHost.Get<IAppPaths>().DataDirectory, "unused.key")))
        {
            Assert.NotEqual(again.Id, runtime.Service.Identity.Id); // another store, another key: the real one is not in a file
        }
        Assert.False(File.Exists(Path.Combine(AppHost.Get<IAppPaths>().DataDirectory, "remote", "device.key")));

        var port = runtime.Service.LanPort!.Value;
        page.LanEnabled = false;
        await Wait(page.Applied);
        Assert.False(page.IsListening);
        using var probe = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => probe.ConnectAsync(IPAddress.Loopback, port));
    }

    [AvaloniaFact]
    public async Task A_port_that_is_taken_is_reported_instead_of_listening()
    {
        var taken = new TcpListener(IPAddress.Any, 0);
        taken.Start();
        try
        {
            using var page = Page();
            AppHost.Get<ISettingsStore>().Update(s => s.Remote.LanPort = ((IPEndPoint)taken.LocalEndpoint).Port);
            Assert.Equal(((IPEndPoint)taken.LocalEndpoint).Port.ToString(), page.Port);

            page.LanEnabled = true;
            await Wait(page.Applied);

            Assert.False(page.IsListening);
            Assert.True(page.HasError);
            Assert.Contains(page.Port, page.ErrorText);
        }
        finally
        {
            taken.Stop();
        }
    }

    [AvaloniaFact]
    public async Task A_device_pairs_from_the_page_shows_as_connected_and_can_be_revoked()
    {
        var runtime = AppHost.Get<RemoteHostRuntime>();
        using var page = Page();
        page.DeviceName = "  Studio Mac ";
        Assert.Equal("Studio Mac", runtime.DeviceName);
        page.LanEnabled = true;
        await Wait(page.Applied);
        using var device = DeviceIdentity.Create();

        // Before "Start pairing" a stranger is turned away.
        var refused = await Assert.ThrowsAsync<HandshakeException>(async () => await Wait(RemoteHostClient.ConnectAsync(await Dial(runtime), device, Laptop,
            PeerExpectation.AnyForPairing, RemoteHostRuntime.Hello, new ConnectOptions { RequestPairing = true })));
        Assert.Equal(HandshakeStatus.PairingClosed, refused.Status);

        await Wait(page.StartPairingCommand.ExecuteAsync(null));
        Assert.True(page.IsPairing);
        Assert.True(page.IsWaitingForRequest);
        var link = PairingLink.TryParse(page.PairingLink)!;
        Assert.Equal(runtime.Service!.Identity.Id, link.DeviceId);
        Assert.Equal("Studio Mac", link.Name);
        Assert.NotEmpty(link.Addresses);

        // The device asks: its row shows the same number as the device does, and nothing happens until "Allow".
        var shown = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connecting = RemoteHostClient.ConnectAsync(await Dial(runtime), device, Laptop, PeerExpectation.AnyForPairing, RemoteHostRuntime.Hello,
            new ConnectOptions { RequestPairing = true, OnPending = shown.SetResult });
        var digits = await Wait(shown.Task);
        await Until(() => page.Requests.Count == 1, "the request shows on the page");
        Assert.Equal("Laptop", page.Requests[0].Name);
        Assert.Equal(digits, page.Requests[0].Sas.Replace(" ", ""));
        Assert.False(page.IsWaitingForRequest);
        Assert.False(connecting.IsCompleted);
        Assert.Empty(page.Devices);

        page.Requests[0].AllowCommand.Execute(null);
        await using var client = await Wait(connecting);
        Assert.Equal("Studio Mac", client.HostDescription.Name);
        await Until(() => page.Devices.Count == 1 && page.Devices[0].IsOnline, "the device shows as paired and connected");
        Assert.Equal("Laptop", page.Devices[0].Name);
        Assert.Equal("电脑", page.Devices[0].Kind);
        Assert.Empty(page.Requests);
        // The list is on disk for the next start, with the public key only.
        var file = Path.Combine(AppHost.Get<IAppPaths>().DataDirectory, "remote", "devices.json");
        Assert.Contains(device.Id, File.ReadAllText(file));

        page.StopPairingCommand.Execute(null);
        Assert.False(page.IsPairing);
        Assert.Null(page.PairingLink);

        // Revoked: gone from the list, cut off at once.
        await Wait(page.RevokeCommand.ExecuteAsync(page.Devices[0]));
        await Wait(client.Completion);
        Assert.Empty(page.Devices);
        Assert.DoesNotContain(device.Id, File.ReadAllText(file));
    }

    [AvaloniaFact]
    public async Task Render_the_page_with_a_request_and_a_paired_device()
    {
        var runtime = AppHost.Get<RemoteHostRuntime>();
        var directory = Path.Combine(AppContext.BaseDirectory, "shots");
        Directory.CreateDirectory(directory);
        using var page = Page();
        page.LanEnabled = true;
        await Wait(page.Applied);
        runtime.Service!.Devices.Add(DeviceIdentity.Create().PublicKey, new DeviceDescription("iPhone", DeviceKind.Phone));
        await Wait(page.StartPairingCommand.ExecuteAsync(null));
        using var device = DeviceIdentity.Create();
        var connecting = RemoteHostClient.ConnectAsync(await Dial(runtime), device, Laptop, PeerExpectation.AnyForPairing, RemoteHostRuntime.Hello,
            new ConnectOptions { RequestPairing = true });
        await Until(() => page.Requests.Count == 1, "the request shows");

        foreach (var dark in new[] { false, true })
        {
            ArcTheme.Apply(t => t.Appearance(dark ? ArcAppearance.Dark : ArcAppearance.Light));
            var window = new Window
            {
                Width = 760, Height = 900,
                Content = new ScrollViewer { Padding = new Avalonia.Thickness(28), Content = new RemoteAccessPageView { DataContext = page } },
            };
            // The page's text styles (title, hint, mono) are those of the settings view.
            window.Styles.Add(new Octopus.App.Common.AppStyles());
            window.Show();
            Frames.Save(window, Path.Combine(directory, $"remote-access-{(dark ? "dark" : "light")}.png"));
            window.Close();
        }
        page.Requests[0].RefuseCommand.Execute(null);
        await Assert.ThrowsAsync<HandshakeException>(() => Wait(connecting));
    }
}
