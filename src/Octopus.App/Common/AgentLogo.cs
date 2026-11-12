using Avalonia;

namespace Octopus.App.Common;

/// <summary>A parsed agent logo: a view box and the shapes in it. Immutable and shared by every icon of that agent.</summary>
internal sealed class AgentLogo
{
    public AgentLogo(Rect viewBox, IReadOnlyList<AgentLogoShape> shapes)
    {
        ViewBox = viewBox;
        Shapes = shapes;
    }

    /// <summary>The area of the shapes' coordinate space the logo occupies (already trimmed to the artwork).</summary>
    public Rect ViewBox { get; }

    public IReadOnlyList<AgentLogoShape> Shapes { get; }
}
