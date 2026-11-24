using Avalonia.Threading;
using Octopus.Agents.Signals;
using Octopus.Agents.Subagents;
using Octopus.App.Common;
using Octopus.App.Services;
using Octopus.Core.Projects;

namespace Octopus.App.Activity;

/// <summary>The subagents an agent session started, per item, for the cards under its row in the sidebar.</summary>
public interface ISubagentService
{
    /// <summary>The subagents of the item, oldest first; empty when it has none.</summary>
    IReadOnlyList<SubagentRun> Of(string itemId);

    /// <summary>The list of an item changed (a subagent came, went, finished or did something). On the UI thread.</summary>
    event EventHandler<string>? Changed;

    /// <summary>About once a second while any subagent runs, so elapsed times can move. On the UI thread.</summary>
    event EventHandler? Tick;

    /// <summary>The item's process exited: what still ran is interrupted.</summary>
    void ReportExit(string itemId);

    /// <summary>Publishes a snapshot of subagents driven directly by an ACP GUI session (no hook transcript polling).</summary>
    void ReportExternal(string itemId, IReadOnlyList<SubagentRun> runs);
}

/// <summary>
/// Feeds each item's <see cref="SubagentTracker"/> from the signal hub, and while any subagent runs, reads the running
/// ones' transcripts once a second (off the UI thread) for their tool calls.
/// </summary>
public sealed class SubagentService : ISubagentService, IDisposable
{
    private static readonly TimeSpan s_pollInterval = TimeSpan.FromSeconds(1);

    private readonly IProjectService _projects;
    private readonly IDisposable? _subscription;
    private readonly Dictionary<string, SubagentTracker> _trackers = [];
    private readonly Dictionary<string, IReadOnlyList<SubagentRun>> _external = [];
    private DispatcherTimer? _timer;
    private bool _polling;
    private bool _pollAgain;

    public SubagentService(IProjectService projects, ISignalHub? signals = null)
    {
        _projects = projects;
        _subscription = signals?.Subscribe(null, null, signal => UiContext.Run(() => OnSignal(signal)));
        _projects.Changed += OnProjectsChanged;
    }

    public event EventHandler<string>? Changed;
    public event EventHandler? Tick;

    public IReadOnlyList<SubagentRun> Of(string itemId) =>
        _external.TryGetValue(itemId, out var ext) ? ext
        : _trackers.TryGetValue(itemId, out var tracker) ? tracker.Runs
        : [];

    public void ReportExternal(string itemId, IReadOnlyList<SubagentRun> runs)
    {
        if (string.IsNullOrEmpty(itemId)) return;
        UiContext.Run(() =>
        {
            if (runs.Count == 0)
            {
                if (!_external.Remove(itemId)) return;
            }
            else
            {
                _external[itemId] = runs;
            }
            Changed?.Invoke(this, itemId);
            UpdateTimer();
        });
    }

    public void ReportExit(string itemId)
    {
        var changed = false;
        if (_external.TryGetValue(itemId, out var ext) && ext.Any(r => r.State == SubagentState.Running))
        {
            var now = DateTimeOffset.Now;
            var updated = ext.Select(r => r.State == SubagentState.Running
                ? SubagentRun.CreateExternal(r.Id, r.Type, r.Description, SubagentState.Interrupted, r.StartedAt, now, r.ToolCount, r.LastAction, r.Background)
                : r).ToList();
            _external[itemId] = updated;
            changed = true;
        }
        if (_trackers.TryGetValue(itemId, out var tracker) && tracker.EndSession(DateTimeOffset.Now))
        {
            changed = true;
        }
        if (changed)
        {
            Changed?.Invoke(this, itemId);
            UpdateTimer();
        }
    }

    private void OnSignal(AgentSignal signal)
    {
        if (signal.ItemId.Length == 0 || _projects.FindItem(signal.ItemId) is not { Kind: ProjectItemKind.Agent })
        {
            return;
        }
        if (!_trackers.TryGetValue(signal.ItemId, out var tracker))
        {
            // Only a subagent creates the list; other agents' turn signals pass by.
            if (signal.Channel is not ("subagent-launch" or "subagent-start"))
            {
                return;
            }
            tracker = new SubagentTracker();
            _trackers[signal.ItemId] = tracker;
        }
        if (tracker.Apply(signal.Channel, signal.Payload, DateTimeOffset.Now))
        {
            Changed?.Invoke(this, signal.ItemId);
        }
        if (signal.Channel is "subagent-start" or "subagent-stop")
        {
            // Read right away: the description in the meta file, the last tool calls of a finished one.
            Poll();
        }
        UpdateTimer();
    }

