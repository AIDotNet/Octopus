using Octopus.Mobile.Shell;
using TKCode.Arc.Mobile;

namespace Octopus.Mobile.Voice;

/// <summary>Me › Voice input (design/ui/移动端输入框重写与语音输入方案.html).</summary>
public partial class VoiceSettingsPage : Page, IClosablePage
{
    private readonly VoiceSettingsViewModel _viewModel;

    public VoiceSettingsPage(VoiceSettingsViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>Left by the system's back gesture as well as the button: keep what was typed.</summary>
    public void OnClosed() => _viewModel.Flush();
}
