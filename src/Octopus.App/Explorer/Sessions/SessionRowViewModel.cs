using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Agents.Sessions;
using Octopus.App.Common;
using Octopus.App.Localization;
using Octopus.Core.Agents;

namespace Octopus.App.Explorer.Sessions;

/// <summary>One saved session in the list: what it is about, when, and how big; continue or move it.</summary>
public sealed partial class SessionRowViewModel : ObservableObject
{
    private readonly Func<SessionRowViewModel, Task> _continue;
    private readonly Func<SessionRowViewModel, AgentSurface, Task>? _continueWithSurface;
    private readonly Func<SessionRowViewModel, Task> _migrate;
    private readonly bool _supportsAcp;
    private readonly AgentSurface _defaultSurface;

    public SessionRowViewModel(
        AgentSessionInfo info,
        DateTimeOffset now,
        string? liveItemId,
        Func<SessionRowViewModel, Task> resume,
        Func<SessionRowViewModel, Task> migrate,
        string? workspaceId = null,
        string? workspacePath = null,
        string? workspaceLabel = null,
        bool isWorktree = false,
        bool showWorkspaceBadge = false,
        bool supportsAcp = false,
        AgentSurface defaultSurface = AgentSurface.Terminal,
        Func<SessionRowViewModel, AgentSurface, Task>? resumeWithSurface = null)
    {
        Info = info;
        LiveItemId = liveItemId;
        WorkspaceId = workspaceId;
        WorkspacePath = workspacePath;
        WorkspaceLabel = workspaceLabel ?? "";
        IsWorktree = isWorktree;
        ShowWorkspaceBadge = showWorkspaceBadge && WorkspaceLabel.Length > 0;
        _supportsAcp = supportsAcp;
        _defaultSurface = supportsAcp ? defaultSurface : AgentSurface.Terminal;
        _continue = resume;
        _continueWithSurface = resumeWithSurface;
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
    /// <summary>The <c>ProjectWorkspace.Id</c> this session belongs to; null for default directory or unmanaged worktree.</summary>
    public string? WorkspaceId { get; }
    /// <summary>The directory where this session was originally recorded.</summary>
    public string? WorkspacePath { get; }
    /// <summary>Short badge label (<c>默认</c> / <c>Default</c> or branch name).</summary>
    public string WorkspaceLabel { get; }
    public bool IsWorktree { get; }
    public bool IsDefaultWorkspace => !IsWorktree;
    public bool ShowWorkspaceBadge { get; }

    /// <summary>The tab the session runs in now; null when no tab of the project runs it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLive), nameof(ContinueTip), nameof(CanContinueInAlternateSurface))]
    public partial string? LiveItemId { get; set; }

    public bool IsLive => LiveItemId is not null;

    public bool CanContinueInAlternateSurface => !IsLive && _supportsAcp && _continueWithSurface is not null;
    public bool AlternateSurfaceIsGui => _defaultSurface != AgentSurface.Gui;
    public string AlternateSurfaceTip => AppLangResources.Get(
        AlternateSurfaceIsGui ? AppLangResourceKind.SessionsContinueInGui : AppLangResourceKind.SessionsContinueInTerminal);

    public string ContinueTip => AppLangResources.Get(IsLive ? AppLangResourceKind.SessionsSwitchTo : AppLangResourceKind.SessionsContinue);

    /// <summary>The tooltip of the row: the full title, workspace path (if any) and session id.</summary>
    public string Tip => string.IsNullOrEmpty(WorkspacePath)
        ? $"{Info.Title}\n{Info.SessionId}"
        : $"{Info.Title}\n{WorkspacePath}\n{Info.SessionId}";

    [RelayCommand]
    private Task Continue() => _continue(this);

    [RelayCommand]
    private Task ContinueAlternate() => _continueWithSurface is not null && _supportsAcp
        ? _continueWithSurface(this, AlternateSurfaceIsGui ? AgentSurface.Gui : AgentSurface.Terminal)
        : _continue(this);

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
