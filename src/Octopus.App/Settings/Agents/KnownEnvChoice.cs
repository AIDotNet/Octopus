using CommunityToolkit.Mvvm.Input;
using Octopus.Core.Agents;

namespace Octopus.App.Settings.Agents;

/// <summary>A variable of the definition's <c>known</c> list, offered by the "Add common variable" menu.</summary>
public sealed class KnownEnvChoice
{
    public KnownEnvChoice(KnownEnvVar variable, Action<KnownEnvVar> add)
    {
        Variable = variable;
        AddCommand = new RelayCommand(() => add(variable));
    }

    public KnownEnvVar Variable { get; }

    public string Title => Variable.TitleFor(AgentLanguage.Current);

    /// <summary>The variable name, plus its description when it has one.</summary>
    public string Detail => Variable.DescriptionFor(AgentLanguage.Current) is { Length: > 0 } description ? $"{Variable.Name} · {description}" : Variable.Name;

    public RelayCommand AddCommand { get; }
}
