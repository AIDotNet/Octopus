using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Octopus.App.Sidebar;

public sealed class DeviceSwitcherRequestedEventArgs(RoutedEvent routedEvent, Control anchor) : RoutedEventArgs(routedEvent)
{
    /// <summary>The device badge or header pill the switcher popover anchors to.</summary>
    public Control Anchor { get; } = anchor;
}

/// <summary>
/// Raised by the rail's device badge or the panel's device echo pill; the shell responds by opening the device switcher
/// popover outside the sidebar's clip boundary.
/// </summary>
public static class DeviceSwitcherRequest
{
    public static readonly RoutedEvent<DeviceSwitcherRequestedEventArgs> RequestedEvent =
        RoutedEvent.Register<Control, DeviceSwitcherRequestedEventArgs>("DeviceSwitcherRequested", RoutingStrategies.Bubble);

    public static void Raise(Control anchor) =>
        anchor.RaiseEvent(new DeviceSwitcherRequestedEventArgs(RequestedEvent, anchor));
}
