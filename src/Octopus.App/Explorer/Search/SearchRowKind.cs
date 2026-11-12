namespace Octopus.App.Explorer.Search;

public enum SearchRowKind
{
    /// <summary>A file or folder found by name.</summary>
    Name,
    /// <summary>The file name above the matches of a content search.</summary>
    FileHeader,
    /// <summary>One matching line.</summary>
    Match
}
