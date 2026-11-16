using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Cursor = Avalonia.Input.Cursor;
using StandardCursorType = Avalonia.Input.StandardCursorType;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Octopus.Core.Terminals;
using TKCode.Terminal.Avalonia;
using TKCode.Terminal.Core;

namespace Octopus.Terminal.Avalonia;

/// <summary>
/// A real terminal: give it a <see cref="LaunchSpec"/> and a connection factory with <see cref="Start"/>. Put it in a
/// layout that stretches (a star-sized grid cell, a dock fill), not in an auto-sized panel.
/// </summary>
public sealed class TerminalControl : Decorator
{
    private static readonly FontFamily DefaultFont = new("Menlo, Cascadia Mono, Consolas, DejaVu Sans Mono, monospace");

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        AvaloniaProperty.Register<TerminalControl, FontFamily>(nameof(FontFamily), DefaultFont);

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<TerminalControl, double>(nameof(FontSize), 13);

    public static readonly StyledProperty<int> ScrollbackLinesProperty =
        AvaloniaProperty.Register<TerminalControl, int>(nameof(ScrollbackLines), 10_000);

    public static readonly StyledProperty<ITerminalKeymap> KeymapProperty =
        AvaloniaProperty.Register<TerminalControl, ITerminalKeymap>(nameof(Keymap), TerminalKeymaps.ForCurrentPlatform());

    public static readonly StyledProperty<string> ExitedMessageFormatProperty =
        AvaloniaProperty.Register<TerminalControl, string>(nameof(ExitedMessageFormat), "Process exited ({0}). Press Enter to restart.");

    public static readonly StyledProperty<bool> ConfirmMultilinePasteProperty =
        AvaloniaProperty.Register<TerminalControl, bool>(nameof(ConfirmMultilinePaste), true);

    public static readonly StyledProperty<Control?> SearchBarProperty =
        AvaloniaProperty.Register<TerminalControl, Control?>(nameof(SearchBar));

    public static readonly DirectProperty<TerminalControl, string?> TitleProperty =
        AvaloniaProperty.RegisterDirect<TerminalControl, string?>(nameof(Title), o => o.Title);

    public static readonly DirectProperty<TerminalControl, bool> IsRunningProperty =
        AvaloniaProperty.RegisterDirect<TerminalControl, bool>(nameof(IsRunning), o => o.IsRunning);

    public static readonly DirectProperty<TerminalControl, int?> ExitCodeProperty =
        AvaloniaProperty.RegisterDirect<TerminalControl, int?>(nameof(ExitCode), o => o.ExitCode);

