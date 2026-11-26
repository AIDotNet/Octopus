namespace Octopus.Mobile.Workspace;

/// <summary>
/// Something a prompt can @-mention, as the host lists it: an existing session (<see cref="Kind"/> <c>session</c>) or an
/// agent the conversation may start (<c>agent</c>).
/// </summary>
/// <param name="Name">The agent's name: the token's main part.</param>
/// <param name="Detail">A session's title, which tells sessions of the same agent apart.</param>
/// <param name="Subtitle">A second line for the list (a session's branch and status).</param>
public sealed record ConversationMentionTarget(string Kind, string TargetId, string Name, string? Detail, string? AgentId, string? Subtitle)
{
    public bool IsSession => Kind == "session";

    /// <summary>What the suggestion strip shows.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Detail) ? $"@{Name}" : $"@{Name} · {Detail}";
}
