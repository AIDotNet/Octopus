using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Common;
using Octopus.App.Localization;

namespace Octopus.App.Files.Code;

/// <summary>
/// Text and source code, read-only: highlighted, line numbered, wrapping on request. Large and binary files show a
/// note instead. Also the viewer for any file no other viewer claims.
/// </summary>
public sealed partial class CodeViewerModel : FileViewerModel
{
    public const long MaxBytes = 2 * 1024 * 1024;

    private int _version;

    public CodeViewerModel(FileViewerContext context) : base(context)
    {
        Language = FileLanguages.For(context.Location.FileName);
        MonoFontFamily = context.MonoFontFamily;
        FontSize = context.FontSize;
    }

    /// <summary>The identifier the code block highlights with; plain text when Arc has no grammar for the file.</summary>
    public string Language { get; }

    /// <summary>The terminal's font; null is the theme's monospace stack.</summary>
    public string? MonoFontFamily { get; }

    public double FontSize { get; }

    /// <summary>Long lines wrap instead of scrolling sideways.</summary>
    [ObservableProperty] public partial bool WrapLines { get; set; }

    [ObservableProperty] public partial string Text { get; private set; } = "";
    [ObservableProperty] public partial int LineCount { get; private set; }
    [ObservableProperty] public partial long FileSize { get; private set; }

    [RelayCommand]
    private void ToggleWrap() => WrapLines = !WrapLines;

    /// <summary>"C# · 1,204 lines · 34 KB": what the bar says quietly about the file.</summary>
    public string MetaText => CodeMeta.Describe(Language, LineCount, FileSize);

    partial void OnTextChanged(string value) => LineCount = CodeMeta.CountLines(value);
    partial void OnLineCountChanged(int value) => OnPropertyChanged(nameof(MetaText));
    partial void OnFileSizeChanged(long value) => OnPropertyChanged(nameof(MetaText));

    /// <summary>Reads the file. A newer read makes an older one drop its result.</summary>
    public override async Task LoadAsync()
    {
        var version = ++_version;
        FileRead read;
        try
        {
            read = await Task.Run(() => FileReader.Read(Path, MaxBytes));
        }
        catch (Exception)
        {
            read = new FileRead(null, AppLangResourceKind.FileUnreadable, 0);
        }
        if (version != _version || Disposed) return;
        if (read.Problem is { } problem)
        {
            Text = "";
            Notice = AppLangResources.Get(problem);
            return;
        }
        var bytes = read.Bytes!;
        if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8192)) >= 0)
        {
            Text = "";
            Notice = AppLangResources.Get(AppLangResourceKind.FileBinary);
            return;
        }
        Notice = null;
        FileSize = bytes.Length;
        var text = Encoding.UTF8.GetString(bytes);
        // An unchanged text is not set again: the view would redraw and lose its place for nothing.
        if (!string.Equals(Text, text, StringComparison.Ordinal)) Text = text;
    }
}

/// <summary>The quiet line of facts the bar shows for a text file; shared by the code and the Markdown viewer.</summary>
internal static class CodeMeta
{
    public static int CountLines(string text) => text.Length == 0 ? 0 : text.Count(c => c == '\n') + (text[^1] == '\n' ? 0 : 1);

    public static string Describe(string language, int lines, long size)
    {
        var name = language == FileLanguages.PlainText ? AppLangResources.Get(AppLangResourceKind.FilePlainText) : FileLanguages.DisplayName(language);
        return string.Join(" · ", name, AppLangResources.Format(AppLangResourceKind.FileLines, lines.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)), FileReader.FormatSize(size));
    }
}
