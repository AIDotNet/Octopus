using Octopus.Platform;

namespace Octopus.App.Tests.Explorer;

/// <summary>A temporary project folder, optionally a git repository, deleted with the test.</summary>
internal sealed class ExplorerFixture : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("octopus-explorer-").FullName;

    public static string? FindGit() =>
        new Octopus.Platform.Unix.UnixExecutableLocator().FindAll("git", Environment.GetEnvironmentVariable("PATH")).FirstOrDefault();

    public string Write(string relative, string content = "")
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Git(params string[] args) => GitAt(null, args);

    /// <summary>Runs git and gives back what it printed, trimmed.</summary>
    public string GitOutput(params string[] args)
    {
        var git = FindGit() ?? throw new InvalidOperationException("git is not installed");
        var result = new ProcessRunner().RunAsync(git, ["-C", Root, .. args]).GetAwaiter().GetResult();
        if (!result.Succeeded) throw new InvalidOperationException(result.StandardError);
        return result.StandardOutput.Trim();
    }

    public void GitAt(string? date, params string[] args)
    {
        var git = FindGit() ?? throw new InvalidOperationException("git is not installed");
        var environment = new Dictionary<string, string>();
        if (date is not null)
        {
            environment["GIT_AUTHOR_DATE"] = date;
            environment["GIT_COMMITTER_DATE"] = date;
        }
        var result = new ProcessRunner()
            .RunAsync(git, ["-C", Root, "-c", "commit.gpgsign=false", "-c", "core.hooksPath=/dev/null", .. args], environment)
            .GetAwaiter().GetResult();
        if (!result.Succeeded) throw new InvalidOperationException(result.StandardError);
    }

    public void InitRepository()
    {
        Git("init", "-q");
        Git("config", "user.name", "Ada Lovelace");
        Git("config", "user.email", "ada@example.com");
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch (IOException) { }
    }
}
