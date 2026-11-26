using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Octopus.App.Common;
using Octopus.Mobile.Workspace;
using TKCode.Arc.Icons.Lucide;

namespace Octopus.Mobile.Common;

/// <summary>
/// Renders a remote project's real desktop badge on mobile with exact priority parity:
/// custom picture thumbnail (<see cref="RemoteProject.ImageBitmap"/>) → built-in pixel icon (<c>pixel:&lt;id&gt;</c>)
/// → Lucide vector icon → colored initial letter. Subscribes to <see cref="RemoteProject"/> changes so cached or
/// freshly downloaded thumbnails update in-place without rebuilding the page.
/// </summary>
public sealed class ProjectBadgeView : ContentControl
{
    public static readonly StyledProperty<RemoteProject?> ProjectProperty =
        AvaloniaProperty.Register<ProjectBadgeView, RemoteProject?>(nameof(Project));

    public static readonly StyledProperty<double> BadgeSizeProperty =
        AvaloniaProperty.Register<ProjectBadgeView, double>(nameof(BadgeSize), 38);

    public static readonly StyledProperty<CornerRadius> BadgeCornerRadiusProperty =
        AvaloniaProperty.Register<ProjectBadgeView, CornerRadius>(nameof(BadgeCornerRadius), new CornerRadius(10));

    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<ProjectBadgeView, double>(nameof(IconSize), 18);

    public static readonly StyledProperty<double> PixelSizeProperty =
        AvaloniaProperty.Register<ProjectBadgeView, double>(nameof(PixelSize), 22);

    public static readonly StyledProperty<double> InitialFontSizeProperty =
        AvaloniaProperty.Register<ProjectBadgeView, double>(nameof(InitialFontSize), 15);

    private readonly Border _frame;
    private readonly Image _image;
    private readonly PixelIconView _pixel;
    private readonly LucideIcon _lucide;
    private readonly TextBlock _initial;
    private RemoteProject? _watched;

    static ProjectBadgeView()
    {
        ProjectProperty.Changed.AddClassHandler<ProjectBadgeView>((view, _) => view.OnProjectChanged());
        BadgeSizeProperty.Changed.AddClassHandler<ProjectBadgeView>((view, _) => view.ApplySizing());
        BadgeCornerRadiusProperty.Changed.AddClassHandler<ProjectBadgeView>((view, _) => view.ApplySizing());
        IconSizeProperty.Changed.AddClassHandler<ProjectBadgeView>((view, _) => view.ApplySizing());
        PixelSizeProperty.Changed.AddClassHandler<ProjectBadgeView>((view, _) => view.ApplySizing());
        InitialFontSizeProperty.Changed.AddClassHandler<ProjectBadgeView>((view, _) => view.ApplySizing());
    }

    public ProjectBadgeView()
    {
        _image = new Image
        {
            Stretch = Stretch.UniformToFill,
            IsVisible = false,
        };
        RenderOptions.SetBitmapInterpolationMode(_image, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);

        _pixel = new PixelIconView
        {
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
        };

        _lucide = new LucideIcon
        {
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
        };

        _initial = new TextBlock
        {
            Foreground = Brushes.White,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = true,
        };

        _frame = new Border
        {
            ClipToBounds = true,
            Child = new Panel
            {
                Children = { _initial, _lucide, _pixel, _image },
            },
        };

        Content = _frame;
        ApplySizing();
    }

    protected override Type StyleKeyOverride => typeof(ContentControl);

    public RemoteProject? Project
    {
        get => GetValue(ProjectProperty);
        set => SetValue(ProjectProperty, value);
    }

    public double BadgeSize
    {
        get => GetValue(BadgeSizeProperty);
        set => SetValue(BadgeSizeProperty, value);
    }

    public CornerRadius BadgeCornerRadius
    {
        get => GetValue(BadgeCornerRadiusProperty);
        set => SetValue(BadgeCornerRadiusProperty, value);
    }

    public double IconSize
    {
        get => GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public double PixelSize
    {
        get => GetValue(PixelSizeProperty);
        set => SetValue(PixelSizeProperty, value);
    }

    public double InitialFontSize
    {
        get => GetValue(InitialFontSizeProperty);
        set => SetValue(InitialFontSizeProperty, value);
    }

    /// <summary>Exposed for tests to verify which visual layer is active.</summary>
    public bool ShowsImage => _image.IsVisible;
    public bool ShowsPixel => _pixel.IsVisible;
    public bool ShowsLucide => _lucide.IsVisible;
    public bool ShowsInitial => _initial.IsVisible;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AttachProject(Project);
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        AttachProject(null);
    }

    private void OnProjectChanged()
    {
        if (VisualRoot is not null) AttachProject(Project);
        Refresh();
    }

    private void AttachProject(RemoteProject? project)
    {
        if (ReferenceEquals(_watched, project)) return;
        _watched?.PropertyChanged -= OnProjectPropertyChanged;
        _watched = project;
        _watched?.PropertyChanged += OnProjectPropertyChanged;
    }

    private void OnProjectPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RemoteProject.ImageBitmap)
            or nameof(RemoteProject.ImageKey)
            or nameof(RemoteProject.Icon)
            or nameof(RemoteProject.Color))
        {
            Refresh();
        }
    }

    private void ApplySizing()
    {
        var size = BadgeSize;
        _frame.Width = size;
        _frame.Height = size;
        _frame.CornerRadius = BadgeCornerRadius;
        _image.Width = size;
        _image.Height = size;
        _lucide.Size = IconSize;
        _pixel.Width = PixelSize;
        _pixel.Height = PixelSize;
        _initial.FontSize = InitialFontSize;
    }

    private void Refresh()
    {
        var project = Project;
        if (project is null)
        {
            _frame.Background = Brushes.Transparent;
            _image.Source = null;
            _image.IsVisible = false;
            _pixel.Icon = null;
            _pixel.IsVisible = false;
            _lucide.IsVisible = false;
            _initial.Text = "";
            _initial.IsVisible = false;
            return;
        }

        _frame.Background = MobileProjectIcons.BrushOf(project);

        if (project.ImageBitmap is { } bitmap)
        {
            _image.Source = bitmap;
            _image.IsVisible = true;
            _pixel.Icon = null;
            _pixel.IsVisible = false;
            _lucide.IsVisible = false;
            _initial.IsVisible = false;
            return;
        }

        _image.Source = null;
        _image.IsVisible = false;

        if (MobileProjectIcons.ParsePixel(project.Icon) is { } pixel)
        {
            _pixel.Icon = pixel;
            _pixel.IsVisible = true;
            _lucide.IsVisible = false;
            _initial.IsVisible = false;
            return;
        }

        _pixel.Icon = null;
        _pixel.IsVisible = false;

        if (MobileProjectIcons.ParseLucide(project.Icon) is { } kind)
        {
            _lucide.Kind = kind;
            _lucide.IsVisible = true;
            _initial.IsVisible = false;
            return;
        }

        _lucide.IsVisible = false;
        _initial.Text = MobileProjectIcons.InitialOf(project.Name);
        _initial.IsVisible = true;
    }
}
