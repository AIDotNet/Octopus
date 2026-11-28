namespace Octopus.Mobile.Common;

/// <summary>
/// Safe fire-and-forget helpers for background mobile tasks so exceptions are never silently swallowed
/// or left as unobserved finalizer-thread faults.
/// </summary>
public static class TaskExtensions
{
    /// <summary>
    /// Observes a fire-and-forget <see cref="Task"/>, ignoring <see cref="OperationCanceledException"/> and
    /// routing any unexpected exception to <see cref="Diagnostics.MobileDiagnostics"/> (and optional <paramref name="onError"/>).
    /// </summary>
    public static void SafeFireAndForget(this Task task, string category, Action<Exception>? onError = null)
    {
        if (task.IsCompleted)
        {
            if (task.IsFaulted && task.Exception is { } agg)
            {
                HandleException(category, agg.Flatten().InnerException ?? agg, onError);
            }
            return;
        }

        _ = ObserveAsync(task, category, onError);
    }

    private static async Task ObserveAsync(Task task, string category, Action<Exception>? onError)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            HandleException(category, ex, onError);
        }
    }

    private static void HandleException(string category, Exception ex, Action<Exception>? onError)
    {
        Diagnostics.MobileDiagnostics.Error(category, $"Unhandled async task fault: {ex.GetType().Name}: {ex.Message}", ex);
        try
        {
            onError?.Invoke(ex);
        }
        catch (Exception callbackEx)
        {
            Diagnostics.MobileDiagnostics.Error(category, $"Error callback threw: {callbackEx.Message}", callbackEx);
        }
    }
}
