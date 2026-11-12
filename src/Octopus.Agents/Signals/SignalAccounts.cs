using System.Security.Cryptography;
using System.Text;
using Octopus.Core.Agents;

namespace Octopus.Agents.Signals;

/// <summary>
/// Agent accounts are told apart by the directory in the definition's account variable (<c>CLAUDE_CONFIG_DIR</c>,
/// <c>CODEX_HOME</c>…). Plugins get a short hash of it so the path never leaves the host (design/plugins/11 §2.3).
/// The forwarder reports the raw value the agent saw, empty when unset; the hub, the planner and the probe runner
/// all turn it into a key here so the same directory always gives the same key.
/// </summary>
public static class SignalAccounts
{
    /// <summary>Hex characters of the key: 48 bits, plenty for the handful of accounts one user has.</summary>
    public const int KeyLength = 12;

    /// <summary>
    /// The account directory: <paramref name="value"/> when set, else the definition's default; <c>~</c> expanded,
    /// made absolute, without a trailing separator. Empty when the agent has no account variable or default.
    /// </summary>
    public static string ResolveDirectory(AgentDefinition definition, string? value, string? home = null)
    {
        var spec = definition.Signals?.Account;
        if (spec is null || string.IsNullOrEmpty(spec.Env))
        {
            return "";
        }
        var raw = string.IsNullOrWhiteSpace(value) ? spec.Default : value;
        return string.IsNullOrWhiteSpace(raw) ? "" : NormalizeDirectory(raw, home);
    }

    /// <summary>The account key for a raw variable value; empty for agents with a single account.</summary>
    public static string Key(AgentDefinition definition, string? value, string? home = null) =>
        KeyOfDirectory(ResolveDirectory(definition, value, home));

    /// <summary>The key of an already resolved directory; empty for an empty directory.</summary>
    public static string KeyOfDirectory(string directory)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return "";
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(directory));
        return Convert.ToHexStringLower(hash)[..KeyLength];
    }

    /// <summary>The last two segments, e.g. <c>accounts/work</c>; what the user sees until they name it.</summary>
    public static string DefaultLabel(string directory)
    {
        var parts = directory.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => directory,
            1 => parts[0],
            _ => parts[^2] + "/" + parts[^1],
        };
    }

    /// <summary>The user's home directory for <c>~</c>; the agent's own HOME (or USERPROFILE) when known.</summary>
    public static string HomeOf(IReadOnlyDictionary<string, string>? environment)
    {
        var name = OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME";
        if (environment is not null && environment.TryGetValue(name, out var home) && !string.IsNullOrWhiteSpace(home))
        {
            return home;
        }
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    internal static string NormalizeDirectory(string path, string? home)
    {
        var expanded = path.Trim();
        if (expanded == "~" || expanded.StartsWith("~/", StringComparison.Ordinal) || expanded.StartsWith("~\\", StringComparison.Ordinal))
        {
            home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            expanded = expanded.Length == 1 ? home : Path.Combine(home, expanded[2..]);
        }
        try
        {
            expanded = Path.GetFullPath(expanded);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Keep the value as given: the key only has to be stable, not the path valid.
        }
        var root = Path.GetPathRoot(expanded) ?? "";
        while (expanded.Length > root.Length && (expanded.EndsWith('/') || expanded.EndsWith('\\')))
        {
            expanded = expanded[..^1];
        }
        return expanded;
    }
}
