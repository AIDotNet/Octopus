using Avalonia;
using Avalonia.Controls;
using System.Linq.Expressions;
using Avalonia.Media;
using Avalonia.Data;
using Avalonia.Layout;
using Octopus.App.Localization;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using Avalonia.Input;
using Avalonia.VisualTree;
using Octopus.App.Common;
using Octopus.App.Composition;

namespace Octopus.App.Explorer;

/// <summary>
/// The right panel: the files of the current project's folder and, when the folder is in a git repository, a source
/// control tab; for a project on an SSH host, a tunnels tab too. The shell hosts one instance, keeps <see cref="ProjectPath"/> and <see cref="ProjectName"/> on the
/// current project, and opens a file tab on <see cref="FileOpenRequested"/>.
/// </summary>
public partial class ExplorerPanelView : UserControl
{
    public static readonly StyledProperty<string?> ProjectPathProperty =
        AvaloniaProperty.Register<ExplorerPanelView, string?>(nameof(ProjectPath));

    public static readonly StyledProperty<string?> ProjectNameProperty =
        AvaloniaProperty.Register<ExplorerPanelView, string?>(nameof(ProjectName));

    /// <summary>The SSH host of the current project; null when its folder is on this computer.</summary>
    public static readonly StyledProperty<string?> ProjectSshHostProperty =
        AvaloniaProperty.Register<ExplorerPanelView, string?>(nameof(ProjectSshHost));

    /// <summary>Room the top row leaves free at its right end for what the window floats there (the panel switch, the caption buttons).</summary>
    public static readonly StyledProperty<double> TrailingInsetProperty =
        AvaloniaProperty.Register<ExplorerPanelView, double>(nameof(TrailingInset), 44);

    private Window? _window;
    private bool _settingProject;

    static ExplorerPanelView()
    {
        ProjectPathProperty.Changed.AddClassHandler<ExplorerPanelView>((view, e) => view.OnProjectChanged());
        TrailingInsetProperty.Changed.AddClassHandler<ExplorerPanelView>((view, e) => view.ApplyTrailingInset());
        // A closed panel (the shell hides it) does no work; it catches up when it opens again.
        IsVisibleProperty.Changed.AddClassHandler<ExplorerPanelView>((view, e) => view.Model.IsActive = view.IsVisible);
        ProjectNameProperty.Changed.AddClassHandler<ExplorerPanelView>((view, e) => view.Model.ProjectName = view.ProjectName ?? (view.ProjectPath is { } p ? PathText.NameOf(p) : ""));
    }

