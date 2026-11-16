using System.Collections.Specialized;
using Octopus.Mobile.Localization;
using Octopus.Mobile.Shell;
using TKCode.Arc.Mobile;

namespace Octopus.Mobile.Hosts;

/// <summary>One computer's projects and sessions (design/ui/移动端布局方案.html, A ②), with the "New" sheet over it.</summary>
public partial class HostPage : Page, IClosablePage
{
    private readonly HostViewModel _viewModel;

    public HostPage(HostViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        // An action sheet takes its rows as controls, not from a list of data: rebuilt when the chosen agent changes.
        viewModel.NewSession.Models.CollectionChanged += OnModelsChanged;
        FillModels();
    }

    public void OnClosed()
    {
        _viewModel.NewSession.Models.CollectionChanged -= OnModelsChanged;
        _viewModel.Dispose();
    }

    private void OnModelsChanged(object? sender, NotifyCollectionChangedEventArgs e) => FillModels();

    private void FillModels()
    {
        var choice = _viewModel.NewSession;
        ModelSheet.Actions.Clear();
        ModelSheet.Actions.Add(new ActionSheetItem
        {
            Content = MobileLangResources.Get(MobileLangResourceKind.NewSessionModelDefault),
            Command = choice.SelectModelCommand,
            CommandParameter = null,
        });
        foreach (var model in choice.Models)
        {
            ModelSheet.Actions.Add(new ActionSheetItem { Content = model, Command = choice.SelectModelCommand, CommandParameter = model });
        }
    }
}
