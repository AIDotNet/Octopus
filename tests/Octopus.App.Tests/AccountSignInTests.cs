using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Octopus.App.Account;
using Octopus.App.Composition;
using Octopus.App.Remote;
using Octopus.App.Shell;
using Octopus.Platform;
using Octopus.Remote.Tests;
using Octopus.Storage;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>Top-bar account capsule, centered sign-in dialog, and signed-in dropdown menu (Scheme B).</summary>
public sealed class AccountSignInTests
{
    private sealed class RecordingOpener : IFileOpener
    {
        public List<string> Urls { get; } = [];
        public void Open(string path) { }
        public void Reveal(string path) { }
        public bool OpenUrl(string url) { Urls.Add(url); return true; }
    }

    private sealed class RecordingNavigator : ISettingsNavigator
    {
        public List<string?> Opened { get; } = [];
        public void OpenSettings(string? section = null) => Opened.Add(section);
    }

    private static async Task Wait(Task task, int milliseconds = 15_000)
    {
        var end = Environment.TickCount64 + milliseconds;
        while (!task.IsCompleted && Environment.TickCount64 < end)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Assert.True(task.IsCompleted, "The operation did not finish in time.");
        await task;
    }

    private static async Task Until(Func<bool> done, string what, int milliseconds = 15_000)
    {
        var end = Environment.TickCount64 + milliseconds;
        while (!done() && Environment.TickCount64 < end)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Assert.True(done(), what);
    }

    [AvaloniaFact]
    public async Task Capsule_opens_modal_signs_in_auto_enables_relay_and_manages_account_menu()
    {
        await using var relay = new FakeRelay();
        var access = AppHost.Get<RelayAccess>();
        access.PollInterval = TimeSpan.FromMilliseconds(30);
        var runtime = AppHost.Get<RemoteHostRuntime>();
        var settings = AppHost.Get<ISettingsStore>();
        var opener = new RecordingOpener();
        var navigator = new RecordingNavigator();

        using var vm = new AccountViewModel(settings, access, runtime, navigator, opener);
        var menuOpens = 0;
        var menuCloses = 0;
        vm.MenuRequested += (_, _) => menuOpens++;
        vm.MenuCloseRequested += (_, _) => menuCloses++;

        // Signed-out defaults: official octoterm.si address, capsule shows "登录".
        Assert.False(vm.IsSignedIn);
        Assert.True(vm.ShowSignedOutCapsule);
        Assert.Equal("https://octoterm.si", vm.OfficialServerAddress);
        Assert.Equal("登录", vm.CapsuleLabel);
        Assert.Contains("octoterm.si", vm.PrimaryActionText);

        // Clicking capsule when signed out opens centered modal (not popover menu).
        vm.ActivateCapsuleCommand.Execute(null);
        Assert.True(vm.IsSignInDialogOpen);
        Assert.Equal(0, menuOpens);

        // Switch to custom server (FakeRelay) and start sign-in.
        vm.ToggleCustomServerCommand.Execute(null);
        Assert.True(vm.ShowCustomServer);
        vm.CustomServerUrl = relay.Url;
        vm.StartSignInCommand.Execute(null);

        await Until(() => vm.UserCode is not null, "device code is displayed");
        Assert.True(vm.IsSigningIn);
        Assert.Matches("^[B-Z]{4}-[B-Z]{4}$", vm.UserCode!);
        Assert.Contains(vm.UserCode!, vm.CapsuleLabel);
        Assert.Equal([$"{relay.Url}/device?code={vm.UserCode}"], opener.Urls);

        // Approve in browser -> signed in, modal auto-closes, relay host auto-enabled & online!
        Assert.True(relay.Approve(vm.UserCode!));
        await Wait(vm.SignInTask);

        Assert.True(vm.IsSignedIn);
        Assert.False(vm.IsSignInDialogOpen);
        Assert.True(vm.RelayHostEnabled);
        Assert.Equal("Ada", vm.AccountName);
        Assert.Equal("A", vm.AccountInitial);
        await Until(() => vm.IsRelayOnline, "host connects to relay automatically after sign-in");

        // Clicking capsule while signed in requests the dropdown menu (not modal).
        vm.ActivateCapsuleCommand.Execute(null);
        Assert.Equal(1, menuOpens);
        Assert.False(vm.IsSignInDialogOpen);

        // Opening web account management and remote settings work and close the menu.
        vm.OpenAccountWebCommand.Execute(null);
        Assert.EndsWith("/account", opener.Urls.Last());
        vm.OpenRemoteSettingsCommand.Execute(null);
        Assert.Equal(["remote"], navigator.Opened);
        Assert.Equal(2, menuCloses);

        // Turning off relay toggle disconnects host while staying signed in.
        vm.RelayHostEnabled = false;
        await Wait(vm.AppliedTask);
        await Until(() => !vm.IsRelayOnline, "relay disconnects when toggled off");
        Assert.True(vm.IsSignedIn);
        Assert.True(vm.IsRelayOff);

        // Signing out resets capsule back to signed-out state.
        vm.SignOutCommand.Execute(null);
        await Wait(vm.AppliedTask);
        Assert.False(vm.IsSignedIn);
        Assert.True(vm.ShowSignedOutCapsule);
        Assert.Equal("登录", vm.CapsuleLabel);
    }