    private void UpdateTimer()
    {
        // Also while a finished one still has its last read to come, or an ACP external subagent is running.
        var running = _trackers.Values.Any(t => t.HasRunning || t.Runs.Any(r => r.NeedsRead && r.TranscriptPath is not null))
            || _external.Values.Any(list => list.Any(r => r.State == SubagentState.Running));
        if (running && _timer is null)
        {
            _timer = new DispatcherTimer { Interval = s_pollInterval };
            _timer.Tick += (_, _) =>
            {
                Poll();
                Tick?.Invoke(this, EventArgs.Empty);
                UpdateTimer();
            };
            _timer.Start();
        }
        else if (!running && _timer is not null)
        {
            _timer.Stop();
            _timer = null;
        }
    }

    /// <summary>
    /// Reads the transcripts of the running subagents, and of finished ones whose description or last calls are not
    /// read yet, on a worker thread; applies what it found on the UI thread. One read at a time.
    /// </summary>
    private void Poll()
    {
        if (_polling)
        {
            _pollAgain = true;
            return;
        }
        var work = Pending().ToList();
        if (work.Count == 0)
        {
            return;
        }
        _polling = true;
        _ = Task.Run(() => Read(work))
            .ContinueWith(read => UiContext.Run(() =>
            {
                _polling = false;
                if (read.IsCompletedSuccessfully)
                {
                    Apply(read.Result);
                }
                if (_pollAgain)
                {
                    _pollAgain = false;
                    Poll();
                }
            }), TaskScheduler.Default);
    }

    /// <summary>The same as <see cref="Poll"/>, on the calling thread. For tests.</summary>
    internal void PollNow() => Apply(Read(Pending().ToList()));

    private static List<(PollWork Work, SubagentProgress Progress)> Read(List<PollWork> work) =>
        work.Select(w => (w, SubagentTranscript.Read(w.Run.TranscriptPath!, w.Offset, !w.Run.MetaRead))).ToList();

    private IEnumerable<PollWork> Pending()
    {
        foreach (var (itemId, tracker) in _trackers)
        {
            foreach (var run in tracker.Runs)
            {
                if (run.TranscriptPath is not null && run.NeedsRead)
                {
                    yield return new PollWork(itemId, tracker, run, run.TranscriptOffset, run.IsFinished);
                }
            }
        }
    }

    private void Apply(List<(PollWork Work, SubagentProgress Progress)> results)
    {
        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (work, progress) in results)
        {
            if (_trackers.TryGetValue(work.ItemId, out var tracker) && ReferenceEquals(tracker, work.Tracker)
                && tracker.ApplyProgress(work.Run, progress, final: work.Finished))
            {
                changed.Add(work.ItemId);
            }
        }
        foreach (var itemId in changed)
        {
            Changed?.Invoke(this, itemId);
        }
    }

    private void OnProjectsChanged(object? sender, ProjectChangedEventArgs e)
    {
        if (e.Kind == ProjectChangeKind.Projects)
        {
            // A removed project takes its items along without an Items event per item: drop their trackers too.
            var gone = _trackers.Keys.Where(id => _projects.FindItem(id) is null).ToList();
            foreach (var id in gone) _trackers.Remove(id);
            var goneExt = _external.Keys.Where(id => _projects.FindItem(id) is null).ToList();
            foreach (var id in goneExt) _external.Remove(id);
            if (gone.Count > 0 || goneExt.Count > 0) UpdateTimer();
            return;
        }
        if (e.Kind == ProjectChangeKind.Items && e.Item is { } item && _projects.FindItem(item.Id) is null
            && (_trackers.Remove(item.Id) | _external.Remove(item.Id)))
        {
            UpdateTimer();
        }
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _projects.Changed -= OnProjectsChanged;
        _timer?.Stop();
    }

    /// <param name="Finished">The run had ended when the read started, so this read is its last.</param>
    private sealed record PollWork(string ItemId, SubagentTracker Tracker, SubagentRun Run, long Offset, bool Finished);
}
