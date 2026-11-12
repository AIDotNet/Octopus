namespace Octopus.Platform.Updates;

/// <summary>How the running copy was installed, which decides whether an update can be installed in place (design/10 §8.4).</summary>
public enum InstallKind
{
    /// <summary>Put there by the installer and writable: updates download and install when the app quits.</summary>
    Installed,
    /// <summary>A portable archive, a dmg, a translocated app: updates are only offered as a download page.</summary>
    Portable,
    /// <summary>Installed, but this user cannot write there (/opt as root, /Applications without admin rights).</summary>
    ReadOnly,
}

/// <summary>What the helper that installs an update after the app has quit needs.</summary>
/// <param name="WorkDirectory">The update's own folder in the cache: the helper script and its files go there.</param>
/// <param name="LogFile">The helper appends what it does, ending with <c>result=ok</c> or <c>result=failed: …</c>.</param>
/// <param name="ProcessId">The app's process: the helper waits for it to end before touching anything.</param>
/// <param name="Relaunch">Start the new version once it is installed.</param>
public sealed record InstallRequest(string WorkDirectory, string LogFile, int ProcessId, bool Relaunch);

/// <summary>
/// Installs a downloaded update on this platform (design/10-自动更新.md §8). The package is already verified against
/// the signed manifest when <see cref="PrepareAsync"/> sees it.
/// </summary>
public interface IUpdateInstaller
{
    /// <summary>The key of this build in the manifest's assets: <c>win-x64</c>, <c>osx-arm64</c>, <c>linux-x64</c>…</summary>
    string Target { get; }

    /// <summary>The folder or bundle an update replaces.</summary>
    string Location { get; }

    InstallKind Detect();

    /// <summary>
    /// Readies a downloaded package (unpack, check its code signature…) and returns what <see cref="LaunchInstall"/>
    /// installs from. Runs on a background thread; throws <see cref="UpdateInstallException"/> when the package cannot be used.
    /// </summary>
    Task<string> PrepareAsync(string package, string workDirectory, CancellationToken cancellationToken);

    /// <summary>
    /// Writes the helper script into the work folder and starts it on its own; returns at once. The helper waits for
    /// the app to end, installs, and relaunches when asked. Called as the app quits.
    /// </summary>
    void LaunchInstall(string prepared, InstallRequest request);
}

/// <summary>A downloaded package that cannot be installed (wrong kind, bad code signature, unpacking failed).</summary>
public sealed class UpdateInstallException(string message, Exception? inner = null) : Exception(message, inner);
