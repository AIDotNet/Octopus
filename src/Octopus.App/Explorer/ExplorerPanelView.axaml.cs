using System.Linq.Expressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Octopus.App.Common;
using Octopus.App.Composition;
using Octopus.App.Localization;
using Octopus.App.Plugins;
using Octopus.App.Plugins.Rendering;
using Octopus.Plugins.Runtime;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;

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
    private bool _trailingInsetScheduled;

    static ExplorerPanelView()
    {
        ProjectPathProperty.Changed.AddClassHandler<ExplorerPanelView>((view, e) => view.OnProjectChanged());
        TrailingInsetProperty.Changed.AddClassHandler<ExplorerPanelView>((view, e) => view.ApplyTrailingInset());
        // A closed panel (the shell hides it) does no work; it catches up when it opens again.
        IsVisibleProperty.Changed.AddClassHandler<ExplorerPanelView>((view, e) =>
        {
            view.Model.IsActive = view.IsVisible;
            view.SyncPluginHost();
        });
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
        // The room changes with the panel's width, and the tabs' worst-case width with the available options and counts.
        // Skip intermediate label-reveal frames so switching tabs never makes the tab bar jump between rows.
        SizeChanged += (_, _) => ApplyTrailingInset();
        Tabs.SizeChanged += (_, _) => { if (!Tabs.IsLabelAnimating) ApplyTrailingInset(); };
        Tabs.LabelsSettled += ScheduleTrailingInset;
        Model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ExplorerPanelViewModel.HasProject) or nameof(ExplorerPanelViewModel.IsGitRepository) or nameof(ExplorerPanelViewModel.IsSshProject) or nameof(ExplorerPanelViewModel.IsHostProject) or nameof(ExplorerPanelViewModel.HasSubagentTab))
            {
                SyncOptions();
            }
            else if (e.PropertyName == nameof(ExplorerPanelViewModel.GitTabTooltip) && _gitOption is not null)
            {
                _gitOption.Title = Model.GitTabTooltip;
            }
            else if (e.PropertyName == nameof(ExplorerPanelViewModel.TunnelsTabTooltip) && _tunnelsOption is not null)
            {
                _tunnelsOption.Title = Model.TunnelsTabTooltip;
            }
            else if (e.PropertyName == nameof(ExplorerPanelViewModel.SubagentTabTooltip) && _subagentOption is not null)
            {
                _subagentOption.Title = Model.SubagentTabTooltip;
            }
            else if (e.PropertyName is nameof(ExplorerPanelViewModel.TabKey) or nameof(ExplorerPanelViewModel.ActivePluginPanelId) or nameof(ExplorerPanelViewModel.IsPluginPanelTab))
            {
                SyncPluginTabExpandedStates();
                SyncPluginHost();
            }
        };
        Model.PluginPanelsChanged += (_, _) =>
        {
            SyncOptions();
            SyncPluginHost();
        };
        _filesOption = TabOption("files", LucideIconKind.Files, AppLangResourceKind.ExplorerTabFiles, m => m.IsFilesTab);
        SyncOptions();
        SyncPluginHost();
    }

    private readonly ToggleGroupOption _filesOption;
    private ToggleGroupOption? _gitOption;
    private ToggleGroupOption? _sessionsOption;
    private ToggleGroupOption? _tunnelsOption;
    private ToggleGroupOption? _subagentOption;
    private readonly Dictionary<string, PluginTabVisual> _pluginTabVisuals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PluginPanelHost> _pluginHosts = new(StringComparer.Ordinal);

    private sealed class PluginTabVisual(ToggleGroupOption option, TabLabelReveal reveal, TextBlock title, TextBlock badge)
    {
        public ToggleGroupOption Option { get; } = option;
        public TabLabelReveal Reveal { get; } = reveal;
        public TextBlock Title { get; } = title;
        public TextBlock Badge { get; } = badge;
        public IDisposable? BadgeColorBinding { get; set; }
        public ArcDesignTokenKind? BoundBadgeColor { get; set; }
    }

    /// <summary>
    /// The tabs in their order: Files; Git while the folder is in a repository (with the number of changed files);
    /// Sessions for a folder on this computer; Tunnels while the project is on an SSH host (with how many run);
    /// followed by any plugin-contributed right panels eligible for the current context.
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
        _subagentOption ??= TabOption("subagent", LucideIconKind.Bot, AppLangResourceKind.ExplorerTabSubagent, m => m.IsSubagentTab,
            m => m.SubagentCountText, m => m.HasSubagentCount, ArcDesignTokenKind.Accent);
        _subagentOption.Title = Model.SubagentTabTooltip;

        var wanted = new List<ToggleGroupOption>();
        if (Model.HasProject)
        {
            wanted.Add(_filesOption);
            if (Model.IsGitRepository) wanted.Add(_gitOption);
            // Sessions are this computer's and tunnels an SSH host's: a project on a computer running Octopus has neither.
            if (Model.IsSshProject) wanted.Add(_tunnelsOption);
            else if (!Model.IsHostProject) wanted.Add(_sessionsOption);
        }
        if (Model.HasSubagentTab)
        {
            wanted.Add(_subagentOption);
        }

        var activePluginIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var panel in Model.VisiblePluginPanels)
        {
            activePluginIds.Add(panel.Id);
            var visual = GetOrCreatePluginTab(panel);
            UpdatePluginTab(visual, panel);
            wanted.Add(visual.Option);
        }

        foreach (var staleId in _pluginTabVisuals.Keys.Where(id => !activePluginIds.Contains(id)).ToList())
        {
            if (_pluginTabVisuals.Remove(staleId, out var removed)) removed.BadgeColorBinding?.Dispose();
        }
        foreach (var staleHost in _pluginHosts.Keys.Where(id => !activePluginIds.Contains(id)).ToList())
        {
            if (_pluginHosts.Remove(staleHost, out var host))
            {
                if (ReferenceEquals(PluginPanelSlot.Child, host)) PluginPanelSlot.Child = null;
                host.Dispose();
            }
        }

        SyncPluginTabExpandedStates();
        if (Tabs.Items.Cast<object>().SequenceEqual(wanted)) return;
        Tabs.ReplaceOptions(wanted, Model.TabKey);
        ScheduleTrailingInset();
    }

    private PluginTabVisual GetOrCreatePluginTab(PluginPanelItem panel)
    {
        if (_pluginTabVisuals.TryGetValue(panel.Id, out var existing)) return existing;
        var name = panel.DisplayTitle;
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        var bitmap = UiIcons.TryLoadPackageBitmap(panel.Host.Package.Directory, panel.Contribution.Icon);
        if (bitmap is not null)
        {
            content.Children.Add(new Image { Source = bitmap, Width = 15, Height = 15, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center });
        }
        else
        {
            var iconKind = UiIcons.Parse(panel.Contribution.Icon) ?? LucideIconKind.Puzzle;
            content.Children.Add(new LucideIcon { Kind = iconKind, Size = 15, VerticalAlignment = VerticalAlignment.Center });
        }
        var titleBlock = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
        var reveal = new TabLabelReveal
        {
            VerticalAlignment = VerticalAlignment.Center,
            IsExpanded = Model.TabKey == panel.TabKey,
            Child = titleBlock,
        };
        content.Children.Add(reveal);
        var badge = new TextBlock
        {
            Margin = new Thickness(5, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeight.SemiBold,
            IsVisible = false,
        };
        ArcTokens.Bind(badge, TextBlock.FontSizeProperty, ArcDesignTokenKind.TypeXS);
        content.Children.Add(badge);

        var option = new ToggleGroupOption { Value = panel.TabKey, Label = name, Title = name, Content = content };
        var visual = new PluginTabVisual(option, reveal, titleBlock, badge);
        _pluginTabVisuals[panel.Id] = visual;
        return visual;
    }

    private static void UpdatePluginTab(PluginTabVisual visual, PluginPanelItem panel)
    {
        var name = panel.DisplayTitle;
        visual.Title.Text = name;
        visual.Option.Label = name;
        visual.Option.Title = !string.IsNullOrWhiteSpace(panel.Tooltip)
            ? panel.Tooltip
            : string.IsNullOrWhiteSpace(panel.Badge) ? name : $"{name} ({panel.Badge})";
        visual.Badge.Text = panel.Badge ?? "";
        visual.Badge.IsVisible = !string.IsNullOrWhiteSpace(panel.Badge);
        var color = panel.BadgeColor switch
        {
            "danger" => ArcDesignTokenKind.Danger,
            "warning" => ArcDesignTokenKind.Warning,
            "success" => ArcDesignTokenKind.Success,
            "accent" => ArcDesignTokenKind.Accent,
            _ => ArcDesignTokenKind.TextMuted,
        };
        if (visual.BoundBadgeColor != color)
        {
            visual.BadgeColorBinding?.Dispose();
            visual.BoundBadgeColor = color;
            visual.BadgeColorBinding = ArcTokens.Bind(visual.Badge, TextBlock.ForegroundProperty, color);
        }
    }

    private void SyncPluginTabExpandedStates()
    {
        var current = Model.TabKey;
        foreach (var (id, visual) in _pluginTabVisuals)
        {
            visual.Reveal.IsExpanded = string.Equals(current, PluginPanels.KeyFor(id), StringComparison.Ordinal);
        }
    }

    private void SyncPluginHost()
    {
        var activeId = Model.IsPluginPanelTab ? Model.ActivePluginPanelId : null;
        foreach (var (id, host) in _pluginHosts)
        {
            host.SetPanelVisible(Model.IsActive && string.Equals(id, activeId, StringComparison.Ordinal));
        }
        if (activeId is null)
        {
            return;
        }
        if (!_pluginHosts.TryGetValue(activeId, out var activeHost))
        {
            activeHost = new PluginPanelHost(
                activeId,
                AppHost.Get<PluginWorkbench>(),
                AppHost.Get<PluginPanels>(),
                AppHost.Get<IPluginProjectContext>());
            _pluginHosts[activeId] = activeHost;
        }
        if (!ReferenceEquals(PluginPanelSlot.Child, activeHost))
        {
            PluginPanelSlot.Child = activeHost;
        }
        activeHost.SetPanelVisible(Model.IsActive);
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
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        content.Children.Add(new LucideIcon { Kind = icon, Size = 15, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TabLabelReveal
        {
            VerticalAlignment = VerticalAlignment.Center,
            [!TabLabelReveal.IsExpandedProperty] = CompiledBinding.Create(isSelected, source: Model),
            Child = new TextBlock
            {
                Text = name,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
        if (count is not null && hasCount is not null)
        {
            var number = new TextBlock
            {
                Margin = new Thickness(5, 0, 0, 0),
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

    /// <summary>The device id of the Octopus host the current project lives on; null otherwise.</summary>
    public string? ProjectRemoteHost { get; private set; }
    private string? _projectRootPath;
    private string? _workspaceBranch;

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

    /// <summary>
    /// The tabs stay in the top row while their widest tab state fits left of the trailing inset, with a small gap;
    /// otherwise they take a row of their own under it, so the corner the window floats over the panel never covers
    /// one of them and switching tabs never makes the tab bar jump between rows.
    /// </summary>
    private void ApplyTrailingInset()
    {
        var tabsWidth = Tabs.ComputeMaxSettledWidth();
        var need = Tabs.Margin.Left + tabsWidth + TabsGap;
        var available = Bounds.Width - TrailingInset;
        var isWrapped = Grid.GetRow(Tabs) == 1;
        var fits = Bounds.Width <= 0 || (isWrapped ? need + WrapHysteresis <= available : need <= available);
        var row = fits ? 0 : 1;
        if (Grid.GetRow(Tabs) == row) return;
        Grid.SetRow(Tabs, row);
        Tabs.Margin = fits ? new Thickness(8, 0, 0, 0) : new Thickness(8, 0, 0, 6);
    }

    private void ScheduleTrailingInset()
    {
        if (_trailingInsetScheduled) return;
        _trailingInsetScheduled = true;
        LayoutUpdated += OnTrailingInsetLayoutUpdated;
    }

    private void OnTrailingInsetLayoutUpdated(object? sender, EventArgs e)
    {
        _trailingInsetScheduled = false;
        LayoutUpdated -= OnTrailingInsetLayoutUpdated;
        ApplyTrailingInset();
    }

    private const double TabsGap = 6;
    private const double WrapHysteresis = 8;

    /// <summary>
    /// Points the panel at a project (or its active worktree). The folder is read again only when the folder, host or
    /// workspace changed: a rename keeps what is open in the tree.
    /// </summary>
    public void SetProject(string? path, string? name, string? sshHost, string? remoteHost = null, string? rootPath = null, string? workspaceBranch = null)
    {
        var changed = path != ProjectPath || sshHost != ProjectSshHost || remoteHost != ProjectRemoteHost
            || rootPath != _projectRootPath || workspaceBranch != _workspaceBranch;
        _settingProject = true;
        try
        {
            ProjectRemoteHost = remoteHost;
            ProjectSshHost = sshHost;
            _projectRootPath = rootPath;
            _workspaceBranch = workspaceBranch;
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
        if (!_settingProject) _ = Model.SetProjectAsync(ProjectPath, ProjectName, ProjectSshHost, ProjectRemoteHost, _projectRootPath, _workspaceBranch);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as Window;
        _window?.Activated += OnWindowActivated;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_trailingInsetScheduled)
        {
            _trailingInsetScheduled = false;
            LayoutUpdated -= OnTrailingInsetLayoutUpdated;
        }
        _window?.Activated -= OnWindowActivated;
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