    [AvaloniaFact]
    public async Task Revoked_device_switches_capsule_to_sign_in_again_state()
    {
        await using var relay = new FakeRelay();
        var access = AppHost.Get<RelayAccess>();
        access.PollInterval = TimeSpan.FromMilliseconds(30);
        var runtime = AppHost.Get<RemoteHostRuntime>();
        var settings = AppHost.Get<ISettingsStore>();

        using var vm = new AccountViewModel(settings, access, runtime, new RecordingNavigator(), new RecordingOpener());
        vm.CustomServerUrl = relay.Url;
        vm.StartSignInCommand.Execute(null);
        await Until(() => vm.UserCode is not null, "device code shown");
        Assert.True(relay.Approve(vm.UserCode!));
        await Wait(vm.SignInTask);
        await Until(() => vm.IsRelayOnline, "host online");

        relay.SignOut(runtime.Service!.Identity.Id);
        await Until(() => vm.ShowEndedCapsule, "capsule enters ended state");
        Assert.True(vm.SignInEnded);
        Assert.Equal("重新登录", vm.CapsuleLabel);
    }

    [AvaloniaFact]
    public void Shell_hosts_account_capsule_and_escape_closes_sign_in_modal()
    {
        var window = new MainWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var capsule = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "AccountCapsuleButton");
        var modal = window.GetVisualDescendants().OfType<AccountSignInOverlay>().FirstOrDefault(v => v.Name == "AccountSignInModal");
        Assert.NotNull(capsule);
        Assert.NotNull(modal);
        Assert.False(modal!.IsVisible);

        var shellView = Assert.IsType<ShellView>(window.Content);
        var shell = Assert.IsType<ShellViewModel>(shellView.DataContext);
        Assert.NotNull(capsule!.Command);
        capsule.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(shell.Account.IsSignInDialogOpen);
        Assert.True(modal.IsVisible);

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.False(modal.IsVisible);

        window.Close();
    }

    [Fact]
    public async Task Protocol_activation_parses_and_forwards_auth_done_uri()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Null(Octopus.App.Services.ProtocolActivation.TryParseProtocolUri(["--foo", "https://octoterm.si"]));
        var parsed = Octopus.App.Services.ProtocolActivation.TryParseProtocolUri(["--flag", "octopus://auth-done"]);
        Assert.NotNull(parsed);
        Assert.Equal("octopus", parsed!.Scheme);
        Assert.Equal("auth-done", parsed.Host);

        var pipe = "oct-t-" + Guid.NewGuid().ToString("N")[..8];
        var received = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = Octopus.App.Services.ProtocolActivation.StartServer(uri => received.TrySetResult(uri), pipe);

        // Give the background listener a brief moment to create the pipe before connecting.
        var delivered = false;
        for (var i = 0; i < 10 && !delivered; i++)
        {
            delivered = Octopus.App.Services.ProtocolActivation.TryForwardToRunningInstance(parsed, pipe, timeoutMs: 200);
            if (!delivered) await Task.Delay(25, ct);
        }

        Assert.True(delivered);
        var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Equal("octopus", got.Scheme);
        Assert.Equal("auth-done", got.Host);
    }
}
