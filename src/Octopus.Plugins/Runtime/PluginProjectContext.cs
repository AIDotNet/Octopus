namespace Octopus.Plugins.Runtime;

public sealed record PluginProjectInfo(string Id, string Name, string Path, string? Color);

/// <summary>The projects as plugins see them; implemented by the app (the plugin layer does not know its project service).</summary>
public interface IPluginProjectContext
{
    PluginProjectInfo? Current { get; }
    IReadOnlyList<PluginProjectInfo> All { get; }
    event EventHandler? CurrentChanged;
}

/// <summary>No projects: for tests and for running plugins without the app.</summary>
public sealed class NoProjects : IPluginProjectContext
{
    public PluginProjectInfo? Current => null;
    public IReadOnlyList<PluginProjectInfo> All => [];
    public event EventHandler? CurrentChanged { add { } remove { } }
}
