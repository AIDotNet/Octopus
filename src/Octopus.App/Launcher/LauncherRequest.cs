using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Octopus.App.Launcher;

public sealed class LauncherRequestedEventArgs(RoutedEvent routedEvent, Control anchor, string? workspaceId = null) : RoutedEventArgs(routedEvent)
{
    /// <summary>The "+" button the menu opens under.</summary>
    public Control Anchor { get; } = anchor;

    /// <summary>
    /// Target workspace id: <c>""</c> for explicitly the project's default directory, a <c>ProjectWorkspace.Id</c> for a
    /// branch worktree, or <c>null</c> to follow the active tab's workspace.
    /// </summary>
    public string? WorkspaceId { get; } = workspaceId;
}

/// <summary>
/// A view that has a "+" button raises this; the main view answers by opening the launcher menu under the button. The
/// views stay free of each other and of the menu.
/// </summary>
public static class LauncherRequest
{
    public static readonly RoutedEvent<LauncherRequestedEventArgs> RequestedEvent =
        RoutedEvent.Register<Control, LauncherRequestedEventArgs>("LauncherRequested", RoutingStrategies.Bubble);

    public static void Raise(Control anchor, string? workspaceId = null) =>
        anchor.RaiseEvent(new LauncherRequestedEventArgs(RequestedEvent, anchor, workspaceId));
}
