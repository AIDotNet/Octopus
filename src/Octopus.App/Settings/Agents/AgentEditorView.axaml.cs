using Avalonia.Controls;

namespace Octopus.App.Settings.Agents;

/// <summary>The form of the agent editor dialog; everything it shows is <see cref="AgentEditorModel"/>.</summary>
public partial class AgentEditorView : UserControl
{
    public AgentEditorView()
    {
        InitializeComponent();
    }

    public AgentEditorView(AgentEditorModel model) : this()
    {
        DataContext = model;
        AttachedToVisualTree += (_, _) => NameInput.Focus();
    }
}
