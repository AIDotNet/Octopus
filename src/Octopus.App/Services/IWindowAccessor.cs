using Avalonia.Controls;

namespace Octopus.App.Services;

/// <summary>The main window, for services that need a window to anchor a picker or a dialog to.</summary>
public interface IWindowAccessor
{
    TopLevel? Window { get; set; }
}
