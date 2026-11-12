using Avalonia.Controls;
using Octopus.App.Files.Code;
using Octopus.App.Files.Documents;
using Octopus.App.Files.Images;

namespace Octopus.App.Files;

/// <summary>One way of opening a file: the model that reads it and the view that shows it, for one <see cref="FileKind"/>.</summary>
public sealed record FileViewerDescriptor(FileKind Kind, Func<FileViewerContext, FileViewerModel> CreateModel, Func<FileViewerModel, Control> CreateView);

/// <summary>
/// The table of viewers. A new way to open files (PDF, a JSON tree, ...) is a <see cref="FileKind"/> value, a rule in
/// <see cref="FileKinds"/>, a model and a view, and one line here.
/// </summary>
public static class FileViewers
{
    private static readonly FileViewerDescriptor[] s_all =
    [
        new(FileKind.Code, c => new CodeViewerModel(c), m => new CodeViewerView { DataContext = m }),
        new(FileKind.Markdown, c => new MarkdownViewerModel(c), m => new MarkdownViewerView { DataContext = m }),
        new(FileKind.Image, c => new ImageViewerModel(c), m => new ImageViewerView { DataContext = m }),
    ];

    public static FileViewerDescriptor For(FileKind kind) => s_all.First(d => d.Kind == kind);
}

/// <summary>What a viewer's view offers the tab around it.</summary>
public interface IFileViewerView
{
    /// <summary>Moves the keyboard focus into the viewer.</summary>
    void FocusViewer();
}
