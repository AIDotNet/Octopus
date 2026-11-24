using System.Globalization;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Mobile.Localization;
using Octopus.Mobile.Shell;
using Octopus.Mobile.Workspace;

namespace Octopus.Mobile.Files;

/// <summary>One line of a file as the viewer shows it.</summary>
public sealed record FileLine(string Number, string Text);

/// <summary>
/// A file of the host, read-only: its lines with their numbers in the monospace face (design/ui/移动端功能扩展方案.html, ⑤
/// "文件查看"). A file that is too large or not text says so instead. Without syntax highlighting: Arc's highlighter
/// is TextMate on a native Oniguruma library that has no iOS or Android build.
/// </summary>
public sealed partial class FileViewerViewModel : ObservableObject
{
    /// <summary>The largest file read: the protocol carries it in 256 KB pieces, and a phone has no use for more.</summary>
    public const long MaxBytes = 1024 * 1024;

    /// <summary>More lines than this are counted but not shown, so that a log does not freeze the page.</summary>
    public const int MaxLines = 5000;

    /// <summary>A longer line is cut (minified files).</summary>
    public const int MaxLineLength = 1000;

    private readonly IRemoteWorkspace _workspace;
    private readonly INavigator _navigator;

    /// <param name="location">Where the file is, for the line under its name ("src/Auth"); the folder of its path when null.</param>
    public FileViewerViewModel(RemoteProject project, string path, string? location, IRemoteWorkspace workspace, INavigator navigator)
    {
        Project = project;
        Path = path;
        _workspace = workspace;
        _navigator = navigator;
        Title = FilesViewModel.NameOf(path);
        Location = location ?? FilesViewModel.NameOf(path[..Math.Max(0, path.Length - Title.Length)]);
        Lines = [];
    }

    public RemoteProject Project { get; }
    public string Path { get; }
    public string Title { get; }
    public string Location { get; }

    [ObservableProperty] public partial IReadOnlyList<FileLine> Lines { get; private set; }
    [ObservableProperty] public partial bool IsLoading { get; private set; }
    /// <summary>Why there are no lines: not text, too large, or the host's words.</summary>
    [ObservableProperty] public partial string? Message { get; private set; }
    [ObservableProperty] public partial string? CappedText { get; private set; }
    [ObservableProperty] public partial int LineCount { get; private set; }
    [ObservableProperty] public partial bool IsWrapped { get; set; }

    public bool HasMessage => Message is not null;
    public bool IsCapped => CappedText is not null;
    /// <summary>Under the title: where the file is, read-only, and how many lines it has once read.</summary>
    public string Subtitle => string.Join(" · ", new[]
    {
        Location,
        MobileLangResources.Get(MobileLangResourceKind.FileReadOnly),
        LineCount > 0 ? string.Format(MobileLangResources.Get(MobileLangResourceKind.FileLineCount), LineCount) : null,
    }.Where(part => !string.IsNullOrEmpty(part)));
    /// <summary>The width of the number column: as many digits as the last line number has.</summary>
    public double NumberWidth => CodeLines.NumberWidth(LineCount);
    public TextWrapping Wrapping => IsWrapped ? TextWrapping.Wrap : TextWrapping.NoWrap;
    public ScrollBarVisibility HorizontalScroll => IsWrapped ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var content = await _workspace.ReadTextFileAsync(Project, Path, MaxBytes);
            switch (content.Kind)
            {
                case RemoteFileContentKind.Binary:
                    Message = MobileLangResources.Get(MobileLangResourceKind.FileBinary);
                    break;
                case RemoteFileContentKind.TooLarge:
                    Message = string.Format(MobileLangResources.Get(MobileLangResourceKind.FileTooLarge), CodeLines.Size(content.Size), CodeLines.Size(MaxBytes));
                    break;
                default:
                    var (lines, total) = CodeLines.Split(content.Text);
                    LineCount = total;
                    Lines = lines;
                    CappedText = total > lines.Count
                        ? string.Format(MobileLangResources.Get(MobileLangResourceKind.LinesCapped), lines.Count, total)
                        : null;
                    break;
            }
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

    partial void OnMessageChanged(string? value) => OnPropertyChanged(nameof(HasMessage));
    partial void OnCappedTextChanged(string? value) => OnPropertyChanged(nameof(IsCapped));

    partial void OnLineCountChanged(int value)
    {
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(NumberWidth));
    }

    partial void OnIsWrappedChanged(bool value)
    {
        OnPropertyChanged(nameof(Wrapping));
        OnPropertyChanged(nameof(HorizontalScroll));
    }
}

/// <summary>What the file viewer and the diff page share: lines cut to size, numbers, sizes in words.</summary>
internal static class CodeLines
{
    /// <summary>The lines of a text (at most <see cref="FileViewerViewModel.MaxLines"/>) and how many it has in all. A last empty line after the final newline is not one.</summary>
    public static (IReadOnlyList<FileLine> Lines, int Total) Split(string text)
    {
        var lines = new List<FileLine>();
        var total = 0;
        var start = 0;
        while (start < text.Length)
        {
            var end = text.IndexOf('\n', start);
            if (end < 0) end = text.Length;
            total++;
            if (lines.Count < FileViewerViewModel.MaxLines)
            {
                lines.Add(new FileLine(total.ToString(CultureInfo.InvariantCulture), Shown(text.AsSpan(start, end - start))));
            }
            start = end + 1;
        }
        return (lines, total);
    }

    /// <summary>A line as shown: without its carriage return, tabs as four spaces, cut when very long.</summary>
    public static string Shown(ReadOnlySpan<char> line)
    {
        if (line.Length > 0 && line[^1] == '\r') line = line[..^1];
        var cut = line.Length > FileViewerViewModel.MaxLineLength;
        var text = (cut ? line[..FileViewerViewModel.MaxLineLength] : line).ToString().Replace("\t", "    ", StringComparison.Ordinal);
        return cut ? text + "…" : text;
    }

    /// <summary>The number column of the monospace face at the pages' size: room for the digits of <paramref name="last"/>.</summary>
    public static double NumberWidth(int last) => Math.Max(2, last.ToString(CultureInfo.InvariantCulture).Length) * 7.5 + 4;

    /// <summary>"18 KB", "1.2 MB".</summary>
    public static string Size(long bytes) => bytes switch
    {
        < 1024 => bytes.ToString(CultureInfo.InvariantCulture) + " B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB",
        < 1024L * 1024 * 1024 => (bytes / (1024.0 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
        _ => (bytes / (1024.0 * 1024 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " GB",
    };
}
