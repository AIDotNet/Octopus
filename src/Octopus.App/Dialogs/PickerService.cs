using Avalonia.Platform.Storage;
using Octopus.App.Localization;
using Octopus.App.Services;

namespace Octopus.App.Dialogs;

/// <summary>The system file and folder pickers of the main window.</summary>
public sealed class PickerService(IWindowAccessor windows) : IPickerService
{
    public async Task<string?> PickFolderAsync()
    {
        var storage = windows.Window?.StorageProvider;
        if (storage is null)
        {
            return null;
        }
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = AppLangResources.Get(AppLangResourceKind.ChooseProjectFolder),
            AllowMultiple = false,
        });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    public async Task<string?> PickFileAsync(string? startDirectory)
    {
        var storage = windows.Window?.StorageProvider;
        if (storage is null)
        {
            return null;
        }
        IStorageFolder? start = null;
        if (startDirectory is not null && Directory.Exists(startDirectory))
        {
            start = await storage.TryGetFolderFromPathAsync(startDirectory);
        }
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = AppLangResources.Get(AppLangResourceKind.OpenFile),
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }
}
