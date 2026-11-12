using Octopus.Git;

namespace Octopus.App.Files;

/// <summary>One line of a diff as the list shows it. The numbers are padded to one width, so a monospace font lines them up.</summary>
public sealed class DiffRowViewModel
{
    public DiffRowViewModel(GitDiffLine line, int digits)
    {
        Kind = line.Kind;
        OldNumber = Pad(line.OldNumber, digits);
        NewNumber = Pad(line.NewNumber, digits);
        Marker = line.Kind switch { GitDiffLineKind.Added => "+", GitDiffLineKind.Removed => "−", _ => " " };
        Text = line.Text;
    }

    public GitDiffLineKind Kind { get; }
    public string OldNumber { get; }
    public string NewNumber { get; }
    public string Marker { get; }
    public string Text { get; }

    public bool IsAdded => Kind == GitDiffLineKind.Added;
    public bool IsRemoved => Kind == GitDiffLineKind.Removed;
    public bool IsHunk => Kind == GitDiffLineKind.Hunk;
    public bool IsNote => Kind == GitDiffLineKind.Note;
    public bool HasGutter => Kind is not (GitDiffLineKind.Hunk or GitDiffLineKind.Note);

    /// <summary>What the code view draws around this row: the two numbers, the sign, the tint and which side of the change it is code of.</summary>
    public CodeLineMark ToMark() => new(OldNumber.Trim(), NewNumber.Trim(), Marker.Trim(), Kind switch
    {
        GitDiffLineKind.Added => CodeLineTint.Added,
        GitDiffLineKind.Removed => CodeLineTint.Removed,
        GitDiffLineKind.Hunk => CodeLineTint.Hunk,
        _ => CodeLineTint.None,
    }, Kind switch
    {
        GitDiffLineKind.Context => (byte)3,
        GitDiffLineKind.Added => (byte)2,
        GitDiffLineKind.Removed => (byte)1,
        _ => (byte)0,
    });

    private static string Pad(int? number, int digits) => (number?.ToString() ?? "").PadLeft(digits);
}
