using System.Globalization;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Mobile.Localization;
using Octopus.Mobile.Shell;
using Octopus.Mobile.Workspace;

namespace Octopus.Mobile.Files;

/// <summary>One line of a diff as the page shows it: added and removed lines are colored, hunk headers set apart.</summary>
public sealed record DiffLineRow(RemoteDiffLineKind Kind, string Number, string Marker, string Text)
{
    public bool IsAdded => Kind == RemoteDiffLineKind.Added;
    public bool IsRemoved => Kind == RemoteDiffLineKind.Removed;
    public bool IsHunk => Kind == RemoteDiffLineKind.Hunk;
    public bool IsNote => Kind == RemoteDiffLineKind.Note;
}

/// <summary>The diff of one uncommitted change (design/ui/移动端功能扩展方案.html, ⑤ "改动审阅", the diff card).</summary>
public sealed partial class DiffViewModel : ObservableObject
{
    private readonly IRemoteWorkspace _workspace;
    private readonly INavigator _navigator;

    public DiffViewModel(RemoteProject project, string? workspacePath, RemoteChange change, IRemoteWorkspace workspace, INavigator navigator)
    {
        Project = project;
        WorkspacePath = workspacePath;
        Change = change;
        _workspace = workspace;
        _navigator = navigator;
        Lines = [];
        // A diff is read on a narrow screen: wrapped, unlike a file, unless the person turns it off.
        IsWrapped = true;
    }

    public RemoteProject Project { get; }
    public string? WorkspacePath { get; }
    public RemoteChange Change { get; }
    public string Title => Change.Name;

    [ObservableProperty] public partial IReadOnlyList<DiffLineRow> Lines { get; private set; }
    [ObservableProperty] public partial bool IsLoading { get; private set; }
    [ObservableProperty] public partial string? Message { get; private set; }
    [ObservableProperty] public partial string? CappedText { get; private set; }
    [ObservableProperty] public partial string? AddedText { get; private set; }
    [ObservableProperty] public partial string? RemovedText { get; private set; }
    [ObservableProperty] public partial double NumberWidth { get; private set; }
    [ObservableProperty] public partial bool IsWrapped { get; set; }

    /// <summary>Under the title: the folder (the old path for a rename) and the counts once read.</summary>
    public string Subtitle => Change.OriginalPath is { Length: > 0 } original ? original + " →" : Change.Folder.Length > 0 ? Change.Folder : Project.Name;
    public bool HasMessage => Message is not null;
    public bool IsCapped => CappedText is not null;
    public bool HasAdded => AddedText is not null;
    public bool HasRemoved => RemovedText is not null;
    public TextWrapping Wrapping => IsWrapped ? TextWrapping.Wrap : TextWrapping.NoWrap;
    public ScrollBarVisibility HorizontalScroll => IsWrapped ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var diff = await _workspace.GetDiffAsync(Project, WorkspacePath, Change);
            Message = diff.Kind switch
            {
                RemoteDiffKind.Binary => MobileLangResources.Get(MobileLangResourceKind.DiffBinary),
                RemoteDiffKind.TooLarge => MobileLangResources.Get(MobileLangResourceKind.DiffTooLarge),
                RemoteDiffKind.Empty => MobileLangResources.Get(MobileLangResourceKind.DiffEmpty),
                _ => null,
            };
            if (diff.Kind != RemoteDiffKind.Text) return;
            AddedText = diff.Added > 0 ? "+" + diff.Added.ToString(CultureInfo.InvariantCulture) : null;
            RemovedText = diff.Removed > 0 ? "−" + diff.Removed.ToString(CultureInfo.InvariantCulture) : null;
            // The host keeps at most 5000 lines; the page shows as many as the file viewer does.
            var shown = diff.Lines.Take(FileViewerViewModel.MaxLines).ToList();
            NumberWidth = CodeLines.NumberWidth(shown.Select(line => line.NewNumber ?? line.OldNumber ?? 0).DefaultIfEmpty(0).Max());
            Lines = [.. shown.Select(RowOf)];
            var total = Math.Max(diff.TotalLines, diff.Lines.Count);
            CappedText = total > shown.Count
                ? string.Format(MobileLangResources.Get(MobileLangResourceKind.LinesCapped), shown.Count, total)
                : null;
        }
        catch (IOException e)
        {
            Message = e.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void Back() => _navigator.Back();

    [RelayCommand]
    private void ToggleWrap() => IsWrapped = !IsWrapped;

    // One number per line: the new file's, or the old one's for a removed line. Two columns do not fit a phone.
    private static DiffLineRow RowOf(RemoteDiffLine line) => line.Kind switch
    {
        RemoteDiffLineKind.Hunk or RemoteDiffLineKind.Note => new(line.Kind, "", "", CodeLines.Shown(line.Text)),
        RemoteDiffLineKind.Added => new(line.Kind, Number(line.NewNumber), "+", CodeLines.Shown(line.Text)),
        RemoteDiffLineKind.Removed => new(line.Kind, Number(line.OldNumber), "-", CodeLines.Shown(line.Text)),
        _ => new(line.Kind, Number(line.NewNumber), " ", CodeLines.Shown(line.Text)),
    };

    private static string Number(int? number) => number?.ToString(CultureInfo.InvariantCulture) ?? "";

    partial void OnMessageChanged(string? value) => OnPropertyChanged(nameof(HasMessage));
    partial void OnCappedTextChanged(string? value) => OnPropertyChanged(nameof(IsCapped));
    partial void OnAddedTextChanged(string? value) => OnPropertyChanged(nameof(HasAdded));
    partial void OnRemovedTextChanged(string? value) => OnPropertyChanged(nameof(HasRemoved));

    partial void OnIsWrappedChanged(bool value)
    {
        OnPropertyChanged(nameof(Wrapping));
        OnPropertyChanged(nameof(HorizontalScroll));
    }
}
