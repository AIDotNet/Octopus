using Avalonia.Media;

namespace Octopus.App.Common;

/// <summary>One filled path of a logo. A null <see cref="Fill"/> means "currentColor": the theme's foreground.</summary>
internal sealed record AgentLogoShape(Geometry Geometry, IBrush? Fill);
