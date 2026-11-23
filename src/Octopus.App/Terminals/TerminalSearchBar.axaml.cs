using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Octopus.App.Localization;
using Octopus.Terminal.Avalonia;
using TKCode.Arc;

namespace Octopus.App.Terminals;

/// <summary>The small search bar at the top right of a terminal. Enter and Shift+Enter step through the matches, Esc closes.</summary>
public partial class TerminalSearchBar : UserControl
{
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(90);

    private TerminalControl? _terminal;
    private DispatcherTimer? _debounce;

    public TerminalSearchBar()
    {
        InitializeComponent();
        Field.TextChanged += (_, _) => ScheduleSearch(Field.Text ?? "");
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _debounce?.Stop();
    }

    private void ScheduleSearch(string query)
    {
        if (query.Length == 0)
        {
            _debounce?.Stop();
            _terminal?.SetSearchQuery("");
            return;
        }
        if (_debounce is null)
        {
            _debounce = new DispatcherTimer { Interval = SearchDebounce };
            _debounce.Tick += (_, _) =>
            {
                _debounce.Stop();
                _terminal?.SetSearchQuery(Field.Text ?? "");
            };
        }
        _debounce.Stop();
        _debounce.Start();
    }

    private void FlushSearch()
    {
        if (_debounce is not { IsEnabled: true }) return;
        _debounce.Stop();
        _terminal?.SetSearchQuery(Field.Text ?? "");
    }

    /// <summary>Connects the bar to the terminal it searches in.</summary>
    public void Attach(TerminalControl terminal)
    {
        _terminal = terminal;
        terminal.SearchChanged += (_, _) => Refresh();
        terminal.SearchFocusRequested += (_, _) =>
        {
            Field.Focus();
            Field.SelectAll();
            if (Field.Text is { Length: > 0 } text) terminal.SetSearchQuery(text);
        };
        Refresh();
    }

    private void Refresh()
    {
        if (_terminal is not { } terminal) return;
        if (!terminal.IsSearchOpen)
        {
            // Closed: the next opening starts empty.
            if (Field.Text is { Length: > 0 }) Field.Text = "";
            return;
        }
        var searching = terminal.SearchQuery.Length > 0;
        var found = terminal.SearchTotal > 0;
        Count.IsVisible = searching;
        Count.Tone = found ? BadgeTone.Neutral : BadgeTone.Warning;
        Count.Content = found
            ? AppLangResources.Format(AppLangResourceKind.TerminalSearchCount, terminal.SearchPosition ?? 0, terminal.SearchTotal)
            : AppLangResources.Get(AppLangResourceKind.TerminalSearchNoMatches);
        Previous.IsEnabled = Next.IsEnabled = found;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                _terminal?.CloseSearch();
                break;
            case Key.Enter:
                e.Handled = true;
                FlushSearch();
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) _terminal?.SearchPrevious();
                else _terminal?.SearchNext();
                break;
        }
    }

    private void OnPrevious(object? sender, RoutedEventArgs e)
    {
        FlushSearch();
        _terminal?.SearchPrevious();
    }

    private void OnNext(object? sender, RoutedEventArgs e)
    {
        FlushSearch();
        _terminal?.SearchNext();
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        _debounce?.Stop();
        _terminal?.CloseSearch();
    }
}
