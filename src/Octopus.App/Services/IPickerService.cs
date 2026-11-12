namespace Octopus.App.Services;

/// <summary>The system pickers, anchored to the main window.</summary>
public interface IPickerService
{
    Task<string?> PickFolderAsync();
    Task<string?> PickFileAsync(string? startDirectory);
    /// <summary>An image file (png, jpg, gif, webp, bmp, ico); null when cancelled.</summary>
    Task<string?> PickImageAsync();
}
