using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Core.Terminals;
using Octopus.Mobile.Common;
using Octopus.Mobile.Localization;
using Octopus.Mobile.Shell;
using Octopus.Mobile.Workspace;
using Octopus.Terminal;
using TKCode.Terminal.Core;

namespace Octopus.Mobile.Terminals;

/// <summary>How the person types into a session on the phone (design/ui/移动端布局方案.html, "终端页").</summary>
public enum TerminalInputMode
{
    /// <summary>Only looking: the session keeps the size another device gave it, and nothing is typed.</summary>
    Viewing,
    /// <summary>A text field whose whole line is sent at once: the default for agents, and kind to input methods.</summary>
    Composing,
    /// <summary>Every key goes straight to the terminal, with a row of the keys a phone keyboard lacks.</summary>
    Direct,
}

/// <summary>What the view model needs from the terminal on the page.</summary>
public interface ITerminalSurface
{
    void SendText(string text);
    void SendKey(TerminalKey key, KeyMods mods);
    /// <summary>The screen and scrollback as plain text.</summary>
    string ReadScreenText();
    /// <summary>The last non-empty lines currently visible in the viewport.</summary>
    string ReadTailText(int maxLines = 16) => ReadScreenText();
    (int Columns, int Rows)? GridSize { get; }
    void FocusTerminal();
}

/// <summary>A key of the row above the keyboard: a named key, a character, or the sticky Ctrl.</summary>
public sealed partial class KeyCapViewModel(string label, TerminalKey? key, string? text, bool isControl, Action<KeyCapViewModel> press) : ObservableObject
{
    public string Label { get; } = label;
    public TerminalKey? Key { get; } = key;
    public string? Text { get; } = text;
    public bool IsControl { get; } = isControl;
    /// <summary>Ctrl was tapped and waits for the key it applies to.</summary>
    [ObservableProperty] public partial bool IsArmed { get; set; }

    [RelayCommand]
    private void Press() => press(this);
}

/// <summary>One session's terminal: who decides its size, how input is typed, and the buttons that answer a question.</summary>
public sealed partial class TerminalViewModel : ObservableObject, IDisposable
{
    private readonly IRemoteWorkspace _workspace;
    private readonly INavigator _navigator;
    private readonly KeyCapViewModel _control;
    private ITerminalSurface? _surface;

    public TerminalViewModel(RemoteSession session, IRemoteWorkspace workspace, INavigator navigator)
    {
        Session = session;
        _workspace = workspace;
        _navigator = navigator;
        Mode = TerminalInputMode.Viewing;
        ComposeText = "";
        _control = Cap("Ctrl", null, null, isControl: true);
        Keys =
        [
            Cap("Esc", TerminalKey.Escape), Cap("Tab", TerminalKey.Tab), _control,
            Cap("↑", TerminalKey.ArrowUp), Cap("↓", TerminalKey.ArrowDown), Cap("←", TerminalKey.ArrowLeft), Cap("→", TerminalKey.ArrowRight),
            Cap("/", null, "/"), Cap("~", null, "~"), Cap("|", null, "|"), Cap("-", null, "-"),
        ];
        // Closed from its own menu: the page leaves (the program on the host has ended).
        Actions = new SessionActionsViewModel(workspace, navigator, session, _ => navigator.Back());
        FontSize = DefaultFontSize;
        session.PropertyChanged += OnSessionChanged;
    }

    /// <summary>The terminal's text size on a phone, and the range "A−" and "A+" move it in.</summary>
    public const double DefaultFontSize = 12, MinFontSize = 8, MaxFontSize = 20;

    public RemoteSession Session { get; }
    /// <summary>The "⋯" menu and what it opens (design/ui/移动端功能扩展方案.html, ②).</summary>
    public SessionActionsViewModel Actions { get; }
    /// <summary>A smaller size fits more columns; while fitted the session follows the new grid.</summary>
    [ObservableProperty] public partial double FontSize { get; set; }
    public string Title => Session.Title;
    /// <summary>Project (with branch when in a worktree), host and how the host is reached, under the title.</summary>
    public string Location => string.Join(" · ",
        Session.WorkspaceBranch is { Length: > 0 } branch
            ? $"{Session.Project.Name} ({branch})"
            : Session.Project.Name,
        Session.Project.Host.Name,
        LinkName(Session.Project.Host.Link));

