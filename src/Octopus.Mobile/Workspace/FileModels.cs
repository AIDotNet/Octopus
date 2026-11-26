namespace Octopus.Mobile.Workspace;

/// <summary>One file or folder of a project's folder on the host.</summary>
/// <param name="Path">The full path on the host, as the host gives it.</param>
public sealed record RemoteFileEntry(string Name, string Path, bool IsDirectory, bool IsHidden);

public enum RemoteFileContentKind
{
    Text,
    /// <summary>Not text (it has NUL bytes): nothing to show but the fact.</summary>
    Binary,
    /// <summary>More bytes than the phone reads; <see cref="RemoteFileContent.Size"/> says how many.</summary>
    TooLarge,
}

/// <summary>A file read for looking at.</summary>
/// <param name="Text">The whole text for <see cref="RemoteFileContentKind.Text"/>; empty otherwise.</param>
/// <param name="Size">The file's size in bytes.</param>
public sealed record RemoteFileContent(RemoteFileContentKind Kind, string Text, long Size);

public enum RemoteFileBytesKind
{
    Ok,
    /// <summary>More bytes than the phone reads; <see cref="RemoteFileBytes.Size"/> says how many.</summary>
    TooLarge,
}

/// <summary>Raw bytes of a remote file (used for image previews and binary detection).</summary>
public sealed record RemoteFileBytes(RemoteFileBytesKind Kind, byte[] Data, long Size);

/// <summary>What a change did to a file; the same order as the host's git service (<c>Octopus.Git.GitChangeKind</c>).</summary>
public enum RemoteChangeKind
{
    Modified,
    Added,
    Deleted,
    Renamed,
    /// <summary>New to git: not added yet.</summary>
    Untracked,
    Conflicted,
}

/// <summary>One uncommitted change, as <c>git status</c> lists it.</summary>
/// <param name="Path">Relative to the repository's root, with forward slashes.</param>
/// <param name="FullPath">The full path on the host.</param>
/// <param name="IsStaged">The change is in the index; a file changed both in the index and after it is listed twice.</param>
/// <param name="OriginalPath">For a rename: the old path.</param>
/// <param name="IsDirectory">A new folder git lists as one entry: it has no diff, only files to look at.</param>
/// <param name="Added">Added lines, when known without reading the diff; null otherwise.</param>
/// <param name="Removed">Removed lines, the same way.</param>
public sealed record RemoteChange(
    string Path,
    string FullPath,
    RemoteChangeKind Kind,
    bool IsStaged,
    string? OriginalPath = null,
    bool IsDirectory = false,
    int? Added = null,
    int? Removed = null)
{
    /// <summary>The file's name: the last part of <see cref="Path"/>.</summary>
    public string Name => Path.LastIndexOf('/') is var slash and >= 0 ? Path[(slash + 1)..] : Path;

    /// <summary>The folder it is in, relative to the repository; empty at the root.</summary>
    public string Folder => Path.LastIndexOf('/') is var slash and >= 0 ? Path[..slash] : "";
}

/// <summary>The uncommitted changes of a folder's repository.</summary>
/// <param name="IsRepository">False when the folder is in no git repository (then there are no changes).</param>
/// <param name="Branch">The current branch; null on a detached HEAD or outside a repository.</param>
public sealed record RemoteChanges(bool IsRepository, string? Branch, IReadOnlyList<RemoteChange> Changes);

public enum RemoteDiffLineKind
{
    /// <summary>A <c>@@ -a,b +c,d @@</c> header.</summary>
    Hunk,
    Context,
    Added,
    Removed,
    /// <summary>A remark of git such as "\ No newline at end of file".</summary>
    Note,
}

/// <param name="OldNumber">Line number in the old file; null for added lines, hunk headers and notes.</param>
/// <param name="NewNumber">Line number in the new file; null for removed lines, hunk headers and notes.</param>
/// <param name="Text">The line without its leading +, - or space (the whole header for a hunk).</param>
public sealed record RemoteDiffLine(RemoteDiffLineKind Kind, int? OldNumber, int? NewNumber, string Text);

public enum RemoteDiffKind
{
    Text,
    Binary,
    /// <summary>The file or its diff is too big to be shown.</summary>
    TooLarge,
    /// <summary>No difference (the change is gone, or only a mode changed).</summary>
    Empty,
}

/// <summary>The diff of one changed file.</summary>
/// <param name="Lines">The lines to show; fewer than <paramref name="TotalLines"/> when the host cut a long diff.</param>
/// <param name="Added">Added lines of the whole diff.</param>
/// <param name="Removed">Removed lines of the whole diff.</param>
public sealed record RemoteDiff(RemoteDiffKind Kind, IReadOnlyList<RemoteDiffLine> Lines, int TotalLines = 0, int Added = 0, int Removed = 0)
{
    public bool IsTruncated => Lines.Count < TotalLines;

    public static RemoteDiff Of(RemoteDiffKind kind) => new(kind, []);
}

/// <summary>Where a session's files are.</summary>
public static class RemoteFolders
{
    /// <summary>
    /// The folder a session works in: its branch workspace's (a git worktree), or null for the project's own folder.
    /// </summary>
    public static string? WorkspacePathOf(RemoteSession session) =>
        session.WorkspaceId is { Length: > 0 } id ? session.Project.Workspaces.FirstOrDefault(workspace => workspace.Id == id)?.Path : null;
}
