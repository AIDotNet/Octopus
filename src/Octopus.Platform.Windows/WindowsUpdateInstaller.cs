using Octopus.Platform.Updates;

namespace Octopus.Platform.Windows;

/// <summary>
/// Updates an Inno Setup install by running the new setup silently over it (design/10-自动更新.md §8.1). Inno upgrades in
/// place: the same AppId finds the install, and <c>/DIR</c> keeps the folder the person chose.
/// </summary>
public sealed class WindowsUpdateInstaller : IUpdateInstaller
{
    /// <summary>The uninstaller Inno leaves in every install; a portable zip has none.</summary>
    public const string UninstallerName = "unins000.exe";

    private readonly string _programFiles;
    private readonly string _programFilesX86;

    /// <param name="appDirectory">The folder of Octopus.exe; the running app's by default.</param>
    public WindowsUpdateInstaller(string? appDirectory = null, string? programFiles = null, string? programFilesX86 = null)
    {
        Location = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDirectory ?? AppContext.BaseDirectory));
        _programFiles = programFiles ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        _programFilesX86 = programFilesX86 ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    }

    public string Target => UpdateHelper.Target("win");

    public string Location { get; }

    public InstallKind Detect() =>
        File.Exists(Path.Combine(Location, UninstallerName)) ? InstallKind.Installed : InstallKind.Portable;

    /// <summary>An install for all users lives under Program Files; its setup needs elevation (one UAC prompt).</summary>
    public bool IsAllUsersInstall => IsUnder(Location, _programFiles) || IsUnder(Location, _programFilesX86);

    public Task<string> PrepareAsync(string package, string workDirectory, CancellationToken cancellationToken)
    {
        if (!package.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateInstallException($"Not a Windows installer: {Path.GetFileName(package)}");
        }
        return Task.FromResult(package);
    }

    public void LaunchInstall(string prepared, InstallRequest request)
    {
        var script = Path.Combine(request.WorkDirectory, "install.ps1");
        UpdateHelper.WritePowerShellScript(script, BuildScript(prepared, request));
        UpdateHelper.StartDetached(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script],
            request.WorkDirectory);
    }

    /// <summary>The helper: wait for the app, run setup silently into the same folder, log the outcome.</summary>
    public string BuildScript(string setup, InstallRequest request)
    {
        var arguments = new List<string>
        {
            "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS",
            IsAllUsersInstall ? "/ALLUSERS" : "/CURRENTUSER",
            // Start-Process joins its arguments with spaces and quotes none of them: the quotes are part of the text.
            $"/DIR=\"{Location}\"",
            $"/LOG=\"{Path.Combine(request.WorkDirectory, "setup.log")}\"",
        };
        if (request.Relaunch) arguments.Add("/RELAUNCH=1");
        var argumentList = string.Join(", ", arguments.Select(UpdateHelper.PsQuote));
        return $$"""
            $ErrorActionPreference = 'Continue'
            $log = {{UpdateHelper.PsQuote(request.LogFile)}}
            function Write-Log([string]$text) { Add-Content -LiteralPath $log -Value ('[' + (Get-Date -Format o) + '] ' + $text) -Encoding UTF8 }
            Write-Log 'waiting for Octopus (pid {{request.ProcessId}}) to exit'
            Wait-Process -Id {{request.ProcessId}} -Timeout 120 -ErrorAction SilentlyContinue
            Write-Log 'running the installer'
            try {
                $p = Start-Process -FilePath {{UpdateHelper.PsQuote(setup)}} -ArgumentList @({{argumentList}}) -Wait -PassThru
                if ($p.ExitCode -eq 0) { Write-Log 'result=ok' } else { Write-Log ('result=failed: setup exited with ' + $p.ExitCode) }
            } catch {
                Write-Log ('result=failed: ' + $_.Exception.Message)
            }

            """;
    }

    private static bool IsUnder(string path, string root)
    {
        if (string.IsNullOrEmpty(root)) return false;
        root = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
