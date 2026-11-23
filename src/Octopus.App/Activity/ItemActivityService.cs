using Octopus.Agents;
using Octopus.Agents.Signals;
using Octopus.App.Common;
using Octopus.App.Services;
using Octopus.Core.Projects;

namespace Octopus.App.Activity;

public sealed class ItemActivityService : IItemActivityService, IDisposable
{
    private readonly IProjectService _projects;
    private readonly IAgentCatalog _catalog;
    private readonly IDisposable? _subscription;
    private readonly Dictionary<string, ItemActivity> _activity = [];
    /// <summary>Whether the last title of the item carried a spinner: only a change of it counts as news.</summary>
    private readonly Dictionary<string, bool> _titleWorking = [];
    /// <summary>Items the session itself has named (a terminal title other than the agent's name, or an ai-title).</summary>
    private readonly HashSet<string> _sessionNamed = [];
    /// <summary>Items titled after their first prompt, while the session has no name of its own.</summary>
    private readonly HashSet<string> _promptTitled = [];
    private readonly Dictionary<string, long> _titleReadAt = [];
    private readonly ISignalHub? _signals;

    public ItemActivityService(IProjectService projects, IAgentCatalog catalog, ISignalHub? signals = null)
    {
        _signals = signals;
        _projects = projects;
        _catalog = catalog;
        // Signals arrive on a background thread; everything here runs on the UI thread.
        _subscription = signals?.Subscribe(null, null, signal => UiContext.Run(() => OnSignal(signal)));
        _projects.Changed += OnProjectsChanged;
    }

    /// <summary>Whether the item is the tab on screen: the current project's active item.</summary>
    private bool IsViewed(string itemId) =>
        _projects.FindProjectOf(itemId) is { } project && _projects.Current?.Id == project.Id && project.ActiveItemId == itemId;

    public event EventHandler<string>? Changed;

    public ItemActivity Of(string itemId) => _activity.GetValueOrDefault(itemId);

    public void ReportTitle(string itemId, string? rawTitle)
    {
        var item = _projects.FindItem(itemId);
        if (item is null || item.Kind is not (ProjectItemKind.Agent or ProjectItemKind.Terminal))
        {
            return;
        }
        var title = SessionTitle.Parse(rawTitle);
        if (title.Text.Length > 0)
        {
            // An agent repeats its own name ("✳ Claude Code") until it names the session; that must not undo a
            // title taken from the prompt or the transcript.
            var isAgentName = item.Kind == ProjectItemKind.Agent && AgentName(item) is { } name
                && string.Equals(title.Text, name, StringComparison.OrdinalIgnoreCase);
            if (!isAgentName)
            {
                _sessionNamed.Add(itemId);
            }
            if (!isAgentName || !(_sessionNamed.Contains(itemId) || _promptTitled.Contains(itemId)))
            {
                // The item's own title; a name the user gave (CustomTitle) still shows instead.
                _projects.SetItemTitle(itemId, title.Text);
            }
        }
        // The spinner changes frame about once a second: that is the same news every time.
        if (_titleWorking.GetValueOrDefault(itemId) != title.Working)
        {
            _titleWorking[itemId] = title.Working;
            Set(itemId, title.Working ? ItemActivity.Working : ItemActivity.Idle);
        }
    }

    public void ReportExit(string itemId)
    {
        _titleWorking.Remove(itemId);
        _sessionNamed.Remove(itemId);
        _promptTitled.Remove(itemId);
        // A program that ended leaves nothing to look at, whatever it was doing.
        Set(itemId, ItemActivity.Idle, finished: false);
    }

    private string? AgentName(ProjectItem item) =>
        item.AgentId is { } id && _catalog.Find(id) is { Name.Length: > 0 } definition ? definition.Name : null;

