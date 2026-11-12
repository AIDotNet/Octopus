using Avalonia;
using Avalonia.Controls;
using Octopus.Core.Settings;
using Octopus.Storage;

namespace Octopus.App.Appearance;

/// <summary>Opens the main window where it was last closed and remembers its placement when it closes.</summary>
public static class WindowPlacementTracker
{
    public static void Track(Window window, ISettingsStore settings)
    {
        Restore(window, settings.Current.Window);
        window.Closing += (_, _) =>
        {
            var previous = settings.Current.Window;
            var maximized = window.WindowState == WindowState.Maximized;
            // A maximized window keeps the size it had before, so it un-maximizes to it next time.
            var placement = maximized && previous is not null
                ? new WindowSettings { X = previous.X, Y = previous.Y, Width = previous.Width, Height = previous.Height, IsMaximized = true }
                : new WindowSettings
                {
                    X = window.Position.X,
                    Y = window.Position.Y,
                    Width = window.ClientSize.Width,
                    Height = window.ClientSize.Height,
                    IsMaximized = maximized
                };
            settings.Update(s => s.Window = placement);
        };
    }

    private static void Restore(Window window, WindowSettings? placement)
    {
        if (placement is null)
        {
            return;
        }
        if (placement.Width >= window.MinWidth && placement.Height >= window.MinHeight)
        {
            window.Width = placement.Width;
            window.Height = placement.Height;
        }
        var position = new PixelPoint(placement.X, placement.Y);
        // A position on a screen that is gone is skipped.
        foreach (var screen in window.Screens.All)
        {
            if (screen.WorkingArea.Contains(position))
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Position = position;
                break;
            }
        }
        if (placement.IsMaximized)
        {
            window.WindowState = WindowState.Maximized;
        }
    }
}
