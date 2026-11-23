using Octopus.FileSystem;
using Octopus.Remote.Client;
using Octopus.Remote.Protocol;

namespace Octopus.App.Remote;

/// <summary>
/// Browses folders on a paired Octopus host through <see cref="RemoteHostClient.BrowseFolderAsync"/>,
/// and surfaces the host's existing projects for quick selection.
/// </summary>
public sealed class HostFolderBrowser(
    RemoteClientRuntime runtime,
    string hostId,
    string hostDisplayName,
    string unsupportedMessage) : IFolderBrowser
{
    private static readonly TimeSpan CallLimit = TimeSpan.FromSeconds(20);
    private IReadOnlyList<FolderBrowseProject>? _projects;

    public string HostDisplayName { get; } = hostDisplayName;
    public bool CanCreateFolder => true;

    public Task<FolderBrowsePage> BrowseAsync(string? path = null, bool listRoots = false, bool fallbackToParent = false, CancellationToken cancellationToken = default) =>
        RunAsync(async client =>
        {
            if (!client.SupportsBrowse) throw new IOException(unsupportedMessage);
            var browseTask = client.BrowseFolderAsync(path, listRoots, fallbackToParent, cancellationToken);
            if (_projects is null)
            {
                try
                {
                    var hostProjects = await client.ListProjectsAsync(cancellationToken).ConfigureAwait(false);
                    _projects = [.. hostProjects.Select(p => new FolderBrowseProject(p.Name, p.Path))];
                }
                catch (Exception e) when (e is IOException or RemoteException or OperationCanceledException)
                {
                    // Existing project shortcuts are a convenience; browsing still works without them.
                    if (cancellationToken.IsCancellationRequested) throw;
                    _projects = [];
                }
            }
            var result = await browseTask.ConfigureAwait(false);
            return new FolderBrowsePage(
                CurrentPath: result.Path,
                ParentPath: result.Parent,
                Separator: string.IsNullOrEmpty(result.Separator) ? "/" : result.Separator,
                HomePath: string.IsNullOrEmpty(result.Home) ? client.Info.Home : result.Home,
                Entries: [.. result.Entries.Select(FromEntry)],
                Roots: [.. result.Roots.Select(FromEntry)],
                ExistingProjects: _projects);
        }, cancellationToken);

    public Task<string> CreateFolderAsync(string parentPath, string name, CancellationToken cancellationToken = default) =>
        RunAsync(async client =>
        {
            if (!client.SupportsBrowse) throw new IOException(unsupportedMessage);
            var clean = FolderBrowserPaths.ValidateFolderName(name);
            return await client.BrowseCreateDirectoryAsync(parentPath, clean, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    private async Task<T> RunAsync<T>(Func<RemoteHostClient, Task<T>> action, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(CallLimit);
        try
        {
            var client = await runtime.ConnectAsync(hostId).WaitAsync(linked.Token).ConfigureAwait(false);
            return await action(client).WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("The host did not answer in time.");
        }
        catch (RemoteException e)
        {
            throw new IOException(e.Message, e);
        }
        catch (ObjectDisposedException e)
        {
            throw new IOException("The connection to the host closed.", e);
        }
    }

    private static FolderBrowseEntry FromEntry(BrowseEntryMessage entry) => new(
        entry.Name,
        entry.Path,
        entry.IsHidden,
        entry.IsExistingProject,
        entry.Kind switch
        {
            "drive" => FolderEntryKind.Drive,
            "home" => FolderEntryKind.Home,
            _ => FolderEntryKind.Folder,
        });
}
