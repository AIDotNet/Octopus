using Avalonia.Media;

namespace Octopus.Mobile.Voice;

/// <summary>The microphone, for rows and buttons outside the conversation page (Arc has no such icon).</summary>
public static class VoiceGlyphs
{
    public static readonly Geometry Microphone =
        StreamGeometry.Parse("M12 3a3 3 0 0 1 3 3v5a3 3 0 0 1-6 0V6a3 3 0 0 1 3-3z M5 11a7 7 0 0 0 14 0 M12 18v3");
}
