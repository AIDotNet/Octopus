using CommunityToolkit.Mvvm.ComponentModel;
using Octopus.App.Common;
using Octopus.App.Composition;
using Octopus.App.Services;
using Octopus.Core.Projects;
using Octopus.FileSystem;
using Octopus.Storage;

namespace Octopus.App.Files;

/// <summary>
/// One file tab. It decides what the file is (<see cref="FileKinds"/>), creates the viewer for that kind, and feeds it
/// the changes the file watcher reports. What the viewer shows and does (read-only code, an editable document, a
/// picture) lives in the viewer's own model; the tab only passes on what the window around it needs: unsaved edits.
/// </summary>
public sealed class FileTabViewModel : ObservableObject, IDisposable
{
    private readonly UiContext _ui = new();
    private readonly IDisposable? _watch;
    private bool _disposed;

    public FileTabViewModel(ProjectItem item, IFileTreeService files, ISettingsStore settings, string? projectRoot = null, IDialogService? dialogs = null)
    {
        Path = item.FilePath ?? "";
        FileName = System.IO.Path.GetFileName(Path);
        var project = (AppHost.Services.GetService(typeof(IProjectService)) as IProjectService)?.FindProjectOf(item.Id);
        var root = projectRoot ?? project?.Path;
        // A copy of a file on an SSH host: the tab names the file where it is there, and the copy is read-only.
        Location = item.RemotePath is { } remote
            ? new FileLocation(Path, remote[..Math.Max(remote.LastIndexOf('/'), 1)], remote)
            : FileLocation.Of(Path, root);
        Kind = Path.Length == 0 ? FileKind.Code : FileKinds.Detect(Path);
        var terminal = settings.Current.Terminal;
        var context = new FileViewerContext(
            Location,
            string.IsNullOrWhiteSpace(terminal.FontFamily) ? null : terminal.FontFamily,
            terminal.FontSize is > 6 and < 60 ? terminal.FontSize : 13,
            dialogs ?? AppHost.Get<IDialogService>(),
            Remote: item.RemotePath is { } remotePath && project?.SshHost is { } host
                ? new RemoteCopy(AppHost.Get<RemoteFiles>(), host, remotePath, File.Exists(Path) ? File.ReadAllBytes(Path) : [], dialogs ?? AppHost.Get<IDialogService>())
                : null);
        Viewer = FileViewers.For(Kind).CreateModel(context);
        Viewer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FileViewerModel.HasUnsavedChanges)) OnPropertyChanged(nameof(HasUnsavedChanges));
        };
        if (Path.Length > 0)
        {
            _watch = files.WatchFile(Path, () => _ui.Post(() => _ = Viewer.OnFileChangedAsync()), TimeSpan.FromMilliseconds(300));
        }
        _ = Viewer.LoadAsync();
    }

    public string Path { get; }
    public string FileName { get; }
    public FileLocation Location { get; }
    public FileKind Kind { get; }
    public FileViewerModel Viewer { get; }

    /// <summary>The folder part of the path shown in the bar, relative to the project when the file is inside it ("src/App/"); empty for a file at the root.</summary>
    public string FolderText => Location.Folder;

    /// <summary>The name shown in the bar.</summary>
    public string FileLabel => Location.Label;

    /// <summary>Edits that are not on disk yet: closing the tab or quitting asks first.</summary>
    public bool HasUnsavedChanges => Viewer.HasUnsavedChanges;

    public bool CanSave => Viewer.CanSave;

    public Task<bool> SaveAsync() => Viewer.SaveAsync();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watch?.Dispose();
        Viewer.Dispose();
    }
}
