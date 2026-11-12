namespace Octopus.App.Shell.Chrome;

/// <summary>What the platform's window frame leaves to the app at the top of an extended client area.</summary>
public interface IWindowChrome
{
    /// <summary>Width the top-left corner keeps free for the system's window buttons (macOS traffic lights).</summary>
    double LeftInset { get; }

    /// <summary>True when the app has to draw minimize, maximize and close at the top right.</summary>
    bool ShowsCaptionButtons { get; }
}
