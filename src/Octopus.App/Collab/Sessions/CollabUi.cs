using Avalonia;
using Avalonia.Threading;

namespace Octopus.App.Collab.Sessions;

/// <summary>Runs work that touches projects or tabs on the UI thread (inline when already there or without an app).</summary>
internal static class CollabUi
{
    public static Task InvokeAsync(Action action)
    {
        if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return Dispatcher.UIThread.InvokeAsync(action).GetTask();
    }
}
