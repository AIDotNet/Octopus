using System.Diagnostics;
using Octopus.Platform.Linux;
using Octopus.Platform.MacOS;
using Octopus.Platform.Updates;
using Octopus.Platform.Windows;

namespace Octopus.Platform.Tests;

/// <summary>
/// The three update installers (design/10-自动更新.md §8). The sh helpers of macOS and Linux run for real here, against
/// throw-away installs in a temporary folder; the Windows one is checked by its text.
/// </summary>
public sealed class UpdateInstallerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("octopus update tests '").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string Dir(params string[] parts) => Directory.CreateDirectory(Path.Combine([_root, .. parts])).FullName;

    private InstallRequest Request(bool relaunch = false, int? pid = null) =>
        new(Dir("work"), Path.Combine(_root, "work", "install.log"), pid ?? FinishedProcessId(), relaunch);

    /// <summary>The id of a process that has already ended, so the helper does not wait.</summary>
    private static int FinishedProcessId()
    {
        using var process = Process.Start(new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh") { ArgumentList = { OperatingSystem.IsWindows() ? "/c" : "-c", "exit 0" } })!;
        process.WaitForExit();
        return process.Id;
    }

    private static async Task<int> RunShAsync(string script)
    {
        using var process = Process.Start(new ProcessStartInfo("/bin/sh") { ArgumentList = { script } })!;
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    [Fact]
    public void TargetsNameTheProcessArchitecture()
    {
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
        Assert.Equal("osx-" + arch, new MacUpdateInstaller(new ProcessRunner(), Dir("a")).Target);
        Assert.Equal("linux-" + arch, new LinuxUpdateInstaller(Dir("a")).Target);
        Assert.Equal("win-" + arch, new WindowsUpdateInstaller(Dir("a")).Target);
    }

    // ------------------------------------------------------------------ Windows

    [Fact]
    public void WindowsInstallsHaveAnUninstaller()
    {
        var dir = Dir("win");
        Assert.Equal(InstallKind.Portable, new WindowsUpdateInstaller(dir).Detect());
        File.WriteAllText(Path.Combine(dir, WindowsUpdateInstaller.UninstallerName), "");
        Assert.Equal(InstallKind.Installed, new WindowsUpdateInstaller(dir).Detect());
    }

    [Fact]
    public void TheWindowsHelperRunsSetupSilentlyIntoTheSameFolder()
    {
        var dir = Dir("Users", "me", "AppData", "Local", "Programs", "Octo's pus");
        var installer = new WindowsUpdateInstaller(dir, programFiles: @"C:\Program Files", programFilesX86: @"C:\Program Files (x86)");
        var script = installer.BuildScript(@"C:\cache\Octopus-1.1.0-win-x64-setup.exe", Request(relaunch: true, pid: 4242));
        Assert.Contains("Wait-Process -Id 4242", script);
        Assert.Contains("-FilePath 'C:\\cache\\Octopus-1.1.0-win-x64-setup.exe'", script);
        Assert.Contains("'/VERYSILENT'", script);
        Assert.Contains("'/CURRENTUSER'", script);
        Assert.DoesNotContain("/ALLUSERS", script);
        // The quote in the folder name is doubled inside PowerShell's single quotes; the folder itself is in double quotes for setup.
        Assert.Contains($"'/DIR=\"{dir.Replace("'", "''")}\"'", script);
        Assert.Contains("'/RELAUNCH=1'", script);
        Assert.Contains("result=ok", script);
    }

    [Fact]
    public void AnAllUsersInstallAsksForElevation()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows paths
        var installer = new WindowsUpdateInstaller(@"C:\Program Files\Octopus", programFiles: @"C:\Program Files", programFilesX86: @"C:\Program Files (x86)");
        Assert.True(installer.IsAllUsersInstall);
        var script = installer.BuildScript(@"C:\setup.exe", Request());
        Assert.Contains("'/ALLUSERS'", script);
        Assert.DoesNotContain("RELAUNCH", script);
        Assert.False(new WindowsUpdateInstaller(@"C:\Program Files Extra\Octopus", programFiles: @"C:\Program Files", programFilesX86: @"C:\Program Files (x86)").IsAllUsersInstall);
    }

    [Fact]
    public async Task WindowsOnlyTakesAnInstaller()
    {
        var installer = new WindowsUpdateInstaller(Dir("w"));
        await Assert.ThrowsAsync<UpdateInstallException>(() => installer.PrepareAsync("a.zip", Dir("work"), CancellationToken.None));
        Assert.Equal("a-setup.exe", await installer.PrepareAsync("a-setup.exe", Dir("work"), CancellationToken.None));
    }

    // ------------------------------------------------------------------ Linux

    private string LinuxInstall(string link = "/home/me/.local/bin/octopus")
    {
        var dir = Dir("opt", "octo pus");
        File.WriteAllText(Path.Combine(dir, LinuxUpdateInstaller.MarkerName), $"link={link}\nentry=/x.desktop\nversion=1.0.0\n");
        return dir;
    }

    /// <summary>A stand-in for the .run installer that records its arguments.</summary>
    private string FakeRun(int exitCode = 0)
    {
        var path = Path.Combine(Dir("dl"), "Octopus-1.1.0-linux-x64.run");
        File.WriteAllText(path, $"#!/bin/sh\nprintf '%s\\n' \"$@\" > \"$(dirname \"$0\")/args\"\nexit {exitCode}\n");
        return path;
    }

    [Fact]
    public void LinuxInstallsHaveTheMarker()
    {
        Assert.Equal(InstallKind.Portable, new LinuxUpdateInstaller(Dir("plain")).Detect());
        Assert.Equal(InstallKind.Installed, new LinuxUpdateInstaller(LinuxInstall()).Detect());
    }

    [Fact]
    public void ALinuxInstallThisUserCannotWriteIsReadOnly()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return;
        var dir = LinuxInstall();
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.Equal(InstallKind.ReadOnly, new LinuxUpdateInstaller(dir).Detect());
        }
        finally
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task TheLinuxHelperRunsTheInstallerOverTheInstall()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = LinuxInstall();
        var installer = new LinuxUpdateInstaller(dir);
        var run = await installer.PrepareAsync(FakeRun(), Dir("work"), CancellationToken.None);
        var request = Request();
        var script = Path.Combine(request.WorkDirectory, "install.sh");
        UpdateHelper.WriteShellScript(script, installer.BuildScript(run, request));
        Assert.Equal(0, await RunShAsync(script));
        Assert.Equal(["--prefix", dir, "--yes"], File.ReadAllLines(Path.Combine(Path.GetDirectoryName(run)!, "args")));
        Assert.Contains("result=ok", File.ReadAllText(request.LogFile));
    }

    [Fact]
    public async Task AnInstallWithoutDesktopEntryStaysWithout()
    {
        if (OperatingSystem.IsWindows()) return;
        var installer = new LinuxUpdateInstaller(LinuxInstall(link: ""));
        Assert.False(installer.HasDesktopEntry);
        var run = await installer.PrepareAsync(FakeRun(), Dir("work"), CancellationToken.None);
        var request = Request();
        var script = Path.Combine(request.WorkDirectory, "install.sh");
        UpdateHelper.WriteShellScript(script, installer.BuildScript(run, request));
        Assert.Equal(0, await RunShAsync(script));
        Assert.Contains("--no-desktop", File.ReadAllLines(Path.Combine(Path.GetDirectoryName(run)!, "args")));
    }

    [Fact]
    public async Task AFailingLinuxInstallerIsLogged()
    {
        if (OperatingSystem.IsWindows()) return;
        var installer = new LinuxUpdateInstaller(LinuxInstall());
        var run = await installer.PrepareAsync(FakeRun(exitCode: 3), Dir("work"), CancellationToken.None);
        var request = Request();
        var script = Path.Combine(request.WorkDirectory, "install.sh");
        UpdateHelper.WriteShellScript(script, installer.BuildScript(run, request));
        Assert.Equal(1, await RunShAsync(script));
        Assert.Contains("result=failed: the installer exited with 3", File.ReadAllText(request.LogFile));
    }

    [Fact]
    public async Task TheHelperWaitsForTheApp()
    {
        if (OperatingSystem.IsWindows()) return;
        var installer = new LinuxUpdateInstaller(LinuxInstall());
        var run = await installer.PrepareAsync(FakeRun(), Dir("work"), CancellationToken.None);
        using var app = Process.Start(new ProcessStartInfo("/bin/sleep") { ArgumentList = { "0.6" } })!;
        var request = Request(pid: app.Id);
        var script = Path.Combine(request.WorkDirectory, "install.sh");
        UpdateHelper.WriteShellScript(script, installer.BuildScript(run, request));
        var clock = Stopwatch.StartNew();
        var helper = RunShAsync(script);
        await Task.Delay(200);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(run)!, "args")), "the installer ran while the app was still up");
        Assert.Equal(0, await helper);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(400));
    }

    // ------------------------------------------------------------------ macOS

    private string MacApp(string parent, string content, string name = "Octopus.app")
    {
        var app = Path.Combine(parent, name);
        Directory.CreateDirectory(Path.Combine(app, "Contents", "MacOS"));
        File.WriteAllText(Path.Combine(app, "Contents", "MacOS", "Octopus"), content);
        return app;
    }

    [Fact]
    public void TheBundleIsFoundFromTheExecutablesFolder()
    {
        if (OperatingSystem.IsWindows()) return; // Unix paths
        Assert.Equal("/Applications/Octopus.app", MacUpdateInstaller.BundleOf("/Applications/Octopus.app/Contents/MacOS"));
        Assert.Equal("/Applications/Octopus.app", MacUpdateInstaller.BundleOf("/Applications/Octopus.app/Contents/MacOS/"));
        Assert.Null(MacUpdateInstaller.BundleOf("/Users/me/code/Octopus/bin/Debug/net11.0"));
        Assert.Null(MacUpdateInstaller.BundleOf("/Applications/Octopus/Contents/MacOS"));
    }

    [Fact]
    public void MacInstallsAreBundlesInAWritablePlace()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var app = MacApp(Dir("Applications"), "old");
        Assert.Equal(InstallKind.Installed, new MacUpdateInstaller(new ProcessRunner(), Path.Combine(app, "Contents", "MacOS")).Detect());
        Assert.Equal(InstallKind.Portable, new MacUpdateInstaller(new ProcessRunner(), Dir("bin")).Detect());
        Assert.Equal(InstallKind.Portable, new MacUpdateInstaller(new ProcessRunner(), "/private/var/folders/x/AppTranslocation/ABC/d/Octopus.app/Contents/MacOS").Detect());
        Assert.Equal(InstallKind.ReadOnly, new MacUpdateInstaller(new ProcessRunner(), "/System/Applications/Octopus.app/Contents/MacOS").Detect());
    }

    [Fact]
    public async Task TheMacHelperSwapsTheBundle()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var apps = Dir("Applications");
        var app = MacApp(apps, "old");
        var fresh = MacApp(Dir("work", "app"), "new");
        var installer = new MacUpdateInstaller(new ProcessRunner(), Path.Combine(app, "Contents", "MacOS"));
        var request = Request();
        var script = Path.Combine(request.WorkDirectory, "install.sh");
        UpdateHelper.WriteShellScript(script, installer.BuildScript(fresh, request));
        Assert.Equal(0, await RunShAsync(script));
        Assert.Equal("new", File.ReadAllText(Path.Combine(app, "Contents", "MacOS", "Octopus")));
        Assert.Equal(["Octopus.app"], Directory.EnumerateFileSystemEntries(apps).Select(Path.GetFileName));
        Assert.Contains("result=ok", File.ReadAllText(request.LogFile));
    }

    [Fact]
    public async Task AFailedCopyLeavesTheOldBundle()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var apps = Dir("Applications");
        var app = MacApp(apps, "old");
        var installer = new MacUpdateInstaller(new ProcessRunner(), Path.Combine(app, "Contents", "MacOS"));
        var request = Request();
        var script = Path.Combine(request.WorkDirectory, "install.sh");
        UpdateHelper.WriteShellScript(script, installer.BuildScript(Path.Combine(_root, "missing.app"), request));
        Assert.Equal(1, await RunShAsync(script));
        Assert.Equal("old", File.ReadAllText(Path.Combine(app, "Contents", "MacOS", "Octopus")));
        Assert.Equal(["Octopus.app"], Directory.EnumerateFileSystemEntries(apps).Select(Path.GetFileName));
        Assert.Contains("result=failed", File.ReadAllText(request.LogFile));
    }

    /// <summary>An ad-hoc signed app zipped the way the release does it.</summary>
    private async Task<string> SignedAppZipAsync(Action<string>? tamper = null)
    {
        var runner = new ProcessRunner();
        var build = Dir("build");
        var app = Path.Combine(build, "Octopus.app");
        Directory.CreateDirectory(Path.Combine(app, "Contents", "MacOS"));
        File.Copy("/usr/bin/true", Path.Combine(app, "Contents", "MacOS", "Octopus"));
        File.WriteAllText(Path.Combine(app, "Contents", "Info.plist"), """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict>
              <key>CFBundleIdentifier</key><string>test.octopus.update</string>
              <key>CFBundleExecutable</key><string>Octopus</string>
              <key>CFBundlePackageType</key><string>APPL</string>
            </dict></plist>
            """);
        Directory.CreateDirectory(Path.Combine(app, "Contents", "Resources"));
        File.WriteAllText(Path.Combine(app, "Contents", "Resources", "data.txt"), "original");
        var sign = await runner.RunAsync("/usr/bin/codesign", ["--force", "--sign", "-", app]);
        Assert.True(sign.Succeeded, sign.StandardError);
        tamper?.Invoke(app);
        var zip = Path.Combine(Dir("dl"), "Octopus-1.1.0-osx-arm64.zip");
        var pack = await runner.RunAsync("/usr/bin/ditto", ["-c", "-k", "--keepParent", app, zip]);
        Assert.True(pack.Succeeded, pack.StandardError);
        return zip;
    }

    [Fact]
    public async Task AMacPackageIsUnpackedAndItsSignatureChecked()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var zip = await SignedAppZipAsync();
        var installer = new MacUpdateInstaller(new ProcessRunner(), Dir("bin"));
        var prepared = await installer.PrepareAsync(zip, Dir("work"), CancellationToken.None);
        Assert.EndsWith("Octopus.app", prepared);
        Assert.True(File.Exists(Path.Combine(prepared, "Contents", "MacOS", "Octopus")));
    }

    [Fact]
    public async Task AChangedMacPackageIsRefused()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var zip = await SignedAppZipAsync(app => File.WriteAllText(Path.Combine(app, "Contents", "Resources", "data.txt"), "changed"));
        var installer = new MacUpdateInstaller(new ProcessRunner(), Dir("bin"));
        var e = await Assert.ThrowsAsync<UpdateInstallException>(() => installer.PrepareAsync(zip, Dir("work"), CancellationToken.None));
        Assert.Contains("code signature", e.Message);
    }

    [Fact]
    public async Task MacOnlyTakesAZip()
    {
        var installer = new MacUpdateInstaller(new ProcessRunner(), Dir("bin"));
        await Assert.ThrowsAsync<UpdateInstallException>(() => installer.PrepareAsync("a.run", Dir("work"), CancellationToken.None));
    }

    [Fact]
    public void ScriptQuotingSurvivesQuotes()
    {
        Assert.Equal("'it'\\''s'", UpdateHelper.ShQuote("it's"));
        Assert.Equal("'it''s'", UpdateHelper.PsQuote("it's"));
    }
}
