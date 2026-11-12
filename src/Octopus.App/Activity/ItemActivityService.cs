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

    public ItemActivityService(IProjectService projects, IAgentCatalog catalog, ISignalHub? signals = null)
    {
        _projects = projects;
        _catalog = catalog;
        // Signals arrive on a background thread; everything here runs on the UI thread.
        _subscription = signals?.Subscribe(null, null, signal => UiContext.Run(() => OnSignal(signal)));
        _projects.Changed += OnProjectsChanged;
    }

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
        Set(itemId, ItemActivity.Idle);
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
        // A removed item: forget it, so a tab reopened under the same id starts clean.
        if (e.Kind == ProjectChangeKind.Items && e.Item is { } item && _projects.FindItem(item.Id) is null)
        {
            _titleWorking.Remove(item.Id);
            _activity.Remove(item.Id);
            _sessionNamed.Remove(item.Id);
            _promptTitled.Remove(item.Id);
        }
    }

    private void Set(string itemId, ItemActivity activity)
    {
        if (Of(itemId) == activity)
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
