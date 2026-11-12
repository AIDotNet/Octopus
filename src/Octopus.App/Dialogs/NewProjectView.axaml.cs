using Avalonia.Controls;
using Avalonia.Interactivity;
using Octopus.App.Common;
using Octopus.App.Localization;
using Octopus.App.Services;

namespace Octopus.App.Dialogs;

/// <summary>The form of the new project dialog: a folder (typed or browsed) and a name that follows the folder until it is edited.</summary>
public partial class NewProjectView : UserControl
{
    private readonly IPickerService? _pickers;
    private string _suggestedName = "";

    public NewProjectView() : this(null, null)
    {
    }

    public NewProjectView(IPickerService? pickers, NewProjectChoice? initial)
    {
        _pickers = pickers;
        InitializeComponent();
        PathInput.Text = initial?.Path ?? "";
        NameInput.Text = initial?.Name ?? "";
        _suggestedName = SuggestName(PathInput.Text);
        if (string.IsNullOrEmpty(NameInput.Text)) NameInput.Text = _suggestedName;
        // The property, not the TextChanged event: the event is raised later, from the dispatcher.
        PathInput.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == nameof(PathInput.Text)) OnPathChanged();
        };
        BrowseButton.Click += OnBrowseClick;
    }

    public string FolderPath => (PathInput.Text ?? "").Trim();

    public string ProjectName => (NameInput.Text ?? "").Trim();

    /// <summary>The message for a folder that cannot be used, or null when it can.</summary>
    public static string? Validate(string path) =>
        path.Length == 0 ? AppLangResources.Get(AppLangResourceKind.NewProjectFolderRequired)
        : !Directory.Exists(ExpandHome(path)) ? AppLangResources.Get(AppLangResourceKind.NewProjectFolderMissing)
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
