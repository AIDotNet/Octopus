namespace Octopus.Core.Agents;

/// <summary>Where a session's work came from, for a badge or a subtitle. Each UI words it in its own language.</summary>
public enum CollabOriginKind
{
    /// <summary>A person started it here: nothing to say.</summary>
    None,
    /// <summary>A session of the same project delegated it (shown by nesting on the desktop, by "delegated by" elsewhere).</summary>
    SameProjectParent,
    /// <summary>A session of this computer, in another project, delegated it.</summary>
    LocalParentElsewhere,
    /// <summary>Another computer's agent delegated it (<see cref="CollabOrigin.Device"/>, <see cref="CollabOrigin.Session"/>).</summary>
    OtherDevice,
}

/// <param name="Device">The other computer (<see cref="CollabOriginKind.OtherDevice"/>).</param>
/// <param name="Session">The delegating session's label or title.</param>
public readonly record struct CollabOrigin(CollabOriginKind Kind, string? Device = null, string? Session = null)
{
    public static CollabOrigin None => default;

    /// <summary>
    /// The one rule for all UIs. A parent known here wins (in another project of this computer, or of the same project);
    /// a parent of another computer, or a delegation recorded by the host, names that computer.
    /// </summary>
    /// <param name="parent">The parent session if it is known here: its title, whether it is in the same project, and the
    /// computer it runs on (null for this one).</param>
    public static CollabOrigin Describe(
        (string Title, bool SameProject, string? Host)? parent,
        string? originDevice,
        string? originSession)
    {
        if (parent is { } p)
        {
            if (p.Host is { Length: > 0 } host) return new CollabOrigin(CollabOriginKind.OtherDevice, host, p.Title);
            return new CollabOrigin(p.SameProject ? CollabOriginKind.SameProjectParent : CollabOriginKind.LocalParentElsewhere, null, p.Title);
        }
        return originDevice is { Length: > 0 } device
            ? new CollabOrigin(CollabOriginKind.OtherDevice, device, string.IsNullOrWhiteSpace(originSession) ? null : originSession)
            : None;
    }
}
