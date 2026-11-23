using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Octopus.App.Composition;
using Octopus.App.Dialogs;
using Octopus.App.Localization;
using Octopus.App.Services;
using Octopus.App.Shell;
using Octopus.Core.Terminals;
using Octopus.Storage;
using Octopus.Terminal;
using TKCode.Arc;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>Verifies the embedded account login dialog layout, link extraction, retry UX, and visual rendering.</summary>
public class AccountLoginDialogTests
{
    private const string CodexLoginSample =
        "Starting local login server on http://localhost:1455.\r\n" +
        "If your browser did not open, navigate to this URL to authenticate:\r\n" +
        "\r\n" +
        "https://auth.openai.com/oauth/authorize?response_type=code&client_id=app_EMoamEEZ73f0CkXaXp7hrann" +
        "&redirect_uri=http%3A%2F%2F127.0.0.1%3A1455%2Fauth%2Fcallback&code_challenge=GfDKB15QMsfvbanBNsTp2Mar3H_Wde_pXMI42J1QdFY" +
        "&code_challenge_method=S256&state=UMOv1D7-pgQgN8HJrkGr8I4mqLYQ5dJbCHfzaw64W_4&scope=openid+profile+email+offline_access" +
        "&id_token_add_organizations=true&codex_cli_simplified_flow=true&originator=codex_cli_rs\r\n" +
        "\r\n" +
        "On a remote or headless machine? Use `codex login --device-auth` instead.\r\n";

    private sealed class ControlledConnection(string output) : ITerminalConnection
    {
        private readonly TaskCompletionSource<TerminalExit> _exit = new();
        public int ProcessId => 1;
        public int Columns { get; private set; }
        public int Rows { get; private set; }
        public Task<TerminalExit> Exited => _exit.Task;

        public ControlledConnection(string output, int columns, int rows) : this(output)
        {
            Columns = columns;
            Rows = rows;
        }

        public void Complete(int exitCode) => _exit.TrySetResult(new TerminalExit(exitCode, null));

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return Encoding.UTF8.GetBytes(output);
            await _exit.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask ResizeAsync(int columns, int rows, int pixelWidth, int pixelHeight)
        {
            Columns = columns;
            Rows = rows;
            return ValueTask.CompletedTask;
        }

        public ValueTask SignalAsync(TerminalSignal signal) => ValueTask.CompletedTask;
        public ValueTask<string?> GetForegroundProcessNameAsync() => ValueTask.FromResult<string?>(null);

