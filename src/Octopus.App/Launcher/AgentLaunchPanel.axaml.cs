using Avalonia.Controls;
using Avalonia.Input;
using TKCode.Arc;

namespace Octopus.App.Launcher;

public partial class AgentLaunchPanel : UserControl
{
    private AgentLaunchViewModel? _model;
    private bool _syncing;

    public AgentLaunchPanel()
    {
        InitializeComponent();
        // Tunnel: the text box handles Enter itself (a new line) before it bubbles up.
        PromptBox.AddHandler(KeyDownEvent, OnPromptKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        ModelSearch.AddHandler(KeyDownEvent, OnModelSearchKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null)
        {
            _model.Efforts.CollectionChanged -= OnEffortsChanged;
            _model.PropertyChanged -= OnModelPropertyChanged;
        }
        _model = DataContext as AgentLaunchViewModel;
        if (_model is not null)
        {
            _model.Efforts.CollectionChanged += OnEffortsChanged;
            _model.PropertyChanged += OnModelPropertyChanged;
        }
        BuildEfforts();
    }

    private void OnEffortsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => BuildEfforts();

    private void OnModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AgentLaunchViewModel.SelectedEffortId) && EffortHost.Content is SegmentedControl segmented && !_syncing)
        {
            _syncing = true;
            segmented.SelectedValue = _model!.SelectedEffortId;
            _syncing = false;
        }
    }

    /// <summary>The effort control is rebuilt when the model changes: each model offers its own levels.</summary>
    private void BuildEfforts()
    {
        if (_model is null || !_model.HasEffort)
        {
            EffortHost.Content = null;
            return;
        }
        var segmented = new SegmentedControl { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        segmented.SetValue(Avalonia.Automation.AutomationProperties.NameProperty, _model.EffortCaption);
        foreach (var choice in _model.Efforts)
        {
            segmented.Items.Add(new SegmentedOption { Value = choice.Id, Label = choice.Label });
        }
        _syncing = true;
        segmented.SelectedValue = _model.SelectedEffortId;
        _syncing = false;
        segmented.SelectionChanged += (_, _) =>
        {
            if (!_syncing && _model is not null && segmented.SelectedValue is string id)
            {
                _syncing = true;
                _model.SelectedEffortId = id;
                _syncing = false;
            }
        };
        EffortHost.Content = segmented;
    }

    /// <summary>Enter in the model search picks the first match and starts the agent.</summary>
    private void OnModelSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _model is not null)
        {
            e.Handled = true;
            _model.CommitModelQuery();
        }
    }

    /// <summary>Enter starts the agent, Shift+Enter adds a line.</summary>
    private void OnPromptKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _model is not null)
        {
            e.Handled = true;
            _model.LaunchCommand.Execute(null);
        }
    }
}
