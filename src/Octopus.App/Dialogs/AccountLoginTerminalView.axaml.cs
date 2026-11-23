using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using Octopus.App.Composition;
using Octopus.App.Localization;
using Octopus.Core.Terminals;
using Octopus.Platform;
using Octopus.Storage;
using Octopus.Terminal;
using Octopus.Terminal.Avalonia;
using TKCode.Arc;

namespace Octopus.App.Dialogs;

/// <summary>
/// Hosts a real <see cref="TerminalControl"/> inside a modal dialog so the user can complete interactive OAuth / device
/// code sign-in for an isolated account without opening a permanent workspace tab.
/// </summary>
public partial class AccountLoginTerminalView : UserControl
{
    private readonly LaunchSpec? _spec;
    private readonly ITerminalConnectionFactory? _factory;
    private readonly Action<string> _openUrl;
    private bool _started;
    private bool _restarting;
    private string? _authUrl;
    private CancellationTokenSource? _copyFeedbackCts;

    public AccountLoginTerminalView()
    {
        InitializeComponent();
        _openUrl = url => AppHost.Get<IFileOpener>().OpenUrl(url);
    }

    public AccountLoginTerminalView(
        LaunchSpec spec,
        ITerminalConnectionFactory factory,
        ISettingsStore settings,
        Action<string>? openUrl = null) : this()
    {
        _spec = spec;
        _factory = factory;
        _openUrl = openUrl ?? (url => AppHost.Get<IFileOpener>().OpenUrl(url));

        var terminalSettings = settings.Current.Terminal;
        Terminal.FontSize = terminalSettings.FontSize;
        Terminal.ScrollbackLines = Math.Max(500, terminalSettings.ScrollbackLines);
        Terminal.ExitedMessageFormat = AppLangResources.Get(AppLangResourceKind.TerminalExited);
        if (!string.IsNullOrWhiteSpace(terminalSettings.FontFamily))
        {
            Terminal.FontFamily = new FontFamily(terminalSettings.FontFamily);
        }

        Terminal.LinkActivated += (_, url) => _openUrl(url);
        Terminal.FrameUpdated += OnTerminalFrameUpdated;
        Terminal.Exited += OnTerminalExited;
        Terminal.PropertyChanged += OnTerminalPropertyChanged;

        OpenLinkButton.Click += (_, _) => OpenDetectedAuthUrl();
        CopyLinkButton.Click += (_, _) => _ = CopyDetectedAuthUrlAsync();
        RestartButton.Click += (_, _) => _ = RestartAsync();
        RetryButton.Click += (_, _) => _ = RestartAsync();

        var linkModifier = OperatingSystem.IsMacOS() ? "⌘" : "Ctrl";
        TerminalHintText.Text = AppLangResources.Format(AppLangResourceKind.AccountLoginTerminalHint, linkModifier);

        SetRunningState();
    }

    public event EventHandler<int?>? Exited;

    /// <summary>The latest external authentication URL detected in the terminal output, if any.</summary>
    public string? DetectedAuthUrl => _authUrl;

    internal TerminalControl TerminalView => Terminal;

    /// <summary>Moves keyboard focus directly into the embedded terminal.</summary>
    public void FocusTerminal() => Terminal.FocusTerminal();

