namespace Octopus.Mobile.Workspace;

/// <summary>
/// The files and the uncommitted changes of a host's projects, for looking at (design/ui/移动端功能扩展方案.html, ⑤
/// "文件查看" and "改动审阅"). The host only lets the phone into its projects' folders and their branch workspaces. Every
/// call throws <see cref="IOException"/> with words for the person when the host cannot answer.
/// </summary>
public partial interface IRemoteWorkspace
{
    /// <summary>The host can list and read its projects' files.</summary>
    bool SupportsFiles(RemoteHost host) => false;

    /// <summary>The host answers git calls on its projects (status and diff).</summary>
    bool SupportsGit(RemoteHost host) => false;

    /// <summary>The files and folders in a folder of the project (its own or a branch workspace's), folders first.</summary>
    Task<IReadOnlyList<RemoteFileEntry>> ListFilesAsync(RemoteProject project, string folder, CancellationToken cancellationToken = default) =>
        throw new IOException("Browsing files is not supported.");

    /// <summary>
    /// Reads a file of the project as text. A file of more than <paramref name="maxBytes"/> is not read
    /// (<see cref="RemoteFileContentKind.TooLarge"/>), nor shown when it is not text (<see cref="RemoteFileContentKind.Binary"/>).
    /// </summary>
    Task<RemoteFileContent> ReadTextFileAsync(RemoteProject project, string path, long maxBytes, CancellationToken cancellationToken = default) =>
        throw new IOException("Reading files is not supported.");

    /// <summary>The uncommitted changes of the repository a folder is in: <paramref name="workspacePath"/>, or the project's own folder when null.</summary>
    Task<RemoteChanges> GetChangesAsync(RemoteProject project, string? workspacePath, CancellationToken cancellationToken = default) =>
        throw new IOException("Reviewing changes is not supported.");

    /// <summary>The host takes pictures for its sessions' programs (<see cref="UploadImageAsync"/>).</summary>
    bool SupportsUpload(RemoteHost host) => false;

    /// <summary>
    /// Hands the host a picture for a session and returns where the host keeps it (its own temporary folder), to be
    /// typed into the session. <paramref name="progress"/> hears the share sent so far, from 0 to 1.
    /// </summary>
    Task<string> UploadImageAsync(RemoteSession session, string name, byte[] data, IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        throw new IOException("Sending pictures is not supported.");

    /// <summary>The diff of one change listed by <see cref="GetChangesAsync"/> for the same folder.</summary>
    Task<RemoteDiff> GetDiffAsync(RemoteProject project, string? workspacePath, RemoteChange change, CancellationToken cancellationToken = default) =>
        throw new IOException("Reviewing changes is not supported.");
}
