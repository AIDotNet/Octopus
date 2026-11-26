using Avalonia.Controls;
using Avalonia.Threading;

namespace Octopus.App.Bots.Groups;

public partial class BotGroupDialogView : UserControl
{
    public BotGroupDialogView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => NameInput.Focus(), DispatcherPriority.Input);
    }

    public BotGroupDialogView(BotGroupDialogViewModel model) : this()
    {
        DataContext = model;
    }
}