    /// <summary>
    /// Picks the most likely external authentication URL from <paramref name="urls"/>, skipping local callback servers
    /// (such as <c>http://localhost:1455</c>) and preferring the latest <c>https://</c> link when present.
    /// </summary>
    public static string? PickAuthUrl(IReadOnlyList<string> urls)
    {
        string? httpFallback = null;
        for (var i = urls.Count - 1; i >= 0; i--)
        {
            var url = urls[i];
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
            if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) continue;
            if (IsLoopbackHost(uri)) continue;

            if (uri.Scheme == Uri.UriSchemeHttps) return url;
            httpFallback ??= url;
        }
        return httpFallback;
    }

    private static bool IsLoopbackHost(Uri uri) =>
        uri.IsLoopback
        || string.Equals(uri.Host, "0.0.0.0", StringComparison.Ordinal)
        || string.Equals(uri.Host, "::", StringComparison.Ordinal);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!_started && _spec is not null && _factory is not null)
        {
            _started = true;
            Terminal.Start(_spec, _factory);
            Dispatcher.UIThread.Post(Terminal.FocusTerminal, DispatcherPriority.Input);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        CancelCopyFeedback();
    }

    /// <summary>Restarts the login command cleanly, clearing any previously detected OAuth URL.</summary>
    public async Task RestartAsync()
    {
        if (_restarting) return;
        _restarting = true;
        RestartButton.IsEnabled = false;
        RetryButton.IsEnabled = false;
        try
        {
            SetDetectedAuthUrl(null);
            SetRunningState();
            await Terminal.RestartAsync();
            Terminal.FocusTerminal();
        }
        finally
        {
            _restarting = false;
            RestartButton.IsEnabled = true;
            RetryButton.IsEnabled = true;
        }
    }

    public Task StopAsync()
    {
        CancelCopyFeedback();
        return Terminal.CloseAsync();
    }

    private void OnTerminalFrameUpdated(object? sender, EventArgs e)
    {
        var url = PickAuthUrl(Terminal.ExtractVisibleWebUrls());
        if (!string.IsNullOrEmpty(url))
        {
            SetDetectedAuthUrl(url);
        }
    }

    private void OnTerminalPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // A user can also press Enter inside the terminal after exit to restart it.
        if (e.Property == TerminalControl.IsRunningProperty && Terminal.IsRunning)
        {
            SetDetectedAuthUrl(null);
            SetRunningState();
        }
    }

    private void OnTerminalExited(object? sender, int? code)
    {
        // Once the login process ends, any local OAuth callback URL it printed is no longer active.
        SetDetectedAuthUrl(null);
        if (code == 0)
        {
            RunningSpinner.IsVisible = false;
            SuccessIcon.IsVisible = true;
            ErrorIcon.IsVisible = false;
            StatusText.Text = AppLangResources.Get(AppLangResourceKind.AccountLoginStatusSuccess);
            RestartButton.IsVisible = true;
            RetryButton.IsVisible = false;
        }
        else
        {
            RunningSpinner.IsVisible = false;
            SuccessIcon.IsVisible = false;
            ErrorIcon.IsVisible = true;
            StatusText.Text = AppLangResources.Format(AppLangResourceKind.AccountLoginStatusExited, code?.ToString() ?? "?");
            RestartButton.IsVisible = false;
            RetryButton.IsVisible = true;
        }
        Exited?.Invoke(this, code);
    }

    private void SetRunningState()
    {
        RunningSpinner.IsVisible = true;
        SuccessIcon.IsVisible = false;
        ErrorIcon.IsVisible = false;
        StatusText.Text = AppLangResources.Get(AppLangResourceKind.AccountLoginStatusRunning);
        RestartButton.IsVisible = true;
        RetryButton.IsVisible = false;
    }

    private void SetDetectedAuthUrl(string? url)
    {
        if (string.Equals(_authUrl, url, StringComparison.Ordinal)) return;
        _authUrl = url;
        var hasUrl = !string.IsNullOrEmpty(url);
        AuthLinkActions.IsVisible = hasUrl;
        Tooltip.SetTip(OpenLinkButton, hasUrl ? url : null);
        if (!hasUrl)
        {
            CancelCopyFeedback();
            ResetCopyIcon();
        }
    }

    private void OpenDetectedAuthUrl()
    {
        if (!string.IsNullOrEmpty(_authUrl))
        {
            _openUrl(_authUrl);
        }
    }

    private async Task CopyDetectedAuthUrlAsync()
    {
        if (string.IsNullOrEmpty(_authUrl)) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;

        await clipboard.SetTextAsync(_authUrl);
        CancelCopyFeedback();
        CopyLinkIcon.IsVisible = false;
        CopiedLinkIcon.IsVisible = true;
        Tooltip.SetTip(CopyLinkButton, AppLangResources.Get(AppLangResourceKind.AccountLoginCopiedLink));

        var cts = new CancellationTokenSource();
        _copyFeedbackCts = cts;
        try
        {
            await Task.Delay(1800, cts.Token);
            if (!cts.IsCancellationRequested)
            {
                ResetCopyIcon();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ResetCopyIcon()
    {
        CopyLinkIcon.IsVisible = true;
        CopiedLinkIcon.IsVisible = false;
        Tooltip.SetTip(CopyLinkButton, AppLangResources.Get(AppLangResourceKind.AccountLoginCopyLink));
    }

    private void CancelCopyFeedback()
    {
        var cts = _copyFeedbackCts;
        _copyFeedbackCts = null;
        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }
}
