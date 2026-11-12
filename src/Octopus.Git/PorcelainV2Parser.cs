namespace Octopus.Git;

/// <summary>Reads the output of <c>git status --porcelain=v2 --branch -z</c>.</summary>
internal static class PorcelainV2Parser
{
    public static GitStatus Parse(string output, string repositoryRoot)
    {
        string? branch = null;
        string? head = null;
        string? upstream = null;
        int ahead = 0, behind = 0;
        var changes = new List<GitChange>();

        var records = output.Split('\0');
        for (var i = 0; i < records.Length; i++)
        {
            var record = records[i];
            if (record.Length < 2) continue;
            switch (record[0])
            {
                case '#':
                    ParseHeader(record, ref branch, ref head, ref upstream, ref ahead, ref behind);
                    break;
                case '1':
                {
                    // 1 XY sub mH mI mW hH hI path
                    var parts = record.Split(' ', 9);
                    if (parts.Length == 9) AddPair(changes, repositoryRoot, parts[1], parts[8], null);
                    break;
                }
                case '2':
                {
                    // 2 XY sub mH mI mW hH hI Xscore path NUL origPath
                    var parts = record.Split(' ', 10);
                    var original = i + 1 < records.Length ? records[++i] : null;
                    if (parts.Length == 10) AddPair(changes, repositoryRoot, parts[1], parts[9], original);
                    break;
                }
                case 'u':
                {
                    // u XY sub m1 m2 m3 mW h1 h2 h3 path
                    var parts = record.Split(' ', 11);
                    if (parts.Length == 11) changes.Add(Make(repositoryRoot, parts[10], GitChangeKind.Conflicted, false, null));
                    break;
                }
                case '?':
                {
                    // A folder listed whole (--untracked-files=normal) ends with a slash.
                    var path = record[2..];
                    var isDirectory = path.EndsWith('/');
                    changes.Add(Make(repositoryRoot, isDirectory ? path.TrimEnd('/') : path, GitChangeKind.Untracked, false, null, isDirectory));
                    break;
                }
            }
        }
        return new GitStatus(branch, head, upstream, ahead, behind, changes);
    }

    private static void ParseHeader(string record, ref string? branch, ref string? head, ref string? upstream, ref int ahead, ref int behind)
    {
        var parts = record.Split(' ', 3);
        if (parts.Length < 3) return;
        var value = parts[2];
        switch (parts[1])
        {
            case "branch.head":
                branch = value == "(detached)" ? null : value;
                break;
            case "branch.oid":
                head = value == "(initial)" ? null : value[..Math.Min(7, value.Length)];
                break;
            case "branch.upstream":
                upstream = value;
                break;
            case "branch.ab":
                foreach (var token in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (token.Length < 2 || !int.TryParse(token.AsSpan(1), out var n)) continue;
                    if (token[0] == '+') ahead = n;
                    else if (token[0] == '-') behind = n;
                }
                break;
        }
    }

    /// <summary>X is the index side (staged), Y the working tree side (unstaged): each non-'.' letter is one change.</summary>
    private static void AddPair(List<GitChange> changes, string root, string xy, string path, string? original)
    {
        if (xy.Length < 2) return;
        if (KindOf(xy[0]) is { } staged)
            changes.Add(Make(root, path, staged, true, staged == GitChangeKind.Renamed ? original : null));
        if (KindOf(xy[1]) is { } unstaged)
            changes.Add(Make(root, path, unstaged, false, unstaged == GitChangeKind.Renamed ? original : null));
    }

    private static GitChangeKind? KindOf(char letter) => letter switch
    {
        'M' or 'T' => GitChangeKind.Modified,
        'A' or 'C' => GitChangeKind.Added,
        'D' => GitChangeKind.Deleted,
        'R' => GitChangeKind.Renamed,
        'U' => GitChangeKind.Conflicted,
        _ => null,
    };

    private static GitChange Make(string root, string path, GitChangeKind kind, bool staged, string? original, bool isDirectory = false) =>
        new(path, Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)), kind, staged, original, isDirectory);
}
