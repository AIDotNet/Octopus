using Avalonia.Controls;

namespace Octopus.App.Bots;

public partial class BotAvatarPickerView : UserControl
{
    public BotAvatarPickerView()
    {
        InitializeComponent();
    }

    public BotAvatarPickerView(BotAvatarPickerViewModel model) : this()
    {
        DataContext = model;
    }
}
