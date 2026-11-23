using Avalonia;
using Avalonia.Media;
using TKCode.Terminal.Avalonia;

namespace Octopus.Terminal.Avalonia;

/// <summary>
/// Draws U+23FA (⏺, the message marker Claude Code prints on macOS) as a plain filled circle in the text color. Without
/// this the renderer may send it to the color emoji font (Apple Color Emoji: a blue rounded square), which is not what
/// iTerm2 or Terminal.app show for it.
/// </summary>
internal sealed class RecordCircleGlyph : ITerminalGlyphProvider
{
    public static readonly RecordCircleGlyph Instance = new();

    public bool Handles(int codepoint) => codepoint == 0x23FA;

    public TerminalGlyph? Create(int codepoint, TerminalGlyphRequest request)
    {
        var radius = Math.Max(1.5, request.Height * 0.22);
        var geometry = new EllipseGeometry(new Rect(
            request.Width / 2.0 - radius, request.Height / 2.0 - radius, radius * 2, radius * 2));
        return new TerminalGlyph(geometry);
    }
}
