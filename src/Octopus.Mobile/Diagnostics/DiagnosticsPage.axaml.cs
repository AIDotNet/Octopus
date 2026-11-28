using Octopus.Mobile.Shell;
using Octopus.Mobile.Workspace;
using TKCode.Arc.Mobile;

namespace Octopus.Mobile.Diagnostics;

/// <summary>Page for managing diagnostics opt-in consent, inspecting sanitized logs, and exporting them to developers.</summary>
public partial class DiagnosticsPage : Page, IClosablePage
{
    private readonly DiagnosticsViewModel _viewModel;

    public DiagnosticsPage(IRemoteWorkspace workspace, IAccountSession account, INavigator navigator, Action<Uri>? openBrowser = null)
        : this(new DiagnosticsViewModel(workspace, account, navigator, openBrowser))
    {
    }

    public DiagnosticsPage(DiagnosticsViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        _viewModel.ToastRequested += OnToastRequested;
    }

    private void OnToastRequested(string message) => DiagToast.Show(message);

    public void OnClosed()
    {
        _viewModel.ToastRequested -= OnToastRequested;
        _viewModel.Dispose();
    }
}
