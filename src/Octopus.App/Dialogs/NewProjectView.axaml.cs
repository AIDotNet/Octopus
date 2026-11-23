using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Octopus.App.Common;
using Octopus.App.Localization;
using Octopus.App.Services;
using Octopus.FileSystem;
using Octopus.Ssh;
using TKCode.Arc;

namespace Octopus.App.Dialogs;

/// <summary>Which machine the new project dialog is browsing folders on.</summary>
public sealed record FolderBrowserTarget(string? SshHost, string? RemoteHost, string DisplayName);

/// <summary>
/// The form of the new project dialog: a location (this computer, a paired Octopus host, or an SSH host), a folder
/// (typed or picked via the built-in folder browser in the same dialog), and a name that follows the folder until edited.
/// </summary>
public partial class NewProjectView : UserControl
{
    /// <summary>The location option for this computer.</summary>
    private const string LocalValue = "local";
    /// <summary>The prefix of an SSH host's option value, so that no host alias can be mistaken for <see cref="LocalValue"/>.</summary>
    private const string HostPrefix = "ssh:";
    /// <summary>The prefix of the option of another computer that runs Octopus; what follows is its device id.</summary>
    private const string RemotePrefix = "octopus:";

    private readonly IPickerService? _pickers;
    private readonly Func<FolderBrowserTarget, IFolderBrowser?>? _browserFactory;
    private readonly Dictionary<string, string> _remoteNames = new(StringComparer.Ordinal);
    private string _suggestedName = "";

    public NewProjectView() : this(null, null)
    {
    }

