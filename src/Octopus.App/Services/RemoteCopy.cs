using System.Linq;
using Octopus.App.Localization;
using Octopus.Ssh;

namespace Octopus.App.Services;

/// <summary>How a write back to the host ended.</summary>
public enum RemoteWriteOutcome
{
    /// <summary>The host has the new content.</summary>
    Written,
    /// <summary>The person did not want to overwrite what the host has now: nothing was written.</summary>
    Declined,
    /// <summary>The host could not be written: <see cref="RemoteWriteResult.Error"/> says why.</summary>
    Failed,
}

public sealed record RemoteWriteResult(RemoteWriteOutcome Outcome, string? Error = null);

/// <summary>
/// The host's copy of a file that is open in a tab. It remembers the content the host had when the app last agreed with
/// it, so a save can tell whether the host has changed since: then the person decides before anything is overwritten.
/// </summary>
public sealed class RemoteCopy(RemoteFiles files, string host, string remotePath, byte[] synced, IDialogService dialogs)
{
    private byte[] _synced = synced;

    /// <summary>The path of the file on the host.</summary>
    public string RemotePath => remotePath;

    /// <summary>Writes the content to the host, after checking that the host still has what the app last saw there.</summary>
    public async Task<RemoteWriteResult> WriteAsync(byte[] bytes, CancellationToken cancellationToken = default)
    {
        var tree = files.TreeFor(host);
        try
        {
            var exists = await tree.ExistsAsync(remotePath, cancellationToken);
            var changed = !exists;
            if (exists)
            {
                try
                {
                    var current = await tree.ReadAsync(remotePath, RemoteFiles.MaxCopyBytes, cancellationToken);
                    changed = !current.SequenceEqual(_synced);
                }
                catch (SshFileTooLargeException)
                {
                    // Larger than the copies the app keeps: it has changed, as far as the app can tell.
                    changed = true;
                }
            }
            if (changed)
            {
                var overwrite = exists
                    ? await dialogs.ConfirmAsync(AppLangResources.Get(AppLangResourceKind.RemoteChangedTitle), AppLangResources.Get(AppLangResourceKind.RemoteChangedBody), AppLangResources.Get(AppLangResourceKind.RemoteOverwrite), isDanger: true)
                    : await dialogs.ConfirmAsync(AppLangResources.Get(AppLangResourceKind.RemoteDeletedTitle), AppLangResources.Get(AppLangResourceKind.RemoteDeletedBody), AppLangResources.Get(AppLangResourceKind.RemoteOverwrite), isDanger: true);
                if (!overwrite) return new RemoteWriteResult(RemoteWriteOutcome.Declined);
            }
            await tree.WriteAsync(remotePath, bytes, cancellationToken);
            _synced = bytes;
            return new RemoteWriteResult(RemoteWriteOutcome.Written);
        }
        catch (Exception e) when (e is SshCommandException or IOException)
        {
            return new RemoteWriteResult(RemoteWriteOutcome.Failed, e.Message);
        }
    }
}
