using Avalonia.Controls;

namespace Octopus.App.Bots;

public partial class BotImportDialogView : UserControl
{
    public BotImportDialogView()
    {
        InitializeComponent();
    }

    public BotImportDialogView(BotImportDialogViewModel model) : this()
    {
        DataContext = model;
    }
}
