using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Octopus.Mobile.Localization;
using Octopus.Mobile.Shell;
using Octopus.Mobile.Voice;
using Octopus.Mobile.Workspace;
using TKCode.Arc.Mobile;

namespace Octopus.Mobile.Conversations;

/// <summary>One GUI conversation on a host (design/ui/移动端功能扩展方案.html, G-A).</summary>
public partial class ConversationPage : Page, IClosablePage
{
    private const double BottomPinTolerance = 36;
    /// <summary>How far above the bar the finger must go to be over "cancel" or "send" (the targets sit about this high).</summary>
    private const double TargetRise = 72;

    private readonly ConversationViewModel _viewModel;
    private bool _pinnedToBottom = true;
    private bool _scrollQueued;
    private Point _holdOrigin;
    private bool _holding;

    public ConversationPage(ConversationViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.SetPickers(
            () => MobileHost.PickImage(Avalonia.Controls.TopLevel.GetTopLevel(this)),
            () => MobileHost.PickFile(Avalonia.Controls.TopLevel.GetTopLevel(this)));
        viewModel.OptionPickerRequested += OnOptionPickerRequested;
        viewModel.AttachPickerRequested += OnAttachPickerRequested;
        viewModel.TimelineChanged += OnTimelineChanged;
        viewModel.ScrollToBottomRequested += ScrollToBottomOnEntry;
        TimelineScroll.ScrollChanged += OnTimelineScrollChanged;
        PromptField.AddHandler(KeyDownEvent, OnPromptKeyDown, RoutingStrategies.Tunnel);
        PromptField.GotFocus += OnPromptGotFocus;
        HoldToTalkBar.PointerPressed += OnHoldPressed;
        HoldToTalkBar.PointerMoved += OnHoldMoved;
        HoldToTalkBar.PointerReleased += OnHoldReleased;
        HoldToTalkBar.PointerCaptureLost += OnHoldCaptureLost;
        if (viewModel.IsLoaded) ScrollToBottomOnEntry();
    }

    /// <summary>True while the model/mode/effort picker sheet is open, for tests.</summary>
    public bool IsOptionSheetOpen => OptionSheet.IsOpen;

    /// <summary>The option labels last shown in the picker sheet, for tests.</summary>
    public IReadOnlyList<string> OptionSheetLabels => [.. OptionSheet.Actions.Select(action => action.Content?.ToString() ?? "")];

    public void OnClosed()
    {
        _viewModel.OptionPickerRequested -= OnOptionPickerRequested;
        _viewModel.AttachPickerRequested -= OnAttachPickerRequested;
        _viewModel.TimelineChanged -= OnTimelineChanged;
        _viewModel.ScrollToBottomRequested -= ScrollToBottomOnEntry;
        TimelineScroll.ScrollChanged -= OnTimelineScrollChanged;
        PromptField.RemoveHandler(KeyDownEvent, OnPromptKeyDown);
        PromptField.GotFocus -= OnPromptGotFocus;
        HoldToTalkBar.PointerPressed -= OnHoldPressed;
        HoldToTalkBar.PointerMoved -= OnHoldMoved;
        HoldToTalkBar.PointerReleased -= OnHoldReleased;
        HoldToTalkBar.PointerCaptureLost -= OnHoldCaptureLost;
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

    private void OnAttachPickerRequested()
    {
        OptionSheet.Title = MobileLangResources.Get(MobileLangResourceKind.ConversationAttach);
        OptionSheet.Actions.Clear();
        OptionSheet.Actions.Add(new ActionSheetItem
        {
            Content = MobileLangResources.Get(MobileLangResourceKind.ConversationAttachImage),
            Command = _viewModel.AttachImageCommand,
        });
        OptionSheet.Actions.Add(new ActionSheetItem
        {
            Content = MobileLangResources.Get(MobileLangResourceKind.ConversationAttachFile),
            Command = _viewModel.AttachFileCommand,
        });
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

    private void PostScrollToBottom()
    {
        if (_scrollQueued) return;
        _scrollQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollQueued = false;
            if (_pinnedToBottom) TimelineScroll.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    // Hold to talk: the bar captures the finger, so it keeps getting moves while the overlay is drawn over everything.
    private void OnHoldPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_holding || !e.GetCurrentPoint(HoldToTalkBar).Properties.IsLeftButtonPressed) return;
        _holding = true;
        _holdOrigin = e.GetPosition(this);
        e.Pointer.Capture(HoldToTalkBar);
        e.Handled = true;
        _viewModel.Voice.Press();
    }

    private void OnHoldMoved(object? sender, PointerEventArgs e)
    {
        if (!_holding) return;
        var at = e.GetPosition(this);
        _viewModel.Voice.Move(ZoneAt(at, _holdOrigin, Bounds.Width));
    }

    private void OnHoldReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_holding) return;
        _holding = false;
        e.Pointer.Capture(null);
        e.Handled = true;
        _viewModel.Voice.Release();
    }

    // The system took the finger away (a call, a gesture of the OS): nothing half-heard goes anywhere.
    private void OnHoldCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_holding) return;
        _holding = false;
        _viewModel.Voice.Abort();
    }

    /// <summary>Over the bar: fill the field. Dragged up: "cancel" on the left half, "send" on the right.</summary>
    internal static VoiceZone ZoneAt(Point at, Point origin, double width) =>
        origin.Y - at.Y < TargetRise ? VoiceZone.Fill
        : at.X < width / 2 ? VoiceZone.Cancel
        : VoiceZone.Send;

    private void OnPromptGotFocus(object? sender, FocusChangedEventArgs e) => _viewModel.CollapseTaskList();

    private void OnPromptKeyDown(object? sender, KeyEventArgs e)
    {
        // Enter inserts a newline for multi-line prompts; Ctrl+Enter / Cmd+Enter sends on hardware keyboards.
        if (e.Key != Key.Enter) return;
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Meta)) return;
        if (_viewModel.CanSend)
        {
            _viewModel.SendCommand.Execute(null);
            e.Handled = true;
        }
    }
}
