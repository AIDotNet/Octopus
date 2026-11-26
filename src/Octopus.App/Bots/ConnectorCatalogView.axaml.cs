using Avalonia.Controls;

namespace Octopus.App.Bots;

public partial class ConnectorCatalogView : UserControl
{
    public ConnectorCatalogView()
    {
        InitializeComponent();
    }

    public ConnectorCatalogView(ConnectorCatalogViewModel model) : this()
    {
        DataContext = model;
    }
}
