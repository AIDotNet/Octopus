using System.Globalization;
using System.Text.Json;
using Octopus.Core.Plugins;

namespace Octopus.Plugins.Manifests;

/// <summary>Collects the issues of one manifest read.</summary>
internal sealed class IssueLog
{
    private readonly List<ManifestIssue> _issues = [];

    public IReadOnlyList<ManifestIssue> Issues => _issues;
    public bool HasErrors => _issues.Any(i => i.Severity == IssueSeverity.Error);

    public void Error(string path, string message) => _issues.Add(new ManifestIssue(IssueSeverity.Error, path, message));
    public void Warn(string path, string message) => _issues.Add(new ManifestIssue(IssueSeverity.Warning, path, message));
}

/// <summary>
/// A JSON object of the manifest with its path, read leniently: a value of the wrong type is reported and treated as
/// missing, unknown properties are reported as warnings by <see cref="WarnUnknown"/>.
/// </summary>
internal readonly struct ManifestObject(JsonElement element, string path, IssueLog log)
{
    public string Path => path;
    public IssueLog Log => log;

    public string At(string name) => $"{path}.{name}";

    public bool Has(string name) => element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null;

    public JsonElement? Raw(string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;

    public string? String(string name, bool required = false)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            if (required) log.Error(At(name), "is required");
            return null;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            log.Error(At(name), "must be a string");
            return null;
        }
        var text = value.GetString()!;
        if (required && text.Trim().Length == 0)
        {
            log.Error(At(name), "must not be empty");
            return null;
        }
        return text;
    }

    public bool Bool(string name, bool fallback = false)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return fallback;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
        log.Error(At(name), "must be true or false");
        return fallback;
    }

    public long? Integer(string name, bool required = false, long min = long.MinValue)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            if (required) log.Error(At(name), "is required");
            return null;
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
        {
            log.Error(At(name), "must be a whole number");
            return null;
        }
        if (number < min)
        {
            log.Error(At(name), $"must be at least {min.ToString(CultureInfo.InvariantCulture)}");
            return null;
        }
        return number;
    }

    /// <summary>An array of strings; a single string is accepted as a one-element array.</summary>
    public IReadOnlyList<string> Strings(string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return [];
        if (value.ValueKind == JsonValueKind.String) return [value.GetString()!];
        if (value.ValueKind != JsonValueKind.Array)
        {
            log.Error(At(name), "must be an array of strings");
            return [];
        }
        var list = new List<string>();
        var i = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString()!);
            else log.Error($"{At(name)}[{i}]", "must be a string");
            i++;
        }
        return list;
    }

    public ManifestObject? Object(string name, bool required = false)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            if (required) log.Error(At(name), "is required");
            return null;
        }
        if (value.ValueKind != JsonValueKind.Object)
        {
            log.Error(At(name), "must be an object");
            return null;
        }
        return new ManifestObject(value, At(name), log);
    }

    /// <summary>The objects of an array property; non-object items are reported and skipped.</summary>
    public IEnumerable<ManifestObject> Objects(string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) yield break;
        if (value.ValueKind != JsonValueKind.Array)
        {
            log.Error(At(name), "must be an array");
            yield break;
        }
        var i = 0;
        foreach (var item in value.EnumerateArray())
        {
            var itemPath = $"{At(name)}[{i}]";
            if (item.ValueKind == JsonValueKind.Object) yield return new ManifestObject(item, itemPath, log);
            else log.Error(itemPath, "must be an object");
            i++;
        }
    }

    /// <summary>The properties of this object (for dictionaries such as <c>menus</c> or <c>env</c>).</summary>
    public IEnumerable<(string Name, JsonElement Value)> Properties()
    {
        foreach (var property in element.EnumerateObject()) yield return (property.Name, property.Value);
    }

    /// <summary>A string → string map; non-string values are reported and skipped.</summary>
    public IReadOnlyDictionary<string, string> StringMap(string name)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Object(name) is not { } obj) return map;
        foreach (var (key, value) in obj.Properties())
        {
            if (value.ValueKind == JsonValueKind.String) map[key] = value.GetString()!;
            else log.Error(obj.At(key), "must be a string");
        }
        return map;
    }

    /// <summary>The optional <c>localized</c> block: language tag → field → text.</summary>
    public PluginLocalized Localized()
    {
        if (Object("localized") is not { } localized) return PluginLocalized.Empty;
        var byLanguage = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (language, value) in localized.Properties())
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                log.Warn(localized.At(language), "must be an object of texts; ignored");
                continue;
            }
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject())
            {
                if (field.Value.ValueKind == JsonValueKind.String) fields[field.Name] = field.Value.GetString()!;
                else log.Warn($"{localized.At(language)}.{field.Name}", "must be a string; ignored");
            }
            byLanguage[language] = fields;
        }
        return new PluginLocalized(byLanguage);
    }

    /// <summary>Reports properties that are not in <paramref name="known"/> (newer manifests on an older app stay loadable).</summary>
    public void WarnUnknown(params string[] known)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!known.Contains(property.Name, StringComparer.Ordinal))
            {
                log.Warn(At(property.Name), "is not known to this version of Octopus; ignored");
            }
        }
    }
}
