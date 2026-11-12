using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Octopus.App.Localization;
using Octopus.Terminal.Avalonia;
using TKCode.Arc;

namespace Octopus.App.Terminals;

/// <summary>The small search bar at the top right of a terminal. Enter and Shift+Enter step through the matches, Esc closes.</summary>
public partial class TerminalSearchBar : UserControl
{
    private TerminalControl? _terminal;

    public TerminalSearchBar()
    {
        InitializeComponent();
        Field.TextChanged += (_, _) => _terminal?.SetSearchQuery(Field.Text ?? "");
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
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
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) _terminal?.SearchPrevious();
                else _terminal?.SearchNext();
                break;
        }
    }

    private void OnPrevious(object? sender, RoutedEventArgs e) => _terminal?.SearchPrevious();

    private void OnNext(object? sender, RoutedEventArgs e) => _terminal?.SearchNext();

    private void OnClose(object? sender, RoutedEventArgs e) => _terminal?.CloseSearch();
}