    /// <summary>What the terminal control is started with: the session on the host, not a program of this device.</summary>
    public LaunchSpec Spec { get; } = new("octopus-remote-session", [], null, new Dictionary<string, string>());
    public ITerminalConnectionFactory Factory => _workspace.OpenTerminal(Session);
    /// <summary>The terminal shows a session another computer runs: the control only mirrors it.</summary>
    public bool IsMirror => _workspace.MirrorsTerminals;

    [ObservableProperty] public partial TerminalInputMode Mode { get; private set; }
    public bool IsViewing => Mode == TerminalInputMode.Viewing;
    public bool IsComposing => Mode == TerminalInputMode.Composing;
    public bool IsDirect => Mode == TerminalInputMode.Direct;
    /// <summary>This phone's view decides the size.</summary>
    public bool IsFitted => Mode != TerminalInputMode.Viewing;

    public string SizeText => IsFitted
        ? string.Format(MobileLangResources.Get(MobileLangResourceKind.SizeFitted), Session.Columns, Session.Rows)
        : string.Format(MobileLangResources.Get(MobileLangResourceKind.SizeOwnedBy), Session.Columns, Session.Rows, Session.SizeOwner ?? Session.Project.Host.Name);

    [ObservableProperty] public partial string ComposeText { get; set; }
    public ObservableCollection<QuickReply> QuickReplies { get; } = [];
    public bool HasQuickReplies => IsComposing && QuickReplies.Count > 0;
    public IReadOnlyList<KeyCapViewModel> Keys { get; }

    /// <summary>The page hands over its terminal once it exists.</summary>
    public void Attach(ITerminalSurface surface)
    {
        _surface = surface;
        RefreshQuickReplies();
    }

    [RelayCommand]
    private void Back() => _navigator.Back();

    /// <summary>
    /// The "⋯" menu: fit or give the size back and the text size first, then the project's files and uncommitted
    /// changes when the host supports them, then what can be done to any session (rename, restart, another like it,
    /// details, close).
    /// </summary>
    [RelayCommand]
    private void OpenMenu()
    {
        var leading = new List<SessionMenuEntry>
        {
            IsFitted
                ? new(MobileLangResources.Get(MobileLangResourceKind.ReturnSize), ReleaseCommand)
                : new(MobileLangResources.Get(MobileLangResourceKind.FitToPhone), FitCommand),
        };
        if (FontSize < MaxFontSize) leading.Add(new(MobileLangResources.Get(MobileLangResourceKind.FontLarger), LargerTextCommand));
        if (FontSize > MinFontSize) leading.Add(new(MobileLangResources.Get(MobileLangResourceKind.FontSmaller), SmallerTextCommand));
        if (_workspace.SupportsFiles(Session.Project.Host)) leading.Add(new(MobileLangResources.Get(MobileLangResourceKind.FilesBrowse), BrowseFilesCommand));
        if (_workspace.SupportsGit(Session.Project.Host)) leading.Add(new(MobileLangResources.Get(MobileLangResourceKind.ChangesView), ViewChangesCommand));
        Actions.ShowMenu(Session, leading);
    }

    [RelayCommand]
    private void BrowseFiles() => _navigator.ShowFiles(Session.Project, RemoteFolders.WorkspacePathOf(Session));

    [RelayCommand]
    private void ViewChanges() => _navigator.ShowChanges(Session.Project, RemoteFolders.WorkspacePathOf(Session));

    [RelayCommand]
    private void LargerText() => FontSize = Math.Min(MaxFontSize, FontSize + 1);

    [RelayCommand]
    private void SmallerText() => FontSize = Math.Max(MinFontSize, FontSize - 1);

    /// <summary>Takes the size for this phone and opens the input that fits the session: the composer for an agent, the keyboard for a terminal.</summary>
    [RelayCommand]
    private async Task FitAsync()
    {
        if (IsFitted) return;
        var grid = _surface?.GridSize ?? (Session.Columns, Session.Rows);
        await _workspace.ClaimSizeAsync(Session, grid.Columns, grid.Rows);
        Mode = Session.Kind == SessionKind.Agent ? TerminalInputMode.Composing : TerminalInputMode.Direct;
        if (IsDirect) _surface?.FocusTerminal();
    }

