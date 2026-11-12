namespace Octopus.App.Services;

/// <summary>The system pickers, anchored to the main window.</summary>
public interface IPickerService
{
    Task<string?> PickFolderAsync();
    Task<string?> PickFileAsync(string? startDirectory);
}
