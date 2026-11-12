using Octopus.App.Common;
using TKCode.Arc.Icons.Lucide;

namespace Octopus.App.Explorer.Search;

/// <summary>A row of the search results: a name hit, a file heading or a matching line.</summary>
public sealed class SearchRowViewModel
{
    private SearchRowViewModel(SearchRowKind kind, string path, string title)
    {
        Kind = kind;
        Path = path;
        Title = title;
    }

    public SearchRowKind Kind { get; }
    public string Path { get; }

    /// <summary>The file name, or the matching line's text (without its indentation).</summary>
    public string Title { get; private init; }

    /// <summary>The folder of a hit, relative to the project (gray).</summary>
    public string Subtitle { get; private init; } = "";

    public LucideIconKind IconKind { get; private init; }
    public bool IsDirectory { get; private init; }

    /// <summary>1-based line number of a match; 0 for the other kinds.</summary>
    public int Line { get; private init; }

    /// <summary>Where the match starts in <see cref="Title"/> and how long it is (a content hit only).</summary>
    public int HighlightStart { get; private init; }
    public int HighlightLength { get; private init; }

    public bool IsName => Kind == SearchRowKind.Name;
    public bool IsHeader => Kind == SearchRowKind.FileHeader;
    public bool IsMatch => Kind == SearchRowKind.Match;
    public bool HasSubtitle => Subtitle.Length > 0;
    public string LineText => Line.ToString();

    public static SearchRowViewModel ForName(string path, string name, string relativeDirectory, bool isDirectory) =>
        new(SearchRowKind.Name, path, name)
        {
            Subtitle = relativeDirectory,
            IsDirectory = isDirectory,
            IconKind = FileIcons.For(name, isDirectory),
        };

    public static SearchRowViewModel ForHeader(string path, string name, string relativeDirectory) =>
        new(SearchRowKind.FileHeader, path, name)
        {
            Subtitle = relativeDirectory,
            IconKind = FileIcons.For(name, false),
        };

    public static SearchRowViewModel ForMatch(string path, int line, int column, string lineText, int length)
    {
        var trimmed = lineText.TrimStart();
        var start = Math.Max(0, column - (lineText.Length - trimmed.Length));
        return new SearchRowViewModel(SearchRowKind.Match, path, trimmed)
        {
            Line = line,
            HighlightStart = start,
            HighlightLength = Math.Min(length, Math.Max(0, trimmed.Length - start)),
        };
    }
}
