using Octopus.Core.Agents;
using Octopus.Storage;

namespace Octopus.App.Collab;

/// <summary>
/// The rules of the collaboration bus that product decisions may change, gathered in one replaceable place (register
/// another implementation in <see cref="CollabModule"/>). <see cref="DefaultCollabPolicy"/> reads the user's settings.
/// </summary>
public interface ICollabPolicy
{
    /// <summary>How deep delegation may nest (parent → child → grandchild is 2).</summary>
    int MaxDelegationDepth { get; }

    /// <summary>The hop budget a new negotiation thread starts with.</summary>
    int DefaultMaxHops { get; }

    /// <summary>Whether a reply (or an outcome) is pushed into the asking session as a new turn when it is not awaiting it.</summary>
    bool WakeCallerOnReply { get; }

    /// <summary>
    /// The thread status of an implicit report (a session ended its turn without reply_thread). <c>agreed</c> closes the
    /// exchange; <c>continue</c> would make two sessions' implicit reports trigger each other until the budget runs out.
    /// </summary>
    string ImplicitReplyStatus { get; }

    /// <summary>The longest reply or result kept and relayed, in characters.</summary>
    int MaxReplyChars { get; }

    /// <summary>When the user stops a session, also cancel the tasks it delegated during that turn.</summary>
    bool CancelDelegationsWhenStopped { get; }

    /// <summary>Whether a delegate on another computer may use <paramref name="mode"/> (it cannot be enforced there yet).</summary>
    bool AllowsRemoteIsolation(CollabIsolationMode mode);
}

public sealed class DefaultCollabPolicy(ISettingsStore settings) : ICollabPolicy
{
    private CollabSettings Settings => settings.Current.Agents.Collab;

    public int MaxDelegationDepth => Settings.MaxDelegationDepth;

    public int DefaultMaxHops => Math.Clamp(Settings.MaxNegotiationHops, CollabSettings.MinNegotiationHops, CollabSettings.MaxAllowedNegotiationHops);

    public bool WakeCallerOnReply => Settings.AutoWakeupCaller;

    public string ImplicitReplyStatus => "agreed";

    public int MaxReplyChars => 3500;

    public bool CancelDelegationsWhenStopped => true;

    public bool AllowsRemoteIsolation(CollabIsolationMode mode) => mode == CollabIsolationMode.Shared;
}
