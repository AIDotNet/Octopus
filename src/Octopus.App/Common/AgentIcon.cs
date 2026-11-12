using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;

namespace Octopus.App.Common;

/// <summary>
/// The small mark that stands for an agent: its brand logo (Assets/AgentLogos/&lt;agent id&gt;.json), or, for an agent
/// without one, a rounded square in the agent's color with one letter. Driven by the agent id, so a custom agent
/// gets a tile too (its first letter on a neutral color).
/// </summary>
public sealed class AgentIcon : Border
{
    public static readonly StyledProperty<string?> AgentIdProperty =
        AvaloniaProperty.Register<AgentIcon, string?>(nameof(AgentId));

    public static readonly StyledProperty<string?> AgentNameProperty =
        AvaloniaProperty.Register<AgentIcon, string?>(nameof(AgentName));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<AgentIcon, double>(nameof(Size), 18);

    // Identity colors of the built-in agents: data, not theme colors, so they stay the same in light and dark.
    private static readonly Dictionary<string, (string Color, string Letter)> s_known = new()
    {
        ["claude"] = ("#D97757", "C"),
        ["codex"] = ("#10A37F", "X"),
        ["grok"] = ("#4B4B57", "G"),
        ["opencode"] = ("#6B7280", "O"),
        ["cursor"] = ("#2563EB", "▲"),
        ["muse"] = ("#0866FF", "M"),
        ["gemini"] = ("#4285F4", "✦"),
        ["copilot"] = ("#8250DF", "P"),
        ["amp"] = ("#F34E3F", "A"),
        ["droid"] = ("#EF6F2E", "D"),
        ["aider"] = ("#14B8A6", "A"),
        ["qwen"] = ("#615CED", "Q"),
        ["kiro"] = ("#9046FF", "K"),
        ["goose"] = ("#3F3F46", "G"),
        ["crush"] = ("#E0489B", "C"),
        ["kimi"] = ("#1E293B", "K"),
    };

    private readonly TextBlock _letter = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = Brushes.White,
        FontWeight = FontWeight.Bold,
    };

    public AgentIcon()
    {
        Child = _letter;
        Update();
    }

    public string? AgentId
    {
        get => GetValue(AgentIdProperty);
        set => SetValue(AgentIdProperty, value);
    }

    /// <summary>The agent's display name; its first letter is used for an agent that has no known tile.</summary>
    public string? AgentName
    {
        get => GetValue(AgentNameProperty);
        set => SetValue(AgentNameProperty, value);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == AgentIdProperty || change.Property == AgentNameProperty || change.Property == SizeProperty)
        {
            Update();
        }
    }

    private void Update()
    {
        Width = Height = Size;
        var logo = AgentLogoStore.Instance.Get(AgentId);
        if (logo is not null)
        {
            ShowLogo(logo);
            return;
        }

        if (!ReferenceEquals(Child, _letter))
        {
            Child = _letter;
        }

        var (color, letter) = AgentId is not null && s_known.TryGetValue(AgentId, out var known)
            ? known
            : ("#71717A", FirstLetter(AgentName ?? AgentId));
        CornerRadius = new CornerRadius(Math.Round(Size * 0.28));
        Background = new SolidColorBrush(Color.Parse(color));
        _letter.Text = letter;
        _letter.FontSize = Math.Round(Size * 0.56);
    }

    private void ShowLogo(AgentLogo logo)
    {
        CornerRadius = default;
        Background = null;
        var box = logo.ViewBox;
        var canvas = new Canvas { Width = box.Width, Height = box.Height };
        foreach (var shape in logo.Shapes)
        {
            var path = new Avalonia.Controls.Shapes.Path { Data = shape.Geometry, IsHitTestVisible = false };
            if (shape.Fill is null)
            {
                // currentColor: the theme's foreground, so a black-and-white logo follows light/dark.
                ArcTokens.Bind(path, Shape.FillProperty, ArcDesignTokenKind.Foreground);
            }
            else
            {
                path.Fill = shape.Fill;
            }

            Canvas.SetLeft(path, -box.X);
            Canvas.SetTop(path, -box.Y);
            canvas.Children.Add(path);
        }

        Child = new Viewbox { Stretch = Stretch.Uniform, Child = canvas };
    }

    private static string FirstLetter(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "?" : name.Trim()[..1].ToUpperInvariant();
}
