using Avalonia.Controls;
using Avalonia.Interactivity;
using Octopus.App.Common;
using Octopus.App.Localization;
using Octopus.App.Services;
using Octopus.Ssh;
using TKCode.Arc;

namespace Octopus.App.Dialogs;

/// <summary>
/// The form of the new project dialog: a location (this computer or an SSH host), a folder (typed or browsed) and a name
/// that follows the folder until it is edited.
/// </summary>
public partial class NewProjectView : UserControl
{
    /// <summary>The location option for this computer.</summary>
    private const string LocalValue = "local";
    /// <summary>The prefix of an SSH host's option value, so that no host alias can be mistaken for <see cref="LocalValue"/>.</summary>
    private const string HostPrefix = "ssh:";

    private readonly IPickerService? _pickers;
    private string _suggestedName = "";

    public NewProjectView() : this(null, null)
    {
    }

    /// <param name="hosts">The SSH hosts offered as locations; with none, the location is not shown.</param>
    public NewProjectView(IPickerService? pickers, NewProjectChoice? initial, IReadOnlyList<SshHost>? hosts = null)
    {
        _pickers = pickers;
        InitializeComponent();
        ConfigureLocation(hosts ?? [], initial?.SshHost);
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
        OnLocationChanged();
    }

    public string FolderPath => (PathInput.Text ?? "").Trim();

    public string ProjectName => (NameInput.Text ?? "").Trim();

    /// <summary>The SSH host the folder is on, or null for a folder on this computer.</summary>
    public string? SshHost =>
        LocationSelect.SelectedValue is string value && value.StartsWith(HostPrefix, StringComparison.Ordinal)
            ? value[HostPrefix.Length..]
            : null;

    /// <summary>
    /// The message for a folder that cannot be used, or null when it can. A folder on an SSH host is not on this computer,
    /// so only its emptiness is checked; the host is checked when a terminal opens there.
    /// </summary>
    public static string? Validate(string path, string? sshHost = null) =>
        path.Length == 0 ? AppLangResources.Get(AppLangResourceKind.NewProjectFolderRequired)
        : sshHost is null && !Directory.Exists(ExpandHome(path)) ? AppLangResources.Get(AppLangResourceKind.NewProjectFolderMissing)
        : null;

    /// <summary>A leading ~ stands for the home folder, as in a shell.</summary>
    public static string ExpandHome(string path) =>
        path == "~" || path.StartsWith("~/", StringComparison.Ordinal)
            ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[1..].TrimStart('/'))
            : path;

    public void ShowError(string? message) => PathInput.ErrorMessage = message;

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        PathInput.Focus();
    }

    /// <summary>Offers this computer and each host; the location is shown only when there is a host to choose.</summary>
    private void ConfigureLocation(IReadOnlyList<SshHost> hosts, string? initialHost)
    {
        LocationSelect.IsVisible = hosts.Count > 0;
        if (hosts.Count == 0) return;
        var options = new List<SelectOption> { new(LocalValue, AppLangResources.Get(AppLangResourceKind.NewProjectLocalMachine)) };
        foreach (var host in hosts)
        {
            options.Add(new SelectOption(HostPrefix + host.Alias, host.HostName == host.Alias ? host.Alias : $"{host.Alias} ({host.HostName})"));
        }
        LocationSelect.ItemsSource = options;
        var chosen = initialHost is not null && hosts.Any(h => h.Alias == initialHost) ? HostPrefix + initialHost : LocalValue;
        LocationSelect.SelectedValue = chosen;
    }

    /// <summary>A folder on a host is a path there: the label, the hint and the browse button follow that.</summary>
    private void OnLocationChanged()
    {
        var remote = SshHost is not null;
        PathInput.Label = AppLangResources.Get(remote ? AppLangResourceKind.NewProjectRemoteFolder : AppLangResourceKind.NewProjectFolder);
        PathInput.Placeholder = AppLangResources.Get(remote ? AppLangResourceKind.NewProjectRemoteFolderPlaceholder : AppLangResourceKind.NewProjectFolderPlaceholder);
        PathInput.Description = remote ? AppLangResources.Get(AppLangResourceKind.NewProjectRemoteHint) : null;
        BrowseButton.IsVisible = !remote;
        PathInput.ErrorMessage = null;
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
        if (_pickers is null) return;
        var picked = await _pickers.PickFolderAsync();
        if (picked is not null) PathInput.Text = picked;
    }

    private static string SuggestName(string? path) =>
        string.IsNullOrWhiteSpace(path) ? "" : PathText.NameOf(path.Trim().TrimEnd('/', '\\'));
}
