namespace Octopus.Agents.Sessions;

/// <summary>
/// Where Claude Code keeps a project's sessions: <c>&lt;config dir&gt;/projects/&lt;encoded path&gt;/&lt;session id&gt;.jsonl</c>,
/// the encoded path being the working directory with every character other than an ASCII letter or digit turned into
/// <c>-</c> (<c>/Users/me/code/app.x</c> → <c>-Users-me-code-app-x</c>). Different folders can encode alike, so readers
/// still check the <c>cwd</c> the records carry.
/// </summary>
public static class ClaudeProjectFolder
{
    /// <summary>Claude Code shortens longer names (and adds a hash); folders are then matched on this prefix.</summary>
    public const int MaxNameLength = 200;

    public static string Encode(string projectPath)
    {
        var chars = projectPath.TrimEnd('/', '\\').ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (!char.IsAsciiLetterOrDigit(chars[i])) chars[i] = '-';
        }
        return new string(chars);
    }

    /// <summary>The folders under <c>&lt;accountDirectory&gt;/projects</c> that may hold the project's sessions (usually one; none when missing).</summary>
    public static IReadOnlyList<string> Candidates(string accountDirectory, string projectPath)
    {
        var root = Path.Combine(accountDirectory, "projects");
        var name = Encode(projectPath);
        var exact = Path.Combine(root, name);
        if (name.Length <= MaxNameLength) return Directory.Exists(exact) ? [exact] : [];
        if (!Directory.Exists(root)) return [];
        var prefix = name[..MaxNameLength];
        try
        {
            return [.. Directory.EnumerateDirectories(root, prefix + "*")];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The folder a session of the project is written to in an account (it may not exist yet).</summary>
    public static string For(string accountDirectory, string projectPath) =>
        Candidates(accountDirectory, projectPath) is [var found, ..] ? found : Path.Combine(accountDirectory, "projects", Encode(projectPath));
}
