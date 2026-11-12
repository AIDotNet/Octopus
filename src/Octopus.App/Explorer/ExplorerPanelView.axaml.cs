using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
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
/// control tab. The shell hosts one instance, keeps <see cref="ProjectPath"/> and <see cref="ProjectName"/> on the
/// current project, and opens a file tab on <see cref="FileOpenRequested"/>.
/// </summary>
public partial class ExplorerPanelView : UserControl
{
    public static readonly StyledProperty<string?> ProjectPathProperty =
        AvaloniaProperty.Register<ExplorerPanelView, string?>(nameof(ProjectPath));

    public static readonly StyledProperty<string?> ProjectNameProperty =
        AvaloniaProperty.Register<ExplorerPanelView, string?>(nameof(ProjectName));

    /// <summary>Room the top row leaves free at its right end for what the window floats there (the panel switch, the caption buttons).</summary>
    public static readonly StyledProperty<double> TrailingInsetProperty =
        AvaloniaProperty.Register<ExplorerPanelView, double>(nameof(TrailingInset), 44);

    private Window? _window;

    static ExplorerPanelView()
    {
        ProjectPathProperty.Changed.AddClassHandler<ExplorerPanelView>((view, e) => view.OnProjectChanged());
        TrailingInsetProperty.Changed.AddClassHandler<ExplorerPanelView>((view, e) => view.ApplyTrailingInset());
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
            if (e.PropertyName == nameof(ExplorerPanelViewModel.IsGitRepository)) SyncGitOption();
            else if (e.PropertyName == nameof(ExplorerPanelViewModel.GitTabTooltip) && _gitOption is not null) _gitOption.Title = Model.GitTabTooltip;
        };
        SyncGitOption();
    }

    private ToggleGroupOption? _gitOption;

    /// <summary>The Git tab exists only while the folder is in a repository; its dot shows when files changed.</summary>
    private void SyncGitOption()
    {
        if (!Model.IsGitRepository)
        {
            if (_gitOption is not null) Tabs.Items.Remove(_gitOption);
            return;
        }
        if (_gitOption is null)
        {
            var dot = new Ellipse
            {
                Width = 7,
                Height = 7,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -3, -4, 0),
                IsHitTestVisible = false,
                [!Ellipse.IsVisibleProperty] = new Binding(nameof(ExplorerPanelViewModel.HasChanges)) { Source = Model },
            };
            ArcTokens.Bind(dot, Shape.FillProperty, ArcDesignTokenKind.Accent);
            _gitOption = new ToggleGroupOption
            {
                Value = "git",
                Label = AppLangResources.Get(AppLangResourceKind.ExplorerTabGit),
                Title = Model.GitTabTooltip,
                Content = new Panel { Children = { new LucideIcon { Kind = LucideIconKind.GitBranch, Size = 16 }, dot } },
            };
        }
        if (!Tabs.Items.Contains(_gitOption)) Tabs.Items.Add(_gitOption);
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

    private void OnProjectChanged() => _ = Model.SetProjectAsync(ProjectPath, ProjectName);

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
