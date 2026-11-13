using CommunityToolkit.Mvvm.Input;
using Octopus.Agents.Sessions;
using Octopus.App.Common;
using Octopus.App.Localization;
using Octopus.Core.Agents;

namespace Octopus.App.Explorer.Sessions;

/// <summary>An installed agent on the first page of the sessions tab: how many sessions it has here and in which accounts.</summary>
public sealed partial class SessionAgentRowViewModel(AgentSessionGroup group, DateTimeOffset now, Action<SessionAgentRowViewModel> open)
{
    public AgentSessionGroup Group { get; } = group;
    public AgentDefinition Agent => Group.Agent;
    public string AgentId => Agent.Id;
    public string Name => Agent.Name;
    public bool HasSessions => Group.Count > 0;

    public string Summary => !Group.IsSupported
        ? AppLangResources.Get(AppLangResourceKind.SessionsUnsupported)
        : Group.LatestAt is { } latest
            ? AppLangResources.Format(AppLangResourceKind.SessionsAgentSummary, Group.Count, RelativeTime.Format(latest, now))
            : AppLangResources.Get(AppLangResourceKind.SessionsNone);

    /// <summary>The accounts with sessions, named in the tooltip of the count shown when there is more than one.</summary>
    public IReadOnlyList<string> AccountLabels => [.. Group.Accounts.Select(a => a.Label)];
    public bool HasAccountLabels => Group.Accounts.Count > 1;
    public string AccountCountText => AppLangResources.Format(AppLangResourceKind.SessionsAccountCount, Group.Accounts.Count);
    public string AccountsTip => string.Join("\n", AccountLabels);

    [RelayCommand]
    private void Open() => open(this);
}
