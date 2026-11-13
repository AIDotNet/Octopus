using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Agents.Sessions;
using Octopus.App.Localization;
using Octopus.Core.Agents;
using TKCode.Arc;

namespace Octopus.App.Explorer.Sessions;

/// <summary>An agent a session can move to, the ways it can get there, and the accounts it can land in.</summary>
public sealed record SessionMigrationTarget(AgentDefinition Agent, IReadOnlyList<SessionMigrationKind> Kinds, IReadOnlyList<AgentSessionAccount> Accounts);

/// <summary>What the person chose in the migration dialog.</summary>
/// <param name="AccountDirectory">The target account's folder; null when the agent has no accounts to choose from.</param>
public sealed record SessionMigrationChoice(AgentDefinition Target, SessionMigrationKind Kind, string? AccountDirectory, bool IncludeToolOutput, bool RecentOnly);

/// <summary>The migration dialog: target agent, native or handoff, account, and what to take along.</summary>
public sealed partial class SessionMigrationFormViewModel : ObservableObject
{
    public const string NativeValue = "native";
    public const string HandoffValue = "handoff";

    public SessionMigrationFormViewModel(AgentSessionInfo session, string sourceName, string sourceAccount, IReadOnlyList<SessionMigrationTarget> targets)
    {
        Session = session;
        SourceName = sourceName;
        SourceAccount = sourceAccount;
        Targets = [.. targets.Select(t => new MigrationTargetRowViewModel(t, Select))];
        // The first agent that can take the session, preferring one that keeps the whole history.
        var first = Targets.FirstOrDefault(t => t.Target.Kinds.Contains(SessionMigrationKind.Native)) ?? Targets.FirstOrDefault(t => t.IsAvailable);
        if (first is not null) Select(first);
    }

    public AgentSessionInfo Session { get; }
    public string SourceName { get; }
    public string SourceAccount { get; }
    public string SourceMeta => $"{SourceName} · {SourceAccount} · {AppLangResources.Format(AppLangResourceKind.SessionsMessages, Session.MessageCount)}";

    public ObservableCollection<MigrationTargetRowViewModel> Targets { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanNative), nameof(CanHandoff), nameof(NativeHint), nameof(Accounts), nameof(HasAccounts))]
    public partial MigrationTargetRowViewModel? Selected { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNative), nameof(IsHandoff))]
    public partial string Mode { get; set; } = HandoffValue;

    [ObservableProperty] public partial string? AccountDirectory { get; set; }
    [ObservableProperty] public partial bool IncludeToolOutput { get; set; } = true;
    [ObservableProperty] public partial bool RecentOnly { get; set; }

    public bool CanNative => Selected?.Target.Kinds.Contains(SessionMigrationKind.Native) == true;
    public bool CanHandoff => Selected?.Target.Kinds.Contains(SessionMigrationKind.Handoff) == true;
    public bool IsNative => Mode == NativeValue;
    public bool IsHandoff => Mode == HandoffValue;

    public string NativeHint => Selected is null
        ? ""
        : CanNative
            ? AppLangResources.Format(AppLangResourceKind.MigrateNativeHint, Selected.Name)
            : AppLangResources.Format(AppLangResourceKind.MigrateNativeMissing, SourceName, Selected.Name);

    public string HandoffHint => AppLangResources.Get(AppLangResourceKind.MigrateHandoffHint);

    public IReadOnlyList<SelectOption> Accounts => Selected is null ? [] : [.. Selected.Target.Accounts.Select(a => new SelectOption(a.Directory, a.Label))];
    public bool HasAccounts => Accounts.Count > 1;

    /// <summary>The choice, or null when the selected agent cannot take the session in the chosen way.</summary>
    public SessionMigrationChoice? Choice
    {
        get
        {
            if (Selected is not { IsAvailable: true } target) return null;
            var kind = IsNative && CanNative ? SessionMigrationKind.Native : SessionMigrationKind.Handoff;
            if (!target.Target.Kinds.Contains(kind)) return null;
            return new SessionMigrationChoice(target.Target.Agent, kind, AccountDirectory, IncludeToolOutput, kind == SessionMigrationKind.Handoff && RecentOnly);
        }
    }

    private void Select(MigrationTargetRowViewModel row)
    {
        if (!row.IsAvailable) return;
        foreach (var t in Targets) t.IsSelected = ReferenceEquals(t, row);
        Selected = row;
        Mode = CanNative ? NativeValue : HandoffValue;
        // The default account, unless that is where the session already is (a copy to the same account is no move).
        AccountDirectory = row.Target.Accounts.FirstOrDefault()?.Directory;
    }
}

/// <summary>A row of the target list: the agent and which ways it can take the session.</summary>
public sealed partial class MigrationTargetRowViewModel(SessionMigrationTarget target, Action<MigrationTargetRowViewModel> select) : ObservableObject
{
    public SessionMigrationTarget Target { get; } = target;
    public string AgentId => Target.Agent.Id;
    public string Name => Target.Agent.Name;
    public bool IsAvailable => Target.Kinds.Count > 0;

    public string KindText => Target.Kinds switch
    {
        [] => AppLangResources.Get(AppLangResourceKind.MigrateKindUnavailable),
        var k when k.Contains(SessionMigrationKind.Native) && k.Contains(SessionMigrationKind.Handoff) => AppLangResources.Get(AppLangResourceKind.MigrateKindBoth),
        var k when k.Contains(SessionMigrationKind.Native) => AppLangResources.Get(AppLangResourceKind.MigrateNative),
        _ => AppLangResources.Get(AppLangResourceKind.MigrateHandoff),
    };

    public BadgeTone KindTone => Target.Kinds.Contains(SessionMigrationKind.Native) ? BadgeTone.Success : IsAvailable ? BadgeTone.Info : BadgeTone.Neutral;

    [ObservableProperty] public partial bool IsSelected { get; set; }

    [RelayCommand]
    private void Choose() => select(this);
}
