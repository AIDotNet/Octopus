using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Octopus.Mobile.Localization;
using Octopus.Mobile.Workspace;
using Octopus.Remote.Client;
using Octopus.Remote.Protocol;

namespace Octopus.Mobile.Remote;

// Files and git of the host's projects (design/12-远程操作.md §6.2): fs/list and fs/read, and git/call with the
// method names and JSON of the desktop's RemoteGitService (Octopus.App/Remote/RemoteGit.cs), which the phone cannot
// reference. Nothing here touches what the pages bind to: the results are plain records the view models show.
internal sealed partial class RemoteWorkspace
{
    // The repository a folder is in, by host and folder: status gives paths relative to it, and the diff wants it.
    private readonly ConcurrentDictionary<(string Host, string Folder), string> _repositories = new();

    public bool SupportsFiles(RemoteHost host) => Shares(host, RemoteMethods.CapabilityFiles);

    public bool SupportsGit(RemoteHost host) => Shares(host, RemoteMethods.CapabilityGit);

    public bool SupportsUpload(RemoteHost host) => Shares(host, RemoteMethods.CapabilityUpload);

    public async Task<string> UploadImageAsync(RemoteSession session, string name, byte[] data, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var path = "";
        await FilesCallAsync(session.Project.Host, RemoteMethods.CapabilityUpload, MobileLangResourceKind.UploadUnsupportedHost, async client =>
            path = await client.UploadAsync(session.Id, name, data, progress, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        return path;
    }

    // A host not reached yet may well have it: the call then says what is wrong.
    private bool Shares(RemoteHost host, string capability) =>
        _connections.ClientOf(host.Id) is not { } client || RemoteHello.Shared(client.OwnHello, client.HostHello).Contains(capability);

    public async Task<IReadOnlyList<RemoteFileEntry>> ListFilesAsync(RemoteProject project, string folder, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<FileEntryMessage> entries = [];
        await FilesCallAsync(project.Host, RemoteMethods.CapabilityFiles, MobileLangResourceKind.FilesUnsupportedHost, async client =>
            entries = await client.ListFolderAsync(folder, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        return
        [
            .. entries
                // The repository's own folder is git's business, not something to read.
                .Where(entry => !(entry.IsDirectory && entry.Name == ".git"))
                .OrderByDescending(entry => entry.IsDirectory)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .Select(entry => new RemoteFileEntry(entry.Name, entry.Path, entry.IsDirectory, entry.IsHidden)),
        ];
    }

    public async Task<RemoteFileContent> ReadTextFileAsync(RemoteProject project, string path, long maxBytes, CancellationToken cancellationToken = default)
    {
        var raw = await ReadFileBytesAsync(project, path, maxBytes, cancellationToken).ConfigureAwait(false);
        return raw.Kind == RemoteFileBytesKind.TooLarge
            ? new RemoteFileContent(RemoteFileContentKind.TooLarge, "", raw.Size)
            : TextOf(raw.Data);
    }

    public async Task<RemoteFileBytes> ReadFileBytesAsync(RemoteProject project, string path, long maxBytes, CancellationToken cancellationToken = default)
    {
        byte[] bytes = [];
        try
        {
            await FilesCallAsync(project.Host, RemoteMethods.CapabilityFiles, MobileLangResourceKind.FilesUnsupportedHost, async client =>
                bytes = await client.ReadFileAsync(path, maxBytes, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        }
        catch (InvalidDataException e)
        {
            // The client stops at the first piece that tells the file is bigger: its size comes along.
            return new RemoteFileBytes(RemoteFileBytesKind.TooLarge, [], e.Data["size"] is long size ? size : maxBytes + 1);
        }
        return new RemoteFileBytes(RemoteFileBytesKind.Ok, bytes, bytes.Length);
    }

    /// <summary>Text when the bytes are text (UTF-8, a byte order mark dropped); binary when they have a NUL byte early on.</summary>
    internal static RemoteFileContent TextOf(byte[] bytes)
    {
        if (bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).Contains((byte)0)) return new RemoteFileContent(RemoteFileContentKind.Binary, "", bytes.Length);
        var text = bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3) : Encoding.UTF8.GetString(bytes);
        return new RemoteFileContent(RemoteFileContentKind.Text, text, bytes.Length);
    }

    public async Task<RemoteChanges> GetChangesAsync(RemoteProject project, string? workspacePath, CancellationToken cancellationToken = default)
    {
        var folder = workspacePath ?? project.Path;
        RemoteChanges? changes = null;
        await FilesCallAsync(project.Host, RemoteMethods.CapabilityGit, MobileLangResourceKind.ChangesUnsupportedHost, async client =>
        {
            var root = await RepositoryOfAsync(client, project.Host, folder, cancellationToken).ConfigureAwait(false);
            if (root is null)
            {
                changes = new RemoteChanges(false, null, []);
                return;
            }
            var reply = await GitAsync(client, "GetStatusAsync", new GitCallArguments { Root = root }, cancellationToken).ConfigureAwait(false);
            var status = reply.Status ?? throw new IOException(MobileLangResources.Get(MobileLangResourceKind.ChangesFailed));
            changes = new RemoteChanges(true, status.Branch,
            [
                .. status.Changes.Select(change => new RemoteChange(change.Path, change.FullPath, (RemoteChangeKind)change.Kind, change.IsStaged,
                    change.OriginalPath, change.IsDirectory)),
            ]);
        }).ConfigureAwait(false);
        return changes!;
    }

    public async Task<RemoteDiff> GetDiffAsync(RemoteProject project, string? workspacePath, RemoteChange change, CancellationToken cancellationToken = default)
    {
        var folder = workspacePath ?? project.Path;
        RemoteDiff? diff = null;
        await FilesCallAsync(project.Host, RemoteMethods.CapabilityGit, MobileLangResourceKind.ChangesUnsupportedHost, async client =>
        {
            var root = await RepositoryOfAsync(client, project.Host, folder, cancellationToken).ConfigureAwait(false)
                ?? throw new IOException(MobileLangResources.Get(MobileLangResourceKind.ChangesNotRepository));
            var reply = await GitAsync(client, "GetFileDiffAsync", new GitCallArguments
            {
                Root = root,
                Path = change.Path,
                Flag = change.IsStaged,
                OriginalPath = change.OriginalPath,
            }, cancellationToken).ConfigureAwait(false);
            var found = reply.Diff ?? throw new IOException(MobileLangResources.Get(MobileLangResourceKind.ChangesFailed));
            diff = new RemoteDiff((RemoteDiffKind)found.Kind,
                [.. found.Lines.Select(line => new RemoteDiffLine((RemoteDiffLineKind)line.Kind, line.OldNumber, line.NewNumber, line.Text))],
                found.TotalLines, found.Added, found.Removed);
        }).ConfigureAwait(false);
        return diff!;
    }

    /// <summary>The root of the repository <paramref name="folder"/> is in, asked once per folder; null outside a repository.</summary>
    private async Task<string?> RepositoryOfAsync(RemoteHostClient client, RemoteHost host, string folder, CancellationToken cancellationToken)
    {
        if (_repositories.TryGetValue((host.Id, folder), out var known)) return known;
        var reply = await GitAsync(client, "FindRepositoryRootAsync", new GitCallArguments { Root = folder }, cancellationToken).ConfigureAwait(false);
        if (reply.Text is not { Length: > 0 } root) return null;
        _repositories[(host.Id, folder)] = root;
        return root;
    }

    private static async Task<GitCallReply> GitAsync(RemoteHostClient client, string method, GitCallArguments arguments, CancellationToken cancellationToken)
    {
        var json = await client.GitAsync(method, JsonSerializer.Serialize(arguments, MobileGitJsonContext.Default.GitCallArguments), cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, MobileGitJsonContext.Default.GitCallReply) ?? new GitCallReply();
    }

    /// <summary>
    /// A call of the files or git part of the host: refused at once when the host has no such part, and what goes wrong
    /// on the way (the host's refusal, a dropped connection, an answer that is not what the two apps agreed on) comes
    /// back as an <see cref="IOException"/> with words for the person.
    /// </summary>
    private async Task FilesCallAsync(RemoteHost host, string capability, MobileLangResourceKind unsupported, Func<RemoteHostClient, Task> call)
    {
        try
        {
            var client = _connections.ClientOf(host.Id) ?? await _connections.ConnectAsync(host.Id).WaitAsync(CallTimeout).ConfigureAwait(false);
            if (!RemoteHello.Shared(client.OwnHello, client.HostHello).Contains(capability)) throw new IOException(MobileLangResources.Get(unsupported));
            await call(client).WaitAsync(CallTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException e)
        {
            throw new IOException("The computer did not answer in time.", e);
        }
        catch (Exception e) when (e is RemoteException or JsonException or ObjectDisposedException or System.Net.WebSockets.WebSocketException)
        {
            throw new IOException(e.Message, e);
        }
    }

    // ---- the JSON of git/call, as Octopus.App's GitCallArguments / GitCallReply write it (camelCase, enums as numbers) ----

    internal sealed class GitCallArguments
    {
        public string Root { get; set; } = "";
        public string? Path { get; set; }
        public string? OriginalPath { get; set; }
        public bool Flag { get; set; }
    }

    internal sealed class GitCallReply
    {
        public string? Text { get; set; }
        public GitStatusReply? Status { get; set; }
        public GitDiffReply? Diff { get; set; }
    }

    internal sealed class GitStatusReply
    {
        public string? Branch { get; set; }
        public List<GitChangeReply> Changes { get; set; } = [];
    }

    internal sealed class GitChangeReply
    {
        public string Path { get; set; } = "";
        public string FullPath { get; set; } = "";
        public int Kind { get; set; }
        public bool IsStaged { get; set; }
        public string? OriginalPath { get; set; }
        public bool IsDirectory { get; set; }
    }

    internal sealed class GitDiffReply
    {
        public int Kind { get; set; }
        public List<GitDiffLineReply> Lines { get; set; } = [];
        public int TotalLines { get; set; }
        public int Added { get; set; }
        public int Removed { get; set; }
    }

    internal sealed class GitDiffLineReply
    {
        public int Kind { get; set; }
        public int? OldNumber { get; set; }
        public int? NewNumber { get; set; }
        public string Text { get; set; } = "";
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RemoteWorkspace.GitCallArguments))]
[JsonSerializable(typeof(RemoteWorkspace.GitCallReply))]
internal sealed partial class MobileGitJsonContext : JsonSerializerContext;
