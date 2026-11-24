using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Octopus.Mobile.Terminals;

/// <summary>Choosing a picture with the system's file picker (Avalonia's storage provider).</summary>
public static class ImagePicking
{
    /// <summary>The most bytes read from a chosen file: a larger one is turned down before it is read whole.</summary>
    public const long MaxReadBytes = 64L * 1024 * 1024;

    /// <summary>
    /// One picture from the system's picker: the photo library on iOS, the system's picker for pictures on Android.
    /// Null when nothing was chosen or the picker is not there.
    /// </summary>
    public static async Task<PickedImage?> PickWithStorageAsync(TopLevel? topLevel)
    {
        if (topLevel?.StorageProvider is not { CanOpen: true } storage) return null;
        // Starting in the pictures folder is what makes iOS show the photo library rather than the Files app.
        IStorageFolder? pictures = null;
        try { pictures = await storage.TryGetWellKnownFolderAsync(WellKnownFolder.Pictures); }
        catch (Exception e) when (e is NotSupportedException or UnauthorizedAccessException or IOException) { }
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.ImageAll],
            SuggestedStartLocation = pictures,
        });
        if (files.Count == 0) return null;
        using var file = files[0];
        if ((await file.GetBasicPropertiesAsync()).Size is ulong size && size > MaxReadBytes)
            throw new ImageTooLargeException();
        await using var stream = await file.OpenReadAsync();
        using var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes);
        return new PickedImage(file.Name, bytes.ToArray());
    }
}
