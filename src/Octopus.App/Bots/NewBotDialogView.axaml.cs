using Avalonia.Controls;
using Avalonia.Threading;

namespace Octopus.App.Bots;

public partial class NewBotDialogView : UserControl
{
    public NewBotDialogView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => NameInput.Focus(), DispatcherPriority.Input);
    }

    public NewBotDialogView(NewBotDialogViewModel model) : this()
    {
        DataContext = model;
    }
}