    /// <summary>Gives the size back and goes back to only looking.</summary>
    [RelayCommand]
    private async Task ReleaseAsync()
    {
        if (!IsFitted) return;
        await _workspace.ReleaseSizeAsync(Session);
        Disarm();
        Mode = TerminalInputMode.Viewing;
    }

    /// <summary>Switches between the composer and typing straight into the terminal.</summary>
    [RelayCommand]
    private void ToggleInput()
    {
        if (!IsFitted) return;
        Disarm();
        Mode = IsComposing ? TerminalInputMode.Direct : TerminalInputMode.Composing;
        if (IsDirect) _surface?.FocusTerminal();
    }

    /// <summary>Sends the composed line and Enter. An empty line still sends Enter: that confirms a default.</summary>
    [RelayCommand]
    private void Send()
    {
        _surface?.SendText(ComposeText + "\r");
        ComposeText = "";
    }

    [RelayCommand]
    private void Reply(QuickReply? reply)
    {
        if (reply is null) return;
        if (reply.Key is { } key) _surface?.SendKey(key, KeyMods.None);
        else _surface?.SendText(reply.Keys);
    }

    /// <summary>
    /// Text typed on the keyboard while Ctrl is armed becomes the control character of its first letter (Ctrl then c is
    /// the interrupt). True when the text was taken; false leaves it to the terminal.
    /// </summary>
    public bool TryTakeTyped(string text)
    {
        if (!_control.IsArmed || text.Length == 0) return false;
        Disarm();
        _surface?.SendText(ControlOf(text[0]));
        return true;
    }

    /// <summary>Reads the choices of the agent's question from the screen while it waits; none otherwise.</summary>
    public void RefreshQuickReplies()
    {
        IReadOnlyList<QuickReply> found = Session.Activity == SessionActivity.Waiting && _surface is not null
            ? Terminals.QuickReplies.Parse(_surface.ReadTailText())
            : [];
        // Esc closes the question without choosing: always the last row when there are choices.
        if (found.Count > 0) found = [.. found, new QuickReply("Esc", "\u001b", TerminalKey.Escape)];
        if (!found.SequenceEqual(QuickReplies))
        {
            QuickReplies.Clear();
            foreach (var reply in found) QuickReplies.Add(reply);
        }
        OnPropertyChanged(nameof(HasQuickReplies));
    }

    public void Dispose()
    {
        Session.PropertyChanged -= OnSessionChanged;
        _workspace.CloseTerminal(Session);
    }

    private KeyCapViewModel Cap(string label, TerminalKey? key, string? text = null, bool isControl = false) => new(label, key, text, isControl, Press);

    private void Press(KeyCapViewModel cap)
    {
        if (cap.IsControl)
        {
            cap.IsArmed = !cap.IsArmed;
            return;
        }
        var armed = _control.IsArmed;
        Disarm();
        if (cap.Key is { } key) _surface?.SendKey(key, armed ? KeyMods.Ctrl : KeyMods.None);
        else if (cap.Text is { Length: > 0 } text) _surface?.SendText(armed ? ControlOf(text[0]) : text);
    }

    private void Disarm() => _control.IsArmed = false;

    // Ctrl with a character is that character's low five bits: Ctrl+C is 0x03, Ctrl+/ is 0x1F.
    private static string ControlOf(char ch) => ((char)(char.ToUpperInvariant(ch) & 0x1F)).ToString();

    private static string LinkName(HostLink link) => MobileLangResources.Get(link switch
    {
        HostLink.Lan => MobileLangResourceKind.LinkLan,
        HostLink.Relay => MobileLangResourceKind.LinkRelay,
        _ => MobileLangResourceKind.LinkOffline,
    });

    partial void OnModeChanged(TerminalInputMode value)
    {
        OnPropertyChanged(nameof(IsViewing));
        OnPropertyChanged(nameof(IsComposing));
        OnPropertyChanged(nameof(IsDirect));
        OnPropertyChanged(nameof(IsFitted));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(HasQuickReplies));
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(RemoteSession.Title):
                OnPropertyChanged(nameof(Title));
                break;
            case nameof(RemoteSession.WorkspaceBranch):
                OnPropertyChanged(nameof(Location));
                break;
            case nameof(RemoteSession.Columns) or nameof(RemoteSession.Rows) or nameof(RemoteSession.SizeOwner):
                OnPropertyChanged(nameof(SizeText));
                break;
            case nameof(RemoteSession.Activity):
                RefreshQuickReplies();
                break;
        }
    }
}
