using Octopus.Platform.Updates;

namespace Octopus.Platform.MacOS;

/// <summary>
/// Updates Octopus.app by replacing the bundle (design/10-自动更新.md §8.2). The update is a zip of the signed and notarized
/// app; it is unpacked and its code signature checked before the app quits, and the helper only swaps the folders.
/// </summary>
public sealed class MacUpdateInstaller : IUpdateInstaller
{
    private readonly IProcessRunner _runner;
    private readonly string? _bundle;

    /// <param name="appDirectory">The folder of the executable (<c>Octopus.app/Contents/MacOS</c>); the running app's by default.</param>
    public MacUpdateInstaller(IProcessRunner runner, string? appDirectory = null)
    {
        _runner = runner;
        _bundle = BundleOf(Path.GetFullPath(appDirectory ?? AppContext.BaseDirectory));
        Location = _bundle ?? Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDirectory ?? AppContext.BaseDirectory));
    }

    public string Target => UpdateHelper.Target("osx");

    public string Location { get; }

    public InstallKind Detect()
    {
        // Not in a bundle (dotnet run), or run where it was opened (a dmg, a quarantined download Gatekeeper moved).
        if (_bundle is null || _bundle.Contains("/AppTranslocation/", StringComparison.Ordinal)) return InstallKind.Portable;
        var parent = Path.GetDirectoryName(_bundle);
        return parent is not null && UpdateHelper.CanWrite(parent) && UpdateHelper.CanWrite(_bundle)
            ? InstallKind.Installed
            : InstallKind.ReadOnly;
    }

    public async Task<string> PrepareAsync(string package, string workDirectory, CancellationToken cancellationToken)
    {
        if (!package.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateInstallException($"Not a macOS app archive: {Path.GetFileName(package)}");
        }
        var extracted = Path.Combine(workDirectory, "app");
        if (Directory.Exists(extracted)) Directory.Delete(extracted, recursive: true);
        Directory.CreateDirectory(extracted);
        // ditto keeps what a zip of a signed app needs: symbolic links, extended attributes, the signature's resources.
        var unzip = await _runner.RunAsync("/usr/bin/ditto", ["-x", "-k", package, extracted], timeout: TimeSpan.FromMinutes(5), cancellationToken: cancellationToken);
        if (!unzip.Succeeded) throw new UpdateInstallException($"The archive could not be unpacked: {Trim(unzip.StandardError)}");

        var app = Directory.EnumerateDirectories(extracted, "*.app").FirstOrDefault()
            ?? throw new UpdateInstallException("The archive holds no app.");
        if (!File.Exists(Path.Combine(app, "Contents", "MacOS", "Octopus")))
        {
            throw new UpdateInstallException("The archive's app has no Octopus executable.");
        }

        var verify = await _runner.RunAsync("/usr/bin/codesign", ["--verify", "--deep", "--strict", app], timeout: TimeSpan.FromMinutes(2), cancellationToken: cancellationToken);
        if (!verify.Succeeded) throw new UpdateInstallException($"The new app's code signature is not valid: {Trim(verify.StandardError)}");

        // A Developer ID build only accepts an update signed by the same team. An ad-hoc build (no team) relies on the
        // signed manifest and its SHA-256 alone.
        if (_bundle is not null && await TeamOfAsync(_bundle, cancellationToken) is { } team)
        {
            var newTeam = await TeamOfAsync(app, cancellationToken);
            if (newTeam != team) throw new UpdateInstallException($"The new app is signed by another team ({newTeam ?? "none"}, expected {team}).");
        }
        return app;
    }

    public void LaunchInstall(string prepared, InstallRequest request)
    {
        if (_bundle is null) throw new UpdateInstallException("Octopus is not running from an app bundle.");
        var script = Path.Combine(request.WorkDirectory, "install.sh");
        UpdateHelper.WriteShellScript(script, BuildScript(prepared, request));
        UpdateHelper.StartDetached("/bin/sh", [script], request.WorkDirectory);
    }

    /// <summary>
    /// The helper: wait for the app, copy the new bundle beside the old one (same volume), swap the two with renames,
    /// put the old one back when the second rename fails, relaunch.
    /// </summary>
    public string BuildScript(string newApp, InstallRequest request)
    {
        var bundle = _bundle ?? Location;
        var relaunch = request.Relaunch ? "open -n \"$app\"" : ":";
        return $$"""
            #!/bin/sh
            # Installs Octopus after it has quit. Written by the app; safe to delete.
            trap '' HUP
            exec >>{{UpdateHelper.ShQuote(request.LogFile)}} 2>&1
            echo "[$(date)] waiting for Octopus (pid {{request.ProcessId}}) to exit"
            {{UpdateHelper.ShWaitForProcess(request.ProcessId)}}
            app={{UpdateHelper.ShQuote(bundle)}}
            new={{UpdateHelper.ShQuote(newApp)}}
            parent=$(dirname "$app")
            name=$(basename "$app")
            staging="$parent/.$name.new"
            old="$parent/.$name.old"
            echo "[$(date)] replacing $app"
            rm -rf "$staging" "$old"
            if ! /usr/bin/ditto "$new" "$staging"; then
              rm -rf "$staging"
              echo "result=failed: the new app could not be copied next to the old one"
              exit 1
            fi
            if ! mv "$app" "$old"; then
              rm -rf "$staging"
              echo "result=failed: the old app could not be moved aside"
              exit 1
            fi
            if ! mv "$staging" "$app"; then
              mv "$old" "$app"
              rm -rf "$staging"
              echo "result=failed: the new app could not be moved into place"
              exit 1
            fi
            rm -rf "$old"
            echo "result=ok"
            {{relaunch}}

            """;
    }

    /// <summary>The bundle around <c>X.app/Contents/MacOS</c>; null when the executable is not in one.</summary>
    public static string? BundleOf(string executableDirectory)
    {
        var macos = new DirectoryInfo(Path.TrimEndingDirectorySeparator(executableDirectory));
        if (macos.Name != "MacOS" || macos.Parent is not { Name: "Contents" } contents || contents.Parent is not { } bundle) return null;
        return bundle.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? bundle.FullName : null;
    }

    private async Task<string?> TeamOfAsync(string app, CancellationToken cancellationToken)
    {
        // codesign -dv prints its details on standard error: "TeamIdentifier=ABCDE12345" or "TeamIdentifier=not set".
        var result = await _runner.RunAsync("/usr/bin/codesign", ["-dv", "--verbose=2", app], timeout: TimeSpan.FromMinutes(1), cancellationToken: cancellationToken);
        var line = (result.StandardError + "\n" + result.StandardOutput)
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("TeamIdentifier=", StringComparison.Ordinal));
        var team = line?["TeamIdentifier=".Length..].Trim();
        return string.IsNullOrEmpty(team) || team == "not set" ? null : team;
    }

    private static string Trim(string text) => text.Trim().Split('\n').LastOrDefault()?.Trim() ?? "";
}
