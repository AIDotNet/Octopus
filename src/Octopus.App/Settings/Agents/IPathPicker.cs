using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace Octopus.App.Settings.Agents;

/// <summary>Lets the person pick a file or a folder (the "Browse…" button of file and directory features).</summary>
public interface IPathPicker
{
    /// <summary>The chosen local path, or null when cancelled.</summary>
    Task<string?> PickAsync(bool folder, string title);
}

/// <summary>Avalonia's storage provider on the main window.</summary>
public sealed class StoragePathPicker : IPathPicker
{
    public async Task<string?> PickAsync(bool folder, string title)
    {
        var top = Application.Current?.ApplicationLifetime switch
        {
            IClassicDesktopStyleApplicationLifetime desktop => desktop.MainWindow,
            ISingleViewApplicationLifetime single => TopLevel.GetTopLevel(single.MainView),
            _ => null
        };
        if (top?.StorageProvider is not { } storage)
        {
            return null;
        }
        if (folder)
        {
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        }
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
