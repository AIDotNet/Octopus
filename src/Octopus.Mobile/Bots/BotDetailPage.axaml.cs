using Octopus.Mobile.Shell;
using TKCode.Arc.Mobile;

namespace Octopus.Mobile.Bots;

public sealed partial class BotDetailPage : Page, IClosablePage
{
    public BotDetailPage(BotDetailViewModel model)
    {
        InitializeComponent();
        DataContext = model;
    }

    public void OnClosed() => (DataContext as IDisposable)?.Dispose();
}
