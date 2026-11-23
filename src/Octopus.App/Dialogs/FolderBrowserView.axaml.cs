using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Octopus.App.Localization;
using Octopus.FileSystem;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace Octopus.App.Dialogs;

/// <summary>One row in the folder browser's directory list.</summary>
public sealed class FolderBrowserRow(FolderBrowseEntry entry)
{
    public FolderBrowseEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public string Path => Entry.Path;
    public bool IsHidden => Entry.IsHidden;
    public bool IsExistingProject => Entry.IsExistingProject;
    public LucideIconKind Icon => Entry.Kind == FolderEntryKind.Drive ? LucideIconKind.Monitor : LucideIconKind.Folder;
}

/// <summary>
/// Built-in cross-platform folder browser used inside the New Project dialog for this computer, paired Octopus hosts,
/// and SSH hosts. Supports breadcrumbs, direct path navigation, quick places, existing host projects, hidden toggle,
/// local filtering, and inline folder creation.
/// </summary>
public partial class FolderBrowserView : UserControl
{
    private readonly ObservableCollection<FolderBrowserRow> _rows = [];
    private IFolderBrowser? _browser;
    private FolderBrowsePage? _page;
    private CancellationTokenSource? _loadCts;
    private bool _showHidden;
    private bool _isLoading;
    private bool _hasError;
    private string? _lastRequestedPath;
    private bool _lastRequestedRoots;
    private bool _lastFallback;

