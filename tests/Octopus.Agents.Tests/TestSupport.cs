using System.Text;
using Octopus.Agents;
using Octopus.Core.Agents;
using Octopus.Platform;

namespace Octopus.Agents.Tests;

/// <summary>Minimal POSIX quoting and splitting, enough for the compiler tests.</summary>
internal sealed class TestShellBuilder : IShellCommandBuilder
{
    public string Quote(string argument, ShellKind kind)
    {
        if (argument.Length > 0 && argument.All(c => char.IsLetterOrDigit(c) || "-_./=:@%+,[]".Contains(c)))
        {
            return argument;
        }
        return "'" + argument.Replace("'", "'\\''") + "'";
    }

    public string Build(string executable, IEnumerable<string> arguments, ShellKind kind) =>
        string.Join(' ', new[] { executable }.Concat(arguments).Select(a => Quote(a, kind)));

    public IReadOnlyList<string> Split(string commandLine)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        var has = false;
        char quote = '\0';
        foreach (var c in commandLine)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0'; else sb.Append(c);
            }
            else if (c is '"' or '\'')
            {
                quote = c;
                has = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (has || sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); has = false; }
            }
            else
            {
                sb.Append(c);
            }
        }
        if (has || sb.Length > 0) result.Add(sb.ToString());
        return result;
    }
}

internal sealed class TestPaths(string root) : IAppPaths
{
    public string DataDirectory { get; } = Path.Combine(root, "data");
    public string CacheDirectory { get; } = Path.Combine(root, "cache");
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "octopus-agents-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch (IOException) { }
    }
}

internal sealed class FakeLoginEnvironment(string path = "/bin") : ILoginEnvironment
{
    public Task<IReadOnlyDictionary<string, string>> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { ["PATH"] = path, ["HOME"] = "/home/x" });
}

internal sealed class FakeLocator(Dictionary<string, string[]> map) : IExecutableLocator
{
    public IReadOnlyList<string> FindAll(string name, string? pathVariable) =>
        map.TryGetValue(name, out var v) ? v : [];
}

internal sealed class FakeRunner(Func<string, IEnumerable<string>, IReadOnlyDictionary<string, string>?, ProcessResult> handler) : IProcessRunner
{
    public List<(string File, IReadOnlyDictionary<string, string>? Env)> Calls { get; } = [];

    public Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null, string? workingDirectory = null,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        lock (Calls) Calls.Add((fileName, environment));
        return Task.FromResult(handler(fileName, arguments, environment));
    }
}

internal sealed class FixedCatalog(params AgentDefinition[] definitions) : IAgentCatalog
{
    public IReadOnlyList<AgentDefinition> Definitions { get; } = definitions;
    public AgentDefinition? Find(string agentId) => Definitions.FirstOrDefault(d => d.Id == agentId);
    public void Reload() { }
    public event EventHandler? Changed { add { } remove { } }
}

internal static class Builtins
{
    private static readonly Lazy<AgentCatalog> Catalog = new(() =>
    {
        var dir = new TempDir();
        return new AgentCatalog(new TestPaths(dir.Path));
    });

    public static AgentDefinition Get(string id) => Catalog.Value.Find(id)!;
    public static AgentCatalog All => Catalog.Value;
}