    /// <summary>
    /// Names the session from a hook payload: the first prompt until the agent names it, then the agent's own name
    /// from its transcript. Covers a session the agent never names in the terminal title (a greeting, for one).
    /// </summary>
    private void NameFromPayload(AgentSignal signal, SignalStatus? status)
    {
        var (prompt, transcript) = SessionName.ReadPayload(signal.Payload);
        var itemId = signal.ItemId;
        if (!_sessionNamed.Contains(itemId) && !_promptTitled.Contains(itemId) && SessionName.FromPrompt(prompt) is { Length: > 0 } fromPrompt)
        {
            _promptTitled.Add(itemId);
            _projects.SetItemTitle(itemId, fromPrompt);
        }
        // The transcript is read once per finished turn (a new topic renames the session), and on any signal until
        // the session has a name.
        if (transcript is { Length: > 0 } path && (status == SignalStatus.Done || !_sessionNamed.Contains(itemId)))
        {
            // Not on every signal (a status line comes every couple of seconds): at most one read per item per 20 s,
            // except at the end of a turn.
            var now = Environment.TickCount64;
            if (status != SignalStatus.Done && _titleReadAt.TryGetValue(itemId, out var last) && now - last < 20_000) return;
            _titleReadAt[itemId] = now;
            _ = Task.Run(() => SessionName.ReadTranscriptTitle(path)).ContinueWith(read =>
            {
                if (read.IsCompletedSuccessfully && read.Result is { } aiTitle)
                {
                    UiContext.Run(() =>
                    {
                        if (_projects.FindItem(itemId) is null) return;
                        _sessionNamed.Add(itemId);
                        _projects.SetItemTitle(itemId, aiTitle);
                    });
                }
            }, TaskScheduler.Default);
        }
    }

    private void OnSignal(AgentSignal signal)
    {
        if (signal.ItemId.Length == 0 || _projects.FindItem(signal.ItemId) is null || _catalog.Find(signal.AgentId) is not { } definition)
        {
            return;
        }
        var status = SignalStatusMap.Resolve(definition, signal);
        NameFromPayload(signal, status);
        switch (status)
        {
            case SignalStatus.Working: Set(signal.ItemId, ItemActivity.Working); break;
            case SignalStatus.Waiting: Set(signal.ItemId, ItemActivity.Waiting); break;
            case SignalStatus.Error: Set(signal.ItemId, ItemActivity.Error); break;
            case SignalStatus.Done: Set(signal.ItemId, ItemActivity.Idle); break;
        }
    }

    private void OnProjectsChanged(object? sender, ProjectChangedEventArgs e)
    {
        // Looking at a finished tab is what clears its mark: a tab chosen, or the project it is in.
        if (e.Kind == ProjectChangeKind.Active && e.Item is { } active)
        {
            MarkSeen(active.Id);
        }
        else if (e.Kind == ProjectChangeKind.CurrentProject && e.Project?.ActiveItemId is { } shown)
        {
            MarkSeen(shown);
        }
        // A removed item: forget it, so a tab reopened under the same id starts clean.
        if (e.Kind == ProjectChangeKind.Items && e.Item is { } item && _projects.FindItem(item.Id) is null)
        {
            Forget(item.Id);
        }
        else if (e.Kind == ProjectChangeKind.Projects)
        {
            // A removed project's items go without an event each.
            var ids = _activity.Keys.Concat(_titleWorking.Keys).Concat(_sessionNamed).Concat(_promptTitled).Concat(_titleReadAt.Keys)
                .Distinct().Where(id => _projects.FindItem(id) is null).ToList();
            foreach (var id in ids) Forget(id);
        }
    }

    private void Forget(string itemId)
    {
        _titleWorking.Remove(itemId);
        _activity.Remove(itemId);
        _sessionNamed.Remove(itemId);
        _promptTitled.Remove(itemId);
        _titleReadAt.Remove(itemId);
        _signals?.ForgetItem(itemId);
    }

    public void ReportRemote(string itemId, ItemActivity activity) => Set(itemId, activity);

    public void MarkSeen(string itemId)
    {
        if (Of(itemId) == ItemActivity.Done)
        {
            Set(itemId, ItemActivity.Idle, finished: false);
        }
    }

    /// <param name="finished">Going from working to idle is a finished turn, unless the item is on screen.</param>
    private void Set(string itemId, ItemActivity activity, bool finished = true)
    {
        var before = Of(itemId);
        if (finished && activity == ItemActivity.Idle)
        {
            // A repeated "idle" must not wipe the mark; the end of a turn nobody watched sets it.
            if (before == ItemActivity.Done) return;
            if (before == ItemActivity.Working && !IsViewed(itemId)) activity = ItemActivity.Done;
        }
        if (before == activity)
        {
            return;
        }
        _activity[itemId] = activity;
        Changed?.Invoke(this, itemId);
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _projects.Changed -= OnProjectsChanged;
    }
}
