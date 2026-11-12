using System.Reflection;
using System.Text.Json;
using Octopus.Core.Agents;
using Octopus.Platform;

namespace Octopus.Agents;

/// <summary>Built-in definitions (embedded JSON) merged with the user's <c>agents/*.json</c>.</summary>
public sealed class AgentCatalog : IAgentCatalog
{
    /// <summary>Order of the built-in agents in lists; custom ones follow.</summary>
    private static readonly string[] BuiltInOrder =
    [
        "claude", "codex", "grok", "opencode", "cursor", "muse", "gemini", "copilot",
        "amp", "droid", "aider", "qwen", "kiro", "goose", "crush", "kimi"
    ];

    private readonly IAppPaths _paths;
    private readonly object _gate = new();
    private IReadOnlyList<AgentDefinition> _definitions = [];
    private IReadOnlyList<string> _loadErrors = [];

    private readonly IReadOnlyList<IAgentDefinitionSource> _sources;

    public AgentCatalog(IAppPaths paths, IEnumerable<IAgentDefinitionSource>? sources = null)
    {
        _paths = paths;
        _sources = sources?.ToList() ?? [];
        foreach (var source in _sources) source.Changed += (_, _) => Reload();
        Reload();
    }

    public IReadOnlyList<AgentDefinition> Definitions => _definitions;

    /// <summary>Files that could not be loaded at the last (re)load, one message each.</summary>
    public IReadOnlyList<string> LoadErrors => _loadErrors;

    public event EventHandler? Changed;

    public AgentDefinition? Find(string agentId) =>
        _definitions.FirstOrDefault(d => string.Equals(d.Id, agentId, StringComparison.OrdinalIgnoreCase));

    public void Reload()
    {
        var errors = new List<string>();
        var list = new List<AgentDefinition>();

        foreach (var def in LoadBuiltIn(errors))
        {
            list.Add(def);
        }
        list.Sort((a, b) => OrderOf(a.Id).CompareTo(OrderOf(b.Id)));

        // Plugins add agents; one with an id that is already taken is left out (it would change what a person chose).
        foreach (var def in LoadFromSources(errors))
        {
            if (list.Any(d => string.Equals(d.Id, def.Id, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"{def.OriginName}: the agent \"{def.Id}\" already exists and is not replaced by a plugin.");
                continue;
            }
            list.Add(def);
        }

        foreach (var def in LoadUser(errors))
        {
            var index = list.FindIndex(d => string.Equals(d.Id, def.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                list[index] = def;
            }
            else
            {
                list.Add(def);
            }
        }

        lock (_gate)
        {
            _definitions = list;
            _loadErrors = errors;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static int OrderOf(string id)
    {
        var i = Array.IndexOf(BuiltInOrder, id);
        return i < 0 ? int.MaxValue : i;
    }

    private static IEnumerable<AgentDefinition> LoadBuiltIn(List<string> errors)
    {
        var assembly = typeof(AgentCatalog).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Order(StringComparer.Ordinal))
        {
            if (!name.Contains(".Catalog.", StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.Ordinal))
            {
                continue;
            }
            AgentDefinition? def = null;
            try
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                def = JsonSerializer.Deserialize(stream, AgentJsonContext.Default.AgentDefinition);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                errors.Add($"{name}: {ex.Message}");
            }
            if (def is not null && Validate(def, name, errors))
            {
                yield return def;
            }
        }
    }

    private IEnumerable<AgentDefinition> LoadFromSources(List<string> errors)
    {
        foreach (var source in _sources)
        {
            foreach (var file in source.Files)
            {
                AgentDefinition? def = null;
                try
                {
                    def = JsonSerializer.Deserialize(File.ReadAllText(file.Path), AgentJsonContext.Default.AgentDefinition);
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{file.OriginName} ({Path.GetFileName(file.Path)}): {ex.Message}");
                }
                if (def is not null && Validate(def, $"{file.OriginName} ({Path.GetFileName(file.Path)})", errors))
                {
                    def.Origin = file.Origin;
                    def.OriginName = file.OriginName;
                    yield return def;
                }
            }
        }
    }

    private IEnumerable<AgentDefinition> LoadUser(List<string> errors)
    {
        string directory;
        try
        {
            directory = Path.Combine(_paths.DataDirectory, "agents");
            if (!Directory.Exists(directory))
            {
                yield break;
            }
        }
        catch (Exception ex)
        {
            errors.Add($"agents folder: {ex.Message}");
            yield break;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(directory, "*.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add($"{directory}: {ex.Message}");
            yield break;
        }
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            AgentDefinition? def = null;
            try
            {
                var text = File.ReadAllText(file);
                def = JsonSerializer.Deserialize(text, AgentJsonContext.Default.AgentDefinition);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
            if (def is not null && Validate(def, Path.GetFileName(file), errors))
            {
                def.IsCustom = true;
                yield return def;
            }
        }
    }

    private static bool Validate(AgentDefinition def, string source, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(def.Id))
        {
            errors.Add($"{source}: missing id");
            return false;
        }
        if (string.IsNullOrWhiteSpace(def.Name))
        {
            def.Name = def.Id;
        }
        return true;
    }
}
