using Octopus.Agents;
using Octopus.Agents.Signals;
using Octopus.App.Activity;
using Octopus.App.Workspace;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.Storage;
using Octopus.Terminal.Avalonia;

namespace Octopus.App.Collab.Sessions;

/// <summary>
/// Native terminals on this computer (CLI agents and shells), reached through their PTY. A CLI agent's turn ends when
/// its hook reports Done (<see cref="ISignalHub"/>); the screen text since the prompt is then its answer.
/// </summary>
internal sealed class TerminalSessionProvider : ICollabSessionProvider
{
    private const int MaxAnswerChars = 3500;

    private readonly CollabSessions _sessions;
    private readonly TerminalSessionRegistry? _terminals;
    private readonly Func<ITabContentRegistry?>? _tabs;
    private readonly ISettingsStore _settings;
    private readonly IAgentCatalog _catalog;
    private readonly CollabProjectIndex _index;
    private readonly IItemActivityService? _activity;
    private readonly Action<string, string> _turnEnded;
    private readonly IDisposable? _signalSub;

    /// <param name="turnEnded">Called with (item id, answer) when a terminal agent with an outstanding prompt finishes its turn.</param>
    public TerminalSessionProvider(
        CollabSessions sessions,
        CollabProjectIndex index,
        TerminalSessionRegistry? terminals,
        Func<ITabContentRegistry?>? tabs,
        ISettingsStore settings,
        IAgentCatalog catalog,
        ISignalHub? signals,
        IItemActivityService? activity,
        Action<string, string> turnEnded)
    {
        _sessions = sessions;
        _index = index;
        _terminals = terminals;
        _tabs = tabs;
        _settings = settings;
        _catalog = catalog;
        _activity = activity;
        _turnEnded = turnEnded;
        _signalSub = signals?.Subscribe(null, null, OnSignal);
    }

    public bool Handles(Project project, ProjectItem item) =>
        !ProjectLocation.IsOnRemoteHost(project) && item.Surface != AgentSurface.Gui;

    public ICollabSessionBridge? TryAttach(Project project, ProjectItem item) =>
        _terminals?.Find(item.Id) is not null ? Bridge(project, item) : null;

    public async Task<ICollabSessionBridge?> EnsureAsync(Project project, ProjectItem item, CancellationToken ct)
    {
        if (!_settings.Current.Agents.Collab.EnableTerminalBridge || _terminals is null) return null;
        if (_terminals.Find(item.Id) is null)
        {
            await CollabUi.InvokeAsync(() =>
            {
                var control = _tabs?.Invoke()?.GetOrCreate(project, item);
                if (control is Terminals.TerminalTabView termView)
                {
                    termView.EnsureStarted();
                }
            }).ConfigureAwait(false);
            // Give the new tab a moment to attach and spawn its PTY.
            for (var i = 0; i < 15 && _terminals.Find(item.Id) is null; i++)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }
        return _terminals.Find(item.Id) is { IsRunning: true } ? Bridge(project, item) : null;
    }

    private TerminalCollabSession Bridge(Project project, ProjectItem item) => new(
        item.Id,
        item.AgentId ?? "terminal",
        item.DisplayTitle,
        string.IsNullOrWhiteSpace(item.WorkingDirectory) ? project.Path : item.WorkingDirectory!,
        _terminals!,
        _activity);

    private void OnSignal(AgentSignal signal)
    {
        if (string.IsNullOrWhiteSpace(signal.ItemId)) return;
        if (_sessions.Find(signal.ItemId) is not TerminalCollabSession terminal) return;
        if (_index.FindItem(signal.ItemId)?.AgentId is not { } agentId || _catalog.Find(agentId) is not { } definition) return;
        if (SignalStatusMap.Resolve(definition, signal) != SignalStatus.Done) return;
        if (terminal.TakeAnswer(MaxAnswerChars) is { } answer) _turnEnded(signal.ItemId, answer);
    }

    public void Dispose() => _signalSub?.Dispose();
}
