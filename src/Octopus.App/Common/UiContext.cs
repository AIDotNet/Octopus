using Avalonia.Threading;

namespace Octopus.App.Common;

/// <summary>
/// The one wrapper for getting work onto the UI thread. An instance runs work on the thread a view model was created
/// on (the UI thread in the app, inline when there is no synchronization context, as in a plain unit test), which is
/// what watcher callbacks from a background thread need. The static members use the Avalonia UI thread directly.
/// </summary>
internal sealed class UiContext
{
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;

    public void Post(Action action)
    {
        if (_context is null)
        {
            action();
            return;
        }
        _context.Post(static s => ((Action)s!)(), action);
    }

    /// <summary>Runs the action now when already on the UI thread, otherwise posts it.</summary>
    public static void Run(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    /// <summary>Posts the action to the UI thread (at the given priority), even when already on it.</summary>
    public static void PostToUi(Action action, DispatcherPriority priority = default) =>
        Dispatcher.UIThread.Post(action, priority == default ? DispatcherPriority.Default : priority);
}
