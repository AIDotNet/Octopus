using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Octopus.App.Launcher;

public sealed class LauncherRequestedEventArgs(RoutedEvent routedEvent, Control anchor) : RoutedEventArgs(routedEvent)
{
    /// <summary>The "+" button the menu opens under.</summary>
    public Control Anchor { get; } = anchor;
}

/// <summary>
/// A view that has a "+" button raises this; the main view answers by opening the launcher menu under the button. The
/// views stay free of each other and of the menu.
/// </summary>
public static class LauncherRequest
{
    public static readonly RoutedEvent<LauncherRequestedEventArgs> RequestedEvent =
        RoutedEvent.Register<Control, LauncherRequestedEventArgs>("LauncherRequested", RoutingStrategies.Bubble);

    public static void Raise(Control anchor) => anchor.RaiseEvent(new LauncherRequestedEventArgs(RequestedEvent, anchor));
}
