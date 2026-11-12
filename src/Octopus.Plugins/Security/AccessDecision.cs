namespace Octopus.Plugins.Security;

/// <summary>Why the access policy refused a path. The plugin sees it with <c>PermissionDenied</c> (design/plugins/04 §4).</summary>
public enum AccessDenial
{
    /// <summary>Inside no scope the plugin holds, or excluded by the scope's globs.</summary>
    NotInScope,
    /// <summary>A secret-looking file (<c>.env</c>, keys, <c>.ssh/</c>…) and "include sensitive files" is off.</summary>
    Sensitive,
    /// <summary>Octopus's own data, another plugin's directory, or <c>.git</c>.</summary>
    Protected,
    /// <summary>A write into an operating-system location.</summary>
    SystemLocation,
    /// <summary>Another application's login credentials: never readable, under any grant.</summary>
    Credential,
    /// <summary>Not an absolute path, or a form that is refused outright (NUL, Windows device paths, streams…).</summary>
    InvalidPath,
    /// <summary>A write in a scope that only allows reading.</summary>
    ReadOnlyScope,
}

/// <summary>The outcome of one <see cref="IPluginAccessPolicy.Check"/>.</summary>
public sealed record AccessDecision
{
    private AccessDecision()
    {
    }

    public bool Allowed => Denial is null;

    /// <summary>
    /// The normalized path with every link resolved. The operation must use this path, not the one the plugin passed:
    /// it is what was checked. Null only for <see cref="AccessDenial.InvalidPath"/>.
    /// </summary>
    public string? RealPath { get; private init; }

    /// <summary>The scope that allowed the access; null when denied.</summary>
    public FileScope? Scope { get; private init; }

    public AccessDenial? Denial { get; private init; }

    /// <summary>An English explanation for the plugin author and the audit log.</summary>
    public string Reason { get; private init; } = "";

    internal static AccessDecision Allow(FileScope scope, string realPath) => new() { Scope = scope, RealPath = realPath };

    internal static AccessDecision Deny(AccessDenial denial, string reason, string? realPath) =>
        new() { Denial = denial, Reason = reason, RealPath = realPath };
}
