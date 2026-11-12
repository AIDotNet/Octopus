using CommunityToolkit.Mvvm.ComponentModel;
using Octopus.Core.Agents;

namespace Octopus.App.Settings.Agents;

/// <summary>
/// One line of an agent's details: a label and a control. The kind of subclass decides the control (the view picks a
/// template by type); the agent's capability blocks decide which fields exist (see <see cref="AgentFieldFactory"/>).
/// </summary>
public abstract class AgentFieldViewModel : ObservableObject
{
    protected AgentFieldViewModel(AgentRowViewModel row, string key, string label, string? description = null)
    {
        Row = row;
        Key = key;
        Label = label;
        Description = description;
    }

    public AgentRowViewModel Row { get; }

    /// <summary><c>command</c>, <c>model</c>, <c>effort</c>, <c>fast</c>, <c>permission</c>, <c>extraArgs</c>, <c>env</c> or <c>feature:id</c>.</summary>
    public string Key { get; }

    public string Label { get; }

    public string? Description { get; }

    public bool HasDescription => !string.IsNullOrEmpty(Description);

    /// <summary>Writes the change into the settings (<see cref="AgentRowViewModel.Mutate"/>).</summary>
    protected void Apply(Action<AgentProfile> mutate) => Row.Mutate(mutate);

    /// <summary>Writes an edit that is still waiting for typing to pause.</summary>
    public virtual void Flush()
    {
    }

    /// <summary>The settings changed somewhere (the global permission, for one): refresh what depends on them.</summary>
    public virtual void OnSettingsChanged()
    {
    }
}
