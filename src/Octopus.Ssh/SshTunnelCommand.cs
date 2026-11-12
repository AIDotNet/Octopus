using Octopus.Core.Projects;

namespace Octopus.Ssh;

/// <summary>The fields of a <see cref="TunnelSpec"/> that can be wrong.</summary>
public enum TunnelField
{
    BindAddress,
    ListenPort,
    TargetHost,
    TargetPort
}

/// <summary>Why a field cannot be used.</summary>
public enum TunnelProblem
{
    /// <summary>The field is empty.</summary>
    Required,
    /// <summary>The port is not a number from 1 to 65535.</summary>
    Port,
    /// <summary>The address has a space, quote, comment sign, a colon outside brackets or a leading dash.</summary>
    Address
}

/// <param name="Field">The field the problem is in.</param>
/// <param name="Problem">What is wrong with it.</param>
public sealed record TunnelIssue(TunnelField Field, TunnelProblem Problem);

/// <summary>
/// The ssh run of one tunnel (design/11-SSH隧道.md §4): <c>ssh -N</c> with the forward, on a connection of its own. It
/// is not given the shared connection of the terminal: a forward asked of a shared connection would stay in it when this
/// run ends, and the tunnel would no longer be what the run says it is. Every value is an argument of its own, so nothing
/// here is quoted for a shell.
/// </summary>
public static class SshTunnelCommand
{
    /// <summary>What is wrong with the spec, field by field; empty when the tunnel can be started.</summary>
    public static IReadOnlyList<TunnelIssue> Validate(TunnelSpec spec)
    {
        var issues = new List<TunnelIssue>();
        CheckAddress(issues, TunnelField.BindAddress, spec.BindAddress);
        CheckAddress(issues, TunnelField.TargetHost, spec.TargetHost);
        CheckPort(issues, TunnelField.ListenPort, spec.ListenPort);
        CheckPort(issues, TunnelField.TargetPort, spec.TargetPort);
        return issues;
    }

    /// <summary>
    /// The arguments of the ssh run for a valid spec. Without a remote command (<c>-N</c>) the run only holds the forward;
    /// <c>ExitOnForwardFailure</c> makes it end when the forward cannot be made, instead of staying up without it.
    /// </summary>
    public static IReadOnlyList<string> Arguments(string host, TunnelSpec spec) =>
    [
        "-N",
        "-o", "ExitOnForwardFailure=yes",
        .. SshCommands.Unattended,
        "-o", "ControlMaster=no",
        "-o", "ControlPath=none",
        // The log is read as it is written: it says when the forward is up and why it ended.
        "-o", "LogLevel=DEBUG1",
        Flag(spec),
        Forward(spec),
        "--", host,
    ];

    /// <summary><c>-L</c> for a local tunnel, <c>-R</c> for a remote one.</summary>
    public static string Flag(TunnelSpec spec) => spec.Kind == TunnelKind.Local ? "-L" : "-R";

    /// <summary>The value of <c>-L</c> or <c>-R</c>: <c>bind:port:targetHost:targetPort</c>, the bind address always written out.</summary>
    public static string Forward(TunnelSpec spec) => $"{spec.BindAddress}:{spec.ListenPort}:{spec.TargetHost}:{spec.TargetPort}";

    /// <summary>True for an address that is bound or reached only on this computer.</summary>
    public static bool IsLoopback(string address) =>
        address is "127.0.0.1" or "localhost" or "[::1]" || address.StartsWith("127.", StringComparison.Ordinal);

    private static void CheckAddress(List<TunnelIssue> issues, TunnelField field, string? value)
    {
        var text = value?.Trim() ?? "";
        if (text.Length == 0)
        {
            issues.Add(new TunnelIssue(field, TunnelProblem.Required));
        }
        else if (!IsAddress(text))
        {
            issues.Add(new TunnelIssue(field, TunnelProblem.Address));
        }
    }

    private static void CheckPort(List<TunnelIssue> issues, TunnelField field, int port)
    {
        if (port is < 1 or > 65535)
        {
            issues.Add(new TunnelIssue(field, TunnelProblem.Port));
        }
    }

    /// <summary>
    /// A host name or address as one word of the forward: <c>:</c> separates the parts of the forward, so an IPv6 address
    /// has to be in brackets, as ssh asks.
    /// </summary>
    internal static bool IsAddress(string value)
    {
        if (!SshHostDraft.IsWord(value))
        {
            return false;
        }
        if (value.StartsWith('['))
        {
            return value.Length > 2 && value.EndsWith(']') && value[1..^1].All(c => char.IsAsciiHexDigit(c) || c is ':' or '.' or '%');
        }
        return !value.Contains(':') && !value.Contains('[') && !value.Contains(']');
    }
}