        public ValueTask DisposeAsync()
        {
            _exit.TrySetResult(new TerminalExit(0, null));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ControlledFactory(string output) : ITerminalConnectionFactory
    {
        public List<ControlledConnection> Connections { get; } = [];
        public ControlledConnection? Latest => Connections.Count > 0 ? Connections[^1] : null;

        public ITerminalConnection Start(LaunchSpec spec, int columns, int rows)
        {
            var conn = new ControlledConnection(output, columns, rows);
            Connections.Add(conn);
            return conn;
        }
    }

    [Fact]
    public void PickAuthUrl_skips_loopback_and_prefers_latest_https()
    {
        var chosen = AccountLoginTerminalView.PickAuthUrl(
        [
            "http://localhost:1455",
            "http://127.0.0.1:8080/callback",
            "https://auth.openai.com/oauth/authorize?state=1",
            "https://auth.openai.com/oauth/authorize?state=2",
        ]);
        Assert.Equal("https://auth.openai.com/oauth/authorize?state=2", chosen);
        Assert.Null(AccountLoginTerminalView.PickAuthUrl(["http://localhost:1455", "http://127.0.0.1:1455"]));
    }

    [AvaloniaFact]
    public async Task Login_dialog_fits_within_surface_without_clipping_and_extracts_auth_url()
    {
        var factory = new ControlledFactory(CodexLoginSample);
        var settings = AppHost.Get<ISettingsStore>();
        var openedUrls = new List<string>();
        var spec = new LaunchSpec("/bin/sh", [], null, new Dictionary<string, string>());

        var view = new AccountLoginTerminalView(spec, factory, settings, url => openedUrls.Add(url));
        var dialog = DialogService.CreateAccountLoginDialog(
            "登录 Codex CLI（账户 1）",
            AppLangResources.Get(AppLangResourceKind.AccountLoginDialogDescription),
            view);

        Assert.False(dialog.CloseOnOutsidePress);
        Assert.False(dialog.CloseOnEscape);

        var window = new Window { Width = 1280, Height = 800 };
        window.Show();
        Frames.Pump();

        var showTask = dialog.ShowAsync(window);
        Frames.Pump();
        Assert.True(Frames.Until(() => view.DetectedAuthUrl is not null), "Expected wrapped OAuth URL to be detected.");

        var surface = window.GetVisualDescendants().OfType<DialogSurface>().First();
        // Dialog widens beyond default Large (560) up to 760, and the terminal fits strictly inside the surface with padding!
        Assert.True(surface.Bounds.Width >= 700 && surface.Bounds.Width <= DialogService.AccountLoginDialogMaxWidth + 1,
            $"Unexpected dialog width: {surface.Bounds.Width}");
        Assert.True(view.Bounds.Width <= surface.Bounds.Width - 32,
            $"Expected terminal view ({view.Bounds.Width}) to stay inside dialog body padding of surface ({surface.Bounds.Width}).");
        Assert.True(view.TerminalView.Bounds.Width <= view.Bounds.Width,
            $"Expected TerminalControl ({view.TerminalView.Bounds.Width}) not to overflow view ({view.Bounds.Width}).");

        // Terminal gets generous column count (~80+ on default fonts, >= 72 even on wider Linux fallbacks) so lines never clip.
        Assert.NotNull(factory.Latest);
        Assert.True(factory.Latest!.Columns >= 72, $"Expected >= 72 terminal columns, got {factory.Latest.Columns}");

        // Wrapped OAuth URL is rejoined cleanly without newlines or truncation, skipping http://localhost:1455.
        Assert.StartsWith("https://auth.openai.com/oauth/authorize?", view.DetectedAuthUrl);
        Assert.EndsWith("originator=codex_cli_rs", view.DetectedAuthUrl);

        // Quick-action bar appears and opens the detected URL when clicked.
        var authActions = view.FindControl<StackPanel>("AuthLinkActions")!;
        Assert.True(authActions.IsVisible);
        var openButton = view.FindControl<TKCode.Arc.Button>("OpenLinkButton")!;
        openButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Frames.Pump();
        Assert.Single(openedUrls, view.DetectedAuthUrl);

        // Non-zero exit shows error status and Retry button; restarting launches a fresh connection.
        factory.Latest.Complete(1);
        Assert.True(Frames.Until(() => view.FindControl<TKCode.Arc.Button>("RetryButton")!.IsVisible));
        Assert.True(view.FindControl<Control>("ErrorIcon")!.IsVisible);

        await view.RestartAsync();
        Frames.Pump();
        Assert.Equal(2, factory.Connections.Count);
        Assert.False(view.FindControl<TKCode.Arc.Button>("RetryButton")!.IsVisible);

        // On narrow windows the dialog shrinks responsively and the terminal still stays inside without clipping.
        window.Width = 600;
        Frames.Pump();
        Assert.True(surface.Bounds.Width <= 600 - 30, $"Expected narrow surface <= 570, got {surface.Bounds.Width}");
        Assert.True(view.Bounds.Width <= surface.Bounds.Width - 32,
            $"Expected narrow terminal view ({view.Bounds.Width}) to stay inside surface ({surface.Bounds.Width}).");

        dialog.Close(true);
        Assert.Equal(true, await showTask);
        await view.StopAsync();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Render_account_login_dialog_screenshots()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "shots");
        Directory.CreateDirectory(dir);
        var settings = AppHost.Get<ISettingsStore>();

        foreach (var dark in new[] { true, false })
        {
            ArcTheme.Apply(t => t.Appearance(dark ? ArcAppearance.Dark : ArcAppearance.Light));
            var theme = dark ? "dark" : "light";
            var factory = new ControlledFactory(CodexLoginSample);
            var spec = new LaunchSpec("/bin/sh", [], null, new Dictionary<string, string>());
            var view = new AccountLoginTerminalView(spec, factory, settings);
            var dialog = DialogService.CreateAccountLoginDialog(
                "登录 Codex CLI（账户 1）",
                AppLangResources.Get(AppLangResourceKind.AccountLoginDialogDescription),
                view);

            var window = new MainWindow { Width = 1280, Height = 800 };
            window.Show();
            Frames.Pump();

            var showTask = dialog.ShowAsync(window);
            Frames.Pump();
            Assert.True(Frames.Until(() => view.DetectedAuthUrl is not null));
            Thread.Sleep(450);
            Frames.Pump();
            Frames.Save(window, Path.Combine(dir, $"account-login-{theme}.png"));

            if (dark)
            {
                factory.Latest!.Complete(1);
                Assert.True(Frames.Until(() => view.FindControl<TKCode.Arc.Button>("RetryButton")!.IsVisible));
                Frames.Pump();
                Frames.Save(window, Path.Combine(dir, "account-login-error-dark.png"));
            }

            dialog.Close(false);
            await showTask;
            await view.StopAsync();
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
