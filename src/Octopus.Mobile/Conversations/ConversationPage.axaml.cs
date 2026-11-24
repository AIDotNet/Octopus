using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Octopus.Mobile.Shell;
using Octopus.Mobile.Workspace;
using TKCode.Arc.Mobile;

namespace Octopus.Mobile.Conversations;

/// <summary>One GUI conversation on a host (design/ui/移动端功能扩展方案.html, G-A).</summary>
public partial class ConversationPage : Page, IClosablePage
{
    private const double BottomPinTolerance = 36;

    private readonly ConversationViewModel _viewModel;
    private bool _pinnedToBottom = true;

    public ConversationPage(ConversationViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.OptionPickerRequested += OnOptionPickerRequested;
        viewModel.TimelineChanged += OnTimelineChanged;
        TimelineScroll.ScrollChanged += OnTimelineScrollChanged;
        PromptField.AddHandler(KeyDownEvent, OnPromptKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>True while the model/mode/effort picker sheet is open, for tests.</summary>
    public bool IsOptionSheetOpen => OptionSheet.IsOpen;

    /// <summary>The option labels last shown in the picker sheet, for tests.</summary>
    public IReadOnlyList<string> OptionSheetLabels => [.. OptionSheet.Actions.Select(action => action.Content?.ToString() ?? "")];

    public void OnClosed()
    {
        _viewModel.OptionPickerRequested -= OnOptionPickerRequested;
        _viewModel.TimelineChanged -= OnTimelineChanged;
        TimelineScroll.ScrollChanged -= OnTimelineScrollChanged;
        PromptField.RemoveHandler(KeyDownEvent, OnPromptKeyDown);
        _viewModel.Dispose();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_pinnedToBottom) PostScrollToBottom();
    }

    // ActionSheets take their rows as controls, not from an ItemsSource.
    private void OnOptionPickerRequested(ConversationOptionKind kind, string title, IReadOnlyList<ConversationOption> options, string? current)
    {
        OptionSheet.Title = title;
        OptionSheet.Actions.Clear();
        foreach (var option in options)
        {
            var label = option.Id == current ? "✓ " + option.Label : option.Label;
            OptionSheet.Actions.Add(new ActionSheetItem
            {
                Content = label,
                Command = _viewModel.SelectOptionCommand,
                CommandParameter = new ConversationOptionChoice(kind, option.Id),
            });
        }
        OptionSheet.IsOpen = true;
    }

    private void OnTimelineChanged()
    {
        if (_pinnedToBottom) PostScrollToBottom();
    }

    private void OnTimelineScrollChanged(object? sender, Avalonia.Controls.ScrollChangedEventArgs e)
    {
        if (Math.Abs(e.ExtentDelta.Y) > 0.5)
        {
            if (_pinnedToBottom) TimelineScroll.ScrollToEnd();
            return;
        }
        if (Math.Abs(e.OffsetDelta.Y) > 0.5)
        {
            var max = TimelineScroll.ScrollBarMaximum.Y;
            _pinnedToBottom = max <= 1 || TimelineScroll.Offset.Y >= max - BottomPinTolerance;
        }
    }

    private void PostScrollToBottom() => Dispatcher.UIThread.Post(() =>
    {
        if (_pinnedToBottom) TimelineScroll.ScrollToEnd();
    }, DispatcherPriority.Background);

    private void OnPromptKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None) return;
        if (_viewModel.CanSend)
        {
            _viewModel.SendCommand.Execute(null);
            e.Handled = true;
        }
    }
}
