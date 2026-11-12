using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Octopus.Core.Agents;
using Octopus.Platform;

namespace Octopus.Agents;

/// <summary>
/// Writes and removes the user's own agent definitions: one JSON file per agent in <c>&lt;data&gt;/agents</c>, read by
/// <see cref="IAgentCatalog"/>. A file whose id is a built-in agent's replaces that agent; removing it brings the
/// built-in one back.
/// </summary>
public interface IAgentDefinitionStore
{
    /// <summary>True when the user has a file for this agent (a custom agent, or one that replaces a built-in).</summary>
    bool HasUserFile(string agentId);

    /// <summary>True when Octopus itself ships an agent with this id.</summary>
    bool IsBuiltIn(string agentId);

    /// <summary>Writes the definition (replacing the file of the same id), then reloads the catalog.</summary>
    void Save(AgentDefinition definition);

    /// <summary>Removes the user's file of this agent, then reloads the catalog. False when there was none.</summary>
    bool Delete(string agentId);

    /// <summary>The definition as the JSON its file holds.</summary>
    string ToJson(AgentDefinition definition);

    /// <summary>Reads a definition from JSON; null with the reason when it is not one.</summary>
    AgentDefinition? Parse(string json, out string? error);

    /// <summary>A copy that can be edited without touching the catalog's object.</summary>
    AgentDefinition Clone(AgentDefinition definition);

    /// <summary>An id made from a name ("My Agent" → "my-agent"), not taken by another agent.</summary>
    string SuggestId(string name);

    /// <summary>True when the id is usable as a file name and in settings: letters, digits, '-', '_' and '.'.</summary>
    bool IsValidId(string id);
}

public sealed partial class AgentDefinitionStore(IAppPaths paths, IAgentCatalog catalog) : IAgentDefinitionStore
{
    private string Directory => Path.Combine(paths.DataDirectory, "agents");

    public bool HasUserFile(string agentId) => FileOf(agentId) is not null;

    public bool IsBuiltIn(string agentId)
    {
        var assembly = typeof(AgentCatalog).Assembly;
        return assembly.GetManifestResourceNames().Any(name =>
            name.EndsWith(".Catalog." + agentId + ".json", StringComparison.OrdinalIgnoreCase));
    }

    public void Save(AgentDefinition definition)
    {
        if (!IsValidId(definition.Id)) throw new ArgumentException($"'{definition.Id}' is not a valid agent id.", nameof(definition));
        System.IO.Directory.CreateDirectory(Directory);
        // The file of this id may have another name (written by hand): it is the one replaced.
        var path = FileOf(definition.Id) ?? Path.Combine(Directory, definition.Id + ".json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, ToJson(definition), new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
        catalog.Reload();
    }

    public bool Delete(string agentId)
    {
        var path = FileOf(agentId);
        if (path is null) return false;
        File.Delete(path);
        catalog.Reload();
        return true;
    }

    public string ToJson(AgentDefinition definition) =>
        JsonSerializer.Serialize(definition, AgentJsonContext.Default.AgentDefinition) + "\n";

    public AgentDefinition? Parse(string json, out string? error)
    {
        try
        {
            var definition = JsonSerializer.Deserialize(json, AgentJsonContext.Default.AgentDefinition);
            if (definition is null)
            {
                error = "The JSON is empty.";
                return null;
            }
            error = null;
            return definition;
        }
        catch (JsonException e)
        {
            error = e.Message;
            return null;
        }
    }

    public AgentDefinition Clone(AgentDefinition definition)
    {
        var copy = JsonSerializer.Deserialize(ToJson(definition), AgentJsonContext.Default.AgentDefinition)!;
        copy.IsCustom = definition.IsCustom;
        return copy;
    }

    public string SuggestId(string name)
    {
        var slug = NonIdCharacters().Replace(name.Trim().ToLowerInvariant(), "-").Trim('-', '.');
        if (slug.Length == 0) slug = "agent";
        var id = slug;
        for (var n = 2; catalog.Find(id) is not null || HasUserFile(id); n++) id = $"{slug}-{n}";
        return id;
    }

    public bool IsValidId(string id) => id.Length is > 0 and <= 64 && ValidId().IsMatch(id);

    /// <summary>The user's file that defines this id (by the id inside, whatever the file is called).</summary>
    private string? FileOf(string agentId)
    {
        if (!System.IO.Directory.Exists(Directory)) return null;
        foreach (var file in System.IO.Directory.GetFiles(Directory, "*.json"))
        {
            try
            {
                using var stream = File.OpenRead(file);
                using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.EnumerateObject().FirstOrDefault(p => string.Equals(p.Name, "id", StringComparison.OrdinalIgnoreCase)) is { Value.ValueKind: JsonValueKind.String } property
                    && string.Equals(property.Value.GetString(), agentId, StringComparison.OrdinalIgnoreCase))
                {
                    return file;
                }
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                // A broken file defines nothing.
            }
        }
        return null;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex ValidId();

    [GeneratedRegex("[^a-z0-9._-]+")]
    private static partial Regex NonIdCharacters();
}
