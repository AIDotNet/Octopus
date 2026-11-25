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
        viewModel.ScrollToBottomRequested += ScrollToBottomOnEntry;
        TimelineScroll.ScrollChanged += OnTimelineScrollChanged;
        PromptField.AddHandler(KeyDownEvent, OnPromptKeyDown, RoutingStrategies.Tunnel);
        if (viewModel.IsLoaded) ScrollToBottomOnEntry();
    }

    /// <summary>True while the model/mode/effort picker sheet is open, for tests.</summary>
    public bool IsOptionSheetOpen => OptionSheet.IsOpen;

    /// <summary>The option labels last shown in the picker sheet, for tests.</summary>
    public IReadOnlyList<string> OptionSheetLabels => [.. OptionSheet.Actions.Select(action => action.Content?.ToString() ?? "")];

    public void OnClosed()
    {
        _viewModel.OptionPickerRequested -= OnOptionPickerRequested;
        _viewModel.TimelineChanged -= OnTimelineChanged;
        _viewModel.ScrollToBottomRequested -= ScrollToBottomOnEntry;
        TimelineScroll.ScrollChanged -= OnTimelineScrollChanged;
        PromptField.RemoveHandler(KeyDownEvent, OnPromptKeyDown);
        _viewModel.Dispose();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ScrollToBottomOnEntry();
    }

    /// <summary>Pins and scrolls the conversation timeline to the bottom whenever the user enters this page.</summary>
    internal void ScrollToBottomOnEntry()
    {
        _pinnedToBottom = true;
        PostScrollToBottom();
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
        var extentY = TimelineScroll.Extent.Height;
        var viewportY = TimelineScroll.Viewport.Height;
        if (extentY <= viewportY + 1)
        {
            _pinnedToBottom = true;
            return;
        }

        var distanceFromBottom = extentY - viewportY - TimelineScroll.Offset.Y;
        // When Viewport grows (e.g. multi-line composer clears on send or keyboard hides) or Extent shrinks,
        // Avalonia's CoerceOffset can clamp Offset.Y down before the new Extent is applied.
        var maxLayoutClampDrop = Math.Max(0.0, e.ViewportDelta.Y) + Math.Max(0.0, -e.ExtentDelta.Y);

        if (e.OffsetDelta.Y < -(maxLayoutClampDrop + 1.0) && distanceFromBottom > 1.0)
        {
            _pinnedToBottom = false;
            return;
        }

        if (!_pinnedToBottom && e.OffsetDelta.Y > 0.5 && distanceFromBottom <= BottomPinTolerance)
        {
            _pinnedToBottom = true;
            return;
        }

        if (Math.Abs(e.ExtentDelta.Y) > 0.5 || Math.Abs(e.ViewportDelta.Y) > 0.5)
        {
            if (_pinnedToBottom && distanceFromBottom > 0.5) TimelineScroll.ScrollToEnd();
            return;
        }

        if (Math.Abs(e.OffsetDelta.Y) > 0.5)
        {
            _pinnedToBottom = distanceFromBottom <= BottomPinTolerance;
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
