using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Octopus.Mobile.Shell;
using TKCode.Arc.Mobile;
using TKCode.Terminal.Core;

namespace Octopus.Mobile.Terminals;

/// <summary>
/// One session's terminal (design/ui/移动端布局方案.html, A ③ and "终端页"). The terminal is the same control the
/// desktop uses; what feeds it is the session on the host.
/// </summary>
/// <remarks>
/// While only looking the terminal keeps the host's size (it is a mirror, design/12-远程操作.md §4.4). Not done yet:
/// panning and zooming a terminal that is wider than the phone; what does not fit is cut off at the right.
/// </remarks>
public partial class TerminalPage : Page, ITerminalSurface, IClosablePage
{
    /// <summary>How often the screen is read for an agent's choices while the page is showing.</summary>
    private static readonly TimeSpan ReplyRefresh = TimeSpan.FromMilliseconds(700);

    private readonly TerminalViewModel _viewModel;
    private readonly DispatcherTimer _replies;

    public TerminalPage(TerminalViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        _replies = new DispatcherTimer { Interval = ReplyRefresh };
        _replies.Tick += (_, _) => viewModel.RefreshQuickReplies();
        // Before the terminal sees it: text typed while Ctrl is armed becomes a control character.
        AddHandler(TextInputEvent, OnTextTyped, RoutingStrategies.Tunnel);
        Terminal.IsMirror = viewModel.IsMirror;
        Terminal.Start(viewModel.Spec, viewModel.Factory);
        viewModel.Attach(this);
    }

    public void SendText(string text) => Terminal.SendText(text);

    public void SendKey(TerminalKey key, KeyMods mods) => Terminal.SendKey(key, mods);

    public string ReadScreenText() => Terminal.ReadScreenText();

    // What fits this phone, not what the terminal has: while only looking the two differ.
    public (int Columns, int Rows)? GridSize => Terminal.ViewGridSize ?? Terminal.GridSize;

    public void FocusTerminal() => Terminal.FocusTerminal();

    /// <summary>The page left the stack: the connection to the session ends (the program on the host keeps running).</summary>
    public void OnClosed()
    {
        _replies.Stop();
        _viewModel.Dispose();
        _ = Terminal.CloseAsync();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _replies.Start();
    }

    // A page that another one covers is taken out of the tree: nothing to read until it is back.
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _replies.Stop();
    }

    private void OnTextTyped(object? sender, TextInputEventArgs e)
    {
        if (!_viewModel.IsDirect || e.Text is not { Length: > 0 } text) return;
        if (_viewModel.TryTakeTyped(text)) e.Handled = true;
    }
}
