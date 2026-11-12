using Octopus.Platform.Updates;

namespace Octopus.Platform.Linux;

/// <summary>
/// Updates an install made by the <c>.run</c> installer by running the new one over it (design/10-自动更新.md §8.3). The
/// installer allows that: a folder with its <c>.octopus-install</c> marker is an earlier install, not someone else's files.
/// </summary>
public sealed class LinuxUpdateInstaller : IUpdateInstaller
{
    /// <summary>Written by the <c>.run</c> installer (packaging/linux/install-header.sh): <c>link=</c>, <c>entry=</c>, <c>version=</c>.</summary>
    public const string MarkerName = ".octopus-install";

    /// <param name="appDirectory">The install folder (where the Octopus executable is); the running app's by default.</param>
    public LinuxUpdateInstaller(string? appDirectory = null)
    {
        Location = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDirectory ?? AppContext.BaseDirectory));
    }

    public string Target => UpdateHelper.Target("linux");

    public string Location { get; }

    public InstallKind Detect()
    {
        if (!File.Exists(Path.Combine(Location, MarkerName))) return InstallKind.Portable;
        return UpdateHelper.CanWrite(Location) ? InstallKind.Installed : InstallKind.ReadOnly;
    }

    /// <summary>
    /// Whether the first install added the launcher entry and the PATH link. The update keeps that choice: an install
    /// made with <c>--no-desktop</c> stays without them.
    /// </summary>
    public bool HasDesktopEntry
    {
        get
        {
            try
            {
                return File.ReadLines(Path.Combine(Location, MarkerName))
                           .Any(line => line.StartsWith("link=", StringComparison.Ordinal) && line.Length > "link=".Length);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }
    }

    public Task<string> PrepareAsync(string package, string workDirectory, CancellationToken cancellationToken)
    {
        if (!package.EndsWith(".run", StringComparison.Ordinal))
        {
            throw new UpdateInstallException($"Not a Linux installer: {Path.GetFileName(package)}");
        }
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(package, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return Task.FromResult(package);
    }

    public void LaunchInstall(string prepared, InstallRequest request)
    {
        var script = Path.Combine(request.WorkDirectory, "install.sh");
        UpdateHelper.WriteShellScript(script, BuildScript(prepared, request));
        UpdateHelper.StartDetached("/bin/sh", [script], request.WorkDirectory);
    }

    /// <summary>The helper: wait for the app, run the installer into the same folder, log the outcome, relaunch.</summary>
    public string BuildScript(string installer, InstallRequest request)
    {
        var desktop = HasDesktopEntry ? "" : " --no-desktop";
        var relaunch = request.Relaunch
            ? $"cd {UpdateHelper.ShQuote(Location)} && nohup ./Octopus >/dev/null 2>&1 &"
            : ":";
        return $$"""
            #!/bin/sh
            # Installs Octopus after it has quit. Written by the app; safe to delete.
            trap '' HUP
            exec >>{{UpdateHelper.ShQuote(request.LogFile)}} 2>&1
            echo "[$(date)] waiting for Octopus (pid {{request.ProcessId}}) to exit"
            {{UpdateHelper.ShWaitForProcess(request.ProcessId)}}
            echo "[$(date)] running the installer"
            {{UpdateHelper.ShQuote(installer)}} --prefix {{UpdateHelper.ShQuote(Location)}} --yes{{desktop}}
            code=$?
            if [ "$code" -ne 0 ]; then
              echo "result=failed: the installer exited with $code"
              exit 1
            fi
            echo "result=ok"
            {{relaunch}}

            """;
    }
}