    /// <param name="hosts">The SSH hosts offered as locations; with none (and no remote hosts), the location is not shown.</param>
    /// <param name="remoteHosts">The paired Octopus hosts offered as locations: device id and name.</param>
    /// <param name="browserFactory">Creates the built-in folder browser for the selected location (local, Octopus host, or SSH host).</param>
    public NewProjectView(IPickerService? pickers, NewProjectChoice? initial, IReadOnlyList<SshHost>? hosts = null,
        IReadOnlyList<(string Id, string Name)>? remoteHosts = null,
        Func<FolderBrowserTarget, IFolderBrowser?>? browserFactory = null)
    {
        _pickers = pickers;
        _browserFactory = browserFactory;
        InitializeComponent();
        ConfigureLocation(hosts ?? [], initial?.SshHost, remoteHosts ?? [], initial?.RemoteHost);
        PathInput.Text = initial?.Path ?? "";
        NameInput.Text = initial?.Name ?? "";
        _suggestedName = SuggestName(PathInput.Text);
        if (string.IsNullOrEmpty(NameInput.Text)) NameInput.Text = _suggestedName;
        // The property, not the TextChanged event: the event is raised later, from the dispatcher.
        PathInput.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == nameof(PathInput.Text)) OnPathChanged();
        };
        LocationSelect.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == nameof(LocationSelect.SelectedValue)) OnLocationChanged();
        };
        BrowseButton.Click += OnBrowseClick;
        BrowserView.SelectionChanged += () => BrowseSelectionChanged?.Invoke();
        BrowserView.FolderConfirmed += path => ApplyPickedFolder(path);
        BrowserView.CancelRequested += CancelBrowse;
        AddHandler(KeyDownEvent, OnViewKeyDown, RoutingStrategies.Tunnel);
        OnLocationChanged();
    }

    /// <summary>Raised when switching between the form view and the in-dialog folder browser view.</summary>
    public event Action? BrowseModeChanged;

    /// <summary>Raised when the folder browser's selection or confirmability changes.</summary>
    public event Action? BrowseSelectionChanged;

    /// <summary>Raised when the selected location changes (so the host dialog can update its description).</summary>
    public event Action? LocationChanged;

    public bool IsBrowsing => BrowserView.IsVisible;

    public bool CanConfirmBrowse => IsBrowsing && BrowserView.CanConfirm;

    public string FolderPath => (PathInput.Text ?? "").Trim();

    public string ProjectName => (NameInput.Text ?? "").Trim();

    /// <summary>The SSH host the folder is on, or null for a folder on this computer.</summary>
    public string? SshHost =>
        LocationSelect.SelectedValue is string value && value.StartsWith(HostPrefix, StringComparison.Ordinal)
            ? value[HostPrefix.Length..]
            : null;

    /// <summary>The paired Octopus host the folder is on (its device id), or null.</summary>
    public string? RemoteHost =>
        LocationSelect.SelectedValue is string value && value.StartsWith(RemotePrefix, StringComparison.Ordinal)
            ? value[RemotePrefix.Length..]
            : null;

    /// <summary>Human-readable name of the currently chosen machine.</summary>
    public string LocationDisplayName =>
        RemoteHost is { } remoteId && _remoteNames.TryGetValue(remoteId, out var remoteName) ? remoteName
        : SshHost is { } sshAlias ? sshAlias
        : AppLangResources.Get(AppLangResourceKind.NewProjectLocalMachine);

    /// <summary>Dialog description text matching the currently selected location.</summary>
    public string DialogDescription =>
        RemoteHost is not null ? AppLangResources.Format(AppLangResourceKind.NewProjectRemoteDescription, LocationDisplayName)
        : SshHost is not null ? AppLangResources.Format(AppLangResourceKind.NewProjectSshDescription, LocationDisplayName)
        : AppLangResources.Get(AppLangResourceKind.NewProjectDescription);

    /// <summary>Dialog title when browsing folders for the currently selected location.</summary>
    public string BrowseDialogTitle =>
        RemoteHost is not null || SshHost is not null
            ? AppLangResources.Format(AppLangResourceKind.FolderBrowserHostTitle, LocationDisplayName)
            : AppLangResources.Get(AppLangResourceKind.FolderBrowserTitle);

    /// <summary>
    /// The message for a folder that cannot be used, or null when it can. A folder on another machine is not on this
    /// computer, so only its emptiness is checked; that machine says the rest (an SSH host when a terminal opens there,
    /// an Octopus host when the project is added).
    /// </summary>
    public static string? Validate(string path, string? sshHost = null, string? remoteHost = null) =>
        path.Length == 0 ? AppLangResources.Get(AppLangResourceKind.NewProjectFolderRequired)
        : sshHost is null && remoteHost is null && !Directory.Exists(ExpandHome(path)) ? AppLangResources.Get(AppLangResourceKind.NewProjectFolderMissing)
        : null;

    /// <summary>A leading ~ stands for the home folder, as in a shell.</summary>
    public static string ExpandHome(string path) =>
        path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
            ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[1..].TrimStart('/', '\\'))
            : path;

    public void ShowError(string? message) => PathInput.ErrorMessage = message;

    /// <summary>Switches the dialog into folder browser mode for the currently selected location.</summary>
    public Task OpenBrowseAsync()
    {
        var target = new FolderBrowserTarget(SshHost, RemoteHost, LocationDisplayName);
        var browser = _browserFactory?.Invoke(target)
            ?? (SshHost is null && RemoteHost is null ? new LocalFolderBrowser(LocationDisplayName) : null);
        if (browser is null) return Task.CompletedTask;

        FormPanel.IsVisible = false;
        BrowserView.IsVisible = true;
        BrowseModeChanged?.Invoke();
        return BrowserView.OpenAsync(browser, FolderPath);
    }

    /// <summary>Applies the currently selected folder in the browser and returns to the form view.</summary>
    public void ConfirmBrowse()
    {
        if (!IsBrowsing || BrowserView.SelectedPath is not { Length: > 0 } picked) return;
        ApplyPickedFolder(picked);
    }

    /// <summary>Cancels folder browsing and returns to the form view without changing the path.</summary>
    public void CancelBrowse()
    {
        if (!IsBrowsing) return;
        BrowserView.CancelPending();
        BrowserView.IsVisible = false;
        FormPanel.IsVisible = true;
        BrowseModeChanged?.Invoke();
        PathInput.Focus();
    }

    /// <summary>Handles Escape while browsing: closes inline editors first, or returns to the form view.</summary>
    public bool HandleEscape() => IsBrowsing && BrowserView.HandleEscape();

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (!IsBrowsing) PathInput.Focus();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        BrowserView.CancelPending();
        base.OnUnloaded(e);
    }

    private void ApplyPickedFolder(string picked)
    {
        BrowserView.CancelPending();
        BrowserView.IsVisible = false;
        FormPanel.IsVisible = true;
        PathInput.Text = picked;
        BrowseModeChanged?.Invoke();
        NameInput.Focus();
    }

    /// <summary>Offers this computer and each host; the location is shown only when there is a host to choose.</summary>
    private void ConfigureLocation(IReadOnlyList<SshHost> hosts, string? initialHost, IReadOnlyList<(string Id, string Name)> remoteHosts, string? initialRemote)
    {
        LocationSelect.IsVisible = hosts.Count > 0 || remoteHosts.Count > 0;
        if (!LocationSelect.IsVisible) return;
        var options = new List<SelectOption> { new(LocalValue, AppLangResources.Get(AppLangResourceKind.NewProjectLocalMachine)) };
        // Computers that run Octopus first: there the project gets agents too.
        foreach (var (id, name) in remoteHosts)
        {
            _remoteNames[id] = name;
            options.Add(new SelectOption(RemotePrefix + id, string.Format(AppLangResources.Get(AppLangResourceKind.NewProjectOctopusHost), name)));
        }
        foreach (var host in hosts)
        {
            options.Add(new SelectOption(HostPrefix + host.Alias, host.HostName == host.Alias ? host.Alias : $"{host.Alias} ({host.HostName})"));
        }
        LocationSelect.ItemsSource = options;
        var chosen = initialRemote is not null && remoteHosts.Any(h => h.Id == initialRemote) ? RemotePrefix + initialRemote
            : initialHost is not null && hosts.Any(h => h.Alias == initialHost) ? HostPrefix + initialHost
            : LocalValue;
        LocationSelect.SelectedValue = chosen;
    }

    /// <summary>A folder on a host is a path there: the label, placeholder, hint and browse availability follow that.</summary>
    private void OnLocationChanged()
    {
        var remote = SshHost is not null || RemoteHost is not null;
        PathInput.Label = AppLangResources.Get(remote ? AppLangResourceKind.NewProjectRemoteFolder : AppLangResourceKind.NewProjectFolder);
        PathInput.Placeholder = AppLangResources.Get(remote ? AppLangResourceKind.NewProjectRemoteFolderPlaceholder : AppLangResourceKind.NewProjectFolderPlaceholder);
        var hint = RemoteHost is not null ? AppLangResources.Get(AppLangResourceKind.NewProjectOctopusHint)
            : remote ? AppLangResources.Get(AppLangResourceKind.NewProjectRemoteHint)
            : null;
        PathHint.Text = hint ?? "";
        PathHint.IsVisible = !string.IsNullOrEmpty(hint);
        // Built-in folder browser is available for local, paired Octopus hosts, and SSH hosts alike.
        BrowseButton.IsVisible = true;
        PathInput.ErrorMessage = null;
        LocationChanged?.Invoke();
    }

    private void OnPathChanged()
    {
        PathInput.ErrorMessage = null;
        var next = SuggestName(FolderPath);
        // The name follows the folder as long as nobody typed a different one.
        if (ProjectName.Length == 0 || ProjectName == _suggestedName) NameInput.Text = next;
        _suggestedName = next;
    }

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        if (_browserFactory is not null || (SshHost is null && RemoteHost is null))
        {
            await OpenBrowseAsync();
            return;
        }
        if (_pickers is not null && SshHost is null && RemoteHost is null)
        {
            var picked = await _pickers.PickFolderAsync();
            if (picked is not null) PathInput.Text = picked;
        }
    }

    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && IsBrowsing)
        {
            e.Handled = BrowserView.HandleEscape();
        }
    }

    private static string SuggestName(string? path) =>
        string.IsNullOrWhiteSpace(path) ? "" : PathText.NameOf(path.Trim().TrimEnd('/', '\\'));
}
