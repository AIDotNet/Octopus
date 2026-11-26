using Octopus.Mobile.Shell;
using TKCode.Arc.Mobile;

namespace Octopus.Mobile.Bots;

public sealed partial class BotGroupDetailPage : Page, IClosablePage
{
    public BotGroupDetailPage(BotGroupDetailViewModel model)
    {
        InitializeComponent();
        DataContext = model;
    }

    public void OnClosed() => (DataContext as IDisposable)?.Dispose();
}
