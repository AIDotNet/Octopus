using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Core.Terminals;
using Octopus.Mobile.Common;
using Octopus.Mobile.Localization;
using Octopus.Mobile.Shell;
using Octopus.Mobile.Workspace;
using Octopus.Remote.Protocol;
using Octopus.Terminal;
using Octopus.Terminal.Avalonia;
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
    /// <summary>Lets the person choose a picture; null when nothing was chosen.</summary>
    Task<PickedImage?> PickImageAsync() => Task.FromResult<PickedImage?>(null);
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
        FontSize = Math.Clamp(MobilePreferences.Current.TerminalFontSize, MinFontSize, MaxFontSize);
        WorkspaceMenu = new SessionWorkspaceViewModel(session, workspace, navigator, Actions, BackCommand)
        {
            ShowFontSizeControls = true,
            SmallerTextCommand = SmallerTextCommand,
            LargerTextCommand = LargerTextCommand,
        };
        SyncWorkspaceMenu();
        session.PropertyChanged += OnSessionChanged;
    }

    /// <summary>The terminal's text size on a phone, and the range "A−" and "A+" move it in.</summary>
    public const double DefaultFontSize = 12, MinFontSize = 8, MaxFontSize = 20;

    public RemoteSession Session { get; }
    /// <summary>The rename sheet, close dialog and notices.</summary>
    public SessionActionsViewModel Actions { get; }
    /// <summary>The Scheme B capsule top bar and Scheme C top-right popover menu (files, changes, branches, sessions, actions).</summary>
    public SessionWorkspaceViewModel WorkspaceMenu { get; }
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

    /// <summary>The host takes pictures for its sessions: the input bars show the picture button.</summary>
    public bool CanSendImage => _workspace.SupportsUpload(Session.Project.Host);
    /// <summary>A picture is on its way to the host.</summary>
    [ObservableProperty] public partial bool IsUploading { get; private set; }
    /// <summary>How far the picture is, for the line above the input.</summary>
    [ObservableProperty] public partial string UploadText { get; private set; } = "";
    /// <summary>True while awaiting user confirmation to paste multi-line text (controlled by <see cref="MobilePreferences.TerminalPasteConfirm"/>).</summary>
    [ObservableProperty] public partial bool IsPasteConfirmOpen { get; set; }
    [ObservableProperty] public partial string PasteConfirmBody { get; private set; } = "";
    [ObservableProperty] public partial string PendingPastePreview { get; private set; } = "";
    private string? _pendingPastePayload;
    private bool _clearComposerAfterPaste;

    /// <summary>The page hands over its terminal once it exists.</summary>
    public void Attach(ITerminalSurface surface)
    {
        _surface = surface;
        RefreshQuickReplies();
    }

    [RelayCommand]
    private void Back() => _navigator.Back();

    /// <summary>Opens the top-right workspace &amp; session popover menu.</summary>
    [RelayCommand]
    private void OpenMenu() => WorkspaceMenu.OpenMenuCommand.Execute(null);

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

    /// <summary>Sends the composed line and Enter. Multi-line content asks for confirmation when <see cref="MobilePreferences.TerminalPasteConfirm"/> is enabled.</summary>
    [RelayCommand]
    private void Send()
    {
        if (TrySendOrConfirmPaste(ComposeText, appendEnter: true, clearComposer: true))
        {
            ComposeText = "";
        }
    }

    /// <summary>
    /// Sends <paramref name="text"/> directly unless it spans multiple lines and <see cref="MobilePreferences.TerminalPasteConfirm"/>
    /// is enabled, in which case a confirmation dialog is opened and <c>false</c> is returned.
    /// </summary>
    public bool TrySendOrConfirmPaste(string text, bool appendEnter = false, bool clearComposer = false)
    {
        var payload = appendEnter ? text + "\r" : text;
        if (MobilePreferences.Current.TerminalPasteConfirm && IsMultiLine(text))
        {
            var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
            _pendingPastePayload = payload;
            _clearComposerAfterPaste = clearComposer;
            PasteConfirmBody = MobileLangResources.Format(MobileLangResourceKind.TerminalPasteConfirmBody, lines.Length);
            PendingPastePreview = lines.Length <= 4
                ? string.Join('\n', lines)
                : string.Join('\n', lines.Take(4)) + "\n…";
            IsPasteConfirmOpen = true;
            return false;
        }
        _surface?.SendText(payload);
        return true;
    }

    [RelayCommand]
    private void ConfirmPaste()
    {
        var payload = _pendingPastePayload;
        var clearComposer = _clearComposerAfterPaste;
        _pendingPastePayload = null;
        _clearComposerAfterPaste = false;
        IsPasteConfirmOpen = false;
        if (payload is not null) _surface?.SendText(payload);
        if (clearComposer) ComposeText = "";
    }

    [RelayCommand]
    private void CancelPaste()
    {
        _pendingPastePayload = null;
        _clearComposerAfterPaste = false;
        IsPasteConfirmOpen = false;
    }

    private static bool IsMultiLine(string text) =>
        text.Contains('\n', StringComparison.Ordinal) || text.Contains('\r', StringComparison.Ordinal);

    /// <summary>
    /// Sends a picture the person chooses to the host and puts the path the host keeps it at where the session takes
    /// input: after the composed line (sent with it), or typed straight into the terminal. The program reads the
    /// picture from there, as when a picture is pasted into a terminal on the computer itself.
    /// </summary>
    [RelayCommand]
    private async Task SendImageAsync()
    {
        if (_surface is null || IsUploading) return;
        try
        {
            var picked = await _surface.PickImageAsync();
            if (picked is null) return;
            IsUploading = true;
            ShowProgress(0);
            var prepared = await Task.Run(() => UploadImages.Prepare(picked));
            var path = await _workspace.UploadImageAsync(Session, prepared.Name, prepared.Data, new Progress<double>(ShowProgress));
            var typed = ClipboardImages.Quote(path);
            if (IsDirect)
            {
                _surface.SendText(typed + " ");
            }
            else
            {
                ComposeText = ComposeText.Length == 0 || char.IsWhiteSpace(ComposeText[^1]) ? ComposeText + typed + " " : ComposeText + " " + typed + " ";
                Actions.ShowNotice(MobileLangResources.Get(MobileLangResourceKind.UploadDone));
            }
        }
        catch (ImageTooLargeException)
        {
            Actions.ShowNotice(string.Format(MobileLangResources.Get(MobileLangResourceKind.UploadTooLarge), RemoteMethods.UploadMaxBytes / (1024 * 1024)));
        }
        catch (InvalidDataException)
        {
            Actions.ShowNotice(MobileLangResources.Get(MobileLangResourceKind.UploadUnreadable));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Actions.ShowNotice(string.Format(MobileLangResources.Get(MobileLangResourceKind.UploadFailed), e.Message));
        }
        finally
        {
            IsUploading = false;
        }
    }

    private void ShowProgress(double share) =>
        UploadText = string.Format(MobileLangResources.Get(MobileLangResourceKind.UploadProgress), (int)Math.Round(share * 100));

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
        var found = Session.Activity == SessionActivity.Waiting && _surface is not null
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
        WorkspaceMenu.Dispose();
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

    partial void OnFontSizeChanged(double value)
    {
        if (WorkspaceMenu is not null) SyncWorkspaceMenu();
    }

    partial void OnModeChanged(TerminalInputMode value)
    {
        OnPropertyChanged(nameof(IsViewing));
        OnPropertyChanged(nameof(IsComposing));
        OnPropertyChanged(nameof(IsDirect));
        OnPropertyChanged(nameof(IsFitted));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(HasQuickReplies));
        if (WorkspaceMenu is not null) SyncWorkspaceMenu();
    }

    private void SyncWorkspaceMenu()
    {
        WorkspaceMenu.PrimaryActionLabel = MobileLangResources.Get(IsFitted ? MobileLangResourceKind.ReturnSize : MobileLangResourceKind.FitToPhone);
        WorkspaceMenu.PrimaryActionCommand = IsFitted ? ReleaseCommand : FitCommand;
        WorkspaceMenu.FontSizeLabel = $"{(int)Math.Round(FontSize)}pt";
        WorkspaceMenu.CanLargerText = FontSize < MaxFontSize;
        WorkspaceMenu.CanSmallerText = FontSize > MinFontSize;
        WorkspaceMenu.UpdateLiveStatus(
            Session.Activity == SessionActivity.Working,
            Session.Activity == SessionActivity.Waiting,
            Session.Activity == SessionActivity.Error,
            Session.Activity == SessionActivity.Working,
            $"{Session.Columns}×{Session.Rows}");
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
                SyncWorkspaceMenu();
                break;
            case nameof(RemoteSession.Activity):
                RefreshQuickReplies();
                SyncWorkspaceMenu();
                break;
        }
    }
}