    public FolderBrowserView()
    {
        InitializeComponent();
        FolderList.ItemsSource = _rows;

        UpButton.Click += (_, _) => NavigateUp();
        HomeButton.Click += (_, _) =>
        {
            if (_page is { HomePath.Length: > 0 }) _ = NavigateAsync(_page.HomePath, listRoots: false, fallbackToParent: false);
            else _ = NavigateAsync(null, listRoots: false, fallbackToParent: false);
        };
        QuickHomeButton.Click += (_, _) =>
        {
            if (_page is { HomePath.Length: > 0 }) _ = NavigateAsync(_page.HomePath, listRoots: false, fallbackToParent: false);
            else _ = NavigateAsync(null, listRoots: false, fallbackToParent: false);
        };
        RefreshButton.Click += (_, _) => Refresh();
        RetryButton.Click += (_, _) => _ = NavigateAsync(_lastRequestedPath, _lastRequestedRoots, _lastFallback);

        EditPathButton.Click += (_, _) => BeginEditPath();
        PathGoButton.Click += (_, _) => CommitEditPath();
        PathCancelButton.Click += (_, _) => EndEditPath();
        PathEditBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitEditPath(); e.Handled = true; }
            else if (e.Key == Key.Escape) { EndEditPath(); e.Handled = true; }
        };

        FilterInput.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == nameof(FilterInput.Text)) ApplyFilter();
        };

        HiddenToggleButton.Click += (_, _) =>
        {
            _showHidden = !_showHidden;
            HiddenToggleButton.Classes.Set("active", _showHidden);
            ApplyFilter();
        };

        NewFolderButton.Click += (_, _) => BeginNewFolder();
        CreateFolderConfirmButton.Click += (_, _) => _ = CommitNewFolderAsync();
        CreateFolderCancelButton.Click += (_, _) => EndNewFolder();
        NewFolderNameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { _ = CommitNewFolderAsync(); e.Handled = true; }
            else if (e.Key == Key.Escape) { EndNewFolder(); e.Handled = true; }
        };

        FolderList.SelectionChanged += (_, _) => UpdateSelectionPreview();
        FolderList.DoubleTapped += OnFolderListDoubleTapped;
        FolderList.PointerPressed += OnFolderListPointerPressed;
        FolderList.KeyDown += OnFolderListKeyDown;

        UpdateControlsState();
    }

    /// <summary>Raised whenever <see cref="SelectedPath"/> or <see cref="CanConfirm"/> changes.</summary>
    public event Action? SelectionChanged;

    /// <summary>Raised when the user confirms a folder directly (e.g., double-clicking an existing project shortcut).</summary>
    public event Action<string>? FolderConfirmed;

    /// <summary>Raised when the user presses Escape at the top level of the browser.</summary>
    public event Action? CancelRequested;

    /// <summary>The path that confirming now will pick (highlighted subfolder, or current directory), or null when none can be picked.</summary>
    public string? SelectedPath =>
        _hasError || _isLoading ? null
        : FolderList.SelectedItem is FolderBrowserRow row ? row.Path
        : _page is { CurrentPath.Length: > 0 } page ? page.CurrentPath
        : null;

    /// <summary>True when a usable folder path is currently selected.</summary>
    public bool CanConfirm => !string.IsNullOrEmpty(SelectedPath);

    /// <summary>The last loaded page (for tests and inspection).</summary>
    public FolderBrowsePage? CurrentPage => _page;

    /// <summary>Opens the browser against <paramref name="browser"/>, starting at <paramref name="initialPath"/> (or home).</summary>
    public Task OpenAsync(IFolderBrowser browser, string? initialPath = null)
    {
        CancelPending();
        _browser = browser;
        _page = null;
        _rows.Clear();
        FilterInput.Text = "";
        EndEditPath();
        EndNewFolder();
        QuickRootsPanel.Children.Clear();
        QuickProjectsPanel.Children.Clear();
        ExistingProjectsSection.IsVisible = false;
        UpdateSelectionPreview();
        var trimmed = string.IsNullOrWhiteSpace(initialPath) ? null : initialPath.Trim();
        return NavigateAsync(trimmed, listRoots: false, fallbackToParent: trimmed is not null);
    }

    /// <summary>Stops any in-flight remote or local listing when closing the browser view.</summary>
    public void CancelPending()
    {
        var previous = Interlocked.Exchange(ref _loadCts, null);
        if (previous is null) return;
        try { previous.Cancel(); } catch (ObjectDisposedException) { }
        previous.Dispose();
    }

    /// <summary>Handles Escape from an outer host (e.g. Dialog): closes inline editors first, else requests returning to the form.</summary>
    public bool HandleEscape()
    {
        if (NewFolderBar.IsVisible)
        {
            EndNewFolder();
            return true;
        }
        if (PathEditBar.IsVisible)
        {
            EndEditPath();
            return true;
        }
        CancelRequested?.Invoke();
        return true;
    }

    public Task NavigateAsync(string? path, bool listRoots = false, bool fallbackToParent = false, string? selectAfterPath = null)
    {
        if (_browser is null) return Task.CompletedTask;
        CancelPending();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        _lastRequestedPath = path;
        _lastRequestedRoots = listRoots;
        _lastFallback = fallbackToParent;
        return LoadCoreAsync(_browser, path, listRoots, fallbackToParent, selectAfterPath, cts);
    }

    private async Task LoadCoreAsync(IFolderBrowser browser, string? path, bool listRoots, bool fallbackToParent, string? selectAfterPath, CancellationTokenSource cts)
    {
        _isLoading = true;
        _hasError = false;
        EndEditPath();
        EndNewFolder();
        UpdateControlsState();

        try
        {
            var page = await browser.BrowseAsync(path, listRoots, fallbackToParent, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(_loadCts, cts)) return;
            _page = page;
            _isLoading = false;
            _hasError = false;
            FilterInput.Text = "";
            RebuildBreadcrumbs();
            RebuildSidebar();
            ApplyFilter(selectAfterPath);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (cts.IsCancellationRequested || !ReferenceEquals(_loadCts, cts)) return;
            _isLoading = false;
            _hasError = true;
            ErrorText.Text = e.Message;
            UpdateControlsState();
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                _loadCts = null;
                cts.Dispose();
            }
        }
    }

    private void NavigateUp()
    {
        if (_page is null || _page.ParentPath is null) return;
        if (_page.ParentPath.Length == 0)
        {
            _ = NavigateAsync(null, listRoots: true, fallbackToParent: false);
        }
        else
        {
            _ = NavigateAsync(_page.ParentPath, listRoots: false, fallbackToParent: false);
        }
    }

    private void Refresh()
    {
        if (_page is null)
        {
            _ = NavigateAsync(_lastRequestedPath, _lastRequestedRoots, _lastFallback);
            return;
        }
        if (_page.CurrentPath.Length == 0)
        {
            _ = NavigateAsync(null, listRoots: true, fallbackToParent: false);
        }
        else
        {
            _ = NavigateAsync(_page.CurrentPath, listRoots: false, fallbackToParent: false);
        }
    }

    private void BeginEditPath()
    {
        PathEditBox.Text = _page?.CurrentPath ?? _lastRequestedPath ?? "";
        BreadcrumbBar.IsVisible = false;
        PathEditBar.IsVisible = true;
        Dispatcher.UIThread.Post(() =>
        {
            PathEditBox.Focus();
        });
    }

    private void CommitEditPath()
    {
        var typed = (PathEditBox.Text ?? "").Trim();
        EndEditPath();
        if (typed.Length == 0)
        {
            _ = NavigateAsync(null, listRoots: false, fallbackToParent: false);
        }
        else
        {
            _ = NavigateAsync(typed, listRoots: false, fallbackToParent: false);
        }
    }

    private void EndEditPath()
    {
        PathEditBar.IsVisible = false;
        BreadcrumbBar.IsVisible = true;
    }

    private void BeginNewFolder()
    {
        if (!CanCreateHere()) return;
        NewFolderNameBox.Text = "";
        NewFolderError.Text = "";
        NewFolderError.IsVisible = false;
        NewFolderBar.IsVisible = true;
        Dispatcher.UIThread.Post(() => NewFolderNameBox.Focus());
    }

    private void EndNewFolder()
    {
        NewFolderBar.IsVisible = false;
        NewFolderError.Text = "";
        NewFolderError.IsVisible = false;
    }

    public async Task CommitNewFolderAsync()
    {
        if (_browser is null || _page is null || !CanCreateHere()) return;
        var raw = (NewFolderNameBox.Text ?? "").Trim();
        string cleanName;
        try
        {
            cleanName = FolderBrowserPaths.ValidateFolderName(raw);
        }
        catch (ArgumentException e)
        {
            NewFolderError.Text = e.Message;
            NewFolderError.IsVisible = true;
            return;
        }

        CreateFolderConfirmButton.IsEnabled = false;
        NewFolderError.IsVisible = false;
        try
        {
            var createdPath = await _browser.CreateFolderAsync(_page.CurrentPath, cleanName);
            EndNewFolder();
            // Reload the directory and highlight the newly created folder so the user can either enter or pick it immediately.
            await NavigateAsync(_page.CurrentPath, listRoots: false, fallbackToParent: false, selectAfterPath: createdPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            NewFolderError.Text = e.Message;
            NewFolderError.IsVisible = true;
        }
        finally
        {
            CreateFolderConfirmButton.IsEnabled = true;
        }
    }

    private void RebuildBreadcrumbs()
    {
        BreadcrumbPanel.Children.Clear();
        if (_page is null) return;

        // Windows virtual root ("Drives") or multi-drive prefix.
        var hasMultipleRoots = _page.Roots.Count > 1 || (_page.Separator == "\\" && _page.Roots.Count > 0);
        if (hasMultipleRoots)
        {
            var drivesButton = MakeCrumbButton(AppLangResources.Get(AppLangResourceKind.FolderBrowserDrives), () =>
                _ = NavigateAsync(null, listRoots: true, fallbackToParent: false));
            BreadcrumbPanel.Children.Add(drivesButton);
            if (_page.CurrentPath.Length == 0) return;
            BreadcrumbPanel.Children.Add(MakeCrumbSeparator());
        }

        var segments = FolderBrowserPaths.SplitBreadcrumb(_page.CurrentPath, _page.Separator);
        for (var i = 0; i < segments.Count; i++)
        {
            if (i > 0) BreadcrumbPanel.Children.Add(MakeCrumbSeparator());
            var segment = segments[i];
            BreadcrumbPanel.Children.Add(MakeCrumbButton(segment.Label, () =>
                _ = NavigateAsync(segment.FullPath, listRoots: false, fallbackToParent: false)));
        }

        // Scroll breadcrumbs to the right so the deepest folder is always visible.
        Dispatcher.UIThread.Post(() => BreadcrumbScroller.Offset = new Vector(BreadcrumbScroller.Extent.Width, 0));
    }

    private static Avalonia.Controls.Button MakeCrumbButton(string label, Action onClick)
    {
        var button = new Avalonia.Controls.Button
        {
            Content = label,
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Classes.Add("crumb");
        button.Click += (_, _) => onClick();
        return button;
    }

    private static Control MakeCrumbSeparator() => new LucideIcon
    {
        Kind = LucideIconKind.ChevronRight,
        Size = 11,
        VerticalAlignment = VerticalAlignment.Center,
        Opacity = 0.5,
    };

    private void RebuildSidebar()
    {
        QuickRootsPanel.Children.Clear();
        QuickProjectsPanel.Children.Clear();
        if (_page is null)
        {
            ExistingProjectsSection.IsVisible = false;
            return;
        }

        foreach (var root in _page.Roots)
        {
            var target = root.Path;
            QuickRootsPanel.Children.Add(MakeSidebarRow(LucideIconKind.Monitor, root.Name, target,
                onClick: () => _ = NavigateAsync(target, listRoots: false, fallbackToParent: false)));
        }

        var projects = _page.ExistingProjects;
        ExistingProjectsSection.IsVisible = projects is { Count: > 0 };
        if (projects is null) return;

        foreach (var project in projects)
        {
            var target = project.Path;
            QuickProjectsPanel.Children.Add(MakeSidebarRow(LucideIconKind.Folder, project.Name, target,
                onClick: () => _ = NavigateAsync(target, listRoots: false, fallbackToParent: false),
                onDoubleClick: () => FolderConfirmed?.Invoke(target)));
        }
    }

    private static Avalonia.Controls.Button MakeSidebarRow(LucideIconKind icon, string label, string tooltip, Action onClick, Action? onDoubleClick = null)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new LucideIcon { Kind = icon, Size = 14, VerticalAlignment = VerticalAlignment.Center });
        var text = new TextBlock
        {
            Text = label,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
        };
        text.Classes.Add("body");
        content.Children.Add(text);

        var button = new Avalonia.Controls.Button
        {
            Content = content,
            Padding = new Thickness(6, 5),
        };
        button.Classes.Add("row");
        ToolTip.SetTip(button, tooltip);
        button.Click += (_, _) => onClick();
        if (onDoubleClick is not null)
        {
            button.DoubleTapped += (_, e) =>
            {
                onDoubleClick();
                e.Handled = true;
            };
        }
        return button;
    }

    private void ApplyFilter(string? selectPath = null)
    {
        var previousSelected = selectPath ?? (FolderList.SelectedItem as FolderBrowserRow)?.Path;
        _rows.Clear();
        if (_page is null)
        {
            UpdateControlsState();
            return;
        }

        var query = (FilterInput.Text ?? "").Trim();
        var filtered = _page.Entries
            .Where(e => _showHidden || !e.IsHidden || query.Length > 0)
            .Where(e => query.Length == 0 || e.Name.Contains(query, StringComparison.OrdinalIgnoreCase));

        FolderBrowserRow? toSelect = null;
        foreach (var entry in filtered)
        {
            var row = new FolderBrowserRow(entry);
            _rows.Add(row);
            if (previousSelected is not null && string.Equals(entry.Path, previousSelected, StringComparison.OrdinalIgnoreCase))
            {
                toSelect = row;
            }
        }

        FolderList.SelectedItem = toSelect;
        if (toSelect is not null)
        {
            FolderList.ScrollIntoView(toSelect);
        }

        UpdateControlsState();
    }

    private void UpdateControlsState()
    {
        LoadingOverlay.IsVisible = _isLoading;
        ErrorOverlay.IsVisible = _hasError && !_isLoading;
        UpButton.IsEnabled = !_isLoading && _page?.ParentPath is not null;
        HomeButton.IsEnabled = !_isLoading && _browser is not null;
        RefreshButton.IsEnabled = !_isLoading && _browser is not null;
        NewFolderButton.IsEnabled = CanCreateHere();

        var hasEntries = _rows.Count > 0;
        var hasQuery = !string.IsNullOrWhiteSpace(FilterInput.Text);
        EmptyStateText.IsVisible = !_isLoading && !_hasError && _page is not null && !hasEntries;
        if (EmptyStateText.IsVisible)
        {
            EmptyStateText.Text = AppLangResources.Get(hasQuery ? AppLangResourceKind.FolderBrowserNoMatches : AppLangResourceKind.FolderBrowserEmpty);
        }

        UpdateSelectionPreview();
    }

    private void UpdateSelectionPreview()
    {
        var path = SelectedPath;
        if (string.IsNullOrEmpty(path))
        {
            SelectedPathText.Text = AppLangResources.Get(AppLangResourceKind.FolderBrowserNoneSelected);
            SelectedPathText.Opacity = 0.6;
            ToolTip.SetTip(SelectedPathText, null);
        }
        else
        {
            SelectedPathText.Text = path;
            SelectedPathText.Opacity = 1.0;
            ToolTip.SetTip(SelectedPathText, path);
        }
        SelectionChanged?.Invoke();
    }

    private bool CanCreateHere() =>
        !_isLoading && !_hasError && _browser is { CanCreateFolder: true } && _page is { CurrentPath.Length: > 0 };

    private void OnFolderListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (FolderList.SelectedItem is FolderBrowserRow row)
        {
            _ = NavigateAsync(row.Path, listRoots: false, fallbackToParent: false);
            e.Handled = true;
        }
    }

    private void OnFolderListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Clicking empty space below the rows clears the row selection so the currently open folder is chosen.
        if (e.Source is Visual visual && visual.FindAncestorOfType<ListBoxItem>() is null)
        {
            FolderList.SelectedItem = null;
        }
    }

    private void OnFolderListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && FolderList.SelectedItem is FolderBrowserRow row)
        {
            _ = NavigateAsync(row.Path, listRoots: false, fallbackToParent: false);
            e.Handled = true;
        }
        else if (e.Key == Key.Back)
        {
            NavigateUp();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = HandleEscape();
        }
    }
}
