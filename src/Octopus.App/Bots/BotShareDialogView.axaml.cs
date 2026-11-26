using Avalonia.Controls;

namespace Octopus.App.Bots;

public partial class BotShareDialogView : UserControl
{
    public BotShareDialogView()
    {
        InitializeComponent();
    }

    public BotShareDialogView(BotShareDialogViewModel model) : this()
    {
        DataContext = model;
    }
}
