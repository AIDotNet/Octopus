using System.Globalization;

namespace Octopus.Ssh;

/// <summary>
/// A host to add to ~/.ssh/config: the fields of one Host block. Empty optional fields are left out of the block, so ssh
/// uses its own defaults for them.
/// </summary>
/// <param name="Port">Kept as text, so that what was typed is what is checked.</param>
public sealed record SshHostDraft(string Alias, string? HostName = null, string? User = null, string? Port = null, string? IdentityFile = null, string? ProxyJump = null)
{
    /// <summary>What is wrong with the draft, field by field; empty when the host can be added.</summary>
    /// <param name="existingAliases">The concrete hosts the file has already; an alias cannot repeat one, in any case.</param>
    public IReadOnlyList<SshHostIssue> Validate(IEnumerable<string> existingAliases)
    {
        var issues = new List<SshHostIssue>();
        var alias = Alias.Trim();
        if (alias.Length == 0)
        {
            issues.Add(new SshHostIssue(SshHostField.Alias, SshHostProblem.Required));
        }
        else if (!IsAlias(alias))
        {
            issues.Add(new SshHostIssue(SshHostField.Alias, SshHostProblem.AliasFormat));
        }
        else if (existingAliases.Contains(alias, StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(new SshHostIssue(SshHostField.Alias, SshHostProblem.AliasExists));
        }

        AddIfNotWord(issues, SshHostField.HostName, HostName);
        AddIfNotWord(issues, SshHostField.User, User);
        AddIfNotWord(issues, SshHostField.ProxyJump, ProxyJump);

        if (Blank(Port) is { } port && !(int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is >= 1 and <= 65535))
        {
            issues.Add(new SshHostIssue(SshHostField.Port, SshHostProblem.Port));
        }
        // A key path may hold spaces (ssh reads it quoted), but not quotes or line breaks.
        if (Blank(IdentityFile) is { } key && (key.Contains('"') || key.Any(char.IsControl)))
        {
            issues.Add(new SshHostIssue(SshHostField.IdentityFile, SshHostProblem.KeyPath));
        }
        return issues;
    }

    /// <summary>A name that can be typed in a command and in a project: letters and digits, then dots, underscores and dashes.</summary>
    internal static bool IsAlias(string alias) =>
        char.IsAsciiLetterOrDigit(alias[0]) && alias.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    /// <summary>A single word as ssh reads it: no spaces, quotes or comment sign, and not an option.</summary>
    internal static bool IsWord(string value) =>
        !value.StartsWith('-') && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '"' or '\'' or '#' or '=');

    private static void AddIfNotWord(List<SshHostIssue> issues, SshHostField field, string? value)
    {
        if (Blank(value) is { } word && !IsWord(word))
        {
            issues.Add(new SshHostIssue(field, SshHostProblem.Word));
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>The fields of <see cref="SshHostDraft"/>.</summary>
public enum SshHostField
{
    Alias,
    HostName,
    User,
    Port,
    IdentityFile,
    ProxyJump
}

/// <summary>Why a field cannot be used.</summary>
public enum SshHostProblem
{
    /// <summary>The alias is empty.</summary>
    Required,
    /// <summary>The alias has characters that ssh and the app do not take in a name.</summary>
    AliasFormat,
    /// <summary>The file already has a host with this alias.</summary>
    AliasExists,
    /// <summary>A value with a space, quote, comment sign, equals sign or a leading dash.</summary>
    Word,
    /// <summary>The port is not a number from 1 to 65535.</summary>
    Port,
    /// <summary>The key path has a quote or a line break.</summary>
    KeyPath
}

/// <param name="Field">The field the problem is in.</param>
/// <param name="Problem">What is wrong with it.</param>
public sealed record SshHostIssue(SshHostField Field, SshHostProblem Problem);
