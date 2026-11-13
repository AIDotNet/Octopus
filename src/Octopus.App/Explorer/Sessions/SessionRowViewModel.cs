using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Agents.Sessions;
using Octopus.App.Common;
using Octopus.App.Localization;

namespace Octopus.App.Explorer.Sessions;

/// <summary>One saved session in the list: what it is about, when, and how big; continue or move it.</summary>
public sealed partial class SessionRowViewModel : ObservableObject
{
    private readonly Func<SessionRowViewModel, Task> _continue;
    private readonly Func<SessionRowViewModel, Task> _migrate;

    public SessionRowViewModel(AgentSessionInfo info, DateTimeOffset now, string? liveItemId, Func<SessionRowViewModel, Task> resume, Func<SessionRowViewModel, Task> migrate)
    {
        Info = info;
        LiveItemId = liveItemId;
        _continue = resume;
        _migrate = migrate;
        WhenText = RelativeTime.Format(info.UpdatedAt, now);
        var parts = new List<string> { AppLangResources.Format(AppLangResourceKind.SessionsMessages, info.MessageCount) };
        if (ModelText(info.Model) is { } model) parts.Add(model);
        if (!string.IsNullOrEmpty(info.GitBranch) && info.GitBranch != "HEAD") parts.Add(info.GitBranch);
        MetaText = string.Join(" · ", parts);
    }

    public AgentSessionInfo Info { get; }

    public string Title => Info.Title;
    public string WhenText { get; }
    public string MetaText { get; }

    /// <summary>The tab the session runs in now; null when no tab of the project runs it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLive), nameof(ContinueTip))]
    public partial string? LiveItemId { get; set; }

    public bool IsLive => LiveItemId is not null;

    public string ContinueTip => AppLangResources.Get(IsLive ? AppLangResourceKind.SessionsSwitchTo : AppLangResourceKind.SessionsContinue);

    /// <summary>The tooltip of the row: the full title and the session id, for the long titles the row cuts.</summary>
    public string Tip => $"{Info.Title}\n{Info.SessionId}";

    [RelayCommand]
    private Task Continue() => _continue(this);

    [RelayCommand]
    private Task Migrate() => _migrate(this);

    /// <summary><c>claude-opus-5-5</c> reads as <c>Opus 5.5</c>; other names are shown as the agent wrote them.</summary>
    internal static string? ModelText(string? model)
    {
        if (string.IsNullOrWhiteSpace(model) || model.StartsWith('<')) return null;
        var parts = model.Split('-');
        if (parts.Length >= 4 && parts[0] == "claude" && int.TryParse(parts[2], out _) && int.TryParse(parts[3], out _))
        {
            return $"{char.ToUpperInvariant(parts[1][0])}{parts[1][1..]} {parts[2]}.{parts[3]}";
        }
        return model;
    }
}
