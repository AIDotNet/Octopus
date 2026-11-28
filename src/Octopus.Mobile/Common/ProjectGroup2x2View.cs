using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Octopus.Mobile.Workspace;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;

namespace Octopus.Mobile.Common;

/// <summary>
/// Faithful mobile equivalent of Desktop's <c>ProjectGroupView.axaml</c> collapsed folder preview:
/// a group-tinted rounded square holding a 2×2 mini-grid of the first 4 member projects (picture thumbnail or colored
/// initial, empty slots softly muted) plus a bottom-right aggregated status dot (Waiting &gt; Error &gt; Working).
/// </summary>
public sealed class ProjectGroup2x2View : ContentControl
{
    public static readonly StyledProperty<string?> GroupColorProperty =
        AvaloniaProperty.Register<ProjectGroup2x2View, string?>(nameof(GroupColor));

    public static readonly StyledProperty<IReadOnlyList<RemoteProject>?> MembersProperty =
        AvaloniaProperty.Register<ProjectGroup2x2View, IReadOnlyList<RemoteProject>?>(nameof(Members));

    public static readonly StyledProperty<bool> HasWaitingProperty =
        AvaloniaProperty.Register<ProjectGroup2x2View, bool>(nameof(HasWaiting));

    public static readonly StyledProperty<bool> HasErrorProperty =
        AvaloniaProperty.Register<ProjectGroup2x2View, bool>(nameof(HasError));

    public static readonly StyledProperty<bool> HasWorkingProperty =
        AvaloniaProperty.Register<ProjectGroup2x2View, bool>(nameof(HasWorking));

    private readonly Border _frame;
    private readonly Border[] _slots = new Border[4];
    private readonly Image[] _images = new Image[4];
    private readonly TextBlock[] _initials = new TextBlock[4];
    private readonly Border _statusDot;

    static ProjectGroup2x2View()
    {
        GroupColorProperty.Changed.AddClassHandler<ProjectGroup2x2View>((v, _) => v.Refresh());
        MembersProperty.Changed.AddClassHandler<ProjectGroup2x2View>((v, _) => v.Refresh());
        HasWaitingProperty.Changed.AddClassHandler<ProjectGroup2x2View>((v, _) => v.RefreshStatus());
        HasErrorProperty.Changed.AddClassHandler<ProjectGroup2x2View>((v, _) => v.RefreshStatus());
        HasWorkingProperty.Changed.AddClassHandler<ProjectGroup2x2View>((v, _) => v.RefreshStatus());
    }

    public ProjectGroup2x2View()
    {
        var grid = new UniformGrid
        {
            Rows = 2,
            Columns = 2,
        };

        for (var i = 0; i < 4; i++)
        {
            var img = new Image
            {
                Stretch = Stretch.UniformToFill,
                IsVisible = false,
            };
            var txt = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 8,
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsVisible = false,
            };
            var slot = new Border
            {
                Margin = new Thickness(1.2),
                CornerRadius = new CornerRadius(3.5),
                ClipToBounds = true,
                Child = new Panel { Children = { txt, img } },
            };
            _images[i] = img;
            _initials[i] = txt;
            _slots[i] = slot;
            grid.Children.Add(slot);
        }

        _frame = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(3.5),
            BorderThickness = new Thickness(1),
            Child = grid,
        };

        _statusDot = new Border
        {
            Width = 11,
            Height = 11,
            CornerRadius = new CornerRadius(5.5),
            BorderThickness = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -2, -2),
            IsVisible = false,
        };
        ArcTokens.Bind(_statusDot, Border.BorderBrushProperty, ArcDesignTokenKind.Surface);

        Content = new Panel
        {
            Width = 38,
            Height = 38,
            Children = { _frame, _statusDot },
        };

        Refresh();
        RefreshStatus();
    }

    protected override Type StyleKeyOverride => typeof(ContentControl);

    public string? GroupColor
    {
        get => GetValue(GroupColorProperty);
        set => SetValue(GroupColorProperty, value);
    }

    public IReadOnlyList<RemoteProject>? Members
    {
        get => GetValue(MembersProperty);
        set => SetValue(MembersProperty, value);
    }

    public bool HasWaiting
    {
        get => GetValue(HasWaitingProperty);
        set => SetValue(HasWaitingProperty, value);
    }

    public bool HasError
    {
        get => GetValue(HasErrorProperty);
        set => SetValue(HasErrorProperty, value);
    }

    public bool HasWorking
    {
        get => GetValue(HasWorkingProperty);
        set => SetValue(HasWorkingProperty, value);
    }

    private void Refresh()
    {
        var baseColor = ParseGroupColor(GroupColor);
        _frame.Background = new SolidColorBrush(Color.FromArgb(62, baseColor.R, baseColor.G, baseColor.B));
        _frame.BorderBrush = new SolidColorBrush(Color.FromArgb(96, baseColor.R, baseColor.G, baseColor.B));

        var members = Members;
        for (var i = 0; i < 4; i++)
        {
            var slot = _slots[i];
            var img = _images[i];
            var txt = _initials[i];
            if (members is not null && i < members.Count)
            {
                var project = members[i];
                if (project.ImageBitmap is { } bmp)
                {
                    slot.Background = Brushes.Transparent;
                    img.Source = bmp;
                    img.IsVisible = true;
                    txt.IsVisible = false;
                }
                else
                {
                    slot.Background = MobileProjectIcons.BrushOf(project);
                    img.Source = null;
                    img.IsVisible = false;
                    txt.Text = MobileProjectIcons.InitialOf(project.Name);
                    txt.IsVisible = true;
                }
            }
            else
            {
                slot.Background = new SolidColorBrush(Color.FromArgb(34, 148, 163, 184));
                img.Source = null;
                img.IsVisible = false;
                txt.Text = "";
                txt.IsVisible = false;
            }
        }
    }

    private void RefreshStatus()
    {
        if (HasWaiting)
        {
            _statusDot.Background = new SolidColorBrush(Color.Parse("#F59E0B"));
            _statusDot.IsVisible = true;
        }
        else if (HasError)
        {
            _statusDot.Background = new SolidColorBrush(Color.Parse("#EF4444"));
            _statusDot.IsVisible = true;
        }
        else if (HasWorking)
        {
            _statusDot.Background = new SolidColorBrush(Color.Parse("#6366F1"));
            _statusDot.IsVisible = true;
        }
        else
        {
            _statusDot.IsVisible = false;
        }
    }

    internal static Color ParseGroupColor(string? hex) =>
        !string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex, out var parsed)
            ? parsed
            : Color.Parse("#5B5BD6");
}