    private readonly TerminalView _view = new() { Padding = 8, ClipToBounds = true };
    private readonly ScrollBar _scrollBar = new()
    {
        Orientation = Orientation.Vertical,
        SmallChange = 3,
        IsVisible = false,
        Focusable = false,
    };
    private bool _updatingScrollBar;
    private readonly SelectionOverlay _overlay;
    private readonly Border _background = new();
    private readonly Border _messageBar;
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };

    private LaunchSpec? _spec;
    private ITerminalConnectionFactory? _factory;
    private TerminalSession? _session;
    // With a registry the session belongs to it (under _key) and outlives this control; without one it is the control's own.
    private TerminalSessionRegistry? _registry;
    private string? _key;
    private TerminalInputRouter? _router;
    private TopLevel? _topLevel;
    private readonly Border _searchHost = new()
    {
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(0, 10, 14, 0),
        IsVisible = false,
    };
    private FrameSnapshot? _frame;
    private LinkHit? _hoverLink;
    private ViewportPoint _hoverCell;
    private string? _pendingLink;
    private bool _searchOpen;
    private string _searchQuery = string.Empty;
    private long _searchTotal;
    private long? _searchPosition;
    private bool _dark = true;
    private bool _startFailed;
    private string? _title;
    private bool _isRunning;
    private int? _exitCode;

    public TerminalControl()
    {
        _overlay = new SelectionOverlay(_view);
        _messageBar = new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Padding = new Thickness(12, 6),
            IsVisible = false,
            IsHitTestVisible = false,
            Child = _message,
        };
        var surface = new Grid { Children = { _view, _overlay, _messageBar, _searchHost } };
        Grid.SetColumn(_scrollBar, 1);
        Grid.SetColumnSpan(_background, 2);
        Child = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,12"),
            Children = { _background, surface, _scrollBar },
        };
        _scrollBar.ValueChanged += (_, e) =>
        {
            if (!_updatingScrollBar) _session?.Use(c => c.ScrollViewport(ViewportScroll.ToRow((long)Math.Round(e.NewValue))));
        };
        ClipToBounds = true;
        ApplyColors();

        _view.KeyPressed += OnKeyPressed;
        _view.TextEntered += (_, text) => { if (IsRunning) _router?.HandleText(text); };
        _view.PointerChanged += OnPointer;
        _view.SizeChanged += (_, _) => RecomputeGrid();
        _view.GotFocus += (_, _) => _router?.HandleFocus(true);
        _view.LostFocus += (_, _) => _router?.HandleFocus(false);
        ActualThemeVariantChanged += (_, _) => ApplyTheme();
    }

    /// <summary>Terminal font. Changing it recomputes the grid.</summary>
    public FontFamily FontFamily { get => GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }

    /// <summary>Terminal font size in DIP. Changing it recomputes the grid.</summary>
    public double FontSize { get => GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }

    /// <summary>Scrollback limit in lines. Read when the process starts.</summary>
    public int ScrollbackLines { get => GetValue(ScrollbackLinesProperty); set => SetValue(ScrollbackLinesProperty, value); }

    /// <summary>Application shortcuts (copy, paste, clear, select all).</summary>
    public ITerminalKeymap Keymap { get => GetValue(KeymapProperty); set => SetValue(KeymapProperty, value); }

    /// <summary>Text shown after the process exited. <c>{0}</c> is the exit code. Meant to be localised.</summary>
    public string ExitedMessageFormat { get => GetValue(ExitedMessageFormatProperty); set => SetValue(ExitedMessageFormatProperty, value); }

    /// <summary>Ask before pasting text with more than one line (not needed when the program uses bracketed paste).</summary>
    public bool ConfirmMultilinePaste { get => GetValue(ConfirmMultilinePasteProperty); set => SetValue(ConfirmMultilinePasteProperty, value); }

    /// <summary>
    /// Shows the confirmation for a multi-line paste and returns true to paste. Without one the text is pasted
    /// as is. Arguments: the text and its preview.
    /// </summary>
    public Func<string, PastePreview, Task<bool>>? PasteConfirmer { get; set; }

    /// <summary>The search bar shown at the top right while searching. Its content is up to the host.</summary>
    public Control? SearchBar { get => GetValue(SearchBarProperty); set => SetValue(SearchBarProperty, value); }

    /// <summary>True while the search bar is open.</summary>
    public bool IsSearchOpen => _searchOpen;

    /// <summary>The text being searched for.</summary>
    public string SearchQuery => _searchQuery;

    /// <summary>Matches in the screen and the scrollback.</summary>
    public long SearchTotal => _searchTotal;

    /// <summary>Position of the current match, 1-based in reading order (oldest first); null when none is current.</summary>
    public long? SearchPosition => _searchPosition;

    /// <summary>The search bar opened (or was asked for again), closed, or its result changed.</summary>
    public event EventHandler? SearchChanged;

    /// <summary>The search bar was asked for: it should take the keyboard focus and select its text.</summary>
    public event EventHandler? SearchFocusRequested;

    /// <summary>A link was followed with the link modifier held. The argument is an http or https URL.</summary>
    public event EventHandler<string>? LinkActivated;

    /// <summary>
    /// A file path was followed with the link modifier held: the full path of an existing file or folder, and the line and
    /// column printed after it (<c>src/a.cs:12:5</c>) when there were some.
    /// </summary>
    public event EventHandler<TerminalPathActivatedEventArgs>? PathActivated;

    /// <summary>
    /// Whether file paths in the output are links. Relative paths are read against the folder the program started in.
    /// Off for a terminal whose files are not on this computer.
    /// </summary>
    public bool PathLinksEnabled { get; set; } = true;

    /// <summary>
    /// The terminal shows a session that another computer runs (see <see cref="TerminalSession.IsMirror"/>). Read when
    /// the control starts a session of its own; a session taken from a registry keeps what it is.
    /// </summary>
    public bool IsMirror { get; set; }

    /// <summary>The window title the program set; null before it set one.</summary>
    public string? Title { get => _title; private set => SetAndRaise(TitleProperty, ref _title, value); }

    /// <summary>True while the process runs.</summary>
    public bool IsRunning { get => _isRunning; private set => SetAndRaise(IsRunningProperty, ref _isRunning, value); }

    /// <summary>Exit code of the last process (128 + signal when it was killed by a signal); null while running.</summary>
    public int? ExitCode { get => _exitCode; private set => SetAndRaise(ExitCodeProperty, ref _exitCode, value); }

    /// <summary>The title changed. The argument is the new title.</summary>
    public event EventHandler<string>? TitleChanged;

    /// <summary>The process exited. The argument is the exit code.</summary>
    public event EventHandler<int?>? Exited;

    /// <summary>The program rang the bell.</summary>
    public event EventHandler? Bell;

    /// <summary>
    /// Runs <paramref name="spec"/>. Safe to call before the control has been laid out: the process starts once the
    /// control has a size, because a pty needs its initial rows and columns. A start failure is shown in the control.
    /// </summary>
    public void Start(LaunchSpec spec, ITerminalConnectionFactory factory) => Start(spec, factory, null, null);

    /// <summary>
    /// Like <see cref="Start(LaunchSpec, ITerminalConnectionFactory)"/>, with the session kept in
    /// <paramref name="registry"/> under <paramref name="key"/>. A session that already runs there (started for a view
    /// on another device before this control existed) is shown as it is, and <paramref name="spec"/> is not started a
    /// second time. Calling this again on a control that shows a session replaces that session with a new run.
    /// </summary>
    public void Start(LaunchSpec spec, ITerminalConnectionFactory factory, TerminalSessionRegistry? registry, string? key)
    {
        var old = _session;
        var (oldRegistry, oldKey) = (_registry, _key);
        DetachSession();
        if (old is not null)
        {
            _ = oldRegistry is not null && oldKey is not null ? oldRegistry.CloseAsync(oldKey, old) : old.DisposeAsync().AsTask();
        }
        (_registry, _key) = registry is not null && key is not null ? (registry, key) : (null, null);
        _spec = spec;
        _factory = factory;
        _startFailed = false;
        Title = null;
        ExitCode = null;
        IsRunning = false;
        HideMessage();
        TryStartSession();
    }

    /// <summary>Restarts the same command (after it exited, or replacing the running process).</summary>
    public async Task RestartAsync()
    {
        if (_spec is null || _factory is null) return;
        if (_session is null)
        {
            _startFailed = false;
            HideMessage();
            TryStartSession();
            return;
        }
        HideMessage();
        ExitCode = null;
        try
        {
            await _session.RestartAsync();
            IsRunning = _session.IsRunning;
        }
        catch (Exception ex)
        {
            ShowMessage(ex.Message);
            IsRunning = false;
        }
    }

    /// <summary>Ends the process (hangup, then kill after two seconds) and frees the terminal.</summary>
    public async Task CloseAsync()
    {
        var session = _session;
        var (registry, key) = (_registry, _key);
        DetachSession();
        _spec = null;
        _factory = null;
        (_registry, _key) = (null, null);
        IsRunning = false;
        if (session is null) return;
        if (registry is not null && key is not null) await registry.CloseAsync(key, session);
        else await session.DisposeAsync();
    }

    /// <summary>Opens the search bar (or asks the open one to take focus again).</summary>
    public void OpenSearch()
    {
        if (SearchBar is null || _session is null) return;
        _searchOpen = true;
        _searchHost.IsVisible = true;
        SearchChanged?.Invoke(this, EventArgs.Empty);
        SearchFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Closes the search bar, removes the highlights and gives the focus back to the terminal.</summary>
    public void CloseSearch()
    {
        if (!_searchOpen) return;
        _searchOpen = false;
        _searchHost.IsVisible = false;
        _searchQuery = string.Empty;
        (_searchTotal, _searchPosition) = (0, null);
        _session?.SetSearch(string.Empty);
        SearchChanged?.Invoke(this, EventArgs.Empty);
        FocusTerminal();
    }

    /// <summary>Searches screen and scrollback for <paramref name="query"/>; the first match is made current and scrolled into view.</summary>
    public void SetSearchQuery(string query)
    {
        _searchQuery = query;
        _session?.SetSearch(query);
        if (query.Length == 0) (_searchTotal, _searchPosition) = (0, null);
        SearchChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Makes the next match (further down, wrapping) the current one.</summary>
    public void SearchNext() => _session?.SearchForward();

    /// <summary>Makes the previous match (further up, wrapping) the current one.</summary>
    public void SearchPrevious() => _session?.SearchBackward();

    /// <summary>Gives the keyboard focus to the terminal.</summary>
    public void FocusTerminal() => _view.Focus();

    /// <summary>True when the foreground process in the terminal is not the shell itself (ask before closing).</summary>
    public Task<bool> HasRunningChildAsync() => _session?.HasRunningChildAsync() ?? Task.FromResult(false);

    /// <summary>Sends text to the process as if it had been typed (no paste wrapping).</summary>
    public void SendText(string text) => _session?.Send(text);

    /// <summary>Sends a key as if it had been pressed (a key of a touch key bar), encoded for the terminal's current modes.</summary>
    public void SendKey(TerminalKey key, KeyMods mods = KeyMods.None) => _router?.SendKey(key, mods);

    /// <summary>Screen and scrollback as plain text. Empty when nothing runs.</summary>
    public string ReadScreenText() => _session?.ReadText() ?? string.Empty;

    /// <summary>
    /// The grid this view holds at its present size, whatever grid the terminal has: for a mirror the two differ while
    /// another view decides the size. Null before the control is laid out.
    /// </summary>
    public (int Columns, int Rows)? ViewGridSize
    {
        get
        {
            if (_view.Bounds is not { Width: > 0, Height: > 0 } bounds) return null;
            var grid = _view.GridSizeFor(bounds.Size);
            return grid.Columns < 2 ? null : (grid.Columns, grid.Rows);
        }
    }

    /// <summary>Current grid size, or null before the process started.</summary>
    public (int Columns, int Rows)? GridSize =>
        _session?.Read(c => ((int, int)?)(c.Size.Columns, c.Size.Rows));

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SearchBarProperty) _searchHost.Child = SearchBar;
        if (change.Property == FontFamilyProperty)
        {
            _view.FontFamily = FontFamily;
            RefitGrid();
        }
        else if (change.Property == FontSizeProperty)
        {
            _view.FontSize = FontSize;
            RefitGrid();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null) _topLevel.ScalingChanged += OnScalingChanged;
        ApplyTheme();
        if (_session is not null)
        {
            // Back in the tree (tab switch): the process kept running. Metrics may belong to another top level.
            ResetMetrics();
            RecomputeGrid();
            _session.ForceFlush();
        }
        else
        {
            RecomputeGrid();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_topLevel is not null) _topLevel.ScalingChanged -= OnScalingChanged;
        _topLevel = null;
        // The process is deliberately left alone: a tab that is switched away comes back.
    }

    private void OnScalingChanged(object? sender, EventArgs e)
    {
        // The view only drops its cell metrics when font properties change, so make it do that.
        ResetMetrics();
        RecomputeGrid();
        _session?.ForceFlush();
    }

    private void ResetMetrics()
    {
        var size = _view.FontSize;
        _view.FontSize = size + 0.01;
        _view.FontSize = size;
    }

    private void RefitGrid()
    {
        RecomputeGrid();
        _session?.ForceFlush();
    }

    private void RecomputeGrid()
    {
        if (_view.Bounds is not { Width: > 0, Height: > 0 } bounds || TopLevel.GetTopLevel(this) is null) return;
        if (_session is null)
        {
            TryStartSession();
            return;
        }
        _session.Resize(_view.GridSizeFor(bounds.Size));
    }

    private void TryStartSession()
    {
        if (_session is not null || _startFailed || _spec is null || _factory is null) return;
        if (_view.Bounds is not { Width: > 0, Height: > 0 } bounds || TopLevel.GetTopLevel(this) is null) return;
        var grid = _view.GridSizeFor(bounds.Size);
        if (grid.Columns < 2) return;

        TerminalSession session;
        var created = true;
        try
        {
            if (_registry is not null && _key is not null)
            {
                session = _registry.GetOrStart(_key, _spec, _factory, grid, TerminalPalettes.For(_dark),
                    TerminalPalettes.SchemeFor(_dark), Math.Max(100, ScrollbackLines), out created);
            }
            else
            {
                session = new TerminalSession(_spec, _factory, grid, TerminalPalettes.For(_dark),
                    TerminalPalettes.SchemeFor(_dark), Math.Max(100, ScrollbackLines), IsOnScreen) { IsMirror = IsMirror };
                try { session.Start(); }
                catch
                {
                    _ = session.DisposeAsync().AsTask();
                    throw;
                }
            }
        }
        catch (Exception ex)
        {
            _startFailed = true;
            IsRunning = false;
            ShowMessage(ex.Message);
            return;
        }
        session.FrameReady += OnFrame;
        session.TitleChanged += OnTitle;
        session.Bell += OnBell;
        session.Exited += OnExited;
        session.SizeOwnerChanged += OnSizeOwnerChanged;
        _session = session;
        _router = new TerminalInputRouter(session, () => Keymap, GetClipboardTextAsync, SaveClipboardImageAsync, SetClipboardTextAsync, AllowPasteAsync, OpenSearch);
        IsRunning = session.IsRunning;
        if (!created)
        {
            // It ran before this control existed: take what it has (its program's title, how it ended) and give it this
            // control's colors and size.
            _spec = session.Spec;
            if (session.Title is { } title) Title = title;
            session.SetPalette(TerminalPalettes.For(_dark), TerminalPalettes.SchemeFor(_dark));
            session.Resize(grid);
            if (!session.IsRunning && session.LastExit is { } exit) ShowExit(exit);
        }
        session.SetViewer(IsOnScreen);
    }

    private void DetachSession()
    {
        if (_session is { } session)
        {
            session.FrameReady -= OnFrame;
            session.TitleChanged -= OnTitle;
            session.Bell -= OnBell;
            session.Exited -= OnExited;
            session.SizeOwnerChanged -= OnSizeOwnerChanged;
            session.SetViewer(null);
        }
        _router?.Dispose();
        _router = null;
        _session = null;
        _view.Snapshot = null;
        _frame = null;
        _scrollBar.IsVisible = false;
        _overlay.Update(null);
        SetHover(null);
        if (_searchOpen)
        {
            _searchOpen = false;
            _searchHost.IsVisible = false;
            _searchQuery = string.Empty;
            (_searchTotal, _searchPosition) = (0, null);
            SearchChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool IsOnScreen() => TopLevel.GetTopLevel(this) is not null && IsEffectivelyVisible;

    private void OnFrame(FrameSnapshot frame)
    {
        _view.Snapshot = frame;
        _frame = frame;
        _overlay.Update(frame);
        _updatingScrollBar = true;
        try
        {
            var scroll = frame.Scrollbar;
            _scrollBar.Maximum = Math.Max(0, scroll.Total - scroll.Length);
            _scrollBar.ViewportSize = scroll.Length;
            _scrollBar.LargeChange = Math.Max(1, scroll.Length - 1);
            _scrollBar.Value = scroll.Offset;
            _scrollBar.IsVisible = _scrollBar.Maximum > 0 && _session?.ReadInputModes().AlternateScreen != true;
        }
        finally { _updatingScrollBar = false; }
        if (_hoverLink is not null) SetHover(FindLink(_hoverCell));
        if (_searchOpen)
        {
            var total = _searchQuery.Length == 0 ? 0 : frame.Search?.TotalMatches ?? 0;
            // The core counts from the newest match (0); the bar counts from the oldest (1).
            long? position = frame.Search?.SelectedIndex is { } index && total > 0 ? total - index : null;
            if (total != _searchTotal || position != _searchPosition)
            {
                (_searchTotal, _searchPosition) = (total, position);
                SearchChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void OnTitle(string title)
    {
        Title = title;
        TitleChanged?.Invoke(this, title);
    }

    private void OnBell() => Bell?.Invoke(this, EventArgs.Empty);

    private void OnExited(TerminalExit exit) => Exited?.Invoke(this, ShowExit(exit));

    private int? ShowExit(TerminalExit exit)
    {
        var code = exit.ExitCode ?? (exit.Signal is { } s ? 128 + s : null);
        IsRunning = false;
        ExitCode = code;
        ShowMessage(string.Format(ExitedMessageFormat, code?.ToString() ?? "?"));
        return code;
    }

    // A view on another device took the say over the size, or gave it back: in the second case this view's size applies again.
    private void OnSizeOwnerChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (_session is { SizeOwner: null }) RecomputeGrid();
    });

    /// <summary>
    /// The person is using this view: the terminal takes this view's size again if a view on another device had it
    /// (the view that was last used decides, design/12-远程操作.md §6.5).
    /// </summary>
    private void ReclaimSize()
    {
        if (_session is not { SizeOwner: not null } session) return;
        if (_view.Bounds is not { Width: > 0, Height: > 0 } bounds) return;
        session.ClaimSize(null, _view.GridSizeFor(bounds.Size));
    }

    private void OnKeyPressed(object? sender, TerminalKeyEventArgs e)
    {
        if (e.Input.Action == KeyAction.Press) ReclaimSize();
        if (!IsRunning)
        {
            // Nothing to type into: Enter starts the command again.
            if (e.Input is { Key: TerminalKey.Enter, Action: KeyAction.Press } && _spec is not null)
            {
                _ = RestartAsync();
                e.Handled = true;
            }
            return;
        }
        _router?.HandleKey(e);
    }

    // One for every terminal: a cursor holds a platform resource, and the pointer crosses links often.
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private const KeyMods ModifierMask = KeyMods.Shift | KeyMods.Ctrl | KeyMods.Alt | KeyMods.Super;

    private void OnPointer(object? sender, TerminalPointerEventArgs e)
    {
        var held = (e.Mouse.Mods & ModifierMask) == Keymap.LinkModifier;
        switch (e.Kind)
        {
            case TerminalPointerKind.Move:
                _hoverCell = e.Cell;
                SetHover(held ? FindLink(e.Cell) : null);
                break;
            case TerminalPointerKind.Press when e.Mouse.Button == MouseButton.Left && !held:
                ReclaimSize();
                break;
            case TerminalPointerKind.Press when e.Mouse.Button == MouseButton.Left && held:
                if (FindLink(e.Cell) is { } link)
                {
                    _pendingLink = link.Url;
                    e.Handled = true;
                    return; // not a selection start, and not reported to a program that tracks the mouse
                }
                break;
            case TerminalPointerKind.Release when e.Mouse.Button == MouseButton.Left && _pendingLink is { } url:
                _pendingLink = null;
                e.Handled = true;
                if (held && FindLink(e.Cell) is { } released && released.Url == url)
                {
                    if (released.IsPath)
                    {
                        if (ResolvePath(released.Url) is { } full) PathActivated?.Invoke(this, new TerminalPathActivatedEventArgs(full, released.Line, released.Column));
                    }
                    else if (TerminalLinks.IsWebUrl(url))
                    {
                        LinkActivated?.Invoke(this, url);
                    }
                }
                return;
        }
        _router?.HandlePointer(e);
    }

    private LinkHit? FindLink(ViewportPoint cell) =>
        _frame is { } frame ? TerminalLinks.Find(frame, cell.Row, cell.Column, PathLinksEnabled ? PathExists : null) : null;

    private bool PathExists(string path) => ResolvePath(path) is not null;

    /// <summary>The full path of an existing file or folder that <paramref name="path"/> names, or null.</summary>
    private string? ResolvePath(string path)
    {
        try
        {
            if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
            var full = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : _spec?.WorkingDirectory is { Length: > 0 } directory ? Path.GetFullPath(Path.Combine(directory, path)) : null;
            return full is not null && (File.Exists(full) || Directory.Exists(full)) ? full : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void SetHover(LinkHit? link)
    {
        if (link?.Url == _hoverLink?.Url && link?.Spans.Count == _hoverLink?.Spans.Count
            && (link is null || link.Spans[0] == _hoverLink!.Spans[0])) return;
        _hoverLink = link;
        _overlay.SetLink(link);
        _view.Cursor = link is null ? null : HandCursor;
    }

    private async Task<bool> AllowPasteAsync(string text, bool bracketed)
    {
        if (!TerminalPasteGuard.NeedsConfirmation(text, bracketed, ConfirmMultilinePaste) || PasteConfirmer is not { } confirm)
            return true;
        return await confirm(text, TerminalPasteGuard.Preview(text)).ConfigureAwait(true);
    }

    private void ApplyTheme()
    {
        var variant = ActualThemeVariant;
        var dark = variant == ThemeVariant.Dark || variant?.InheritVariant == ThemeVariant.Dark;
        if (dark == _dark && _session is null) { ApplyColors(); return; }
        if (dark == _dark) return;
        _dark = dark;
        ApplyColors();
        _session?.SetPalette(TerminalPalettes.For(dark), TerminalPalettes.SchemeFor(dark));
    }

    private void ApplyColors()
    {
        var palette = TerminalPalettes.For(_dark);
        _background.Background = new SolidColorBrush(ToColor(palette.Background));
        _messageBar.Background = new SolidColorBrush(ToColor(palette.Foreground), 0.14);
        _message.Foreground = new SolidColorBrush(ToColor(palette.Foreground));
    }

    private static Color ToColor(TerminalRgb rgb) => Color.FromRgb(rgb.R, rgb.G, rgb.B);

    private void ShowMessage(string text)
    {
        _message.Text = text;
        _messageBar.IsVisible = true;
    }

    private void HideMessage() => _messageBar.IsVisible = false;

    private async Task<string?> GetClipboardTextAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return null;
        return await clipboard.TryGetTextAsync();
    }

    /// <summary>Writes an image on the clipboard to a temporary PNG and returns its path; null when there is none.</summary>
    private async Task<string?> SaveClipboardImageAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return null;
        try
        {
            if (!(await clipboard.GetDataFormatsAsync()).Contains(global::Avalonia.Input.DataFormat.Bitmap)) return null;
            using var bitmap = await clipboard.TryGetBitmapAsync();
            if (bitmap is null) return null;
            return ClipboardImages.Save(bitmap);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { return null; }
    }

    private Task SetClipboardTextAsync(string text) =>
        TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard ? clipboard.SetTextAsync(text) : Task.CompletedTask;

}

/// <summary>A file path followed in the terminal (see <see cref="TerminalControl.PathActivated"/>).</summary>
/// <param name="path">Full path of an existing file or folder.</param>
public sealed class TerminalPathActivatedEventArgs(string path, int? line, int? column) : EventArgs
{
    public string Path { get; } = path;
    public int? Line { get; } = line;
    public int? Column { get; } = column;
}
