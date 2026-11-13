using Octopus.App.Localization;
using Octopus.App.Services;
using TKCode.Arc;

namespace Octopus.App.Activity;

/// <summary>
/// Tells the user when an agent stops to ask something (a question, a permission request) and the user is not looking at
/// that tab: a yellow toast with a button that goes to the tab. The tab, its project and its folder carry a yellow dot
/// as long as the agent waits; the toast goes away when it stops waiting.
/// </summary>
internal sealed class AgentAttentionNotifier(IItemActivityService activity, IProjectService projects, IWindowAccessor windows, Shell.TrayService tray)
{
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(12);
    private bool _started;

    public void Start()
    {
        if (_started) return;
        _started = true;
        activity.Changed += (_, itemId) => OnChanged(itemId);
    }

    private static string ToastId(string itemId) => "agent-waiting-" + itemId;

    private void OnChanged(string itemId)
    {
        if (windows.Window is not { } window) return;
        if (activity.Of(itemId) != ItemActivity.Waiting)
        {
            // It went on, or ended: the question is answered.
            Toast.Dismiss(window, ToastId(itemId));
            return;
        }
        if (projects.FindItem(itemId) is not { } item || projects.FindProjectOf(itemId) is not { } project) return;
        // Looking at it already: the tab is the one in front, in the project on screen, in a window that has the focus.
        if (window is Avalonia.Controls.Window { IsActive: true, IsVisible: true } && projects.Current?.Id == project.Id && project.ActiveItemId == itemId) return;
        Toast.Show(
            window,
            AppLangResources.Format(AppLangResourceKind.AgentWaitingToast, item.DisplayTitle),
            ToastKind.Warning,
            project.Name,
            new ToastAction(AppLangResources.Get(AppLangResourceKind.AgentWaitingView), () => Go(project.Id, itemId)),
            Duration,
            ToastId(itemId));
    }

    private void Go(string projectId, string itemId)
    {
        if (projects.Current?.Id != projectId) projects.SetCurrent(projectId);
        projects.ActivateItem(itemId);
        // The window may be hidden in the tray or minimized.
        tray.ShowWindow();
    }
}
