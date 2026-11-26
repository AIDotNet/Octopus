using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
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
    private static readonly Dictionary<string, (string Color, string Letter)> Known = new()
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
        ["cline"] = ("#0F766E", "C"),
        ["kilo"] = ("#CA8A04", "K"),
        ["auggie"] = ("#16A34A", "A"),
        ["codebuddy"] = ("#2563EB", "C"),
        ["qoder"] = ("#22C55E", "Q"),
        ["qoder-cn"] = ("#22C55E", "Q"),
        ["vibe"] = ("#FA5310", "V"),
        ["pi"] = ("#7C3AED", "π"),
        ["openhands"] = ("#CA8A04", "H"),
        ["continue"] = ("#E11D48", "C"),
        ["junie"] = ("#FE315D", "J"),
        ["letta"] = ("#4F46E5", "L"),
        ["codebuff"] = ("#65A30D", "B"),
        ["nanocoder"] = ("#0891B2", "N"),
        ["jules"] = ("#9333EA", "J"),
        ["omp"] = ("#0EA5E9", "O"),
        ["agy"] = ("#4F46E5", "A"),
        ["devin"] = ("#14B8A6", "D"),
        ["hermes"] = ("#8B5CF6", "H"),
        ["autohand"] = ("#F97316", "A"),
        ["mimo"] = ("#FF6900", "M"),
        ["vtcode"] = ("#059669", "V"),
        ["bob"] = ("#0F62FE", "B"),
        ["stakpak"] = ("#6366F1", "S"),
        ["minimax"] = ("#EC4899", "M"),
        ["glm"] = ("#3859FF", "G"),
        ["comate"] = ("#2932E1", "C"),
        ["trae"] = ("#10B981", "T"),
        ["dimcode"] = ("#6366F1", "D"),
        ["cortex"] = ("#29B5E8", "C"),
        ["poolside"] = ("#0EA5E9", "P"),
        ["deepagents"] = ("#1C3C3C", "D"),
        ["fast-agent"] = ("#F59E0B", "F"),
        ["dirac"] = ("#7C3AED", "D"),
        ["sigit"] = ("#0D9488", "S"),
        ["nova"] = ("#4F46E5", "N"),
        ["kimchi"] = ("#DC2626", "K"),
        ["harn"] = ("#9333EA", "H"),
        ["crow"] = ("#27272A", "C"),
        ["corust"] = ("#EA580C", "R"),
        ["minion"] = ("#EAB308", "M"),
        ["agoragentic"] = ("#0284C7", "A"),
        ["oz"] = ("#01A4FF", "O"),
        ["forge"] = ("#F97316", "F"),
        ["openclaw"] = ("#E11D48", "C"),
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

        var (color, letter) = AgentId is not null && Known.TryGetValue(AgentId, out var known)
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
