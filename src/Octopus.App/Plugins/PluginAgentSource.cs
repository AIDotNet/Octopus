using Octopus.Agents;
using Octopus.Plugins.Catalog;

namespace Octopus.App.Plugins;

/// <summary>The agent definitions enabled plugins ship (<c>contributes.agents</c>), for the agent catalog.</summary>
public sealed class PluginAgentSource : IAgentDefinitionSource
{
    private readonly IPluginCatalog _catalog;

    public PluginAgentSource(IPluginCatalog catalog)
    {
        _catalog = catalog;
        catalog.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<AgentDefinitionFile> Files =>
    [
        .. _catalog.Packages
            .Where(p => p.Status == PluginStatus.Enabled && p.Manifest is { Contributes.Agents.Count: > 0 })
            // The manifest reader checked that every path stays inside the package.
            .SelectMany(p => p.Manifest!.Contributes.Agents.Select(file =>
                new AgentDefinitionFile(Path.Combine(p.Directory, file), p.Id, p.Manifest.NameFor(null)))),
    ];

    public event EventHandler? Changed;
}
