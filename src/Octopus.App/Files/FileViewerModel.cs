using CommunityToolkit.Mvvm.ComponentModel;
using Octopus.App.Services;

namespace Octopus.App.Files;

/// <summary>What a viewer is given when it is created.</summary>
public sealed record FileViewerContext(FileLocation Location, string? MonoFontFamily, double FontSize, IDialogService Dialogs)
{
    public string Path => Location.Path;
}

/// <summary>
/// The model behind one viewer of a file tab. The tab (<see cref="FileTabViewModel"/>) owns the file watcher and the
/// shared info bar; a viewer reads the file, says what the bar shows, and may take edits. Each kind has one subclass
/// and one view, registered together in <see cref="FileViewers"/>.
/// </summary>
public abstract partial class FileViewerModel(FileViewerContext context) : ObservableObject, IDisposable
{
    protected FileViewerContext Context { get; } = context;

    public FileLocation Location => Context.Location;
    public string Path => Context.Path;

    /// <summary>Set when there is nothing to show (missing, too large, unreadable, damaged): the view puts an empty state with this text in place of its content.</summary>
    [ObservableProperty] public partial string? Notice { get; protected set; }

    public bool HasNotice => Notice is not null;
    public bool HasContent => Notice is null;

    partial void OnNoticeChanged(string? value)
    {
        OnPropertyChanged(nameof(HasNotice));
        OnPropertyChanged(nameof(HasContent));
        NoticeChanged();
    }

    /// <summary>A subclass whose own properties depend on <see cref="Notice"/> raises their change here.</summary>
    protected virtual void NoticeChanged() { }

    /// <summary>True while edits are held that are not on disk (the tab asks before it closes).</summary>
    public virtual bool HasUnsavedChanges => false;

    /// <summary>True when the viewer takes edits right now (the save shortcut means something).</summary>
    public virtual bool CanSave => false;

    /// <summary>Writes the edits to the file. False when the person declined or it failed; true also when there was nothing to write.</summary>
    public virtual Task<bool> SaveAsync() => Task.FromResult(true);

    /// <summary>Reads the file for the first time.</summary>
    public abstract Task LoadAsync();

    /// <summary>The file changed on disk: by default it is simply read again.</summary>
    public virtual Task OnFileChangedAsync() => LoadAsync();

    protected bool Disposed { get; private set; }

    public virtual void Dispose() => Disposed = true;
}
