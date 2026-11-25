namespace Octopus.App.Files;

/// <summary>What a file is opened with: each kind has one viewer (see <see cref="FileViewers"/>).</summary>
public enum FileKind
{
    /// <summary>Text and source code: the editable, highlighted code editor. Also the fallback for anything unknown.</summary>
    Code,
    /// <summary>Markdown: a rendered, editable document.</summary>
    Markdown,
    /// <summary>A raster picture: the zoomable image view.</summary>
    Image,
}
