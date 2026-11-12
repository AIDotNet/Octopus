namespace Octopus.App.Activity;

/// <summary>What the program in a terminal or agent tab is doing, as far as Octopus can tell.</summary>
public enum ItemActivity
{
    /// <summary>At rest, finished, or unknown.</summary>
    Idle,
    /// <summary>The agent is working on a turn.</summary>
    Working,
    /// <summary>The agent stopped to ask the user (a permission request, a notification).</summary>
    Waiting,
    /// <summary>The turn ended with an error.</summary>
    Error,
}

/// <summary>
/// Follows the session of each terminal and agent tab: its title (which becomes the item's title unless the user
/// named it) and whether it is working. Two sources, as in Orca: the agent's own hooks through the signal hub,
/// and the terminal title as the fallback. The latest change from either source wins.
/// </summary>
public interface IItemActivityService
{
    ItemActivity Of(string itemId);

    /// <summary>Raised on the UI thread with the item id when its activity changed.</summary>
    event EventHandler<string>? Changed;

    /// <summary>The terminal of the item set its title (OSC 0/2). Call on the UI thread.</summary>
    void ReportTitle(string itemId, string? rawTitle);

    /// <summary>The program of the item exited, or its tab was closed. Call on the UI thread.</summary>
    void ReportExit(string itemId);
}
