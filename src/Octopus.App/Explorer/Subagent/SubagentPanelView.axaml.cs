using Avalonia.Controls;

namespace Octopus.App.Explorer.Subagent;

public partial class SubagentPanelView : UserControl
{
    private SubagentPanelViewModel? _model;

    public SubagentPanelView()
    {
        InitializeComponent();
        SubagentTimeline.LinkOpenRequested += OnLinkOpenRequested;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _model?.ScrollToBottomRequested -= OnScrollToBottom;
        _model = DataContext as SubagentPanelViewModel;
        _model?.ScrollToBottomRequested += OnScrollToBottom;
    }

    private void OnScrollToBottom(object? sender, bool force) => SubagentTimeline.QueueScrollToBottom(force);

    private void OnLinkOpenRequested(object? sender, string path) => _model?.RequestOpenFile(path);
}