    public ExplorerPanelView()
    {
        Model = AppHost.Get<ExplorerPanelViewModel>();
        Model.FileOpenRequested += (_, path) => FileOpenRequested?.Invoke(this, path);
        Model.DiffRequested += (_, request) => DiffRequested?.Invoke(this, request);
        // The view model is attached after the tree is built: a child with its own DataContext binding would otherwise
        // inherit this one for a moment and its compiled bindings would throw an InvalidCastException.
        InitializeComponent();
        DataContext = Model;
        ApplyTrailingInset();
        Model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ExplorerPanelViewModel.IsGitRepository) or nameof(ExplorerPanelViewModel.IsSshProject)) SyncOptions();
            else if (e.PropertyName == nameof(ExplorerPanelViewModel.GitTabTooltip) && _gitOption is not null) _gitOption.Title = Model.GitTabTooltip;
            else if (e.PropertyName == nameof(ExplorerPanelViewModel.TunnelsTabTooltip) && _tunnelsOption is not null) _tunnelsOption.Title = Model.TunnelsTabTooltip;
        };
        _filesOption = TabOption("files", LucideIconKind.Files, AppLangResourceKind.ExplorerTabFiles, m => m.IsFilesTab);
        SyncOptions();
    }

    private readonly ToggleGroupOption _filesOption;
    private ToggleGroupOption? _gitOption;
    private ToggleGroupOption? _sessionsOption;
    private ToggleGroupOption? _tunnelsOption;

    /// <summary>
    /// The tabs in their order: Files; Git while the folder is in a repository (with the number of changed files);
    /// Sessions for a folder on this computer; Tunnels while the project is on an SSH host (with how many run).
    /// </summary>
    private void SyncOptions()
    {
        _gitOption ??= TabOption("git", LucideIconKind.GitBranch, AppLangResourceKind.ExplorerTabChanges, m => m.IsGitTab,
            m => m.ChangeCountText, m => m.HasChanges, ArcDesignTokenKind.Accent);
        _gitOption.Title = Model.GitTabTooltip;
        _sessionsOption ??= TabOption("sessions", LucideIconKind.History, AppLangResourceKind.ExplorerTabSessions, m => m.IsSessionsTab);
        _tunnelsOption ??= TabOption("tunnels", LucideIconKind.Waypoints, AppLangResourceKind.ExplorerTabTunnels, m => m.IsTunnelsTab,
            m => m.RunningTunnelsText, m => m.HasRunningTunnels, ArcDesignTokenKind.Success);
        _tunnelsOption.Title = Model.TunnelsTabTooltip;

        var wanted = new List<ToggleGroupOption> { _filesOption };
        if (Model.IsGitRepository) wanted.Add(_gitOption);
        if (!Model.IsSshProject) wanted.Add(_sessionsOption);
        else wanted.Add(_tunnelsOption);
        if (Tabs.Items.Cast<object>().SequenceEqual(wanted)) return;
        Tabs.Items.Clear();
        foreach (var option in wanted) Tabs.Items.Add(option);
        Tabs.SelectedValue = Model.TabKey;
    }

    /// <summary>
    /// One tab: its icon, its name while it is the chosen one (the others stay narrow), and a number when there is
    /// one to show. The full name is the tooltip.
    /// </summary>
    private ToggleGroupOption TabOption(
        string value,
        LucideIconKind icon,
        AppLangResourceKind label,
        Expression<Func<ExplorerPanelViewModel, bool>> isSelected,
        Expression<Func<ExplorerPanelViewModel, string>>? count = null,
        Expression<Func<ExplorerPanelViewModel, bool>>? hasCount = null,
        ArcDesignTokenKind countColor = ArcDesignTokenKind.TextMuted)
    {
        var name = AppLangResources.Get(label);
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        content.Children.Add(new LucideIcon { Kind = icon, Size = 15, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock
        {
            Text = name,
            VerticalAlignment = VerticalAlignment.Center,
            [!TextBlock.IsVisibleProperty] = CompiledBinding.Create(isSelected, source: Model),
        });
        if (count is not null && hasCount is not null)
        {
            var number = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeight.SemiBold,
                [!TextBlock.TextProperty] = CompiledBinding.Create(count, source: Model),
                [!TextBlock.IsVisibleProperty] = CompiledBinding.Create(hasCount, source: Model),
            };
            ArcTokens.Bind(number, TextBlock.FontSizeProperty, ArcDesignTokenKind.TypeXS);
            ArcTokens.Bind(number, TextBlock.ForegroundProperty, countColor);
            content.Children.Add(number);
        }
        return new ToggleGroupOption { Value = value, Label = name, Title = name, Content = content };
    }

    /// <summary>Absolute path of the current project's folder; null when there is no project.</summary>
    public string? ProjectPath
    {
        get => GetValue(ProjectPathProperty);
        set => SetValue(ProjectPathProperty, value);
    }

    public string? ProjectName
    {
        get => GetValue(ProjectNameProperty);
        set => SetValue(ProjectNameProperty, value);
    }

    public string? ProjectSshHost
    {
        get => GetValue(ProjectSshHostProperty);
        set => SetValue(ProjectSshHostProperty, value);
    }

    public double TrailingInset
    {
        get => GetValue(TrailingInsetProperty);
        set => SetValue(TrailingInsetProperty, value);
    }

    public ExplorerPanelViewModel Model { get; }

    /// <summary>Raised with the absolute path of a file the user wants opened as a tab.</summary>
    public event EventHandler<string>? FileOpenRequested;

    /// <summary>Raised with the changed file whose diff the user wants opened as a tab.</summary>
    public event EventHandler<Octopus.App.Files.DiffTabRequest>? DiffRequested;

    private void ApplyTrailingInset() => TopBar.ColumnDefinitions[2].Width = new GridLength(TrailingInset);

    /// <summary>
    /// Points the panel at a project. The folder is read again only when the folder or the host changed: a rename keeps
    /// what is open in the tree.
    /// </summary>
    public void SetProject(string? path, string? name, string? sshHost)
    {
        var changed = path != ProjectPath || sshHost != ProjectSshHost;
        _settingProject = true;
        try
        {
            ProjectSshHost = sshHost;
            ProjectName = name;
            ProjectPath = path;
        }
        finally
        {
            _settingProject = false;
        }
        if (changed) OnProjectChanged();
    }

    private void OnProjectChanged()
    {
        if (!_settingProject) _ = Model.SetProjectAsync(ProjectPath, ProjectName, ProjectSshHost);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null) _window.Activated += OnWindowActivated;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_window is not null) _window.Activated -= OnWindowActivated;
        _window = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnWindowActivated(object? sender, EventArgs e) => Model.NotifyActivated();

    /// <summary>The empty part of the top row drags the window, like the rest of the window's top edge.</summary>
    private void OnTopBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Control { } source && source.FindAncestorOfType<ToggleGroup>(includeSelf: true) is not null) return;
        if (TopLevel.GetTopLevel(this) is Window window && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            window.BeginMoveDrag(e);
        }
    }
}
