using Octopus.Core.Plugins;

namespace Octopus.Plugins.Runtime;

/// <summary>
/// What one plugin may do right now: a permission counts only when the manifest declares it and the user granted it
/// (built-in plugins come with their declared permissions granted, design/plugins/12 §6), and it was not denied.
/// </summary>
public sealed class PluginGrants
{
    private readonly HashSet<string> _declared;
    private readonly Dictionary<string, string> _granted;
    private readonly HashSet<string> _denied;

    public PluginGrants(PluginManifest manifest, PluginRecord? record, bool builtin)
    {
        Manifest = manifest;
        Record = record;
        _declared = new HashSet<string>(manifest.Permissions.Requests.Select(r => r.Key), StringComparer.Ordinal);
        _denied = new HashSet<string>(record?.Denied ?? [], StringComparer.Ordinal);
        _granted = new Dictionary<string, string>(StringComparer.Ordinal);
        if (builtin)
        {
            foreach (var request in manifest.Permissions.Requests)
            {
                _granted[request.Key] = request.Details.FirstOrDefault() ?? "granted";
            }
        }
        foreach (var (key, value) in record?.Grants ?? [])
        {
            _granted[key] = value;
        }
    }

    public PluginManifest Manifest { get; }
    public PluginRecord? Record { get; }

    public bool IsDeclared(string key) => _declared.Contains(key);

    public bool Has(string key) => _declared.Contains(key) && _granted.ContainsKey(key) && !_denied.Contains(key);

    /// <summary>The granted level (<c>read</c>, <c>readwrite</c>, <c>granted</c>…); null when not held.</summary>
    public string? Level(string key) => Has(key) ? _granted[key] : null;

    /// <summary>Throws the error the plugin sees when it lacks <paramref name="key"/>.</summary>
    public void Require(string key)
    {
        if (!Has(key))
        {
            throw PluginApiException.Denied(key, canRequest: _declared.Contains(key) && !_denied.Contains(key));
        }
    }

    /// <summary>The details of a declared list permission (net hosts, runnable programs) when it is granted.</summary>
    public IReadOnlyList<string> Details(string key) =>
        Has(key) ? Manifest.Permissions.Requests.FirstOrDefault(r => r.Key == key)?.Details ?? [] : [];
}
